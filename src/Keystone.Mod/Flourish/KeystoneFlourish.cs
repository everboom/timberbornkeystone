using System;
using Keystone.Core.Flourish;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.Persistence;
using Timberborn.TickSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace Keystone.Mod.Flourish {

  // The three flourish axes (FlourishPhase, FlourishLifeStatus,
  // FlourishHealth) plus the FlourishVisualLeaf enum and the pure
  // LeafFor / ShouldDieFromBadwater rules live in
  // Keystone.Core.Flourish.FlourishVisuals. This file consumes them
  // and adds the Mod-side wiring (GameObject leaf resolution + active
  // toggle, vanilla DyingNaturalResource event subscriptions,
  // BaseComponent lifecycle, save/load, per-tick badwater self-kill).

  /// <summary>
  /// Per-entity decorator attached by the template chain to any blueprint
  /// carrying <see cref="KeystoneFlourishSpec"/>. Exposes Keystone's
  /// own three-axis visual model -- <see cref="FlourishPhase"/>,
  /// <see cref="FlourishLifeStatus"/>, and <see cref="FlourishHealth"/>
  /// -- and toggles the matching child GameObject in the blueprint's
  /// vanilla-shaped hierarchy.
  ///
  /// <para><b>Hierarchy assumed (mirrors vanilla flora):</b>
  /// <list type="bullet">
  ///   <item><c>#Models / Seedling / #Alive</c>, <c>/Seedling/#Dying</c>,
  ///         <c>/Seedling/#Dead</c></item>
  ///   <item><c>#Models / Mature / #Alive</c>, <c>/Mature/#Dying</c>,
  ///         <c>/Mature/#Dead</c></item>
  ///   <item><c>#Models / Stump</c> (optional; trees only)</item>
  /// </list>
  /// Six leaves for crops, seven for trees. Authors omit any leaves
  /// that don't apply to a given flourish; missing leaves resolve to
  /// null and are silently skipped by <see cref="UpdateVisuals"/>.</para>
  ///
  /// <para><b>Axis responsibilities:</b>
  /// <list type="bullet">
  ///   <item><b>Phase</b> -- manual. No auto-progression. Defaults to
  ///         <see cref="FlourishPhase.Mature"/>; callers move it via
  ///         <see cref="SetPhase"/>.</item>
  ///   <item><b>LifeStatus</b> -- manual. Defaults to
  ///         <see cref="FlourishLifeStatus.Alive"/>; callers move it
  ///         via <see cref="SetLifeStatus"/>.
  ///         <see cref="LivingNaturalResource.Died"/> is deliberately
  ///         <i>not</i> subscribed -- if vanilla's drought timer expires
  ///         and flips <c>IsDead</c>, this decorator ignores it. Death
  ///         is a Keystone-domain decision.</item>
  ///   <item><b>Health</b> -- auto. Driven by
  ///         <see cref="DyingNaturalResource.StartedDying"/> /
  ///         <see cref="DyingNaturalResource.StoppedDying"/>; no spec
  ///         means no auto-wiring and Health stays
  ///         <see cref="FlourishHealth.Healthy"/>. Manual
  ///         <see cref="SetHealth"/> overrides until the next vanilla
  ///         event fires.</item>
  /// </list></para>
  ///
  /// <para><b>Why not vanilla's <c>NaturalResourceModel</c>.</b> Keystone
  /// flourishes opt out of <c>GrowableSpec</c>, which is what attaches
  /// vanilla's <c>Growable</c> + per-stage <c>NaturalResourceLifecycleModel</c>
  /// machinery. Without <c>Growable</c>, <c>NaturalResourceModel.ShowCurrentModel</c>
  /// NREs (patched out by
  /// <see cref="Keystone.Mod.HarmonyPatches.NaturalResourceModelShowCurrentModelPatch"/>).
  /// This decorator replaces that pipeline with a simpler one we own
  /// end-to-end.</para>
  /// </summary>
  public sealed class KeystoneFlourish
      : TickableComponent, IRegisteredComponent, IInitializableEntity,
        IDeletableEntity, IPersistentEntity {

    // IRegisteredComponent is an empty marker that makes this component
    // reachable through EntityComponentRegistry.GetEnabled<KeystoneFlourish>().
    // KeystoneFlourishDecayTicker relies on that to enumerate every live
    // flourish once per decay cycle and roll the dead ones for removal,
    // rather than walking the block service tile-by-tile. Adding it has no
    // other behavioural effect (the entity system auto-registers it at
    // spawn and unregisters at delete).

    #region Persistence keys

    private static readonly ComponentKey ComponentKey = new("KeystoneFlourish");
    private static readonly PropertyKey<string> PhaseKey = new("Phase");
    private static readonly PropertyKey<string> LifeStatusKey = new("LifeStatus");

    #endregion

    #region Fields

    private readonly IWaterQuery _water;
    private BlockObject? _blockObject;
    private DyingNaturalResource? _dying;

    // Cached leaf GameObjects, one per (phase, life-status, health)
    // visual cell plus the standalone Stump. Resolved once at
    // InitializeEntity; null entries are blueprints that don't author
    // the corresponding leaf.
    private GameObject? _seedlingAlive;
    private GameObject? _seedlingDying;
    private GameObject? _seedlingDead;
    private GameObject? _matureAlive;
    private GameObject? _matureDying;
    private GameObject? _matureDead;
    private GameObject? _stump;

    private bool _initialized;

    #endregion

    #region Construction

    public KeystoneFlourish(IWaterQuery water) {
      _water = water;
    }

    #endregion

    #region Public state

    /// <summary>Current phase. Default <see cref="FlourishPhase.Mature"/>;
    /// move via <see cref="SetPhase"/>.</summary>
    public FlourishPhase CurrentPhase { get; private set; } = FlourishPhase.Mature;

    /// <summary>Current life status. Default
    /// <see cref="FlourishLifeStatus.Alive"/>; move via
    /// <see cref="SetLifeStatus"/>. Not auto-driven.</summary>
    public FlourishLifeStatus CurrentLifeStatus { get; private set; } = FlourishLifeStatus.Alive;

    /// <summary>Current health. Default <see cref="FlourishHealth.Healthy"/>;
    /// driven by <see cref="DyingNaturalResource"/> events when the
    /// blueprint has Watered/Floodable specs, or moved manually via
    /// <see cref="SetHealth"/>.</summary>
    public FlourishHealth CurrentHealth { get; private set; } = FlourishHealth.Healthy;

    #endregion

    #region Static helpers

    /// <summary>
    /// True if <paramref name="bo"/> carries a
    /// <see cref="KeystoneFlourish"/> whose
    /// <see cref="CurrentLifeStatus"/> is
    /// <see cref="FlourishLifeStatus.Dead"/>. Spawn handlers use this
    /// to recognise the dead-flourish-is-replaceable contract:
    /// killed-by-attrition flourishes leave persistent dead visuals
    /// in the world, and when the biome eventually recovers enough
    /// for spawning to resume, the new spawn takes over the tile
    /// (replacing the dead remains).
    /// </summary>
    public static bool IsDeadFlourish(BlockObject bo) {
      if (bo == null) return false;
      var flourish = bo.GetComponent<KeystoneFlourish>();
      return flourish != null
          && flourish.CurrentLifeStatus == FlourishLifeStatus.Dead;
    }

    #endregion

    #region Public API

    /// <summary>Set the current phase and refresh visuals. No-op if
    /// the new value equals the current one. Safe to call before
    /// <see cref="InitializeEntity"/>; the visual update is deferred
    /// until init resolves the leaf GameObjects.</summary>
    public void SetPhase(FlourishPhase phase) {
      if (CurrentPhase == phase) return;
      CurrentPhase = phase;
      if (_initialized) UpdateVisuals();
    }

    /// <summary>Set the current life status and refresh visuals. No-op
    /// if the new value equals the current one.</summary>
    public void SetLifeStatus(FlourishLifeStatus status) {
      if (CurrentLifeStatus == status) return;
      CurrentLifeStatus = status;
      if (_initialized) UpdateVisuals();
    }

    /// <summary>Set the current health and refresh visuals. No-op if
    /// the new value equals the current one. Last-write-wins against
    /// auto-driven Watered/Floodable events: callers can override
    /// vanilla health, but the next moisture/flood transition will
    /// recompute and may overwrite this value.</summary>
    public void SetHealth(FlourishHealth health) {
      if (CurrentHealth == health) return;
      CurrentHealth = health;
      if (_initialized) UpdateVisuals();
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public void InitializeEntity() {
      // Per-entity isolation: a malformed vanilla DyingNaturalResource
      // event API (mod that patches it weirdly, missing event raiser,
      // etc.) or a leaf-resolution failure on an unexpected mesh
      // hierarchy shouldn't kill the entity at spawn or skip every
      // subsequent entity's init. Catch + log; leave the flourish in
      // a minimal-working state (no event subscription, default
      // visuals from spec).
      try {
        _blockObject = GetComponent<BlockObject>();
        _dying = GetComponent<DyingNaturalResource>();

        ResolveLeaves();

        if (_dying != null) {
          _dying.StartedDying += OnDyingStarted;
          _dying.StoppedDying += OnDyingStopped;
        }

        // Pull initial Health from vanilla if the source is wired up; if
        // not, the default (Healthy) holds. Phase and LifeStatus stay at
        // their defaults regardless.
        _initialized = true;
        RecomputeHealthFromVanilla();
        UpdateVisuals();
      } catch (System.Exception ex) {
        LifecycleGuard.HandleError($"KeystoneFlourish.InitializeEntity on '{Name}'", "Per-entity init errors", ex);
      }
    }

    /// <inheritdoc />
    /// <remarks>Per-entity badwater self-kill: if the flourish is
    /// alive and standing in water whose contamination exceeds
    /// <see cref="BadwaterContaminationThreshold"/>, flip
    /// <see cref="CurrentLifeStatus"/> to <see cref="FlourishLifeStatus.Dead"/>.
    /// Same architecture as <see cref="KeystoneRockTint"/>'s per-tick
    /// re-tint poll: the entity reads its own tile signal directly
    /// rather than waiting for a chunk-aggregated <c>AttritionHandler</c>
    /// rule, which can miss localised badwater pools because of
    /// bilinear-sample dilution at chunk boundaries.
    /// <para>Fast-path: dead flourishes do nothing; flourishes
    /// without a captured <see cref="_blockObject"/> (init not run
    /// yet) do nothing; the underwater + contamination check is two
    /// port reads per cycle. Soil-side contamination kills (Dry's
    /// non-habitat rule, Contaminated's blanket rule) still flow
    /// through <c>AttritionHandler</c> — the per-tile self-kill is
    /// just for the case the chunk-aggregate signal can't catch.</para></remarks>
    public override void Tick() {
      var bo = _blockObject;
      if (bo == null) return;
      // Per-entity isolation: a port read (water depth / contamination)
      // can fail unexpectedly under a degraded mod stack -- a missing
      // surface, a divergent adapter shape, etc. A throw here would
      // skip every subsequent flourish in the tick queue. Catch + log
      // once per entity, leave life status at last known value; the
      // next tick re-tries naturally.
      try {
        var coords = bo.Coordinates;
        var surface = new SurfaceCoord(coords.x, coords.y, coords.z);
        if (FlourishVisuals.ShouldDieFromBadwater(
                CurrentLifeStatus,
                _water.WaterDepthAt(surface),
                _water.WaterContaminationAt(surface),
                FlourishVisuals.BadwaterContaminationThreshold)) {
          SetLifeStatus(FlourishLifeStatus.Dead);
        }
      } catch (System.Exception ex) {
        LifecycleGuard.HandleErrorOnce("KeystoneFlourish.Tick", "Per-entity tick errors", ex, ref _tickFailureLogged);
      }
    }

    /// <summary>One-shot rate-limit so a persistently-failing entity
    /// doesn't spam <c>Player.log</c> with one error per tick. Cleared
    /// only when the entity is destroyed.</summary>
    private bool _tickFailureLogged;

    /// <inheritdoc />
    public void DeleteEntity() {
      // Unwind subscriptions so destroyed entities don't leak handlers.
      if (_dying != null) {
        _dying.StartedDying -= OnDyingStarted;
        _dying.StoppedDying -= OnDyingStopped;
      }
    }

    /// <inheritdoc />
    /// <remarks>Persists <see cref="CurrentPhase"/> and
    /// <see cref="CurrentLifeStatus"/> so attrition kills (LifeStatus
    /// = Dead) and any future phase transitions survive save/load.
    /// <see cref="CurrentHealth"/> is intentionally not persisted —
    /// it re-derives from <c>DyingNaturalResource</c> events at init
    /// via <see cref="RecomputeHealthFromVanilla"/>, so the current
    /// environmental state is what determines it post-load.
    /// <para>Elide writes when both axes are at their defaults so
    /// untouched flourishes don't bloat the save file. Matches the
    /// convention used by <c>KeystoneVariant</c>.</para></remarks>
    public void Save(IEntitySaver entitySaver) {
      if (CurrentPhase == FlourishPhase.Mature
          && CurrentLifeStatus == FlourishLifeStatus.Alive) {
        return;
      }
      var saver = entitySaver.GetComponent(ComponentKey);
      saver.Set(PhaseKey, CurrentPhase.ToString());
      saver.Set(LifeStatusKey, CurrentLifeStatus.ToString());
    }

    /// <inheritdoc />
    /// <remarks>Restores <see cref="CurrentPhase"/> and
    /// <see cref="CurrentLifeStatus"/>. We bypass
    /// <see cref="SetPhase"/> / <see cref="SetLifeStatus"/> because
    /// <see cref="_initialized"/> is still false here — the visual
    /// refresh would be premature. <see cref="InitializeEntity"/>
    /// fires <see cref="UpdateVisuals"/> after this returns, using
    /// the restored values.</remarks>
    public void Load(IEntityLoader entityLoader) {
      if (!entityLoader.TryGetComponent(ComponentKey, out var loader)) return;
      if (loader.Has(PhaseKey)) {
        if (Enum.TryParse<FlourishPhase>(loader.Get(PhaseKey), out var phase)) {
          CurrentPhase = phase;
        }
      }
      if (loader.Has(LifeStatusKey)) {
        if (Enum.TryParse<FlourishLifeStatus>(loader.Get(LifeStatusKey), out var status)) {
          CurrentLifeStatus = status;
        }
      }
    }

    #endregion

    #region Internals

    private void ResolveLeaves() {
      _seedlingAlive = Transform.Find("#Models/Seedling/#Alive")?.gameObject;
      _seedlingDying = Transform.Find("#Models/Seedling/#Dying")?.gameObject;
      _seedlingDead  = Transform.Find("#Models/Seedling/#Dead")?.gameObject;
      _matureAlive   = Transform.Find("#Models/Mature/#Alive")?.gameObject;
      _matureDying   = Transform.Find("#Models/Mature/#Dying")?.gameObject;
      _matureDead    = Transform.Find("#Models/Mature/#Dead")?.gameObject;
      _stump         = Transform.Find("#Models/Stump")?.gameObject;

      // If every leaf resolves to null, the blueprint hierarchy doesn't
      // match what this decorator expects. Most likely cause: the JSON
      // wasn't redeployed after a hierarchy refactor. Without leaves we
      // can't toggle anything, so all state GameObjects stay in their
      // prefab-default active state -- which usually means several
      // variants render simultaneously. Log loudly so the cause is
      // obvious instead of debugging the visuals.
      if (_seedlingAlive == null && _seedlingDying == null && _seedlingDead == null
          && _matureAlive == null && _matureDying == null && _matureDead == null
          && _stump == null) {
        KeystoneLog.Warn(
            $"[Keystone] KeystoneFlourish '{Name}': no state leaves found under " +
            "#Models/(Seedling|Mature)/#(Alive|Dying|Dead) or #Models/Stump. " +
            "Visual switching will be inert and all default-active variants " +
            "will render. Check that the deployed blueprint matches the " +
            "decorator's expected hierarchy (run SDK Mod Builder if the JSON " +
            "was edited).");
      }
    }

    private void OnDyingStarted(object sender, EventArgs e) {
      try {
        CurrentHealth = FlourishHealth.Dry;
        UpdateVisuals();
      } catch (Exception ex) {
        KeystoneLog.Error($"[Keystone] KeystoneFlourish '{Name}' OnDyingStarted threw: {ex}");
      }
    }

    private void OnDyingStopped(object sender, EventArgs e) {
      try {
        CurrentHealth = FlourishHealth.Healthy;
        UpdateVisuals();
      } catch (Exception ex) {
        KeystoneLog.Error($"[Keystone] KeystoneFlourish '{Name}' OnDyingStopped threw: {ex}");
      }
    }

    private void RecomputeHealthFromVanilla() {
      // Initial-state poll for Health based on whether DyingNaturalResource
      // already reports drying at init time. Defensive in case the event
      // fired before our subscription -- though in practice
      // InitializeEntity runs before vanilla decorators get a chance to
      // tick.
      var isDying = _dying != null && _dying.IsDying;
      CurrentHealth = isDying ? FlourishHealth.Dry : FlourishHealth.Healthy;
    }

    private void UpdateVisuals() {
      // Hide every leaf, then activate the one matching
      // (phase, life-status, health). Cheap because there are at
      // most seven SetActive calls.
      if (_seedlingAlive != null) _seedlingAlive.SetActive(false);
      if (_seedlingDying != null) _seedlingDying.SetActive(false);
      if (_seedlingDead  != null) _seedlingDead.SetActive(false);
      if (_matureAlive   != null) _matureAlive.SetActive(false);
      if (_matureDying   != null) _matureDying.SetActive(false);
      if (_matureDead    != null) _matureDead.SetActive(false);
      if (_stump         != null) _stump.SetActive(false);

      var leaf = LeafFor(CurrentPhase, CurrentLifeStatus, CurrentHealth);
      if (leaf != null) leaf.SetActive(true);
    }

    private GameObject? LeafFor(
        FlourishPhase phase,
        FlourishLifeStatus lifeStatus,
        FlourishHealth health) {
      var leaf = FlourishVisuals.LeafFor(phase, lifeStatus, health);
      return leaf switch {
        FlourishVisualLeaf.SeedlingAlive => _seedlingAlive,
        FlourishVisualLeaf.SeedlingDying => _seedlingDying,
        FlourishVisualLeaf.SeedlingDead => _seedlingDead,
        FlourishVisualLeaf.MatureAlive => _matureAlive,
        FlourishVisualLeaf.MatureDying => _matureDying,
        FlourishVisualLeaf.MatureDead => _matureDead,
        FlourishVisualLeaf.Stump => _stump,
        _ => null,
      };
    }

    #endregion

  }

}
