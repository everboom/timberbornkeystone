using Keystone.Core.Tiles;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Read-side port over the host's soil contamination (badwater) field.
  /// Engine-agnostic counterpart of Timberborn's
  /// <c>ISoilContaminationService</c>; the Mod layer supplies an adapter
  /// that handles index translation.
  ///
  /// Same two-axis shape as <see cref="IMoistureQuery"/>: column-level
  /// float plus per-voxel predicate.
  /// </summary>
  public interface IContaminationQuery {

    /// <summary>
    /// Soil contamination at the surface of the column at
    /// <paramref name="column"/>. Shared across all stacked surfaces in
    /// the column.
    /// <para><b>Units.</b> Same "tiles to nearest source, linear decay"
    /// shape as <see cref="IMoistureQuery.MoistureAt"/> — natural range
    /// roughly [0, 16], <i>not</i> [0, 1]. Core consumers that want a
    /// [0, 1] fraction must normalise themselves. (Separate from the
    /// water-column contamination value, which is fractional and uses
    /// the 0.05 badwater threshold.)</para>
    /// </summary>
    float ContaminationAt(TileCoord column);

    /// <summary>
    /// Per-voxel "is this surface in the contamination plume" predicate
    /// as the game defines it. Stacked surfaces can return different
    /// answers.
    /// </summary>
    bool IsContaminatedAt(SurfaceCoord surface);

  }

}
