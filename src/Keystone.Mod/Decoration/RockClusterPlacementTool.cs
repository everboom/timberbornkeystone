using System;
using Keystone.Core.Ports;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.CursorToolSystem;
using Timberborn.EntitySystem;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;
using Random = System.Random;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// Dev tool: left-click to spawn one of the
  /// <c>KeystoneRockCluster*</c> inanimate Class C blueprints at the
  /// cursor's tile. Picks at random across the eight authored
  /// clusters; the entity goes through
  /// <see cref="BlockObjectFactory.CreateFinished"/> so
  /// <see cref="IInitializableEntity"/> fires, the
  /// <see cref="Flourish.KeystoneRockTint"/> tick component begins
  /// running, and the runtime per-tile material swap takes effect
  /// from the next tick.
  ///
  /// <para><b>Why not a Class C recipe.</b> A recipe-driven test
  /// requires waiting for the chunk rules cycle to fire on a tile of
  /// the right biome + level, which is slow to iterate. This tool
  /// short-circuits that for the smoke test: spawn anywhere, observe
  /// tint behaviour, prove the pipeline. Once tinting is confirmed,
  /// authoring Class C recipes against these blueprints is the
  /// follow-up.</para>
  ///
  /// <para><b>Class stamp.</b> Stamps
  /// <see cref="KeystoneVariant.Class"/> = "C" so the entity behaves
  /// like a real Class C spawn (selection / demolish UI, attrition
  /// targeting). Mirrors the stamping in <see cref="ClassCSpawnHandler"/>.</para>
  /// </summary>
  public sealed class RockClusterPlacementTool : ITool, IInputProcessor, IToolDescriptor {

    #region Constants

    private const string DisplayNameKey = "Tool.Keystone.RockCluster.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.RockCluster.Description";

    /// <summary>Blueprint names the tool picks from. Keep in sync with
    /// the <c>KeystoneRockCluster*</c> entries in
    /// <c>KeystoneNaturalResources.blueprint.json</c>. Hardcoded for
    /// the dev tool; a recipe-driven spawn handler can enumerate via
    /// the catalog instead.</summary>
    private static readonly string[] ClusterBlueprintNames = {
        "KeystoneRockCluster1",
        "KeystoneRockCluster2",
        "KeystoneRockCluster3",
        "KeystoneRockCluster4",
        "KeystoneRockCluster5",
        "KeystoneRockCluster6",
        "KeystoneRockCluster7",
        "KeystoneRockCluster8",
    };

    #endregion

    #region Fields + ctor

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly BlockObjectFactory _blockObjectFactory;
    private readonly BlueprintResolver _blueprints;
    private readonly IPlantingMarkQuery _marks;
    private readonly ILoc _loc;
    private readonly Random _random = new();

    public RockClusterPlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        BlockObjectFactory blockObjectFactory,
        BlueprintResolver blueprints,
        IPlantingMarkQuery marks,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _blockObjectFactory = blockObjectFactory;
      _blueprints = blueprints;
      _marks = marks;
      _loc = loc;
    }

    #endregion

    #region ITool / IToolDescriptor

    public ToolDescription DescribeTool() {
      return new ToolDescription.Builder(_loc.T(DisplayNameKey))
          .AddSection(_loc.T(DescriptionKey))
          .Build();
    }

    public void Enter() {
      _inputService.AddInputProcessor(this);
      KeystoneLog.Verbose(
          "[Keystone] RockClusterPlacementTool entered. Left-click a tile " +
          "to spawn a random Class C rock cluster. Esc/right-click to exit.");
    }

    public void Exit() {
      _inputService.RemoveInputProcessor(this);
    }

    #endregion

    #region IInputProcessor

    public bool ProcessInput() {
      if (_inputService.MouseOverUI) return false;
      if (!_inputService.MainMouseButtonDown) return false;

      var picked = _cursorCoordinatesPicker.Pick();
      if (!picked.HasValue) return false;

      var tile = picked.Value.TileCoordinates;
      if (_marks.IsMarked(tile.x, tile.y, tile.z)) {
        KeystoneLog.Verbose(
            $"[Keystone] RockClusterPlacementTool: tile {tile} is marked " +
            "for planting; skipping (player intent overrides dev placement).");
        return true;
      }
      try {
        SpawnAt(tile);
      } catch (Exception ex) {
        KeystoneLog.Warn(
            $"[Keystone] RockClusterPlacementTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true;
    }

    #endregion

    #region Spawn

    private void SpawnAt(Vector3Int tile) {
      var blueprintName = ClusterBlueprintNames[
          _random.Next(ClusterBlueprintNames.Length)];
      var blueprint = _blueprints.Resolve(blueprintName);
      if (blueprint == null) {
        // Warning logged inside BlueprintResolver.Resolve.
        return;
      }
      var spec = blueprint.GetSpec<BlockObjectSpec>();
      var placement = new Placement(tile, Orientation.Cw0, FlipMode.Unflipped);
      var entity = _blockObjectFactory.CreateFinished(spec, placement);
      if (entity == null) {
        KeystoneLog.Verbose(
            $"[Keystone] RockClusterPlacementTool: CreateFinished returned " +
            $"null for '{blueprintName}' at {tile} (tile occupied?).");
        return;
      }
      var variant = entity.GetComponent<KeystoneVariant>();
      if (variant != null) {
        // Class B: yielding to construction, instant-destroyed via the
        // building demolish tool (BuildingDeconstructionClassBPatch widens
        // that tool's picker to include Class B entities).
        //
        // Class C ("blocking + markable for beaver demolition") was the
        // intended target but the mark-for-destruction tool consistently
        // failed to pick the entity up, even after composing the full
        // vanilla decorator chain (DemolishableSpec, DemolishableFromTopSpec,
        // LabeledEntitySpec, CollidersSpec on every child mesh) and an
        // EntityComponent.PreInitialize / Initialize / PostInitialize /
        // PostLoad push after CreateFinished. The cluster's BaseComponents
        // dump showed Demolishable, DemolishJob, BlockObjectAccessible,
        // SelectableObject, Reservable, BuilderPrioritizable all attached,
        // and selection raycast worked, but the demolish-tool picker still
        // returned an empty list for our entity. Diagnosis on hold; see
        // docs/timberborn-specs.md case study for the full trail.
        variant.SetClass("B");
      }
      KeystoneLog.Verbose(
          $"[Keystone] RockClusterPlacementTool: spawned '{blueprintName}' " +
          $"at {tile} as Class B.");
    }

    #endregion

  }

}
