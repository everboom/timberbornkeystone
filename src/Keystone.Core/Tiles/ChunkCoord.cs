using Keystone.Core.Regions;

namespace Keystone.Core.Tiles {

  /// <summary>
  /// Identifies one chunk within one region of the global chunk grid.
  /// The unit of work for the chunk-driven rules scheduler.
  ///
  /// <para><b>Why both <see cref="RegionId"/> and global chunk
  /// coordinates.</b> A single 2D chunk (x/y position on the global
  /// chunk lattice) can host surfaces in multiple regions when the
  /// terrain has vertical separations (a flat plateau region above a
  /// dug pit region in the same column). Each region's
  /// <c>RegionEcologyField</c> counts that chunk as one of its own,
  /// so the rules scheduler processes it once per <i>(region, chunk)</i>
  /// pair and only touches surfaces that belong to that region.</para>
  ///
  /// <para><b>Coordinates are global, not local.</b>
  /// <see cref="GlobalChunkX"/> / <see cref="GlobalChunkY"/> are the
  /// chunk indices on the world-wide lattice (tile coord divided by
  /// <c>RegionEcologyField.ChunkSize</c>), not chunk indices relative
  /// to a region's field origin. The same global-chunk coordinates are
  /// what <c>ChunkValueStore</c> keys against.</para>
  /// </summary>
  /// <param name="RegionId">Region this chunk's surfaces belong to.</param>
  /// <param name="GlobalChunkX">Chunk X on the global lattice (tile X / ChunkSize).</param>
  /// <param name="GlobalChunkY">Chunk Y on the global lattice (tile Y / ChunkSize).</param>
  public readonly record struct ChunkCoord(
      RegionId RegionId,
      int GlobalChunkX,
      int GlobalChunkY);

}
