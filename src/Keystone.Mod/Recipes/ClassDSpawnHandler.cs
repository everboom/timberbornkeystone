using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Flora;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Flora;
using Keystone.Mod.Flourish;
using Keystone.Mod.Settings;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.Cutting;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.Growing;
using Timberborn.NaturalResourcesLifecycle;
using UnityEngine;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Rule handler for Class D recipes — vanilla flora donors (Pine,
  /// Maple, Birch, etc.) placed by biome/level dynamics. Same dispatch
  /// shape as Class A/B/C (filter → level activation → weighted pick).
  /// Two Class-D-specific behaviours:
  ///
  /// <list type="bullet">
  ///   <item>A <c>(tile, levelId)</c> memo records successful spawns
  ///         so a cut tree doesn't re-spawn from the same level.
  ///         Vanilla <c>ReproducibleSpec</c> handles regrowth instead;
  ///         Keystone only fires once per (tile, level).</item>
  ///   <item>Class B entities at the target tile yield to Class D
  ///         (succession): any Keystone Class B
  ///         (<c>KeystoneVariant.Class == "B"</c>) found at the spawn
  ///         tile is demolished immediately before the spawn (via
  ///         <see cref="EntityService.Delete"/>).</item>
  /// </list>
  ///
  /// <para><b>Tile-selection mode is now per-level, not per-class.</b>
  /// Class D levels typically declare <c>Mode: "Stochastic"</c> so
  /// trees trickle in over real time, but the choice lives in the
  /// level spec — the base class' <see cref="SpawnHandlerBase{T}.EvaluateLevel"/>
  /// honours <see cref="BiomeLevel.Mode"/> regardless of handler.</para>
  ///
  /// <para><b>Vanilla lifecycle takes over after spawn.</b> The
  /// blueprint is fully vanilla — carries <c>GrowableSpec</c>,
  /// <c>CuttableSpec</c>, <c>ReproducibleSpec</c>. No growth fast-
  /// forwarding here; spawns appear as seedlings and mature in real
  /// game-time.</para>
  ///
  /// <para><b>Save/load gap.</b> The memo is session-local. A "save →
  /// cut → reload" sequence currently re-enables Keystone-driven
  /// spawning at the cut tile (the memo entry is gone after reload).
  /// Persisting the memo via <c>KeystonePersistence</c> is future
  /// work.</para>
  /// </summary>
  public sealed class ClassDSpawnHandler : SpawnHandlerBase<ClassDRecipe> {

    #region Fields

    private readonly FlourishCatalog _catalog;
    private readonly IBlockService _blockService;
    private readonly ITerrainQuery _terrain;
    private readonly BlockObjectFactory _blockObjectFactory;
    private readonly BlueprintResolver _blueprints;
    private readonly EntityService _entityService;
    private readonly ICuttingMarkQuery _cuttingMarks;
    private readonly FloraCatalog _floraCatalog;
    private readonly KeystoneFloraSettings _settings;

    /// <summary>Reusable scratch buffer for Class B replacement at the
    /// spawn tile. Allocating once at the field level avoids per-spawn
    /// list churn on the hot path.</summary>
    private readonly List<BlockObject> _replacementScratch = new();

    /// <summary>(tile, levelId) pairs we've successfully spawned in
    /// this session. Set on successful spawn only; failed activation
    /// rolls re-roll next cycle.</summary>
    private readonly HashSet<(SurfaceCoord Surface, string LevelId)> _spawnedAtLevel = new();

    #endregion

    #region Construction

    public ClassDSpawnHandler(
        FlourishCatalog catalog,
        RecipeFilterRegistry filters,
        IPlantingMarkQuery marks,
        IBlockService blockService,
        ITerrainQuery terrain,
        BlockObjectFactory blockObjectFactory,
        BlueprintResolver blueprints,
        EntityService entityService,
        ICuttingMarkQuery cuttingMarks,
        FloraCatalog floraCatalog,
        KeystoneFloraSettings settings)
        : base(filters, marks) {
      _catalog = catalog;
      _blockService = blockService;
      _terrain = terrain;
      _blockObjectFactory = blockObjectFactory;
      _blueprints = blueprints;
      _entityService = entityService;
      _cuttingMarks = cuttingMarks;
      _floraCatalog = floraCatalog;
      _settings = settings;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads <see cref="ClassDRecipe.Category"/> from any recipe in the
    /// bucket. <c>FlourishCatalog</c> enforces single-category buckets
    /// at PostLoad, so <c>recipes[0].Category</c> is well-defined.
    /// </remarks>
    protected override float GetDensityMultiplier(IReadOnlyList<ClassDRecipe> recipes)
        => _settings.MultiplierFor(recipes[0].Category);

    #endregion

    #region SpawnHandlerBase

    /// <inheritdoc />
    protected override IReadOnlyList<ClassDRecipe> GetAllRecipes() => _catalog.AllClassD;

    /// <inheritdoc />
    protected override IReadOnlyList<ClassDRecipe> GetRecipes(BiomeKind biome, string levelId)
        => _catalog.ClassDFor(biome, levelId);

    /// <inheritdoc />
    protected override string GetFilter(ClassDRecipe recipe) => recipe.Filter;

    /// <inheritdoc />
    protected override float GetWeight(ClassDRecipe recipe) => recipe.Weight;

    /// <inheritdoc />
    protected override (BiomeKind Biome, string LevelId) GetBucketKey(ClassDRecipe recipe) =>
        (recipe.Biome, recipe.LevelId);

    /// <inheritdoc />
    protected override void EvaluateLevel(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress,
        IReadOnlyList<ClassDRecipe> recipes) {
      // Already-spawned tiles drop out before the activation roll so
      // we don't burn RNG / hash work on tiles we'd never spawn into
      // anyway. The memo is mode-agnostic — it guards re-spawn after
      // a player cut under stochastic mode, and avoids the per-cycle
      // re-attempt under deterministic mode too.
      if (_spawnedAtLevel.Contains((surface, level.LevelId))) return;
      // Same-chunk seedling gate: any immature vanilla flora in this
      // surface's 4x4 ecology chunk blocks new Class D spawns there.
      // Population grows in waves -- one seedling at a time per chunk,
      // matures, then the next can sprout. Applies across levels (a
      // Wetland-L3 seedling blocks L4 spawns and vice versa) and counts
      // player-planted seedlings the same as Keystone-spawned ones.
      if (HasImmatureFloraInChunk(surface)) return;
      base.EvaluateLevel(surface, biome, level, progress, recipes);
    }

    /// <inheritdoc />
    protected override void OnRecipeChosen(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, ClassDRecipe recipe) {
      // Belt-and-suspenders on the dispatcher's marked-tile skip: never
      // place a BlockObject on a tile the player has designated for
      // planting, even if a future call path bypasses ChunkRulesApplier.
      if (IsMarked(surface)) return;
      // Cutting-mark filter (see TrySpawnClassD docstring for the policy).
      if (IsBlockedByCuttingMark(surface.X, surface.Y, surface.Z, recipe.BlueprintName)) return;
      var tile = new Vector3Int(surface.X, surface.Y, surface.Z);
      if (TrySpawnClassD(recipe.BlueprintName, tile, recipe.Height) != null) {
        _spawnedAtLevel.Add((surface, level.LevelId));
      }
    }

    #endregion

    #region Public spawn helper (used by dev placement tools)

    /// <summary>Replacement-aware Class D spawn helper. Performs the
    /// occupant check (rejects vanilla buildings, vanilla flora,
    /// Class C, other Class D), demolishes any Class B occupants
    /// (succession), resolves the blueprint, and spawns. Returns the
    /// new <see cref="BlockObject"/> on success, or <c>null</c> if
    /// any step failed.
    /// <para><b>Public so dev tools can reuse this path.</b>
    /// <c>VanillaFloraPlacementTool</c> calls this directly so a
    /// force-placement exercises the same replacement logic the
    /// handler does. The session memo is *not* updated by dev-tool
    /// calls — force-placements are transient by intent.</para>
    /// <para><paramref name="height"/> is the spawned tree's vertical
    /// extent in voxels; the placement voxel and <paramref name="height"/>
    /// voxels above it must be free. Dev-tool callers pass the recipe's
    /// resolved Height (defaulting to 2 for Class D).</para>
    /// <para><b>Cutting-mark filter.</b> When the target tile is in the
    /// player's tree-cutting designation area, spawning is blocked for
    /// every Class D species *except* trees (<see cref="FloraKind.Tree"/>),
    /// and even trees only spawn when an adult cuttable tree already
    /// exists in the 8-tile Moore neighbourhood. The clearing-vs-
    /// management trade-off resolves itself: a single tree marked inside
    /// a dense forest still regrows because neighbours are present; a
    /// clear-cut swath progressively loses neighbours as cutting
    /// proceeds, eventually staying clear.</para></summary>
    public BlockObject? TrySpawnClassD(string blueprintName, Vector3Int tile, int height) {
      // Honour player planting marks even on dev-tool call paths that
      // skip the rule dispatcher. The placement tools that consume
      // this method already check marks ahead of the call, but the
      // guard here makes the invariant local to the spawn method --
      // any future caller gets it for free.
      if (IsMarked(tile)) return null;
      if (IsBlockedByCuttingMark(tile.x, tile.y, tile.z, blueprintName)) return null;
      if (!TryClearForReplacement(tile, _replacementScratch)) return null;
      var blueprint = _blueprints.Resolve(blueprintName);
      if (blueprint == null) {
        _replacementScratch.Clear();
        return null;
      }
      // Demolish replaceable occupants before clearance check — multi-
      // voxel stumps would otherwise block IsAboveClear.
      for (var i = 0; i < _replacementScratch.Count; i++) {
        _entityService.Delete(_replacementScratch[i]);
      }
      _replacementScratch.Clear();
      if (!VerticalClearance.IsAboveClear(_blockService, _terrain, tile, height)) return null;
      return TrySpawn(blueprint, tile);
    }

    #endregion

    #region Helpers

    /// <summary>True if any <see cref="BlockObject"/> in the 4x4
    /// ecology chunk containing <paramref name="surface"/> carries a
    /// still-growing <see cref="Growable"/>. Iterates the chunk's 16
    /// columns, walks every surveyed surface height per column, and
    /// short-circuits on the first immature hit.
    /// <para>Chunk origin uses Euclidean division so negative tile
    /// coordinates land in the correct chunk (vanilla
    /// <see cref="RegionEcologyField.ChunkSize"/> alignment).</para></summary>
    private bool HasImmatureFloraInChunk(SurfaceCoord surface) {
      const int chunkSize = RegionEcologyField.ChunkSize;
      var chunkOriginX = FloorDiv(surface.X, chunkSize) * chunkSize;
      var chunkOriginY = FloorDiv(surface.Y, chunkSize) * chunkSize;
      for (var dx = 0; dx < chunkSize; dx++) {
        for (var dy = 0; dy < chunkSize; dy++) {
          var x = chunkOriginX + dx;
          var y = chunkOriginY + dy;
          var col = new TileCoord(x, y);
          if (!_terrain.Contains(col)) continue;
          var heights = _terrain.SurfaceHeightsAt(col);
          for (var i = 0; i < heights.Count; i++) {
            var voxel = new Vector3Int(x, y, heights[i]);
            // Per-BO isolation: one malformed third-party BO in this
            // voxel (Growable accessor on a divergent shape, etc.)
            // shouldn't skip the whole level evaluation. Treat per-BO
            // failures as "not immature here" and continue.
            foreach (var bo in _blockService.GetObjectsAt(voxel)) {
              if (bo == null) continue;
              try {
                var growable = bo.GetComponent<Growable>();
                if (growable != null && GrowableTimeTriggerAccessor.IsImmature(growable)) {
                  return true;
                }
              } catch (System.Exception ex) {
                KeystoneLog.Warn(
                    $"[Keystone] ClassDSpawnHandler.HasImmatureFloraInChunk: BO at " +
                    $"{voxel} threw {ex.GetType().Name}: {ex.Message}. Treating as non-immature.");
                Diagnostics.KeystoneIntegrationHealth.TryRecord(
                    "Per-tile errors",
                    $"ClassDSpawnHandler immaturity probe: {ex.GetType().Name}");
              }
            }
          }
        }
      }
      return false;
    }

    /// <summary>Floor-division used by the chunk-origin computation
    /// so negative tile coordinates still resolve to the correct
    /// chunk. <c>x / chunkSize</c> alone truncates toward zero, which
    /// would put e.g. <c>x = -1</c> in chunk <c>0</c> instead of
    /// <c>-1</c>.</summary>
    private static int FloorDiv(int x, int divisor) {
      var q = x / divisor;
      if ((x % divisor != 0) && ((x < 0) != (divisor < 0))) q--;
      return q;
    }

    private bool TryClearForReplacement(Vector3Int tile, List<BlockObject> toRemove) {
      toRemove.Clear();
      foreach (var bo in _blockService.GetObjectsAt(tile)) {
        if (bo == null) continue;
        if (!IsReplaceable(bo)) return false;
        toRemove.Add(bo);
      }
      return true;
    }

    /// <summary>True if <paramref name="bo"/> can be cleared to make
    /// way for a Class D spawn. Three cases: live Class B Keystone
    /// entities (succession — flourishes step aside for vanilla flora),
    /// dead Keystone flourishes of any class (biome recovery), and
    /// vanilla tree stumps whose yield has been fully harvested.</summary>
    private static bool IsReplaceable(BlockObject bo) {
      if (KeystoneFlourish.IsDeadFlourish(bo)) return true;
      if (IsHarvestedStump(bo)) return true;
      if (!bo.HasComponent<KeystoneVariant>()) return false;
      return bo.GetComponent<KeystoneVariant>().Class == "B";
    }

    private static bool IsHarvestedStump(BlockObject bo) {
      var living = bo.GetComponent<LivingNaturalResource>();
      if (living == null || !living.IsDead) return false;
      var cuttable = bo.GetComponent<Cuttable>();
      return cuttable != null && cuttable.Yielder.IsYieldRemoved;
    }

    /// <summary>Cutting-mark filter for Class D spawns at
    /// <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>.
    /// Returns <c>true</c> when the spawn should be blocked.
    ///
    /// <para>Rules:
    /// <list type="number">
    ///   <item>Tile not marked for cutting → not blocked.</item>
    ///   <item>Tile marked + recipe is not a <see cref="FloraKind.Tree"/>
    ///         (bushes, crops, ground cover) → blocked. The player has
    ///         signalled they want this tile cleared; planting a bush
    ///         there doesn't get auto-cut, and would just clutter the
    ///         area as the player tries to harvest.</item>
    ///   <item>Tile marked + recipe is a tree + no adjacent adult
    ///         cuttable tree → blocked. Without a nearby donor we'd
    ///         start a fresh forest on a tile the player is trying to
    ///         clear; the "needs a neighbour" gate makes clearings
    ///         self-stabilise as cutting proceeds.</item>
    ///   <item>Tile marked + tree recipe + adjacent adult cuttable tree
    ///         exists → not blocked. The new tree will be auto-added to
    ///         the cutting area when it grows up
    ///         (<see cref="TreeCuttingArea.OnEntityInitialized"/>), so
    ///         selective harvesting inside a Keystone forest keeps
    ///         working.</item>
    /// </list></para>
    ///
    /// <para>Unknown blueprints (not in <see cref="FloraCatalog"/>) are
    /// treated as non-tree → blocked. Conservative default for modded
    /// content Keystone hasn't catalogued.</para></summary>
    private bool IsBlockedByCuttingMark(int x, int y, int z, string blueprintName) {
      if (!_cuttingMarks.IsMarkedForCutting(x, y, z)) return false;
      var entry = _floraCatalog.Get(blueprintName);
      if (entry == null || entry.Kind != FloraKind.Tree) return true;
      return !HasAdjacentAdultCuttableTree(x, y);
    }

    /// <summary>True if any of the 8 Moore-neighbour columns of
    /// <c>(<paramref name="x"/>, <paramref name="y"/>)</c> hosts an
    /// adult cuttable tree at any surveyed surface height. "Adult" =
    /// <see cref="Growable.IsGrown"/> (timer finished). "Cuttable" =
    /// has a <see cref="Cuttable"/> component (excludes Tree variants
    /// that for some reason aren't cuttable; in vanilla all are).
    /// Already-dead trees are excluded so a fresh stump doesn't keep
    /// "neighbour" status after its tree is cut.</summary>
    private bool HasAdjacentAdultCuttableTree(int x, int y) {
      for (var dx = -1; dx <= 1; dx++) {
        for (var dy = -1; dy <= 1; dy++) {
          if (dx == 0 && dy == 0) continue;
          if (HasAdultCuttableTreeInColumn(x + dx, y + dy)) return true;
        }
      }
      return false;
    }

    /// <summary>True if column <c>(<paramref name="x"/>,
    /// <paramref name="y"/>)</c> hosts an adult cuttable tree at any
    /// surveyed surface height. Walks the column's surface list (handles
    /// terraced terrain / overhangs by checking every surface), probing
    /// at each surface coordinate — <see cref="ITerrainQuery.SurfaceHeightsAt"/>
    /// returns the first air voxel above each piece of terrain, which is
    /// exactly where tree BlockObjects sit and where cutting marks are
    /// stored.</summary>
    private bool HasAdultCuttableTreeInColumn(int x, int y) {
      var col = new TileCoord(x, y);
      if (!_terrain.Contains(col)) return false;
      var heights = _terrain.SurfaceHeightsAt(col);
      for (var i = 0; i < heights.Count; i++) {
        var coord = new Vector3Int(x, y, heights[i]);
        var tree = _blockService.GetFirstObjectWithComponentAt<TreeComponent>(coord);
        if (tree == null) continue;
        if (tree.GetComponent<Cuttable>() == null) continue;
        var growable = tree.GetComponent<Growable>();
        if (growable == null || !growable.IsGrown) continue;
        var living = tree.GetComponent<LivingNaturalResource>();
        if (living != null && living.IsDead) continue;
        return true;
      }
      return false;
    }

    /// <summary>Returns the spawned <see cref="BlockObject"/> on
    /// success, <c>null</c> on failure (warns inside). Class D rides
    /// the vanilla pipeline — no <see cref="KeystoneVariant"/> stamp.</summary>
    private BlockObject? TrySpawn(Blueprint blueprint, Vector3Int tile) {
      try {
        var spec = blueprint.GetSpec<BlockObjectSpec>();
        var placement = new Placement(tile, Orientation.Cw0, FlipMode.Unflipped);
        var entity = _blockObjectFactory.CreateFinished(spec, placement);
        if (entity == null) {
          // Routine: placement service rejected the tile (occupied,
          // water sim, etc.). Verbose-only; the spawn budget keeps
          // looking for a different tile.
          KeystoneLog.Verbose(
              $"[Keystone] ClassDSpawnHandler: CreateFinished returned " +
              $"null for '{blueprint.Name}' at {tile}.");
        }
        return entity;
      } catch (Exception ex) {
        // Same as the null path -- a thrown InvalidOperationException
        // ("Cannot place BlockObject ... at ...") is the normal signal
        // that the tile isn't usable, not a real fault.
        KeystoneLog.Verbose(
            $"[Keystone] ClassDSpawnHandler: spawn '{blueprint.Name}' at " +
            $"{tile} threw: {ex.GetType().Name}: {ex.Message}");
        return null;
      }
    }

    #endregion

  }

}
