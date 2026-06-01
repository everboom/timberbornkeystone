using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Timberborn.MapIndexSystem;
using Timberborn.WaterSystem;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="IWaterQuery"/> implementation backed by Timberborn's
  /// <see cref="IThreadSafeWaterMap"/>. Most queries are voxel-keyed; the
  /// adapter just translates <see cref="SurfaceCoord"/> to
  /// <see cref="Vector3Int"/>. <see cref="WaterSurfaceHeightAt"/> instead
  /// walks the column stack by index (via <see cref="MapIndexService"/>),
  /// mirroring the vanilla "Water columns" debug panel.
  /// </summary>
  public sealed class WaterQueryAdapter : IWaterQuery {

    #region Fields

    private readonly IThreadSafeWaterMap _waterMap;
    private readonly MapIndexService _mapIndex;

    #endregion

    #region Construction

    public WaterQueryAdapter(IThreadSafeWaterMap waterMap, MapIndexService mapIndex) {
      _waterMap = waterMap;
      _mapIndex = mapIndex;
    }

    #endregion

    #region IWaterQuery

    /// <inheritdoc />
    public float WaterDepthAt(SurfaceCoord surface) {
      return _waterMap.WaterDepth(new Vector3Int(surface.X, surface.Y, surface.Z));
    }

    /// <inheritdoc />
    public float WaterSurfaceHeightAt(SurfaceCoord surface) {
      // Walk the column stack at this tile the way the vanilla "Water
      // columns" panel does (CellToIndex + i * VerticalStride), and return
      // the resting water body's absolute surface height = Floor +
      // WaterDepth. Columns are stored sorted by Floor ascending.
      var index2D = _mapIndex.CellToIndex(new Vector2Int(surface.X, surface.Y));
      var stride = _mapIndex.VerticalStride;
      var count = _waterMap.ColumnCount(index2D);
      for (var i = 0; i < count; i++) {
        var index3D = index2D + i * stride;
        int floor = _waterMap.ColumnFloor(index3D);
        // The water resting on this surface is the lowest watered column
        // whose body covers it. Tolerate a one-cell gap on the low side:
        // the surveyed surface Z is the air cell above solid terrain and
        // can read one below the column floor over deep water. A floor
        // more than a cell above the surface is *perched* water (e.g. a
        // pool over an overhang above a dry cave floor) and must NOT lift
        // this surface's box -- stop scanning (columns sorted by floor).
        if (floor > surface.Z + 1) break;
        var depth = _waterMap.WaterDepth(index3D);
        if (depth > 0f && surface.Z < floor + depth) return floor + depth;
      }
      return 0f;
    }

    /// <inheritdoc />
    public FlowVector FlowAt(SurfaceCoord surface) {
      var v = _waterMap.WaterFlowDirection(new Vector3Int(surface.X, surface.Y, surface.Z));
      return new FlowVector(v.x, v.y);
    }

    /// <inheritdoc />
    public bool HasWaterAtColumn(TileCoord column) {
      return _waterMap.IsWaterOnAnyHeight(new Vector2Int(column.X, column.Y));
    }

    /// <inheritdoc />
    public float WaterContaminationAt(SurfaceCoord surface) {
      return _waterMap.ColumnContamination(new Vector3Int(surface.X, surface.Y, surface.Z));
    }

    #endregion

  }

}
