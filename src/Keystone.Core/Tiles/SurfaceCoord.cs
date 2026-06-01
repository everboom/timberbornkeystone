using System;

namespace Keystone.Core.Tiles {

  /// <summary>
  /// Three-dimensional surface voxel coordinate. Identifies a single
  /// buildable surface in the voxel terrain — one column
  /// (<see cref="TileCoord"/>) can host multiple <see cref="SurfaceCoord"/>
  /// values when the player has dug, stacked, or built overhangs.
  /// </summary>
  /// <param name="X">East-west index of the column.</param>
  /// <param name="Y">North-south index of the column.</param>
  /// <param name="Z">Integer height of the surface voxel within the column.</param>
  public readonly record struct SurfaceCoord(int X, int Y, int Z)
      : IComparable<SurfaceCoord> {

    /// <summary>The 2D column this surface belongs to.</summary>
    public TileCoord Column => new(X, Y);

    /// <summary>
    /// Total order over surface coordinates: <c>X</c>, then <c>Y</c>,
    /// then <c>Z</c>. Used wherever <see cref="Keystone.Core.Regions.RegionService"/>
    /// needs deterministic iteration -- e.g., picking flood-fill seeds
    /// in <c>Index()</c>, choosing which component keeps the parent
    /// id during a split, or assigning new ids to attaches in a batch.
    /// </summary>
    public int CompareTo(SurfaceCoord other) {
      var c = X.CompareTo(other.X);
      if (c != 0) return c;
      c = Y.CompareTo(other.Y);
      if (c != 0) return c;
      return Z.CompareTo(other.Z);
    }

  }

}
