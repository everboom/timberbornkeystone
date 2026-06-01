using Keystone.Core.Tiles;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Narrow read-side port for "is the voxel at this coordinate occupied
  /// by a natural obstacle that should be excluded from region
  /// membership?" Drives <c>TerrainSurveyor</c>'s per-surface
  /// <c>IsBlocked</c> classification.
  ///
  /// <para><b>Why distinct from <see cref="IBuildingQuery"/>.</b>
  /// Buildings and blockages share the surface "something is here"
  /// quality but have very different rules attached. A building anchors
  /// the Settled halo (its 8 lateral neighbours flip to Settled), while
  /// a blockage covers its own footprint with no halo. A building
  /// remains in its region as a member surface (paths over it are
  /// traversable); a blockage is excluded from any region (the surface
  /// is unreachable). Keeping the two axes as separate ports prevents
  /// future Building-related branches from having to relitigate "wait,
  /// does Blocked count too?" each time.</para>
  ///
  /// <para><b>Surface vs voxel.</b> Implementations interpret the
  /// <see cref="SurfaceCoord"/> as a 3D voxel reference (X, Y, Z); the
  /// same coordinate type is reused for surface lookups and for
  /// "is the voxel directly above this terrain cell occupied by X?"
  /// queries.</para>
  /// </summary>
  public interface IBlockingQuery {

    /// <summary>
    /// True iff a natural-obstacle block object occupies the voxel at
    /// <paramref name="voxel"/>. Trees, crops, gatherables, and other
    /// passable naturals are NOT blocking; only the curated set of
    /// natural impassables (dams, blockages, geysers, overhangs,
    /// reserve piles, etc.) qualify. Buildings are not blocking even
    /// when impassable -- they go through <see cref="IBuildingQuery"/>.
    /// </summary>
    bool IsBlockedAt(SurfaceCoord voxel);

  }

}
