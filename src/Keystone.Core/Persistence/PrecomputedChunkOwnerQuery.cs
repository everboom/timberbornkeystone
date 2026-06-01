using System.Collections.Generic;
using Keystone.Core.Regions;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// <see cref="IChunkOwnerQuery"/> backed by a precomputed
  /// <c>(chunkX, chunkY, z) → owning region</c> map — typically built by
  /// <see cref="RegionService.BuildChunkFootprintOwnerIndex"/>. O(1) per
  /// lookup, used to make the map-wide <c>ChunkReconciler</c> sweep cheap
  /// instead of doing a footprint walk per chunk.
  ///
  /// <para>The map is a point-in-time snapshot: a lookup is valid only as
  /// long as region topology hasn't changed since the index was built.
  /// Build it immediately before a single sweep and discard it after — do
  /// not cache across topology edits.</para>
  /// </summary>
  public sealed class PrecomputedChunkOwnerQuery : IChunkOwnerQuery {

    private readonly IReadOnlyDictionary<(int ChunkX, int ChunkY, int Z), ChunkFootprintOwnership> _index;

    public PrecomputedChunkOwnerQuery(
        IReadOnlyDictionary<(int ChunkX, int ChunkY, int Z), ChunkFootprintOwnership> index) {
      _index = index;
    }

    /// <inheritdoc />
    public RegionId? OwnerOfChunk(int chunkX, int chunkY, int z) {
      return _index.TryGetValue((chunkX, chunkY, z), out var f) ? f.Majority : (RegionId?)null;
    }

    /// <inheritdoc />
    public bool RegionOwnsChunk(RegionId region, int chunkX, int chunkY, int z) {
      return _index.TryGetValue((chunkX, chunkY, z), out var f) && f.Contains(region);
    }

  }

}
