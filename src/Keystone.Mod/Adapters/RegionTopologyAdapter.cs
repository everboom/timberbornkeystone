using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="IRegionTopologyQuery"/> implementation backed by
  /// <see cref="RegionService"/>. Membership lookups go through the
  /// service's <c>_surfaceToRegion</c> dictionary indirectly, via
  /// <see cref="RegionService.Containing"/>; we just construct the
  /// <see cref="SurfaceCoord"/> from the requested (x, y) using the
  /// region's known Z and compare ids.
  ///
  /// <para><b>Two dictionary lookups per call.</b> One to resolve
  /// the region id to its <see cref="Region.Z"/>, one to ask which
  /// region owns the surface at that (x, y, z). Cheap enough for an
  /// A* inner loop on a region of typical size; the search itself
  /// dominates.</para>
  /// </summary>
  public sealed class RegionTopologyAdapter : IRegionTopologyQuery {

    private readonly RegionService _regions;

    public RegionTopologyAdapter(RegionService regions) {
      _regions = regions;
    }

    /// <inheritdoc />
    public bool ContainsTile(RegionId region, int x, int y) {
      var r = _regions.Get(region);
      if (r == null) return false;
      var owner = _regions.Containing(new SurfaceCoord(x, y, r.Z));
      return owner != null && owner.Id.Equals(region);
    }

  }

}
