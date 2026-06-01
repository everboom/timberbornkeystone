using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Flora;
using Keystone.Mod.Recipes;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.Cutting;
using Timberborn.EntitySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.CursorToolSystem;
using Timberborn.Growing;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;
using Random = System.Random;

namespace Keystone.Mod.Flourish {

  /// <summary>
  /// Dev-mode tool: when active, left-click on a tile to force-place
  /// a Class B flourish appropriate to the tile's biome. Bypasses the
  /// per-tile activation hash and the level-progress gate that the
  /// <see cref="Keystone.Mod.Recipes.ClassBSpawnHandler"/> normally uses, so the
  /// flourish lands even on tiles that wouldn't naturally spawn one
  /// (no Investment yet, hash above threshold, or both). Useful for
  /// staging content on a fresh map without waiting for ecology
  /// dynamics to catch up.
  ///
  /// <para><b>Biome resolution.</b> Walks every <see cref="BiomeKind"/>
  /// that has at least one Class B recipe registered, samples the
  /// chunk's short-term Score at the cursor's tile, and picks the
  /// biome with the highest Score. (Score, not Investment, so the
  /// tool reflects current conditions on freshly-built terrain
  /// before any Investment has accrued.) A random recipe from that
  /// biome's recipe list is then spawned.</para>
  ///
  /// <para><b>Refusal cases.</b> No Score &gt; 0 for any biome with a
  /// recipe, no region containing the cursor, or the picked recipe's
  /// blueprint not loaded -- the click is consumed but no entity
  /// spawns; a warning explains why.</para>
  ///
  /// <para>Surfaced in the bottom bar via
  /// <see cref="Keystone.Mod.Toolbar.KeystoneToolGroup"/> as the
  /// Class-B button (eco-responsive flourish).</para>
  /// </summary>
  public sealed class FlourishPlacementTool : ITool, IInputProcessor, IToolDescriptor {

    private const string DisplayNameKey = "Tool.Keystone.Flourish.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.Flourish.Description";

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly BlockObjectFactory _blockObjectFactory;
    private readonly IBlockService _blockService;
    private readonly EntityService _entityService;
    private readonly ITerrainQuery _terrain;
    private readonly Keystone.Mod.Recipes.BlueprintResolver _blueprints;
    private readonly FlourishCatalog _catalog;
    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly IPlantingMarkQuery _marks;
    private readonly ILoc _loc;

    /// <summary>Cached enum values; same allocation-avoidance reason
    /// as the rest of the periodic ecology pipeline.</summary>
    private static readonly BiomeKind[] AllBiomes =
        (BiomeKind[])System.Enum.GetValues(typeof(BiomeKind));

    /// <summary>Per-tool random source. Non-deterministic across
    /// sessions, which is fine -- the tool is user-driven, no
    /// save-replay correctness requirement. Used to pick among
    /// multiple recipes registered for the same biome.</summary>
    private readonly Random _random = new();

    public FlourishPlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        BlockObjectFactory blockObjectFactory,
        IBlockService blockService,
        EntityService entityService,
        ITerrainQuery terrain,
        Keystone.Mod.Recipes.BlueprintResolver blueprints,
        FlourishCatalog catalog,
        RegionService regions,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        IPlantingMarkQuery marks,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _blockObjectFactory = blockObjectFactory;
      _blockService = blockService;
      _entityService = entityService;
      _terrain = terrain;
      _blueprints = blueprints;
      _catalog = catalog;
      _regions = regions;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _marks = marks;
      _loc = loc;
    }

    public ToolDescription DescribeTool() {
      return new ToolDescription.Builder(_loc.T(DisplayNameKey))
          .AddSection(_loc.T(DescriptionKey))
          .Build();
    }

    public void Enter() {
      _inputService.AddInputProcessor(this);
      KeystoneLog.Verbose(
          "[Keystone] FlourishPlacementTool entered. Left-click a tile to " +
          "force-place a biome-appropriate Class B flourish. Esc/right-click to exit.");
    }

    public void Exit() {
      _inputService.RemoveInputProcessor(this);
    }

    public bool ProcessInput() {
      if (_inputService.MouseOverUI) return false;
      if (!_inputService.MainMouseButtonDown) return false;

      var picked = _cursorCoordinatesPicker.Pick();
      if (!picked.HasValue) return false;

      var tile = picked.Value.TileCoordinates;
      try {
        SpawnAt(tile);
      } catch (Exception ex) {
        KeystoneLog.Warn(
            $"[Keystone] FlourishPlacementTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true; // input consumed
    }

    private void SpawnAt(Vector3Int tile) {
      if (_marks.IsMarked(tile.x, tile.y, tile.z)) {
        KeystoneLog.Verbose(
            $"[Keystone] FlourishPlacementTool: tile {tile} is marked for " +
            "planting; skipping (player intent overrides dev placement).");
        return;
      }
      var surface = new SurfaceCoord(tile.x, tile.y, tile.z);
      var region = _regions.Containing(surface);
      if (region == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FlourishPlacementTool: no region at {tile}; skipping.");
        return;
      }
      var field = _fieldQuery.FieldFor(region.Id);
      if (field == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FlourishPlacementTool: no ecology field for region " +
            $"{region.Id} (settled?); skipping.");
        return;
      }

      var (biome, recipe) = PickBiomeRecipe(region.Id, field, tile);
      if (recipe == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FlourishPlacementTool: no Class B recipe applies at " +
            $"{tile} (no biome with a registered recipe scores > 0 here).");
        return;
      }

      if (!Keystone.Mod.Recipes.VerticalClearance.IsAboveClear(_blockService, _terrain, tile, recipe.Height)) {
        KeystoneLog.Verbose(
            $"[Keystone] FlourishPlacementTool: clearance above {tile} insufficient " +
            $"for '{recipe.BlueprintName}' (Height={recipe.Height}); skipping.");
        return;
      }

      var bp = _blueprints.Resolve(recipe.BlueprintName);
      if (bp == null) return;  // warning logged inside

      ClearReplaceableOccupants(tile);

      var spec = bp.GetSpec<BlockObjectSpec>();
      var entity = _blockObjectFactory.CreateFinished(spec,
          new Placement(tile, Orientation.Cw0, FlipMode.Unflipped));
      if (entity == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FlourishPlacementTool: CreateFinished returned null " +
            $"for '{recipe.BlueprintName}' at {tile} (tile occupied?).");
        return;
      }
      // Stamp Class B on the variant so the entity behaves like a
      // Class B handler-spawned entity (suppressed selection /
      // demolish UI). Force-placed and reconciled entities are
      // indistinguishable post-spawn.
      var variant = entity.GetComponent<Keystone.Mod.Recipes.KeystoneVariant>();
      if (variant != null) variant.SetClass("B");
      PostSpawnPolish(entity);
      KeystoneLog.Verbose(
          $"[Keystone] FlourishPlacementTool: force-placed '{recipe.BlueprintName}' " +
          $"at {tile} for biome {biome} (overriding score gate).");
    }

    /// <summary>Picks the biome at the cursor with the highest
    /// chunk-score that has at least one registered Class B recipe.
    /// Returns a randomly chosen recipe from that biome's recipe
    /// list -- random across clicks gives the player some variety
    /// when the same biome has multiple recipes registered (e.g.
    /// the three Grassland tiers).</summary>
    private (BiomeKind Biome, ClassBRecipe? Recipe) PickBiomeRecipe(
        RegionId regionId, RegionEcologyField field, Vector3Int tile) {
      var bestBiome = (BiomeKind)0;
      var bestScore = 0f;
      IReadOnlyList<ClassBRecipe>? bestRecipes = null;

      for (var i = 0; i < AllBiomes.Length; i++) {
        var biome = AllBiomes[i];
        var recipes = new List<ClassBRecipe>(_catalog.ClassBForBiome(biome));
        if (recipes.Count == 0) continue;

        var score = ChunkBiomeSampler.SampleSuitability(
            _biomeValues, regionId, biome,
            field.OriginX, field.OriginY,
            field.ChunksX, field.ChunksY,
            tile.x, tile.y);
        if (score > bestScore) {
          bestScore = score;
          bestBiome = biome;
          bestRecipes = recipes;
        }
      }

      if (bestRecipes == null) return (bestBiome, null);
      return (bestBiome, bestRecipes[_random.Next(bestRecipes.Count)]);
    }

    /// <summary>
    /// Post-spawn fixups for ambient flourishes: fast-forward growth if
    /// the entity carries a <see cref="Growable"/> (the static asset
    /// won't, but defensive against future variants), and suppress
    /// NavMesh contribution so beavers walk through. The Harmony
    /// ambient-filter patches handle selection/demolish suppression
    /// upstream -- nothing to do here for those.
    /// </summary>
    /// <summary>Remove dead Keystone flourishes and harvested vanilla
    /// stumps from the tile so the new flourish can take their place.</summary>
    private void ClearReplaceableOccupants(Vector3Int tile) {
      foreach (var bo in _blockService.GetObjectsAt(tile)) {
        if (bo == null) continue;
        if (KeystoneFlourish.IsDeadFlourish(bo) || IsHarvestedStump(bo)) {
          _entityService.Delete(bo);
        }
      }
    }

    private static bool IsHarvestedStump(BlockObject bo) {
      var living = bo.GetComponent<LivingNaturalResource>();
      if (living == null || !living.IsDead) return false;
      var cuttable = bo.GetComponent<Cuttable>();
      return cuttable != null && cuttable.Yielder.IsYieldRemoved;
    }

    private static void PostSpawnPolish(BlockObject entity) {
      GrowableTimeTriggerAccessor.FastForwardToMature(entity.GetComponent<Growable>());
      foreach (var component in entity.AllComponents) {
        if (component is BaseComponent bc) {
          var typeName = bc.GetType().Name;
          if (typeName == "BlockObjectNavMesh" || typeName == "BlockObjectPreviewNavMesh") {
            bc.DisableComponent();
          }
        }
      }
    }

  }

}
