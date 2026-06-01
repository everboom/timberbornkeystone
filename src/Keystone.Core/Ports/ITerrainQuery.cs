using System.Collections.Generic;
using Keystone.Core.Tiles;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Read-side port over the host's terrain. Lets Core inspect the playable
  /// map's bounds and per-column surface heights without taking a
  /// dependency on Timberborn or Unity types. The Mod layer is responsible
  /// for providing an adapter that wraps the game's <c>ITerrainService</c>.
  ///
  /// Timberborn columns can host multiple stacked buildable surfaces (a
  /// dug platform plus an overhang above it, terraced builds, etc.) — the
  /// surface enumeration returns every Z value in the column.
  /// </summary>
  public interface ITerrainQuery {

    /// <summary>Number of tile columns along the X axis.</summary>
    int Width { get; }

    /// <summary>Number of tile columns along the Y axis.</summary>
    int Height { get; }

    /// <summary>Highest Z any terrain voxel currently occupies on the map.</summary>
    int MaxHeight { get; }

    /// <summary>
    /// Whether <paramref name="column"/> is inside the playable map. Tiles
    /// outside this range have no defined terrain or soil readings.
    /// </summary>
    bool Contains(TileCoord column);

    /// <summary>
    /// All buildable surface Z values in the column at
    /// <paramref name="column"/>, sorted ascending. Returns an empty list
    /// for an in-bounds column with no surfaces (rare; usually indicates a
    /// hole in the world). Caller must ensure <see cref="Contains"/> is
    /// true.
    /// </summary>
    IReadOnlyList<int> SurfaceHeightsAt(TileCoord column);

    /// <summary>
    /// True if any terrain voxel sits above <paramref name="surface"/> —
    /// i.e., the surface is roofed by an overhang, stacked platform, or
    /// cantilevered stack. This is the cave-detection probe; only natural
    /// terrain counts (player-built roofs are not considered).
    /// </summary>
    bool HasTerrainAbove(SurfaceCoord surface);

    /// <summary>
    /// True if the voxel at <paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/> is occupied by natural terrain. Returns
    /// <c>false</c> for out-of-bounds positions, air voxels, and
    /// player-built stackables (those are tracked by the block system,
    /// not the terrain service). Used for voxel-level adjacency probes
    /// where surface enumeration alone is insufficient — e.g. cliff
    /// detection across columns with overhangs and floating geometry.
    /// </summary>
    bool IsTerrainVoxel(int x, int y, int z);

  }

}
