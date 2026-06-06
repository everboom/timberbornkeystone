using Keystone.Core.Ports;
using Timberborn.BlockSystem;
using UnityEngine;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Shared vertical-clearance check for the Class B/C/D spawn paths.
  /// A spawn at <c>tile = (x, y, z)</c> with extent <c>height</c>
  /// requires every voxel from <c>(x, y, z + 1)</c> through
  /// <c>(x, y, z + height)</c> to be free of both natural terrain and
  /// <see cref="BlockObject"/>s. <b>The placement voxel itself is
  /// outside this check</b>: the per-class replacement rules
  /// (dead-flourish yield, harvested-stump clearing) own that voxel and
  /// run before this helper is consulted.
  /// <para><b>Above-tile occupants are unconditional blockers.</b>
  /// Dead Keystone flourishes and live Class B entities sitting in
  /// the clearance band block the spawn just like any other
  /// <see cref="BlockObject"/> -- the replacement allowances only
  /// cover the placement tile.</para>
  /// </summary>
  internal static class VerticalClearance {

    /// <summary>True if the <paramref name="height"/> voxels directly
    /// above <paramref name="tile"/> are all free. Returns <c>true</c>
    /// for <paramref name="height"/> &lt;= 0 (no clearance needed).</summary>
    public static bool IsAboveClear(
        IBlockService blockService, ITerrainQuery terrain,
        Vector3Int tile, int height) {
      for (var dz = 1; dz <= height; dz++) {
        var z = tile.z + dz;
        if (terrain.IsTerrainVoxel(tile.x, tile.y, z)) return false;
        if (HasAnyBlockObject(blockService, new Vector3Int(tile.x, tile.y, z))) return false;
      }
      return true;
    }

    private static bool HasAnyBlockObject(IBlockService blockService, Vector3Int voxel) {
      foreach (var bo in blockService.GetObjectsAt(voxel)) {
        if (bo != null) return true;
      }
      return false;
    }

  }

}
