using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Ports;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Sweep;
using Keystone.Mod.Visualization;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.PrefabOptimization;
using Timberborn.TemplateCollectionSystem;
using UnityEngine;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// Singleton registry for non-block-object decorations: passive
  /// (Class A) and eco-responsive (Class B). Both are non-persisted
  /// visual objects; Class A has no controller, Class B carries one
  /// that polls environment state. See <c>DESIGN.md</c> § "Content
  /// classes" for the taxonomy this implements.
  ///
  /// <para><b>What it owns.</b>
  /// <list type="bullet">
  ///   <item>The live set of <see cref="KeystoneDecoration"/> instances --
  ///         cloned vanilla prefabs (or, eventually, custom assets)
  ///         that sit in the world without entity-system participation.</item>
  ///   <item>The amortised tick that calls
  ///         <see cref="IDecorationController.Tick"/> on each decoration
  ///         that registered a controller. Built on
  ///         <see cref="RollingSweepTicker{TUnit}"/> so per-cycle
  ///         work spreads evenly across the cycle's ticks rather
  ///         than spiking once every N frames. Inert decorations
  ///         (controller == null) are skipped in <see cref="ProcessUnit"/>
  ///         so they cost nothing per cycle.</item>
  ///   <item>Cleanup on <see cref="Despawn"/>: destroys the GameObject and
  ///         removes the entry from the live list.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Cycle cadence.</b> 5 game-seconds. Reactive controllers
  /// (e.g. moisture-driven flora visuals) need quick response to
  /// environment changes; sub-game-second cadence isn't visible to
  /// the player but a slow cadence (game-hours) would feel laggy.
  /// Five game-seconds matches the old per-frame throttle's effective
  /// rate at 1x while making the cycle duration deterministic across
  /// game speeds.</para>
  ///
  /// <para><b>Mid-cycle Spawn / Despawn.</b> A decoration added
  /// during a cycle isn't in this cycle's schedule; it gets its
  /// initial <see cref="IDecorationController.Tick"/> in
  /// <see cref="Spawn"/>, then re-tick next cycle. A decoration
  /// removed during a cycle keeps its slot in the schedule -- the
  /// controller is still called but
  /// <see cref="KeystoneDecoration.Root"/> will be <c>null</c>, which
  /// well-behaved controllers check before doing work.</para>
  /// </summary>
  public sealed class KeystoneDecorationRegistry : RollingSweepTicker<KeystoneDecoration> {

    /// <summary>5 game-seconds per cycle. Game-time cadence so
    /// behaviour is consistent across game speeds.</summary>
    private const float CycleDays = 5f / 86400f;

    private readonly IPrefabOptimizationChain _prefabChain;
    private readonly TemplateCollectionService _templates;
    private readonly IMoistureQuery _moisture;
    private readonly IContaminationQuery _contamination;
    private readonly IWaterQuery _water;
    private readonly KeystoneVisibilityHider _visibilityHider;

    private readonly List<KeystoneDecoration> _decorations = new();

    public KeystoneDecorationRegistry(
        IPrefabOptimizationChain prefabChain,
        TemplateCollectionService templates,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IWaterQuery water,
        IClock clock,
        PerfTracker perf,
        KeystoneVisibilityHider visibilityHider)
        : base(clock, perf, CycleDays) {
      _prefabChain = prefabChain;
      _templates = templates;
      _moisture = moisture;
      _contamination = contamination;
      _water = water;
      _visibilityHider = visibilityHider;
    }

    /// <summary>Live decorations, exposed read-only for diagnostics or
    /// external callers (e.g. cleanup on chunk-state change).</summary>
    public IReadOnlyList<KeystoneDecoration> Decorations => _decorations;

    /// <summary>
    /// Clone the visual prefab for <paramref name="donorBlueprintName"/>
    /// and place it at <paramref name="tile"/>. The clone is purely a
    /// visual -- no entity-system registration, no save/load, no Bindito
    /// decorators wired up.
    ///
    /// <para>Pass <paramref name="controller"/> = <c>null</c> for an inert
    /// decoration (no reactivity, zero per-cycle cost). Pass an
    /// <see cref="IDecorationController"/> implementation for reactive
    /// behaviour.</para>
    ///
    /// <para>Returns <c>null</c> if the donor blueprint can't be found
    /// in the loaded templates or the prefab chain returns no prefab.
    /// In that case nothing is spawned.</para>
    /// </summary>
    public KeystoneDecoration? Spawn(
        string donorBlueprintName,
        Vector3Int tile,
        IDecorationController? controller) {
      var donor = _templates.AllTemplates
          .FirstOrDefault(b => b.Name == donorBlueprintName);
      if (donor == null) {
        KeystoneLog.Warn(
            $"[Keystone] DecorationRegistry.Spawn: no blueprint named " +
            $"'{donorBlueprintName}' in AllTemplates. Skipping.");
        return null;
      }

      var prefab = _prefabChain.Process(donor);
      if (prefab == null) {
        KeystoneLog.Warn(
            $"[Keystone] DecorationRegistry.Spawn: prefab chain returned " +
            $"null for '{donorBlueprintName}'. Skipping.");
        return null;
      }

      // Natural-resource prefabs use BlockObject anchor convention --
      // origin at the tile's south-west corner -- so GridToWorld (un-
      // centered) lands the visual on the named tile. The
      // PassiveObjectProbe established this convention.
      var worldPos = CoordinateSystem.GridToWorld(tile);
      var instance = UnityEngine.Object.Instantiate(prefab, worldPos, Quaternion.identity);
      instance.name = $"Keystone.Decoration.{donorBlueprintName}.{tile}";

      var decoration = new KeystoneDecoration(instance, tile, controller);
      _decorations.Add(decoration);

      // Honour the vertical-view cutaway. Tile is stationary so the
      // closure can capture it; the hider re-applies on level-change
      // and on its own Tick.
      _visibilityHider.Track(instance, () => decoration.Tile);

      // Run the controller once at spawn so the decoration starts in
      // the right state instead of needing to wait for the next cycle.
      try {
        controller?.Tick(decoration, _moisture, _contamination, _water);
      } catch (Exception ex) {
        KeystoneLog.Error(
            $"[Keystone] DecorationRegistry.Spawn: initial Tick threw: {ex}");
      }

      KeystoneLog.Verbose(
          $"[Keystone] DecorationRegistry: spawned " +
          $"'{donorBlueprintName}' at {tile} -> world {worldPos} " +
          $"(reactive={controller != null}). Total live: {_decorations.Count}.");
      return decoration;
    }

    /// <summary>
    /// Register an already-built GameObject as a decoration at
    /// <paramref name="tile"/>. Use when the caller constructs its
    /// own visual (e.g. a Unity <c>ParticleSystem</c> attached to a
    /// fresh GameObject) rather than cloning a vanilla prefab via
    /// <see cref="Spawn"/>.
    ///
    /// <para>The registry positions <paramref name="existing"/> at the
    /// tile's world coordinate (BlockObject convention -- SW corner of
    /// the tile) and takes ownership: <see cref="Despawn"/> will
    /// destroy the GameObject. Don't pass a GameObject you intend to
    /// keep references to or destroy yourself.</para>
    /// </summary>
    public KeystoneDecoration RegisterExisting(
        GameObject existing,
        Vector3Int tile,
        IDecorationController? controller) {
      var worldPos = CoordinateSystem.GridToWorld(tile);
      existing.transform.position = worldPos;

      var decoration = new KeystoneDecoration(existing, tile, controller);
      _decorations.Add(decoration);

      // Honour the vertical-view cutaway. Same as Spawn -- the
      // visibility hider applies the current state immediately and
      // re-applies on level-change events.
      _visibilityHider.Track(existing, () => decoration.Tile);

      try {
        controller?.Tick(decoration, _moisture, _contamination, _water);
      } catch (Exception ex) {
        KeystoneLog.Error(
            $"[Keystone] DecorationRegistry.RegisterExisting: initial Tick threw: {ex}");
      }

      KeystoneLog.Verbose(
          $"[Keystone] DecorationRegistry: registered '{existing.name}' at " +
          $"{tile} -> world {worldPos} (reactive={controller != null}). " +
          $"Total live: {_decorations.Count}.");
      return decoration;
    }

    /// <summary>Destroys the decoration's GameObject and removes it
    /// from the live list. No-op if already removed.</summary>
    public void Despawn(KeystoneDecoration decoration) {
      if (!_decorations.Remove(decoration)) return;
      if (decoration.Root != null) {
        _visibilityHider.Untrack(decoration.Root);
        UnityEngine.Object.Destroy(decoration.Root);
      }
    }

    /// <inheritdoc />
    protected override bool ShouldRun() => _decorations.Count > 0;

    /// <inheritdoc />
    protected override void BuildSchedule(List<KeystoneDecoration> schedule) {
      // Snapshot the live set at cycle start. Mid-cycle Spawn calls
      // get their initial Tick directly in Spawn; mid-cycle Despawn
      // calls leave their reference in the schedule but Root will be
      // null, which controllers check before working.
      schedule.AddRange(_decorations);
    }

    /// <inheritdoc />
    protected override void ProcessUnit(KeystoneDecoration decoration) {
      if (decoration.Controller == null) return;
      try {
        decoration.Controller.Tick(decoration, _moisture, _contamination, _water);
      } catch (Exception ex) {
        KeystoneLog.Error(
            $"[Keystone] DecorationRegistry: controller Tick threw " +
            $"on '{decoration.Root?.name}': {ex}");
      }
    }

  }

}
