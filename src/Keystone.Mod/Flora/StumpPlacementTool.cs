using System;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.Cutting;
using Timberborn.CursorToolSystem;
using Timberborn.EntitySystem;
using Timberborn.Growing;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;

namespace Keystone.Mod.Flora {

  /// <summary>
  /// Dev tool: spawns a Birch at the clicked tile, fast-forwards it
  /// to maturity, kills it, and removes its yield — leaving a
  /// harvested stump. Used to test stump-replacement logic in the
  /// Class B/D spawn handlers.
  /// </summary>
  public sealed class StumpPlacementTool : ITool, IInputProcessor, IToolDescriptor {

    private const string BlueprintName = "Birch";
    private const string DisplayNameKey = "Tool.Keystone.Stump.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.Stump.Description";

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly BlockObjectFactory _blockObjectFactory;
    private readonly BlueprintResolver _blueprints;
    private readonly ILoc _loc;

    public StumpPlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        BlockObjectFactory blockObjectFactory,
        BlueprintResolver blueprints,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _blockObjectFactory = blockObjectFactory;
      _blueprints = blueprints;
      _loc = loc;
    }

    public ToolDescription DescribeTool() {
      return new ToolDescription.Builder(_loc.T(DisplayNameKey))
          .AddSection(_loc.T(DescriptionKey))
          .Build();
    }

    public void Enter() {
      _inputService.AddInputProcessor(this);
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
        SpawnStumpAt(tile);
      } catch (Exception ex) {
        KeystoneLog.Warn(
            $"[Keystone] StumpPlacementTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true;
    }

    private void SpawnStumpAt(Vector3Int tile) {
      var blueprint = _blueprints.Resolve(BlueprintName);
      if (blueprint == null) return;

      var spec = blueprint.GetSpec<BlockObjectSpec>();
      var entity = _blockObjectFactory.CreateFinished(
          new EntitySetup.Builder(spec.Blueprint),
          new Placement(tile, Orientation.Cw0, FlipMode.Unflipped));
      if (entity == null) {
        KeystoneLog.Verbose(
            $"[Keystone] StumpPlacementTool: CreateFinished returned null at {tile}.");
        return;
      }

      GrowableTimeTriggerAccessor.FastForwardToMature(entity.GetComponent<Growable>());

      var living = entity.GetComponent<LivingNaturalResource>();
      if (living != null) living.Die();

      var cuttable = entity.GetComponent<Cuttable>();
      if (cuttable != null) cuttable.Yielder.RemoveRemainingYield();

      KeystoneLog.Verbose(
          $"[Keystone] StumpPlacementTool: placed harvested Birch stump at {tile}.");
    }

  }

}
