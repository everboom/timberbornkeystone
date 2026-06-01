using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
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
  /// Dev tool: spawns an aquatic fauna entity at a clicked water tile
  /// and hands it to <see cref="KeystoneAquaticAgent"/> for continuous
  /// pathfinding-driven swimming. Uniform roll between the registered
  /// fish blueprints per click; click a dry tile and nothing spawns
  /// (logged).
  ///
  /// <para>Click gate: water depth > 0. The agent's per-tile gate
  /// (<see cref="KeystoneAquaticAgentSpec.MinWaterDepth"/> +
  /// Wetland/Lake biome membership) is stricter — fish dropped on a
  /// trickle or far from a valid biome cluster will spawn but may not
  /// find any valid destination and float in place. That's intentional
  /// for the smoke-test stage.</para>
  ///
  /// <para>No registry, no despawn, no persistence — accumulates
  /// within a session, clear by reloading.</para>
  /// </summary>
  public sealed class FishSmokeTestTool : ITool, IInputProcessor, IToolDescriptor {

    private const string DisplayNameKey = "Tool.Keystone.FishSmokeTest.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.FishSmokeTest.Description";

    /// <summary>Blueprint names rolled uniformly on each click. Extend
    /// this list to add another fish to the smoke-test rotation.</summary>
    private static readonly string[] FishBlueprintNames = {
        "KeystoneFish1",
        "KeystoneFish2",
    };

    /// <summary>Biomes where the dev tool will spawn a fish. Same set
    /// the agent treats as "home" — mirroring it here means dev-placed
    /// fish never start out-of-biome. Fish stuck in a non-home biome
    /// later (e.g. water sim drift) is the agent's problem to handle.</summary>
    private static readonly HashSet<BiomeKind> SpawnBiomes = new() {
        BiomeKind.Wetland,
        BiomeKind.Lake,
    };

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly EntityService _entityService;
    private readonly TemplateCollectionService _templates;
    private readonly RegionService _regions;
    private readonly IWaterQuery _water;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly KeystoneFaunaRegistry _faunaRegistry;
    private readonly ILoc _loc;

    private readonly Random _random = new();

    public FishSmokeTestTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        EntityService entityService,
        TemplateCollectionService templates,
        RegionService regions,
        IWaterQuery water,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        KeystoneFaunaRegistry faunaRegistry,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _entityService = entityService;
      _templates = templates;
      _regions = regions;
      _water = water;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _faunaRegistry = faunaRegistry;
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
          "[Keystone] FishSmokeTestTool entered. Left-click a water tile " +
          "to spawn a fish (KeystoneAquaticAgent takes over, pathfinding " +
          "across Wetland+Lake water tiles). Esc/right-click to exit.");
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
            $"[Keystone] FishSmokeTestTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
      }
      return true;
    }

    private void SpawnAt(Vector3Int tile) {
      var surface = new SurfaceCoord(tile.x, tile.y, tile.z);
      var waterDepth = _water.WaterDepthAt(surface);
      if (waterDepth <= 0f) {
        KeystoneLog.Verbose(
            $"[Keystone] FishSmokeTestTool: tile {tile} has no water " +
            $"(depth {waterDepth:F2}); skipping spawn.");
        return;
      }
      var region = _regions.Containing(surface);
      if (region == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FishSmokeTestTool: no region at {tile}; agent " +
            "needs region context for pathfinding. Skipping spawn.");
        return;
      }

      // Biome gate: fish should not spawn in River (or Badwater, etc.) —
      // they're only natives of Wetland/Lake. The agent allows a fish
      // to swim out of a non-home biome if it can find a path, but the
      // spawn must happen at home.
      var field = _fieldQuery.FieldFor(region.Id);
      if (field != null) {
        var (dominant, _) = ChunkBiomeSampler.SampleDominantBiome(
            _biomeValues, region.Id,
            field.OriginX, field.OriginY, field.ChunksX, field.ChunksY,
            tile.x, tile.y);
        if (!dominant.HasValue || !SpawnBiomes.Contains(dominant.Value)) {
          KeystoneLog.Verbose(
              $"[Keystone] FishSmokeTestTool: tile {tile} dominant biome " +
              $"is {(dominant?.ToString() ?? "<none>")}, not in {{Wetland,Lake}}; skipping spawn.");
          return;
        }
      }

      var blueprintName = FishBlueprintNames[_random.Next(FishBlueprintNames.Length)];
      var blueprint = _templates.AllTemplates
          .FirstOrDefault(b => b.Name == blueprintName);
      if (blueprint == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FishSmokeTestTool: blueprint '{blueprintName}' not in " +
            "TemplateCollectionService.AllTemplates. Confirm KeystoneFauna template " +
            "collection lists it and the JSON was deployed.");
        return;
      }

      var entity = _entityService.Instantiate(blueprint);
      if (entity == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FishSmokeTestTool: EntityService.Instantiate returned " +
            $"null for '{blueprintName}'.");
        return;
      }
      entity.Transform.rotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 360f, 0f);

      var animator = entity.GetComponent<KeystoneFaunaAnimator>();
      var agent = entity.GetComponent<KeystoneAquaticAgent>();
      if (agent == null) {
        KeystoneLog.Verbose(
            $"[Keystone] FishSmokeTestTool: spawned entity has no " +
            "KeystoneAquaticAgent. Did the blueprint drop KeystoneAquaticAgentSpec?");
        return;
      }
      // Agent owns position: Configure snaps Transform to the
      // water-surface coords for the initial tile and starts the swim
      // clip / first pathfind.
      agent.Configure(animator, region, new TileCoord(tile.x, tile.y));
      // Join the registry so the visibility hider sees the fish (the
      // cutaway slider wouldn't otherwise mask dev-placed fish). Fish
      // persist overnight (KeystoneAquaticAgent.PersistsOvernight =>
      // true), so the dusk teardown skips them; the dawn capacity-
      // reconcile pass alone manages fish population.
      _faunaRegistry.Add(entity, agent, blueprintName, agent.PersistsOvernight);

      KeystoneLog.Verbose(
          $"[Keystone] FishSmokeTestTool: spawned '{blueprintName}' at " +
          $"{tile} (waterDepth {waterDepth:F2}, region {region.Id}).");
    }

  }

}
