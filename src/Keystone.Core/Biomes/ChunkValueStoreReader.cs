using Keystone.Core.Persistence;
using Keystone.Core.Regions;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// <see cref="IChunkBiomeValues"/> backed by the string-keyed
  /// <see cref="ChunkValueStore"/>. Used where same-tick freshness
  /// is required (e.g. <see cref="BiomeMaturityUpdater"/> reading
  /// suitability values that <see cref="BiomeSuitabilityUpdater"/>
  /// just wrote in the same tick, before the sync to
  /// <see cref="ChunkDataStore"/> has run).
  /// </summary>
  public sealed class ChunkValueStoreReader : IChunkBiomeValues {

    private readonly ChunkValueStore _store;

    public ChunkValueStoreReader(ChunkValueStore store) {
      _store = store;
    }

    /// <inheritdoc />
    public float? GetSuitability(RegionId regionId, int chunkX, int chunkY, BiomeKind biome) {
      return _store.Get(regionId, chunkX, chunkY, BiomeValueKinds.ForSuitability(biome));
    }

    /// <inheritdoc />
    public float? GetMaturity(RegionId regionId, int chunkX, int chunkY, BiomeKind biome) {
      return _store.Get(regionId, chunkX, chunkY, BiomeValueKinds.ForMaturity(biome));
    }

    /// <inheritdoc />
    /// <remarks>String-keyed implementation has no ChunkData backing
    /// array; always returns <c>null</c>. Callers fall back to the
    /// per-read API.</remarks>
    public ChunkData? GetChunkData(RegionId regionId, int chunkX, int chunkY) => null;

  }

}
