using System;
using System.Collections.Generic;
using Keystone.Mod.Diagnostics;
using Timberborn.LevelVisibilitySystem;
using Timberborn.Navigation;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace Keystone.Mod.Visualization {

  /// <summary>
  /// Honour the vertical-view (cutaway) slider for Keystone-owned
  /// non-<c>BlockObject</c> visuals -- specifically fauna agents and
  /// decoration GameObjects (mist, etc.). Vanilla
  /// <c>BlockObjectModelController</c> handles this automatically for
  /// every <c>BlockObject</c>; non-block visuals fall outside that
  /// system and would otherwise float above the cutaway level
  /// unmasked.
  ///
  /// <para><b>Pattern.</b> Mirrors vanilla
  /// <c>CharacterModelHider</c>: subscribe to
  /// <see cref="MaxVisibleLevelChangedEvent"/> for the on-change burst
  /// re-evaluation, and additionally re-evaluate per
  /// <see cref="ITickableSingleton.Tick"/> while the slider is below
  /// max (fauna move every frame, so a static set won't do). When the
  /// slider is at max, both the event handler and the tick body
  /// short-circuit -- no cost when the player isn't using the cutaway.</para>
  ///
  /// <para><b>Tracking model.</b> Consumers (fauna registry, decoration
  /// registry) push tracked instances in via <see cref="Track"/> at
  /// spawn time and pull them out via <see cref="Untrack"/> at despawn.
  /// The hider doesn't reach into the registries directly to avoid a
  /// constructor cycle (registries inject the hider; the hider would
  /// have to inject them back). Each tracked entry caches its child
  /// <see cref="Renderer"/> array on first apply so per-frame work is
  /// a renderer-enabled write per child, not a
  /// <c>GetComponentsInChildren</c> walk.</para>
  ///
  /// <para><b>Toggle mechanism.</b> Disabling <see cref="Renderer.enabled"/>
  /// on every child renderer -- handles <see cref="SkinnedMeshRenderer"/>
  /// for fauna meshes and <see cref="ParticleSystemRenderer"/> for
  /// mist particle systems uniformly. The entity GameObject itself
  /// stays active so agent components keep ticking, which is what we
  /// want (the agent should keep walking under the cutaway -- it's
  /// just not visible right now). This is functionally equivalent to
  /// vanilla's <c>SetActive(false)</c> on a named model child but
  /// doesn't depend on a specific naming convention.</para>
  ///
  /// <para><b>Coordinate convention.</b> Fauna coords come from
  /// <see cref="NavigationCoordinateSystem.WorldToGridInt"/> (the same
  /// helper vanilla's character hider uses) -- it nudges the world
  /// position up by 0.1 in Y so a character standing on a surface
  /// lands in the air voxel above the terrain, not in the terrain
  /// voxel itself. Decorations use their explicit
  /// <see cref="Vector3Int"/> tile directly (mist sits on a known
  /// tile, no nudging needed).</para>
  /// </summary>
  public sealed class KeystoneVisibilityHider : ILoadableSingleton,
                                                IUnloadableSingleton,
                                                ITickableSingleton {

    #region Fields

    private readonly EventBus _eventBus;
    private readonly ILevelVisibilityService _visibility;
    private readonly PerfTracker _perf;
    private readonly Dictionary<GameObject, Entry> _tracked = new();

    /// <summary>Reusable scratch list for renderer collection so the
    /// per-track first-apply doesn't allocate beyond the cached array
    /// we keep on the entry.</summary>
    private readonly List<Renderer> _rendererScratch = new();

    /// <summary>List used by <see cref="Tick"/> to iterate tracked
    /// entries safely while consumers may call <see cref="Untrack"/>
    /// from inside a controller / agent path mid-iteration.</summary>
    private readonly List<GameObject> _iterationScratch = new();

    #endregion

    #region Construction

    public KeystoneVisibilityHider(
        EventBus eventBus,
        ILevelVisibilityService visibility,
        PerfTracker perf) {
      _eventBus = eventBus;
      _visibility = visibility;
      _perf = perf;
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public void Load() {
      try {
        _eventBus.Register(this);
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleError(
            "KeystoneVisibilityHider.Load", "Subsystem failed", ex);
      }
    }

    /// <inheritdoc />
    public void Unload() {
      try {
        _eventBus.Unregister(this);
      } catch (System.Exception ex) {
        KeystoneLog.Error($"[Keystone] KeystoneVisibilityHider.Unload threw: {ex}");
      }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Register <paramref name="root"/> for cutaway-visibility
    /// management. <paramref name="getGridCoord"/> returns the
    /// instance's current grid coordinate -- called on each
    /// re-evaluation, so it can return a fresh value for moving
    /// instances (fauna) or a constant for stationary ones
    /// (decorations).
    ///
    /// <para>The hider applies the current visibility state once
    /// immediately so a freshly-spawned instance above the cutaway
    /// starts hidden rather than flickering for one frame.</para>
    ///
    /// <para>Re-registering the same root replaces the previous
    /// entry's coord-getter and renderer cache.</para>
    /// </summary>
    public void Track(GameObject root, Func<Vector3Int> getGridCoord) {
      if (root == null) return;
      var entry = new Entry(getGridCoord);
      _tracked[root] = entry;
      // Apply immediately so a spawn during cutaway-engaged time
      // doesn't render a flash frame at the wrong visibility.
      ApplyTo(root, entry);
    }

    /// <summary>Drop <paramref name="root"/> from tracking. No-op if
    /// the root wasn't registered or has already been untracked.
    /// Callers should invoke this in their Despawn path so the
    /// tracked dictionary doesn't accumulate dead references when
    /// Unity destroys the GameObject.</summary>
    public void Untrack(GameObject root) {
      if (root == null) return;
      _tracked.Remove(root);
    }

    #endregion

    #region Event handling

    /// <summary>Vanilla cutaway-level changed. Re-evaluate every
    /// tracked instance once.</summary>
    [OnEvent]
    public void OnMaxVisibleLevelChanged(MaxVisibleLevelChangedEvent e) {
      ApplyAll();
    }

    #endregion

    #region Tick

    /// <inheritdoc />
    public void Tick() {
      // When the slider is at max, every tracked block is visible by
      // definition -- no work to do, and the on-change event handler
      // will re-apply when the player engages the cutaway again.
      if (_visibility.LevelIsAtMax) return;
      // Outermost try/catch around ApplyAll. The inner per-entry
      // ApplyTo already has its own try/catch (line 191) for coord-
      // getter throws; this guards the iteration / scratch logic.
      try {
        ApplyAll();
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorOnce(
            "KeystoneVisibilityHider.Tick", "Subsystem failed", ex, ref _tickFailureLogged);
      }
    }

    private bool _tickFailureLogged;

    #endregion

    #region Apply

    private void ApplyAll() {
      // Fires from both the cutaway-slider event (off-tick, otherwise
      // fully hidden) and the per-Tick path (buried in the
      // Engine.TickWork aggregate). One scope makes both attributable.
      // Skip the empty case so a slider scrub with nothing tracked
      // doesn't dilute the average toward zero.
      if (_tracked.Count == 0) return;
      using var _ = _perf.Track("VisibilityHider.ApplyAll");
      _iterationScratch.Clear();
      foreach (var kv in _tracked) {
        _iterationScratch.Add(kv.Key);
      }
      for (var i = 0; i < _iterationScratch.Count; i++) {
        var root = _iterationScratch[i];
        // Defensive: the GameObject may have been Destroyed without
        // Untrack being called (cleanup path didn't fire, scene
        // teardown, etc.). Drop dead entries on first encounter.
        if (root == null) {
          _tracked.Remove(root);
          continue;
        }
        if (!_tracked.TryGetValue(root, out var entry)) continue;
        ApplyTo(root, entry);
      }
    }

    private void ApplyTo(GameObject root, Entry entry) {
      Vector3Int grid;
      try {
        grid = entry.GetGridCoord();
      } catch (Exception ex) {
        // A faulty coord-getter (e.g. fauna agent that tried to read a
        // disposed Transform) shouldn't take down the whole pass.
        KeystoneLog.Verbose(
            $"[Keystone] VisibilityHider: coord-getter on '{root.name}' threw " +
            $"{ex.GetType().Name}: {ex.Message}. Skipping.");
        return;
      }

      var shouldBeVisible = _visibility.BlockIsVisible(grid);
      if (entry.LastApplied.HasValue && entry.LastApplied.Value == shouldBeVisible) {
        return;
      }

      EnsureRenderersCached(root, entry);
      var renderers = entry.Renderers;
      for (var i = 0; i < renderers.Length; i++) {
        var r = renderers[i];
        if (r != null) r.enabled = shouldBeVisible;
      }
      entry.LastApplied = shouldBeVisible;
    }

    private void EnsureRenderersCached(GameObject root, Entry entry) {
      if (entry.Renderers != null) return;
      _rendererScratch.Clear();
      root.GetComponentsInChildren<Renderer>(includeInactive: true, _rendererScratch);
      entry.Renderers = _rendererScratch.ToArray();
    }

    #endregion

    #region Nested types

    /// <summary>Per-tracked-root state. Mutable for in-place renderer
    /// caching and last-applied tracking -- class, not struct, so the
    /// dictionary stores a reference rather than copies on lookup.</summary>
    private sealed class Entry {
      public Func<Vector3Int> GetGridCoord;
      public Renderer[]? Renderers;
      public bool? LastApplied;
      public Entry(Func<Vector3Int> getGridCoord) {
        GetGridCoord = getGridCoord;
      }
    }

    #endregion

  }

}
