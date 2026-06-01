namespace Keystone.Core.Tiles {

  /// <summary>
  /// Two-dimensional column coordinate. Used as a key into per-column data
  /// (moisture/contamination floats, neighbor enumeration) and as the
  /// "address" of a stack of <see cref="SurfaceCoord"/> values within a
  /// single column. See <see cref="TileMap{TKey, TValue}"/>.
  /// </summary>
  /// <param name="X">East-west index.</param>
  /// <param name="Y">North-south index.</param>
  public readonly record struct TileCoord(int X, int Y);

}
