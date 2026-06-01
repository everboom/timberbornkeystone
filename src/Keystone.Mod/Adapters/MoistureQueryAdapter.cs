using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Timberborn.MapIndexSystem;
using Timberborn.SoilMoistureSystem;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="IMoistureQuery"/> implementation backed by Timberborn's
  /// <see cref="ISoilMoistureService"/>. Routes the column-level fractional
  /// query through <see cref="MapIndexService.CellToIndex"/> and the
  /// per-voxel predicate through the service's <c>SoilIsMoist</c> overload
  /// directly.
  /// </summary>
  public sealed class MoistureQueryAdapter : IMoistureQuery {

    #region Fields

    private readonly ISoilMoistureService _moisture;
    private readonly MapIndexService _mapIndex;

    #endregion

    #region Construction

    public MoistureQueryAdapter(ISoilMoistureService moisture, MapIndexService mapIndex) {
      _moisture = moisture;
      _mapIndex = mapIndex;
    }

    #endregion

    #region IMoistureQuery

    /// <inheritdoc />
    public float MoistureAt(TileCoord column) {
      var idx = _mapIndex.CellToIndex(new Vector2Int(column.X, column.Y));
      return _moisture.SoilMoisture(idx);
    }

    /// <inheritdoc />
    public bool IsMoistAt(SurfaceCoord surface) {
      return _moisture.SoilIsMoist(new Vector3Int(surface.X, surface.Y, surface.Z));
    }

    #endregion

  }

}
