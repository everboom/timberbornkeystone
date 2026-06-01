using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.CursorToolSystem;
using Timberborn.Growing;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;
using Random = System.Random;

namespace Keystone.Mod.Flora {

  /// <summary>
  /// Dev tool: spawns a Class D vanilla flora at the clicked tile.
  /// Biome-sensitive — picks the cursor's dominant biome by short-term
  /// Score, then picks a random Class D recipe registered for that
  /// biome (via <see cref="FlourishCatalog.ClassDForBiome"/>) and
  /// spawns through <see cref="ClassDSpawnHandler.TrySpawnClassD"/>
  /// so the Class B replacement logic is exercised exactly the same
  /// way the natural handler path does it.
  ///
  /// <para><b>Why route through the handler.</b> The earlier
  /// implementation called <c>NaturalResourceFactory.SpawnIgnoringConstraints</c>
  /// directly, which bypassed the handler's tile-occupancy
  /// classification (and therefore the Class B replacement). That
  /// made the dev tool a worse-than-useless test for the replacement
  /// logic — clicks always succeeded regardless of what was on the
  /// tile, so a broken replacement check would still appear to "work".
  /// Going through <c>TrySpawnClassD</c> means a Class B at the tile
  /// gets demolished before the spawn (succession), and a non-Class-B
  /// occupant blocks the spawn — both behaviors observable in the
  /// game-time after a click.</para>
  ///
  /// <para><b>Biome resolution mirrors <see cref="Keystone.Mod.Flourish.FlourishPlacementTool"/>.</b>
  /// Walks every <see cref="BiomeKind"/> with at least one Class D
  /// recipe registered, samples the short-term Score at the cursor,
  /// picks the highest-scoring biome. Score (not Investment) so the
  /// tool reflects current conditions on freshly-built terrain
  /// before Investment has accrued — see
  /// <c>memory/feedback_dev_tool_uses_score.md</c>.</para>
  ///
  /// <para><b>Refusal cases.</b> No Score &gt; 0 for any biome with a
  /// Class D recipe at the cursor; no region containing the cursor;
  /// the picked recipe's tile is occupied by a non-Class-B blocker
  /// (vanilla building, pre-existing vanilla flora, Class C/D
  /// entity); the picked blueprint isn't loaded. Click consumed in
  /// every case but no entity spawns; a warning explains why.</para>
  ///
  /// <para><b>Cosmetic post-spawn step:</b> fast-forward the spawned
  /// entity's <c>Growable</c> so it appears mature instantly. Pure
  /// dev-tool nicety; the handler-driven path lets entities grow
  /// from seedling at vanilla rate. Doesn't affect the replacement
  /// logic under test (which already ran).</para>
  /// </summary>
  public sealed class VanillaFloraPlacementTool : ITool, IInputProcessor, IToolDescriptor {

    private const string DisplayNameKey = "Tool.Keystone.VanillaFlora.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.VanillaFlora.Description";

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly ClassDSpawnHandler _handler;
    private readonly FlourishCatalog _catalog;
    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly IPlantingMarkQuery _marks;
    private readonly ILoc _loc;

    /// <summary>Cached enum values; iterating <c>Enum.GetValues</c>
    /// per click would allocate a fresh array and box each value.
    /// Same allocation-avoidance pattern as the rest of the periodic
    /// ecology pipeline.</summary>
    private static readonly BiomeKind[] AllBiomes =
        (BiomeKind[])Enum.GetValues(typeof(BiomeKind));

    /// <summary>Per-tool RNG. Non-deterministic across sessions; the
    /// tool is user-driven so save-replay correctness isn't a concern.</summary>
    private readonly Random _random = new();

    public VanillaFloraPlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        ClassDSpawnHandler handler,
        FlourishCatalog catalog,
        RegionService regions,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        IPlantingMarkQuery marks,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _handler = handler;
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
          "[Keystone] VanillaFloraPlacementTool entered. Left-click a tile to " +
          "force-place a biome-appropriate Class D recipe via the handler's " +
          "replacement-aware path. Esc/right-click to exit.");
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
            $"[Keystone] VanillaFloraPlacementTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true; // input consumed
    }

    private void SpawnAt(Vector3Int tile) {
      if (_marks.IsMarked(tile.x, tile.y, tile.z)) {
        KeystoneLog.Verbose(
            $"[Keystone] VanillaFloraPlacementTool: tile {tile} is marked for " +
            "planting; skipping (player intent overrides dev placement).");
        return;
      }
      var surface = new SurfaceCoord(tile.x, tile.y, tile.z);
      var region = _regions.Containing(surface);
      if (region == null) {
        KeystoneLog.Verbose(
            $"[Keystone] VanillaFloraPlacementTool: no region at {tile}; skipping.");
        return;
      }
      var field = _fieldQuery.FieldFor(region.Id);
      if (field == null) {
        KeystoneLog.Verbose(
            $"[Keystone] VanillaFloraPlacementTool: no ecology field for region " +
            $"{region.Id} (settled?); skipping.");
        return;
      }

      var (biome, recipe) = PickBiomeRecipe(region.Id, field, tile);
      if (recipe == null) {
        KeystoneLog.Verbose(
            $"[Keystone] VanillaFloraPlacementTool: no Class D recipe applies at " +
            $"{tile} (no biome with a registered recipe scores > 0 here).");
        return;
      }

      var entity = _handler.TrySpawnClassD(recipe.BlueprintName, tile, recipe.Height);
      if (entity == null) {
        KeystoneLog.Verbose(
            $"[Keystone] VanillaFloraPlacementTool: TrySpawnClassD returned null " +
            $"for '{recipe.BlueprintName}' at {tile} (tile blocked by non-Class-B, " +
            "blueprint unloaded, or factory failure -- see preceding warnings).");
        return;
      }
      FastForwardGrowth(entity);
      KeystoneLog.Verbose(
          $"[Keystone] VanillaFloraPlacementTool: force-placed '{recipe.BlueprintName}' " +
          $"at {tile} for biome {biome} (via ClassDSpawnHandler.TrySpawnClassD).");
    }

    /// <summary>Pick the highest-Score biome at the cursor that has at
    /// least one Class D recipe registered, then return a random
    /// recipe from that biome's Class D list (uniform across the
    /// list -- no weighting; this is dev placement, not the
    /// handler's hash-pick).</summary>
    private (BiomeKind Biome, ClassDRecipe? Recipe) PickBiomeRecipe(
        RegionId regionId, RegionEcologyField field, Vector3Int tile) {
      var bestBiome = (BiomeKind)0;
      var bestScore = 0f;
      IReadOnlyList<ClassDRecipe>? bestRecipes = null;

      for (var i = 0; i < AllBiomes.Length; i++) {
        var biome = AllBiomes[i];
        var recipes = new List<ClassDRecipe>(_catalog.ClassDForBiome(biome));
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

    private static void FastForwardGrowth(BaseComponent resource) {
      GrowableTimeTriggerAccessor.FastForwardToMature(resource.GetComponent<Growable>());
    }

  }

}
