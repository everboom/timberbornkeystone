using System;
using Keystone.Core.Ports;
using Keystone.Mod.Diagnostics;
using Timberborn.CursorToolSystem;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// Dev tool: when active, left-click on a tile to spawn a Class-B
  /// decoration via <see cref="KeystoneDecorationRegistry"/>. Currently
  /// hardcoded to spawn a Dandelion clone with a
  /// <see cref="FloraLifecycleMoistureController"/> attached -- proves
  /// out the reactivity path end-to-end on a vanilla flora prefab.
  /// Future iterations can pick the donor and controller from a recipe
  /// registry.
  ///
  /// <para>Companion to <see cref="Keystone.Mod.Flourish.FlourishPlacementTool"/>;
  /// the two share the same input pattern but spawn fundamentally
  /// different content classes (A vs B per <c>DESIGN.md</c>).</para>
  /// </summary>
  public sealed class DecorationPlacementTool : ITool, IInputProcessor, IToolDescriptor {

    private const string DonorBlueprintName = "Dandelion";
    private const string DisplayNameKey = "Tool.Keystone.Decoration.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.Decoration.Description";

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly KeystoneDecorationRegistry _registry;
    private readonly IPlantingMarkQuery _marks;
    private readonly ILoc _loc;

    public DecorationPlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        KeystoneDecorationRegistry registry,
        IPlantingMarkQuery marks,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _registry = registry;
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
          "[Keystone] DecorationPlacementTool entered. Left-click a tile " +
          "to place a reactive '" + DonorBlueprintName + "' decoration. " +
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
      if (_marks.IsMarked(tile.x, tile.y, tile.z)) {
        KeystoneLog.Verbose(
            $"[Keystone] DecorationPlacementTool: tile {tile} is marked for " +
            "planting; skipping (player intent overrides dev placement).");
        return true; // input consumed; no spawn
      }
      try {
        // Reactive variant: pass a fresh lifecycle-aware controller.
        // Inert variant would be exactly the same call with
        // controller: null. Both paths are first-class in the registry.
        _registry.Spawn(DonorBlueprintName, tile, new FloraLifecycleMoistureController());
      } catch (Exception ex) {
        KeystoneLog.Warn(
            $"[Keystone] DecorationPlacementTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true; // input consumed
    }

  }

}
