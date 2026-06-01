using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;

namespace Keystone.Core.Tests.Helpers {

  /// <summary>
  /// Test fake for <see cref="IBlockingQuery"/>. Holds a mutable set of
  /// blocked voxels so tests can flip surfaces in and out of the blocked
  /// state mid-test (for incremental-region scenarios). The
  /// <see cref="NothingBlocked"/> factory yields an empty set -- the
  /// common "no blockages anywhere" case most existing tests want.
  /// </summary>
  internal sealed class FakeBlocking : IBlockingQuery {

    private readonly HashSet<SurfaceCoord> _blocked = new();

    public bool IsBlockedAt(SurfaceCoord voxel) => _blocked.Contains(voxel);

    public void Block(SurfaceCoord voxel) => _blocked.Add(voxel);

    public void Unblock(SurfaceCoord voxel) => _blocked.Remove(voxel);

    public static FakeBlocking NothingBlocked() => new();

  }

}
