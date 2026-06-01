using Keystone.Core.Regions;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Narrow read-side port for "is this 2D tile a member of this region?"
  /// Drives the fauna pathfinder without copying any region membership
  /// data into the caller.
  ///
  /// <para>A Keystone region is a 4-connected component of surfaces
  /// sharing a single Z (see <see cref="Region"/>'s docstring). Once
  /// the region is identified, pathfinding within it is purely 2D —
  /// no caller of this port ever needs to ask about Z. The agent that
  /// translates a pathfinder result back into world coordinates reads
  /// <see cref="Region.Z"/> directly off the region object.</para>
  ///
  /// <para>The production implementation is a thin adapter over the
  /// existing <see cref="RegionService.Containing"/> lookup; tests
  /// hand-roll a fake. Both are cheap because membership is already
  /// indexed by surface coordinate in <c>RegionService</c>'s
  /// <c>_surfaceToRegion</c> dictionary.</para>
  /// </summary>
  public interface IRegionTopologyQuery {

    /// <summary>True iff the tile <paramref name="x"/>,<paramref name="y"/>
    /// is a member of region <paramref name="region"/>. Out-of-bounds
    /// coordinates and tiles belonging to other regions both return
    /// false. The region's Z is implicit — callers don't need to
    /// supply it.</summary>
    bool ContainsTile(RegionId region, int x, int y);

  }

}
