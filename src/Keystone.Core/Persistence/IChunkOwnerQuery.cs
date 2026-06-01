using Keystone.Core.Regions;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Answers "which live region physically owns this chunk's footprint
  /// at this Z?" — the single spatial fact <see cref="ChunkReconciler"/>
  /// needs to re-bind chunk data after region topology changes.
  ///
  /// <para>The production implementation wraps
  /// <c>RegionService.FindRegionByChunkFootprint(chunkX, chunkY,
  /// RegionEcologyField.ChunkSize, z)</c>; the interface exists so the
  /// reconciler stays unit-testable against a hand-rolled owner map
  /// without standing up a full surveyor + region graph (the same
  /// port/adapter seam <c>IEcologyFieldQuery</c> uses for the cluster
  /// index).</para>
  ///
  /// <para><b>Z-strict contract (load-bearing).</b> Implementations MUST
  /// only consider surfaces at exactly <paramref name="z"/>. Returning a
  /// region at a different Z would let stacked regions sharing an
  /// <c>(X, Y)</c> steal each other's chunk data, violating the
  /// <see cref="ChunkValueKey"/> Z invariant. When more than one region
  /// owns surfaces in the footprint at that Z (two same-Z components
  /// interleaving inside one 4×4 chunk, or a settled/wild split), return
  /// the majority owner; the minority region's slice of the chunk is an
  /// accepted localized loss.</para>
  /// </summary>
  public interface IChunkOwnerQuery {

    /// <summary>
    /// The region owning the majority of surfaces in the chunk at
    /// <c>(<paramref name="chunkX"/>, <paramref name="chunkY"/>)</c> on
    /// the global chunk lattice, restricted to surfaces at exactly
    /// <paramref name="z"/>. <c>null</c> when no region owns any surface
    /// there at that Z (every voxel blocked, dug away, or out of bounds).
    /// </summary>
    RegionId? OwnerOfChunk(int chunkX, int chunkY, int z);

    /// <summary>
    /// True if <paramref name="region"/> has at least one surface in the
    /// chunk footprint at exactly <paramref name="z"/> — i.e. it still
    /// legitimately owns the chunk, possibly as a minority co-owner of a
    /// boundary-straddling chunk. <see cref="ChunkReconciler"/> uses this to
    /// KEEP such chunks and re-home only genuinely stranded ones (whose
    /// keyed region is absent from the footprint). Z-strict, same as
    /// <see cref="OwnerOfChunk"/>.
    /// </summary>
    bool RegionOwnsChunk(RegionId region, int chunkX, int chunkY, int z);

  }

}
