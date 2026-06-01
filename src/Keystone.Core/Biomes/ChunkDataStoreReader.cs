using Keystone.Core.Persistence;
using Keystone.Core.Regions;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// <see cref="IChunkBiomeValues"/> backed by the ordinal-indexed
  /// <see cref="ChunkDataStore"/>. Default read path for all
  /// consumers that don't need same-tick freshness — eliminates
  /// string hashing from the hot path.
  /// </summary>
  public sealed class ChunkDataStoreReader : IChunkBiomeValues {

    private readonly ChunkDataStore _store;

    public ChunkDataStoreReader(ChunkDataStore store) {
      _store = store;
    }

    /// <inheritdoc />
    public float? GetSuitability(RegionId regionId, int chunkX, int chunkY, BiomeKind biome) {
      var data = _store.Get(regionId, chunkX, chunkY);
      if (data == null) return null;
      return data.Get(BiomeValueKinds.SuitabilityOrdinal(biome));
    }

    /// <inheritdoc />
    public float? GetMaturity(RegionId regionId, int chunkX, int chunkY, BiomeKind biome) {
      var data = _store.Get(regionId, chunkX, chunkY);
      if (data == null) return null;
      return data.Get(BiomeValueKinds.MaturityOrdinal(biome));
    }

    /// <inheritdoc />
    public ChunkData? GetChunkData(RegionId regionId, int chunkX, int chunkY) {
      return _store.Get(regionId, chunkX, chunkY);
    }

  }

}
