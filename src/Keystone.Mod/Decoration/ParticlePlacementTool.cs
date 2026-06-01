using Keystone.Core.Ports;
using Keystone.Mod.Atmosphere;
using Keystone.Mod.Diagnostics;
using Timberborn.CursorToolSystem;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// Dev tool: places a mist instance at the clicked tile by routing
  /// through <see cref="WetlandMistDirector.PlaceTestMist"/>, so the
  /// manually-placed mist exercises exactly the same code path as the
  /// auto-rolled wetland mist (prewarm suppression, renderer occlusion
  /// override, fade-in/out ramp, telemetry hookup).
  ///
  /// <para>De-dup: clicking a tile that already has a live or scheduled
  /// mist is a no-op (the director's <c>PlaceTestMist</c> returns false).
  /// Useful for placing one and orbiting the camera around it during
  /// render-debug iteration without piling up duplicates on the same
  /// tile.</para>
  ///
  /// <para>Independent of the wetland gates -- this tool deliberately
  /// bypasses the biome / maturity / water-depth checks so dev placement
  /// works on any tile during testing.</para>
  /// </summary>
  public sealed class ParticlePlacementTool : ITool, IInputProcessor, IToolDescriptor {

    private const string DisplayNameKey = "Tool.Keystone.Particle.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.Particle.Description";

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly WetlandMistDirector _director;
    private readonly IPlantingMarkQuery _marks;
    private readonly ILoc _loc;

    public ParticlePlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        WetlandMistDirector director,
        IPlantingMarkQuery marks,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _director = director;
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
          "[Keystone] ParticlePlacementTool entered. Left-click to spawn a " +
          "particle decoration. Esc/right-click to exit.");
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
            $"[Keystone] ParticlePlacementTool: tile {tile} is marked for " +
            "planting; skipping (player intent overrides dev placement).");
        return true;
      }
      var placed = _director.PlaceTestMist(tile);
      KeystoneLog.Verbose(placed
          ? $"[Keystone] ParticlePlacementTool: placed mist at {tile}."
          : $"[Keystone] ParticlePlacementTool: mist already present at {tile}; ignored.");
      return true;
    }

  }

}
