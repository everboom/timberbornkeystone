using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Flourish;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using UnityEngine;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Rule handler for Class C recipes — the "selectable + demolishable"
  /// tier between inert Class B flourishes and full Class D vanilla
  /// flora. Differs from <see cref="ClassBSpawnHandler"/> only in the
  /// per-entity <c>KeystoneVariant.Class</c> stamp; the runtime tile-
  /// occupancy check handles re-evaluation after the player demolishes
  /// ("weeds the player can pull out, but they grow back if you don't
  /// address what's drawing them").
  /// </summary>
  public sealed class ClassCSpawnHandler : SpawnHandlerBase<ClassCRecipe> {

    private readonly FlourishCatalog _catalog;
    private readonly IBlockService _blockService;
    private readonly ITerrainQuery _terrain;
    private readonly BlockObjectFactory _blockObjectFactory;
    private readonly BlueprintResolver _blueprints;
    private readonly EntityService _entityService;

    /// <summary>Reusable scratch buffer for collecting replaceable
    /// occupants (dead flourishes) at the spawn tile.</summary>
    private readonly List<BlockObject> _replacementScratch = new();

    public ClassCSpawnHandler(
        FlourishCatalog catalog,
        RecipeFilterRegistry filters,
        IPlantingMarkQuery marks,
        IBlockService blockService,
        ITerrainQuery terrain,
        BlockObjectFactory blockObjectFactory,
        BlueprintResolver blueprints,
        EntityService entityService)
        : base(filters, marks) {
      _catalog = catalog;
      _blockService = blockService;
      _terrain = terrain;
      _blockObjectFactory = blockObjectFactory;
      _blueprints = blueprints;
      _entityService = entityService;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<ClassCRecipe> GetAllRecipes() => _catalog.AllClassC;

    /// <inheritdoc />
    protected override IReadOnlyList<ClassCRecipe> GetRecipes(BiomeKind biome, string levelId)
        => _catalog.ClassCFor(biome, levelId);

    /// <inheritdoc />
    protected override string GetFilter(ClassCRecipe recipe) => recipe.Filter;

    /// <inheritdoc />
    protected override float GetWeight(ClassCRecipe recipe) => recipe.Weight;

    /// <inheritdoc />
    protected override (BiomeKind Biome, string LevelId) GetBucketKey(ClassCRecipe recipe) =>
        (recipe.Biome, recipe.LevelId);

    /// <inheritdoc />
    protected override void OnRecipeChosen(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, ClassCRecipe recipe) {
      // Belt-and-suspenders on the dispatcher's marked-tile skip: never
      // place a BlockObject on a tile the player has designated for
      // planting, even if a future call path bypasses ChunkRulesApplier.
      if (IsMarked(surface)) return;
      var tile = new Vector3Int(surface.X, surface.Y, surface.Z);
      if (!VerticalClearance.IsAboveClear(_blockService, _terrain, tile, recipe.Height)) return;
      if (!TryClearForReplacement(tile, _replacementScratch)) return;
      var blueprint = _blueprints.Resolve(recipe.BlueprintName);
      if (blueprint == null) {
        _replacementScratch.Clear();
        return;
      }
      for (var i = 0; i < _replacementScratch.Count; i++) {
        _entityService.Delete(_replacementScratch[i]);
      }
      _replacementScratch.Clear();
      TrySpawn(blueprint, tile);
    }

    /// <summary>True if the tile is empty OR occupied only by dead
    /// Keystone flourishes (which yield to a new live spawn); fills
    /// <paramref name="toRemove"/> with those entities so the caller
    /// can demolish them before spawning. Any other occupant returns
    /// false and the spawn is aborted.</summary>
    private bool TryClearForReplacement(Vector3Int tile, List<BlockObject> toRemove) {
      toRemove.Clear();
      foreach (var bo in _blockService.GetObjectsAt(tile)) {
        if (bo == null) continue;
        if (!KeystoneFlourish.IsDeadFlourish(bo)) return false;
        toRemove.Add(bo);
      }
      return true;
    }

    private void TrySpawn(Blueprint blueprint, Vector3Int tile) {
      try {
        var spec = blueprint.GetSpec<BlockObjectSpec>();
        var placement = new Placement(tile, Orientation.Cw0, FlipMode.Unflipped);
        var entity = _blockObjectFactory.CreateFinished(spec, placement);
        if (entity == null) {
          // Routine: placement service rejected the tile.
          KeystoneLog.Verbose(
              $"[Keystone] ClassCSpawnHandler: CreateFinished returned " +
              $"null for '{blueprint.Name}' at {tile}.");
          return;
        }
        var variant = entity.GetComponent<KeystoneVariant>();
        if (variant != null) {
          variant.SetClass("C");
        } else {
          // Config bug: blueprint forgot KeystoneVariantSpec. Selection
          // suppression won't apply, so the player can click and demolish
          // this entity. Mirrors the Class B handler's warn.
          KeystoneLog.Warn(
              $"[Keystone] ClassCSpawnHandler: blueprint '{blueprint.Name}' " +
              "has no KeystoneVariant component; selection suppression won't apply. " +
              "Add KeystoneVariantSpec to the blueprint.");
        }
      } catch (Exception ex) {
        // Same as null path -- placement rejected the tile.
        KeystoneLog.Verbose(
            $"[Keystone] ClassCSpawnHandler: spawn '{blueprint.Name}' at " +
            $"{tile} threw: {ex.GetType().Name}: {ex.Message}");
      }
    }

  }

}
