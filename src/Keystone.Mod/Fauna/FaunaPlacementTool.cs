using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Timberborn.BaseComponentSystem;
using Timberborn.Coordinates;
using Timberborn.CursorToolSystem;
using Timberborn.EntitySystem;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.TemplateCollectionSystem;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;
using Random = System.Random;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Dev tool: spawns a fauna agent at the cursor, picking species
  /// by the chunk's biome. End-to-end smoke test of the
  /// custom-mesh-plus-animation-plus-AI pipeline for fauna assets.
  ///
  /// <para><b>Species selection.</b> Resolves the chunk's
  /// highest-scoring biome that has at least one registered
  /// <c>Class E</c> recipe, then picks one of those recipes
  /// uniformly at random and spawns its blueprint. Click in
  /// grassland → deer (today); click in forest → fox (once registered);
  /// click somewhere with no registered fauna → no spawn (logged).</para>
  ///
  /// <para><b>Disregards the natural-spawn gates.</b> No time-of-day
  /// gate (works at any hour) and no maturity gate (spawns even on
  /// freshly-suitable terrain). The agent itself is configured with
  /// <c>minMaturity = 0</c> so it can roam the full region for
  /// visual testing.</para>
  ///
  /// <para><b>Spawn path mirrors vanilla beavers.</b> Uses
  /// <see cref="EntityService.Instantiate"/> with the resolved
  /// blueprint, the same API <c>BeaverFactory.CreateNewBeaver</c>
  /// uses. EntityService runs the full DI / lifecycle so injected
  /// services on <c>TimbermeshAnimator</c> and our own components
  /// re-inject correctly.</para>
  /// </summary>
  public sealed class FaunaPlacementTool : ITool, IInputProcessor, IToolDescriptor {

    private const string DisplayNameKey = "Tool.Keystone.Fauna.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.Fauna.Description";

    /// <summary>Cached enum values; iterating the array avoids an
    /// allocation each click.</summary>
    private static readonly BiomeKind[] AllBiomes =
        (BiomeKind[])Enum.GetValues(typeof(BiomeKind));

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly EntityService _entityService;
    private readonly TemplateCollectionService _templates;
    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly FlourishCatalog _catalog;
    private readonly KeystoneFaunaRegistry _faunaRegistry;
    private readonly IPlantingMarkQuery _marks;
    private readonly ILoc _loc;

    /// <summary>Per-tool random source. Non-deterministic across
    /// sessions; the dev tool isn't on the save-replay path.</summary>
    private readonly Random _random = new();

    public FaunaPlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        EntityService entityService,
        TemplateCollectionService templates,
        RegionService regions,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        FlourishCatalog catalog,
        KeystoneFaunaRegistry faunaRegistry,
        IPlantingMarkQuery marks,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _entityService = entityService;
      _templates = templates;
      _regions = regions;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _catalog = catalog;
      _faunaRegistry = faunaRegistry;
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
          "[Keystone] FaunaPlacementTool entered. Left-click a tile to spawn " +
          "the fauna recipe for that biome (no maturity / time-of-day check). " +
          "Esc/right-click to exit.");
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
            $"[Keystone] FaunaPlacementTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
      }
      return true;
    }

    private void SpawnAt(Vector3Int tile) {
      if (_marks.IsMarked(tile.x, tile.y, tile.z)) {
        KeystoneLog.Verbose(
            $"[Keystone] FaunaPlacementTool: tile {tile} is marked for " +
            "planting; skipping (player intent overrides dev placement).");
        return;
      }

      var region = _regions.Containing(new SurfaceCoord(tile.x, tile.y, tile.z));
      if (region == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FaunaPlacementTool: no region at {tile}; agent " +
            "needs region context for pathfinding. Skipping spawn.");
        return;
      }
      var field = _fieldQuery.FieldFor(region.Id);
      if (field == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FaunaPlacementTool: no ecology field for region " +
            $"{region.Id} at {tile}; can't pick a biome. Skipping spawn.");
        return;
      }

      // Find the highest-scoring biome with at least one registered
      // Class E recipe, and pick a recipe from its bucket at random.
      var (biome, recipe) = PickBiomeRecipe(region.Id, field, tile);
      if (recipe == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FaunaPlacementTool: no Class E recipe applies at {tile}. " +
            "Either no biome scores > 0 here, or no fauna recipes are registered " +
            "for any of those biomes.");
        return;
      }

      var blueprint = _templates.AllTemplates
          .FirstOrDefault(b => b.Name == recipe.BlueprintName);
      if (blueprint == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FaunaPlacementTool: blueprint '{recipe.BlueprintName}' not in " +
            "TemplateCollectionService.AllTemplates. Check that the JSON ships in the " +
            "Mods folder and is listed in a TemplateCollection blueprint.");
        return;
      }

      var entity = _entityService.Instantiate(blueprint);
      if (entity == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FaunaPlacementTool: EntityService.Instantiate returned " +
            $"null for '{recipe.BlueprintName}'.");
        return;
      }
      entity.Transform.position = CoordinateSystem.GridToWorldCentered(tile);
      // Random yaw so multiple agents don't all face the same default
      // direction. Each spawn picks its own angle from the shared
      // per-tool RNG.
      entity.Transform.rotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 360f, 0f);

      var animator = entity.GetComponent<KeystoneFaunaAnimator>();
      var agent = entity.GetComponent<KeystoneFaunaAgent>();
      if (agent == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FaunaPlacementTool: spawned entity has no " +
            "KeystoneFaunaAgent. Did the blueprint drop KeystoneFaunaAgentSpec?");
        return;
      }
      // Dev-placed agents get maturity=0 so they can walk anywhere in
      // the region. Natural-spawn agents (FaunaDayCycleHandler) will
      // pass the recipe's level's LowerMaturity instead.
      agent.Configure(animator, region, new TileCoord(tile.x, tile.y), biome, minMaturityThreshold: 0f);
      // Dev-placed deer still join the registry so dusk despawns them
      // with the rest. Keeps behaviour consistent across spawn sources.
      // Note: at the next dawn reconcile, dev-placed fauna count toward
      // the cluster's capacity and may get culled if they overshoot —
      // that's intentional (the dev tool bypasses the spawn gate, not
      // the cull gate). PersistsOvernight is read off the agent so
      // dev-placed aquatic species (if a Class E fish recipe is ever
      // wired in this tool) survive dusk like the natural-spawn ones.
      _faunaRegistry.Add(entity, agent, recipe.BlueprintName, agent.PersistsOvernight);

      KeystoneLog.Verbose(
          $"[Keystone] FaunaPlacementTool: spawned '{recipe.BlueprintName}' " +
          $"at {tile} for biome {biome} in region {region.Id}.");
    }

    /// <summary>Walk every biome that has at least one Class E recipe
    /// registered, sample the chunk's Suitability score at the cursor,
    /// and return the highest-scoring biome's bucket together with a
    /// uniformly-random recipe from it. Returns <c>(default, null)</c>
    /// when no biome with a recipe scores above 0.</summary>
    private (BiomeKind Biome, ClassERecipe? Recipe) PickBiomeRecipe(
        RegionId regionId, RegionEcologyField field, Vector3Int tile) {
      var bestBiome = default(BiomeKind);
      var bestScore = 0f;
      IReadOnlyList<ClassERecipe>? bestRecipes = null;

      for (var i = 0; i < AllBiomes.Length; i++) {
        var biome = AllBiomes[i];
        var recipes = new List<ClassERecipe>(_catalog.ClassEForBiome(biome));
        if (recipes.Count == 0) continue;

        var score = ChunkBiomeSampler.SampleSuitability(
            _biomeValues, regionId, biome,
            field.OriginX, field.OriginY, field.ChunksX, field.ChunksY,
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

  }

}
