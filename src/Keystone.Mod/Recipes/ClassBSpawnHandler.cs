using System;
using System.Collections.Generic;
using System.Diagnostics;
using Keystone.Core.Biomes;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Flourish;
using Keystone.Mod.Settings;
using Timberborn.BlockSystem;
using Timberborn.Cutting;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using UnityEngine;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Rule handler for Class B recipes — one-shot spawns of persistent,
  /// non-interactive entities. Uses the shared dispatch in
  /// <see cref="SpawnHandlerBase{TRecipe}"/>; on each picked recipe,
  /// instantiates the blueprint via
  /// <see cref="BlockObjectFactory.CreateFinished"/> and stamps
  /// <c>KeystoneVariant.Class = "B"</c> so Harmony selection-
  /// suppression patches keep the player from selecting / demolishing
  /// the entity.
  ///
  /// <para><b>One-shot lifecycle.</b> Class B spawns are persistent;
  /// the entity is in Timberborn's hands once placed. The handler
  /// only ever spawns, never despawns. No attempt-memo — the runtime
  /// <see cref="IBlockService"/> occupancy check catches "we already
  /// spawned here" across cycles and save/load.</para>
  /// </summary>
  public sealed class ClassBSpawnHandler : SpawnHandlerBase<ClassBRecipe> {

    private readonly FlourishCatalog _catalog;
    private readonly IBlockService _blockService;
    private readonly ITerrainQuery _terrain;
    private readonly BlockObjectFactory _blockObjectFactory;
    private readonly BlueprintResolver _blueprints;
    private readonly EntityService _entityService;
    private readonly KeystoneFloraSettings _settings;

    /// <summary>Reusable scratch buffer for collecting replaceable
    /// occupants (dead flourishes) at the spawn tile.</summary>
    private readonly List<BlockObject> _replacementScratch = new();

    public ClassBSpawnHandler(
        FlourishCatalog catalog,
        RecipeFilterRegistry filters,
        IPlantingMarkQuery marks,
        IBlockService blockService,
        ITerrainQuery terrain,
        BlockObjectFactory blockObjectFactory,
        BlueprintResolver blueprints,
        EntityService entityService,
        KeystoneFloraSettings settings,
        PerfTracker perf)
        : base(filters, marks) {
      _catalog = catalog;
      _blockService = blockService;
      _terrain = terrain;
      _blockObjectFactory = blockObjectFactory;
      _blueprints = blueprints;
      _entityService = entityService;
      _settings = settings;
      _perf = perf;
    }

    private readonly PerfTracker _perf;

    // Aggregated per-tick stage timers for the OnUnit-level breakdown.
    // Reset/flush nominally happens at ChunkRulesApplier tick boundaries,
    // but this handler doesn't see those — we Record one sample per
    // OnUnit call, which lands per-call (not per-tick) data in the perf
    // window. Per-call distributions are exactly what we need to see
    // whether the average cost is dominated by gate rejects, no-spawn
    // evaluations, or rare-but-expensive spawns.
    private const string ClassBScope =
        "ChunkRulesApplier.Tick.HandlerDispatch.ClassBSpawnHandler";
    private const string GateScope = ClassBScope + ".Gate";
    private const string GatePassedScope = ClassBScope + ".GatePassed";
    private const string GateRejectsScope = ClassBScope + ".Rejects.Units";
    private const string GatePassedCountScope = ClassBScope + ".PassedCalls.Units";

    // Finer-grained breakdown inside the post-gate path. Pick covers
    // the eligible-pool build + activation hash + weighted recipe pick;
    // Chosen covers the post-pick spawn work (occupancy re-check,
    // replaceable demolish, blueprint resolve, vertical clearance,
    // entity creation). Spawn counts how often the pick actually
    // produces a recipe (per-call avg of 1 means "every passed-gate
    // call resulted in a spawn attempt"); ActivationFail counts the
    // opposite.
    private const string PickScope = ClassBScope + ".Pick";
    private const string ChosenScope = ClassBScope + ".Chosen";
    private const string SpawnsScope = ClassBScope + ".Spawns.Units";
    private const string ActivationFailScope = ClassBScope + ".ActivationFail.Units";

    // Sub-scopes inside Chosen so we can see whether the cost is the
    // pre-spawn checks (occupancy re-walk, blueprint resolve, vertical
    // clearance) or the entity-creation call itself.
    private const string ChosenPrepareScope = ChosenScope + ".Prepare";
    private const string ChosenClearanceScope = ChosenScope + ".Clearance";
    private const string ChosenSpawnScope = ChosenScope + ".Spawn";
    private const string ChosenRejectedScope = ChosenScope + ".Rejected.Units";
    private const string ChosenSpawnedScope = ChosenScope + ".Spawned.Units";

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static double TicksToMs(long ticks) =>
        (double)ticks * 1000.0 / Stopwatch.Frequency;

    // Per-tick counter accumulators. Incremented per event during
    // OnUnit calls within the tick; flushed once in OnTickEnd. The
    // perf window's counter avg then reads as "events per tick",
    // not the meaningless average of per-event "1" records.
    private int _tickRejects;
    private int _tickPassedCalls;
    private int _tickSpawns;
    private int _tickActivationFails;
    private int _tickChosenRejected;
    private int _tickChosenSpawned;

    /// <inheritdoc />
    protected override float GetDensityMultiplier(IReadOnlyList<ClassBRecipe> recipes)
        => _settings.ClassBDensityPercent.Value / 100f;

    /// <inheritdoc />
    protected override IReadOnlyList<ClassBRecipe> GetAllRecipes() => _catalog.AllClassB;

    /// <inheritdoc />
    protected override IReadOnlyList<ClassBRecipe> GetRecipes(BiomeKind biome, string levelId)
        => _catalog.ClassBFor(biome, levelId);

    /// <inheritdoc />
    protected override string GetFilter(ClassBRecipe recipe) => recipe.Filter;

    /// <inheritdoc />
    protected override float GetWeight(ClassBRecipe recipe) => recipe.Weight;

    /// <inheritdoc />
    protected override (BiomeKind Biome, string LevelId) GetBucketKey(ClassBRecipe recipe) =>
        (recipe.Biome, recipe.LevelId);

    /// <inheritdoc />
    protected override ClassBRecipe? TryDeterministicPick(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress,
        IReadOnlyList<ClassBRecipe> recipes, float densityMultiplier) {
      var t = Stopwatch.GetTimestamp();
      var recipe = base.TryDeterministicPick(
          surface, biome, level, progress, recipes, densityMultiplier);
      _perf.Record(PickScope, TicksToMs(Stopwatch.GetTimestamp() - t));
      if (recipe != null) _tickSpawns++; else _tickActivationFails++;
      return recipe;
    }

    /// <inheritdoc />
    protected override ClassBRecipe? TryStochasticPick(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress,
        IReadOnlyList<ClassBRecipe> recipes, System.Random rng, float densityMultiplier) {
      var t = Stopwatch.GetTimestamp();
      var recipe = base.TryStochasticPick(
          surface, biome, level, progress, recipes, rng, densityMultiplier);
      _perf.Record(PickScope, TicksToMs(Stopwatch.GetTimestamp() - t));
      if (recipe != null) _tickSpawns++; else _tickActivationFails++;
      return recipe;
    }

    /// <inheritdoc />
    public override void OnTickEnd() {
      _perf.RecordCount(GateRejectsScope, _tickRejects);
      _perf.RecordCount(GatePassedCountScope, _tickPassedCalls);
      _perf.RecordCount(SpawnsScope, _tickSpawns);
      _perf.RecordCount(ActivationFailScope, _tickActivationFails);
      _perf.RecordCount(ChosenRejectedScope, _tickChosenRejected);
      _perf.RecordCount(ChosenSpawnedScope, _tickChosenSpawned);
      _tickRejects = 0;
      _tickPassedCalls = 0;
      _tickSpawns = 0;
      _tickActivationFails = 0;
      _tickChosenRejected = 0;
      _tickChosenSpawned = 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Class B's bottleneck on established maps is the per-surface
    /// dispatch evaluating the eligible-pool build + activation hash
    /// + recipe pick for tiles that are already saturated — the
    /// occupancy check lives in <see cref="OnRecipeChosen"/>, which
    /// only fires after the pick has succeeded, so the per-call cost
    /// stays high regardless of whether anything will actually spawn.
    /// <para>This override moves the occupancy gate to the top of
    /// the call: tile not empty-or-replaceable → skip immediately,
    /// no pool build, no hash, no pick. On a saturated map this
    /// short-circuits the vast majority of <c>OnUnit</c> calls. The
    /// downstream <see cref="OnRecipeChosen"/> check stays as a
    /// belt-and-suspenders guard.</para></remarks>
    protected override void EvaluateLevel(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress,
        IReadOnlyList<ClassBRecipe> recipes) {
      var tile = new Vector3Int(surface.X, surface.Y, surface.Z);

      // Stage 1: occupancy gate. Time it explicitly so we can see in
      // the perf window whether the gate cost itself is what's eating
      // ms (suggests BlockService.GetObjectsAt is slow), vs the
      // post-gate path (suggests BuildEligiblePool / spawn is slow).
      var tGate = Stopwatch.GetTimestamp();
      var passed = IsTileEmptyOrReplaceable(tile);
      _perf.Record(GateScope, TicksToMs(Stopwatch.GetTimestamp() - tGate));

      if (!passed) {
        _tickRejects++;
        return;
      }

      // Stage 2: the original spawn evaluation. Timed separately so
      // the breakdown lets us see post-gate cost in isolation.
      _tickPassedCalls++;
      var tPassed = Stopwatch.GetTimestamp();
      base.EvaluateLevel(surface, biome, level, progress, recipes);
      _perf.Record(GatePassedScope, TicksToMs(Stopwatch.GetTimestamp() - tPassed));
    }

    /// <summary>Read-only equivalent of <see cref="TryClearForReplacement"/>:
    /// returns true if the tile is empty or occupied only by entities
    /// we'd be willing to demolish (dead flourishes, harvested stumps).
    /// Does not populate the replacement scratch — the downstream
    /// <see cref="OnRecipeChosen"/> path re-walks the tile and fills
    /// the scratch when a recipe is actually picked.</summary>
    private bool IsTileEmptyOrReplaceable(Vector3Int tile) {
      foreach (var bo in _blockService.GetObjectsAt(tile)) {
        if (bo == null) continue;
        if (!KeystoneFlourish.IsDeadFlourish(bo)
            && !IsHarvestedStump(bo)) return false;
      }
      return true;
    }

    /// <inheritdoc />
    protected override void OnRecipeChosen(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, ClassBRecipe recipe) {
      var t = Stopwatch.GetTimestamp();
      OnRecipeChosenInner(surface, biome, level, recipe);
      _perf.Record(ChosenScope, TicksToMs(Stopwatch.GetTimestamp() - t));
    }

    private void OnRecipeChosenInner(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, ClassBRecipe recipe) {
      // Phase 1: prepare — marked check, occupancy re-walk, blueprint
      // resolve, demolish replaceables. Bails early on any failure.
      var tPrepare = Stopwatch.GetTimestamp();
      if (IsMarked(surface)) {
        _perf.Record(ChosenPrepareScope, TicksToMs(Stopwatch.GetTimestamp() - tPrepare));
        _tickChosenRejected++;
        return;
      }
      var tile = new Vector3Int(surface.X, surface.Y, surface.Z);
      if (!TryClearForReplacement(tile, _replacementScratch)) {
        _perf.Record(ChosenPrepareScope, TicksToMs(Stopwatch.GetTimestamp() - tPrepare));
        _tickChosenRejected++;
        return;
      }
      var blueprint = _blueprints.Resolve(recipe.BlueprintName);
      if (blueprint == null) {
        _replacementScratch.Clear();
        _perf.Record(ChosenPrepareScope, TicksToMs(Stopwatch.GetTimestamp() - tPrepare));
        _tickChosenRejected++;
        return;
      }
      // Demolish replaceable occupants (dead flourishes, harvested
      // stumps) BEFORE the clearance check — multi-voxel stumps would
      // otherwise block IsAboveClear even though they're about to go.
      for (var i = 0; i < _replacementScratch.Count; i++) {
        _entityService.Delete(_replacementScratch[i]);
      }
      _replacementScratch.Clear();
      _perf.Record(ChosenPrepareScope, TicksToMs(Stopwatch.GetTimestamp() - tPrepare));

      // Phase 2: clearance check — vertical clearance above the tile.
      var tClearance = Stopwatch.GetTimestamp();
      var hasClearance =
          VerticalClearance.IsAboveClear(_blockService, _terrain, tile, recipe.Height);
      _perf.Record(ChosenClearanceScope, TicksToMs(Stopwatch.GetTimestamp() - tClearance));
      if (!hasClearance) {
        _tickChosenRejected++;
        return;
      }

      // Phase 3: actual entity creation.
      var tSpawn = Stopwatch.GetTimestamp();
      TrySpawn(blueprint, tile);
      _perf.Record(ChosenSpawnScope, TicksToMs(Stopwatch.GetTimestamp() - tSpawn));
      _tickChosenSpawned++;
    }

    /// <summary>True if the tile is empty OR occupied only by
    /// replaceable entities; fills <paramref name="toRemove"/> with
    /// those entities so the caller can demolish them before spawning.
    /// Replaceable: dead Keystone flourishes, and vanilla tree stumps
    /// whose yield has been fully harvested.</summary>
    private bool TryClearForReplacement(Vector3Int tile, List<BlockObject> toRemove) {
      toRemove.Clear();
      foreach (var bo in _blockService.GetObjectsAt(tile)) {
        if (bo == null) continue;
        if (!KeystoneFlourish.IsDeadFlourish(bo)
            && !IsHarvestedStump(bo)) return false;
        toRemove.Add(bo);
      }
      return true;
    }

    private static bool IsHarvestedStump(BlockObject bo) {
      var living = bo.GetComponent<LivingNaturalResource>();
      if (living == null || !living.IsDead) return false;
      var cuttable = bo.GetComponent<Cuttable>();
      return cuttable != null && cuttable.Yielder.IsYieldRemoved;
    }

    private void TrySpawn(Blueprint blueprint, Vector3Int tile) {
      try {
        var spec = blueprint.GetSpec<BlockObjectSpec>();
        var placement = new Placement(tile, Orientation.Cw0, FlipMode.Unflipped);
        var entity = _blockObjectFactory.CreateFinished(
            new EntitySetup.Builder(spec.Blueprint), placement);
        if (entity == null) {
          // Routine: placement service rejected the tile. The spawn
          // budget will look elsewhere; not a real fault.
          KeystoneLog.Verbose(
              $"[Keystone] ClassBSpawnHandler: CreateFinished returned " +
              $"null for '{blueprint.Name}' at {tile}.");
          return;
        }
        var variant = entity.GetComponent<KeystoneVariant>();
        if (variant != null) {
          variant.SetClass("B");
        } else {
          // Config bug: blueprint forgot KeystoneVariantSpec. Selection
          // suppression won't apply, so the player can click and demolish
          // this entity. Warn so a missing spec surfaces in the player log
          // rather than only in dev runs.
          KeystoneLog.Warn(
              $"[Keystone] ClassBSpawnHandler: blueprint '{blueprint.Name}' " +
              "has no KeystoneVariant component; selection suppression won't apply. " +
              "Add KeystoneVariantSpec to the blueprint.");
        }
      } catch (Exception ex) {
        // Same as null path -- placement rejected the tile.
        KeystoneLog.Verbose(
            $"[Keystone] ClassBSpawnHandler: spawn '{blueprint.Name}' at " +
            $"{tile} threw: {ex.GetType().Name}: {ex.Message}");
      }
    }

  }

}
