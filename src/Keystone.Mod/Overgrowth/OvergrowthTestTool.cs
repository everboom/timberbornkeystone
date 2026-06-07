using System;
using Keystone.Core.Biomes;
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
  /// Dev tool: clicking a tile drives the overgrowth lifecycle on the tree
  /// there — every (non-water) tree carries a <see cref="KeystoneOvergrowth"/>
  /// via the <c>AddDecorator&lt;TreeComponentSpec, _&gt;</c> registration.
  /// Successive clicks cycle: barren → overgrown → <b>reseeded</b> (the dead
  /// tree replaced by a new Class D seedling that drops the felled wood and
  /// comes out overgrown). Lets the full arc — overlay, then the
  /// delete + spawn + wood-drop reseed mechanism — be exercised by hand,
  /// without waiting for biome maturity / a dead tree to occur naturally.
  ///
  /// <para>The reseed step here bypasses the natural eligibility gates
  /// (host-tree-dead, overgrowth maturity, biome maturity) that
  /// <c>OvergrowthHandler</c> enforces in real play — the dev tool reseeds
  /// any overgrown tree so the mechanism is reachable on demand. Terminal
  /// death (<c>#Dead</c>) and decay cleanup are driven by the biome systems
  /// (badwater / Dry attrition / decay ticker), not this tool.</para>
  /// </summary>
  public sealed class OvergrowthTestTool : ITool, IInputProcessor, IToolDescriptor {

    #region Constants

    private const string DisplayNameKey = "Tool.Keystone.Overgrowth.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.Overgrowth.Description";

    /// <summary>Biome / source level / composition the dev reseed draws
    /// from. Grassland L4 is the tree table (Birch-heavy); the composition
    /// is one of the purpose-built overgrowth minis carried onto the new
    /// seedling.</summary>
    private const BiomeKind DevReseedBiome = BiomeKind.Grassland;
    private const string DevReseedSourceLevel = "L4";
    private const string DevReseedComposition = "KeystoneOvergrowthMini1";

    #endregion

    #region Injected services

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly IBlockService _blockService;
    private readonly OvergrowthReseeder _reseeder;
    private readonly ILoc _loc;

    #endregion

    #region Construction

    public OvergrowthTestTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        IBlockService blockService,
        OvergrowthReseeder reseeder,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _blockService = blockService;
      _reseeder = reseeder;
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
      // Cycle: barren -> overgrown -> reseeded (new seedling, overgrown).
      if (!overgrowth.IsOvergrown) {
        overgrowth.Apply();
      } else {
        var reseeded = _reseeder.TryReseed(
            overgrowth, DevReseedBiome, DevReseedSourceLevel, DevReseedComposition);
        KeystoneLog.Verbose(
            $"[Keystone] OvergrowthTestTool: reseed at {tile} " +
            (reseeded ? "succeeded." : "failed (see reseeder log)."));
      }
    }

    #endregion

  }

}
