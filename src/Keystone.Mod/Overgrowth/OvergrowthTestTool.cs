using System;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.CursorToolSystem;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;

namespace Keystone.Mod.Overgrowth {

  /// <summary>
  /// Dev tool: clicking a tile toggles overgrowth on the tree there —
  /// every (non-water) tree carries a <see cref="KeystoneOvergrowth"/>
  /// via the <c>AddDecorator&lt;TreeComponentSpec, _&gt;</c> registration.
  /// First click drapes flourishes around the trunk; second click clears
  /// them. Lets us validate the overlay + lifecycle mechanism by hand
  /// before the biome-driven <c>OvergrowthHandler</c> exists.
  /// </summary>
  public sealed class OvergrowthTestTool : ITool, IInputProcessor, IToolDescriptor {

    #region Constants

    private const string DisplayNameKey = "Tool.Keystone.Overgrowth.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.Overgrowth.Description";

    #endregion

    #region Injected services

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly IBlockService _blockService;
    private readonly ILoc _loc;

    #endregion

    #region Construction

    public OvergrowthTestTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        IBlockService blockService,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _blockService = blockService;
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
      try {
        ToggleAt(tile);
      } catch (Exception ex) {
        KeystoneLog.Warn(
            $"[Keystone] OvergrowthTestTool: toggle at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true;
    }

    #endregion

    #region Helpers

    private void ToggleAt(Vector3Int tile) {
      var overgrowth = _blockService.GetFirstObjectWithComponentAt<KeystoneOvergrowth>(tile);
      if (overgrowth == null) {
        KeystoneLog.Verbose(
            $"[Keystone] OvergrowthTestTool: no overgrowth-capable entity at {tile}.");
        return;
      }
      if (overgrowth.IsOvergrown) {
        overgrowth.Clear();
        KeystoneLog.Verbose($"[Keystone] OvergrowthTestTool: cleared overgrowth at {tile}.");
      } else {
        overgrowth.Apply();
      }
    }

    #endregion

  }

}
