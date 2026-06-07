using System.Collections.Generic;
using Keystone.Core.Flourish;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Decoration;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.NaturalResourcesMoisture;
using Timberborn.Persistence;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using Timberborn.WorldPersistence;

namespace Keystone.Mod.Overgrowth {

  /// <summary>
  /// Per-instance augmentation component attached (via
  /// <c>AddDecorator&lt;TreeComponentSpec, KeystoneOvergrowth&gt;</c>) to
  /// every tree in the game — vanilla and any faction's, since they all
  /// carry <c>TreeComponentSpec</c>. Water-based trees (mangrove and any
  /// future aquatic species) self-filter out at runtime (see
  /// <see cref="CanOvergrow"/>). Holds an additive flourish decoration
  /// layered around the host entity; the host (the tree) is never
  /// modified, so all its native behaviour (growth, cutting,
  /// reproduction) is untouched.
  ///
  /// <para><b>Two independent concerns on one component.</b>
  /// <list type="bullet">
  ///   <item><b>Overgrowth (graphics)</b> — the optional flourish overlay
  ///         (<see cref="IsOvergrown"/>). Purely cosmetic; draped /
  ///         removed by the biome driver at the player-tunable overgrowth
  ///         rate.</item>
  ///   <item><b>Reclamation maturity (gameplay clock)</b> — a <i>dead</i>
  ///         host accrues <see cref="Maturity"/> at
  ///         <see cref="MaturityGainPerDay"/> per game-day while the tile
  ///         is moist (and uncontaminated), shedding
  ///         <see cref="MaturityLossPerDay"/> per game-day while dry (slow
  ///         accrue, fast loss; floored at 0). This is the reseed clock:
  ///         once maturity (plus biome maturity) is high enough,
  ///         <see cref="OvergrowthHandler"/> replaces the dead tree with a
  ///         seedling. <b>It runs whether or not the tree is visually
  ///         overgrown</b>, so the overgrowth rate (graphical cost) and the
  ///         replacement speed (gameplay) are separate knobs (see
  ///         <c>KeystoneOvergrowthSettings</c>). A living host has no
  ///         clock.</item>
  /// </list>
  /// Throttled tick; living, un-decorated trees early-out at near-zero
  /// cost — same shape as <c>KeystoneGrowthBonus</c>.</para>
  ///
  /// <para><b>Persistence.</b> The logical state (is-decorated, which
  /// composition, accrued reclamation maturity, terminal overgrowth-dead
  /// flag) persists via <see cref="IPersistentEntity"/> — including a dead
  /// barren tree's maturity, which exists with no visual. The decoration
  /// GameObject itself is a non-persisted
  /// <see cref="KeystoneDecorationRegistry"/> object, re-spawned from the
  /// saved <see cref="_decorationId"/> in <see cref="InitializeEntity"/> on
  /// load. Reversible alive/dying is moisture-derived by the controller
  /// (not persisted); the terminal <see cref="IsDead"/> overgrowth state IS
  /// persisted.</para>
  ///
  /// <para><b>Rendering note.</b> The host tree renders from the custom
  /// flora matrix (not its Unity Transform), so the overlay can't read
  /// the tree's randomised scale/rotation off the Transform. The
  /// decoration is a plain GameObject that DOES honour its own transform,
  /// so it renders fine; matching it to the tree's random size is a later
  /// refinement (see issue #33).</para>
  /// </summary>
  public sealed class KeystoneOvergrowth
      : TickableComponent, IRegisteredComponent,
        IInitializableEntity, IDeletableEntity, IPersistentEntity {

    #region Constants

    /// <summary>Default overgrowth composition — used by the dev tool's
    /// no-arg <see cref="Apply()"/> and as the fallback when a recipe or a
    /// loaded save supplies an empty composition. The biome-driven
    /// <see cref="OvergrowthHandler"/> passes a real composition (weighted-
    /// picked from the recipe's <c>Compositions</c>), so this is only a
    /// floor. One of the purpose-built overgrowth minis (undergrowth rings
    /// the trunk via <c>--clear-center</c>; carries the standard
    /// <c>#Models/Mature/#Alive|#Dying|#Dead</c> hierarchy the lifecycle
    /// controller manages).</summary>
    private const string DefaultComposition = "KeystoneOvergrowthMini1";

    /// <summary>Tick-counter cadence for the maturity update — a few
    /// times per game day (matches <c>KeystoneGrowthBonus</c>). The rate
    /// uses elapsed game-days, so the cadence only batches the work.</summary>
    private const int MaturityCheckIntervalTicks = 800;

    /// <summary>Maturity gained per game-day while the dead host's tile is
    /// moist (~1 point/day, so maturity reads as "reclaimable days").</summary>
    private const float MaturityGainPerDay = 1.0f;

    /// <summary>Maturity shed per game-day while the tile is dry — twice the
    /// gain rate (the mod's slow-accrue / fast-loss grammar).</summary>
    private const float MaturityLossPerDay = 2.0f;

    private static readonly ComponentKey ComponentKey = new("KeystoneOvergrowth");
    private static readonly PropertyKey<bool> DecoratedKey = new("Decorated");
    private static readonly PropertyKey<string> DecorationIdKey = new("DecorationId");
    private static readonly PropertyKey<float> MaturityKey = new("Maturity");
    private static readonly PropertyKey<bool> DeadKey = new("Dead");

    #endregion

    #region Injected services

    private readonly KeystoneDecorationRegistry _decorations;
    private readonly IDayNightCycle _dayNightCycle;
    private readonly IMoistureQuery _moisture;
    private readonly IWaterQuery _water;

    #endregion

    #region Per-instance state

    /// <summary>Logical "is this tree overgrown" flag — the persisted,
    /// authoritative visual state. The live decoration GameObjects in
    /// <see cref="_spawned"/> are rebuilt from it on load.</summary>
    private bool _decorated;

    /// <summary>Which composition this tree is overgrown with. Persisted
    /// so the same one returns after reload.</summary>
    private string _decorationId = DefaultComposition;

    /// <summary>Accrued reclamation maturity of a <i>dead</i> host — the
    /// reseed clock. Accrues independently of the overgrowth visual; 0 on
    /// living trees. Persisted.</summary>
    private float _maturity;

    /// <summary>Terminal dead state of the overgrowth <i>visual</i> — set by
    /// <see cref="Kill"/> (Dry-biome attrition / badwater). Once dead, the
    /// controller pins <c>#Dead</c> and the decay ticker eventually clears
    /// the overlay. Independent of the host tree's life state and of
    /// <see cref="_maturity"/>. Cleared by <see cref="Clear"/>. Persisted.</summary>
    private bool _dead;

    /// <summary>Cached host life component (trees carry one). Read every
    /// tick to decide whether the reclamation clock runs.</summary>
    private LivingNaturalResource _living;

    /// <summary>Mirror of "host was dead last tick", so the clock baseline
    /// (<see cref="_lastCheckDay"/>) is reset the moment a tree dies rather
    /// than back-dating the whole time it was alive. Transient.</summary>
    private bool _wasDead;

    private readonly List<KeystoneDecoration> _spawned = new();
    private float _lastCheckDay;
    private int _tickCounter;
    private bool _tickFailureLogged;

    #endregion

    #region Construction

    public KeystoneOvergrowth(
        KeystoneDecorationRegistry decorations,
        IDayNightCycle dayNightCycle,
        IMoistureQuery moisture,
        IWaterQuery water) {
      _decorations = decorations;
      _dayNightCycle = dayNightCycle;
      _moisture = moisture;
      _water = water;
    }

    #endregion

    #region Public API

    /// <summary>True when this tree is logically overgrown (persisted).
    /// Drives the dev-tool toggle and re-hydration on load.</summary>
    public bool IsOvergrown => _decorated;

    /// <summary>Accrued reclamation maturity of a dead host (the reseed
    /// clock); 0 on living trees. Read by the reseed gate; exposed for
    /// diagnostics.</summary>
    public float Maturity => _maturity;

    /// <summary>True once the overgrowth <i>visual</i> has been terminally
    /// killed (Dry attrition / badwater). The decoration shows <c>#Dead</c>
    /// and awaits removal by the decay ticker. Does not affect the
    /// reclamation clock.</summary>
    public bool IsDead => _dead;

    /// <summary>Terminally kill the overgrowth visual (Dry-biome attrition /
    /// badwater). No-op if not overgrown or already dead. The controller
    /// flips to <c>#Dead</c> on its next tick. Leaves <see cref="Maturity"/>
    /// untouched — the dead host keeps reclaiming toward reseed.</summary>
    public void Kill() {
      if (!_decorated || _dead) return;
      _dead = true;
    }

    /// <summary>False for water-based trees (mangrove and any future
    /// aquatic species): overgrowth is a land-recovery signal, so trees
    /// standing in water are skipped. Detected via
    /// <c>FloodableNaturalResourceSpec.MinWaterHeight</c> — the same
    /// aquatic gate <c>KeystoneGrowthBonus</c> uses.</summary>
    public bool CanOvergrow {
      get {
        var floodable = GetComponent<FloodableNaturalResourceSpec>();
        return floodable == null || floodable.MinWaterHeight <= 0;
      }
    }

    /// <summary>Drape the host in a flourish decoration of the given
    /// <paramref name="composition"/> blueprint. No-op if already
    /// overgrown, the host has no <see cref="BlockObject"/>, or the host
    /// is a water-based tree (<see cref="CanOvergrow"/>). An empty
    /// composition falls back to the default. Purely cosmetic — does not
    /// touch the reclamation clock.</summary>
    public void Apply(string composition) {
      if (_decorated || !CanOvergrow) return;
      if (GetComponent<BlockObject>() == null) return;
      _decorated = true;
      _decorationId = string.IsNullOrEmpty(composition) ? DefaultComposition : composition;
      SpawnDecoration();
    }

    /// <summary>Overgrow with the default composition — used by
    /// <see cref="OvergrowthTestTool"/>. The biome-driven
    /// <see cref="OvergrowthHandler"/> calls <see cref="Apply(string)"/>
    /// with the recipe's composition.</summary>
    public void Apply() => Apply(DefaultComposition);

    /// <summary>Remove the overgrowth <i>visual</i> from this entity
    /// (logical + GameObjects). Deliberately does <b>not</b> reset
    /// <see cref="Maturity"/>: clearing a decayed overlay off a still-dead
    /// tree must not wipe its reclamation progress (the visual and the
    /// reseed clock are independent). Maturity resets only when the host
    /// stops being dead (revival / replacement).</summary>
    public void Clear() {
      _decorated = false;
      _dead = false;
      DespawnAll();
    }

    #endregion

    #region Lifecycle

    /// <summary>Runs after <see cref="Load"/> on load (and on fresh
    /// placement). Caches the host life component, and if the persisted
    /// state says decorated, re-spawns the (non-persisted) decoration from
    /// <see cref="_decorationId"/>. On a fresh, never-overgrown tree the
    /// re-spawn is a no-op.</summary>
    public void InitializeEntity() {
      _living = GetComponent<LivingNaturalResource>();
      if (_decorated && CanOvergrow) {
        SpawnDecoration();
      }
    }

    /// <summary>Tick-system init: stagger the throttle by tile so trees
    /// don't all update on the same frame, and seed the elapsed-time
    /// baseline.</summary>
    public override void StartTickable() {
      _lastCheckDay = _dayNightCycle.PartialDayNumber;
      _tickCounter = StaggerTicks(GetComponent<BlockObject>());
    }

    /// <summary>Per-tick work, split between the two concerns:
    /// <list type="bullet">
    ///   <item><b>Visual:</b> badwater kills the overgrowth overlay (if
    ///         present + alive) — the SAME predicate + threshold Class B
    ///         uses.</item>
    ///   <item><b>Clock:</b> a dead host accrues / sheds reclamation
    ///         maturity (throttled), moist → gain, dry/badwater → loss,
    ///         floored at 0 — regardless of the visual.</item>
    /// </list>
    /// Living, un-decorated trees (the majority) early-out.</summary>
    public override void Tick() {
      var blockObject = GetComponent<BlockObject>();
      if (blockObject == null) return;
      var hostDead = _living != null && _living.IsDead;

      // Fast path: a living, un-decorated tree has no overlay to maintain
      // and no reclamation clock. (Clear a revived tree's stale maturity.)
      if (!_decorated && !hostDead) {
        _wasDead = false;
        if (_maturity != 0f) _maturity = 0f;
        return;
      }

      var c = blockObject.Coordinates;
      var surface = new SurfaceCoord(c.x, c.y, c.z);

      try {
        var badwater = FlourishVisuals.ShouldDieFromBadwater(
            FlourishLifeStatus.Alive,
            _water.WaterDepthAt(surface),
            _water.WaterContaminationAt(surface),
            FlourishVisuals.BadwaterContaminationThreshold);

        // Visual: contaminated water kills the overlay outright.
        if (_decorated && !_dead && badwater) {
          Kill();
        }

        // Clock: only a dead, land host reclaims.
        if (!hostDead) {
          _wasDead = false;
          if (_maturity != 0f) _maturity = 0f;
          return;
        }
        if (!CanOvergrow) return;
        if (!_wasDead) {
          // First tick since the host died (or since load): start the clock
          // fresh so we don't back-date the time it spent alive.
          _wasDead = true;
          _lastCheckDay = _dayNightCycle.PartialDayNumber;
          _tickCounter = StaggerTicks(blockObject);
        }

        if (++_tickCounter < MaturityCheckIntervalTicks) return;
        _tickCounter = 0;
        var currentDay = _dayNightCycle.PartialDayNumber;
        var intervalDays = currentDay - _lastCheckDay;
        _lastCheckDay = currentDay;
        if (intervalDays <= 0f) return;

        var good = !badwater && _moisture.IsMoistAt(surface);
        _maturity += (good ? MaturityGainPerDay : -MaturityLossPerDay) * intervalDays;
        if (_maturity < 0f) _maturity = 0f;
      } catch (System.Exception ex) {
        LifecycleGuard.HandleErrorOnce(
            "KeystoneOvergrowth.Tick", "Per-entity tick errors", ex, ref _tickFailureLogged);
      }
    }

    /// <summary>Tear down the overlay when the host entity is deleted so
    /// decorations don't outlive the tree. (Cut-to-stump leaves the
    /// entity alive, so that path needs a CuttableCutEvent hook — a later
    /// slice; deletion cleanup is enough for now.)</summary>
    public void DeleteEntity() {
      DespawnAll();
    }

    #endregion

    #region Persistence

    /// <inheritdoc />
    public void Save(IEntitySaver entitySaver) {
      // Persist when there's anything non-default: the overgrowth visual, or
      // a dead tree's accrued reclamation maturity (which exists with no
      // visual). A pristine living/barren tree writes nothing.
      if (!_decorated && _maturity <= 0f) return;
      var saver = entitySaver.GetComponent(ComponentKey);
      saver.Set(DecoratedKey, _decorated);
      saver.Set(DecorationIdKey, _decorationId);
      saver.Set(MaturityKey, _maturity);
      saver.Set(DeadKey, _dead);
    }

    /// <inheritdoc />
    public void Load(IEntityLoader entityLoader) {
      if (!entityLoader.TryGetComponent(ComponentKey, out var loader)) return;
      // Legacy saves (before maturity was decoupled) had no DecoratedKey —
      // the presence of the block meant "decorated". Honour that fallback.
      _decorated = loader.Has(DecoratedKey) ? loader.Get(DecoratedKey) : true;
      if (loader.Has(DecorationIdKey)) {
        _decorationId = loader.Get(DecorationIdKey) ?? DefaultComposition;
      }
      if (loader.Has(MaturityKey)) {
        _maturity = loader.Get(MaturityKey);
      }
      if (loader.Has(DeadKey)) {
        _dead = loader.Get(DeadKey);
      }
    }

    #endregion

    #region Helpers

    /// <summary>Per-tile throttle offset so trees don't all fire their
    /// maturity update on the same tick. Used at start and on the
    /// alive→dead transition.</summary>
    private static int StaggerTicks(BlockObject blockObject) {
      return blockObject != null
          ? (blockObject.Coordinates.GetHashCode() & 0x7FFF) % MaturityCheckIntervalTicks
          : 0;
    }

    /// <summary>Spawn the decoration for <see cref="_decorationId"/> at
    /// the host's tile with a lifecycle controller (single alive/dying
    /// variant, ecology-driven via moisture — not tied to the host
    /// tree's life state). Guards against double-spawn.</summary>
    private void SpawnDecoration() {
      if (_spawned.Count > 0) return;
      var blockObject = GetComponent<BlockObject>();
      if (blockObject == null) return;

      var decoration = _decorations.Spawn(
          _decorationId, blockObject.Coordinates, new OvergrowthLifecycleController(() => _dead));
      if (decoration != null) {
        _spawned.Add(decoration);
      }
      KeystoneLog.Verbose(
          $"[Keystone] Overgrowth: overlay '{_decorationId}' at " +
          $"{blockObject.Coordinates} (live={_spawned.Count}).");
    }

    private void DespawnAll() {
      for (var i = 0; i < _spawned.Count; i++) {
        _decorations.Despawn(_spawned[i]);
      }
      _spawned.Clear();
    }

    #endregion

  }

}
