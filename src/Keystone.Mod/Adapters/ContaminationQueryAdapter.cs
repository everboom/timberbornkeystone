using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Timberborn.MapIndexSystem;
using Timberborn.SoilContaminationSystem;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="IContaminationQuery"/> implementation backed by Timberborn's
  /// <see cref="ISoilContaminationService"/>. Routes the column-level
  /// fractional query through <see cref="MapIndexService.CellToIndex"/> and
  /// the per-voxel predicate through the service's
  /// <c>SoilIsContaminated</c> overload directly.
  /// </summary>
  public sealed class ContaminationQueryAdapter : IContaminationQuery {

    #region Fields

    private readonly ISoilContaminationService _contamination;
    private readonly MapIndexService _mapIndex;

    #endregion

    #region Construction

    public ContaminationQueryAdapter(ISoilContaminationService contamination, MapIndexService mapIndex) {
      _contamination = contamination;
      _mapIndex = mapIndex;
    }

    #endregion

    #region IContaminationQuery

    /// <inheritdoc />
    public float ContaminationAt(TileCoord column) {
      var idx = _mapIndex.CellToIndex(new Vector2Int(column.X, column.Y));
      return _contamination.Contamination(idx);
    }

    /// <inheritdoc />
    public bool IsContaminatedAt(SurfaceCoord surface) {
      return _contamination.SoilIsContaminated(new Vector3Int(surface.X, surface.Y, surface.Z));
    }

    #endregion

  }

}
