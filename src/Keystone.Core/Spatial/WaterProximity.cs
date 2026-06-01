using Keystone.Core.Ecology.Fields;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Spatial helper for water-proximity queries. Reads the precomputed
  /// signed distance from <see cref="RegionTileData"/> (filled by
  /// <c>EcologyFieldUpdater</c>'s worker thread each cycle).
  ///
  /// <para>Signed distance: positive = land (distance to water),
  /// negative = water (distance to shore). See
  /// <see cref="WaterDistanceCalculator"/> for the full value table.</para>
  /// </summary>
  public sealed class WaterProximity {

    #region Fields

    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly TileSlotRegistry _tileSlots;
    private int _waterDistanceSlot = -1;

    #endregion

    #region Construction

    public WaterProximity(
        RegionService regions,
        IEcologyFieldQuery fieldQuery,
        TileSlotRegistry tileSlots) {
      _regions = regions;
      _fieldQuery = fieldQuery;
      _tileSlots = tileSlots;
    }

    #endregion

    #region Queries

    /// <summary>
    /// Signed Chebyshev distance from <paramref name="surface"/> to
    /// the water/land boundary. Positive = land, negative = water.
    /// Returns <see cref="WaterDistanceCalculator.OutOfRange"/> when
    /// the surface isn't in any region or tile data hasn't been
    /// computed yet.
    /// </summary>
    public int WaterDistanceAt(SurfaceCoord surface) {
      if (_waterDistanceSlot < 0) {
        var slot = _tileSlots.TryOrdinalFor("keystone.tile.waterDistance");
        if (slot == null) return WaterDistanceCalculator.OutOfRange;
        _waterDistanceSlot = slot.Value;
      }
      var region = _regions.Containing(surface);
      if (region == null) return WaterDistanceCalculator.OutOfRange;
      var tileData = _fieldQuery.TileDataFor(region.Id);
      if (tileData == null) return WaterDistanceCalculator.OutOfRange;
      return (int)tileData.Get(surface.X, surface.Y, _waterDistanceSlot);
    }

    /// <summary>
    /// True if <paramref name="surface"/> is land within Chebyshev
    /// distance 1 of water.
    /// </summary>
    public bool BordersWater(SurfaceCoord surface) {
      return WaterDistanceAt(surface) == 1;
    }

    /// <summary>
    /// True if <paramref name="surface"/> is land within Chebyshev
    /// distance <paramref name="maxDistance"/> of water.
    /// </summary>
    public bool IsNearWater(SurfaceCoord surface, int maxDistance) {
      var dist = WaterDistanceAt(surface);
      return dist >= 1 && dist <= maxDistance;
    }

    /// <summary>
    /// True if <paramref name="surface"/> is water within Chebyshev
    /// distance <paramref name="maxDistance"/> of land (shore).
    /// </summary>
    public bool IsNearShore(SurfaceCoord surface, int maxDistance) {
      var dist = WaterDistanceAt(surface);
      return dist <= -1 && dist >= -maxDistance;
    }

    #endregion

  }

}
