using System;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.CursorToolSystem;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;

namespace Keystone.Mod.Flora {

  /// <summary>
  /// Dev tool: force-places the <c>KeystoneDeadPine1</c> flourish (a
  /// vanilla dead-pine trunk wrapped in the Keystone <c>PineIvy</c>
  /// mesh) at the clicked tile. Smoke test for the dead-tree + fitted-
  /// ivy composition authored by
  /// <c>tools/generate-flourish-blueprints.py --dead-tree</c>.
  /// <para>Unlike <see cref="Keystone.Mod.Flourish.FlourishPlacementTool"/>, this bypasses
  /// biome/score resolution entirely and always spawns the one fixed
  /// blueprint, so the asset can be inspected on any tile regardless of
  /// ecology state. Mirrors <see cref="StumpPlacementTool"/> minus the
  /// kill/yield-removal step.</para>
  /// </summary>
  public sealed class DeadTreePlacementTool : ITool, IInputProcessor, IToolDescriptor {

    private const string BlueprintName = "KeystoneDeadPine1";
    private const string DisplayNameKey = "Tool.Keystone.DeadTree.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.DeadTree.Description";

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly BlockObjectFactory _blockObjectFactory;
    private readonly BlueprintResolver _blueprints;
    private readonly ILoc _loc;

    public DeadTreePlacementTool(
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
        SpawnAt(tile);
      } catch (Exception ex) {
        KeystoneLog.Warn(
            $"[Keystone] DeadTreePlacementTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true;
    }

    private void SpawnAt(Vector3Int tile) {
      var blueprint = _blueprints.Resolve(BlueprintName);
      if (blueprint == null) {
        KeystoneLog.Warn(
            $"[Keystone] DeadTreePlacementTool: blueprint '{BlueprintName}' did " +
            "not resolve. Confirm it's listed in the KeystoneNaturalResources " +
            "TemplateCollection and the SDK Mod Builder deployed it.");
        return;
      }

      var spec = blueprint.GetSpec<BlockObjectSpec>();
      var entity = _blockObjectFactory.CreateFinished(spec,
          new Placement(tile, Orientation.Cw0, FlipMode.Unflipped));
      if (entity == null) {
        KeystoneLog.Verbose(
            $"[Keystone] DeadTreePlacementTool: CreateFinished returned null at {tile}.");
        return;
      }

      KeystoneLog.Verbose(
          $"[Keystone] DeadTreePlacementTool: placed '{BlueprintName}' at {tile}.");
    }

  }

}
