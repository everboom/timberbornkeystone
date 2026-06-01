using Keystone.Core.Persistence;
using Keystone.Core.Regions;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Read-only accessor for per-chunk biome suitability and maturity
  /// values. Abstracts the underlying store so callers don't couple
  /// to the storage representation (string-keyed dictionary vs.
  /// ordinal-indexed flat array).
  ///
  /// <para>Two production implementations:
  /// <see cref="ChunkValueStoreReader"/> (string-keyed, backed by
  /// <see cref="Persistence.ChunkValueStore"/>) and
  /// <see cref="ChunkDataStoreReader"/> (ordinal-indexed, backed by
  /// <see cref="Persistence.ChunkDataStore"/>). The ordinal path
  /// eliminates string hashing from the hot path and is the default
  /// for all consumers except those that need same-tick freshness
  /// from the string-keyed store.</para>
  /// </summary>
  public interface IChunkBiomeValues {

    /// <summary>Read the Suitability value for <paramref name="biome"/>
    /// at the given chunk, or <c>null</c> if the chunk has no entry.
    /// Nullable return preserves the bilinear sampler's edge-clamping
    /// (missing corners are excluded and weights renormalized).</summary>
    float? GetSuitability(RegionId regionId, int chunkX, int chunkY, BiomeKind biome);

    /// <summary>Read the Maturity value for <paramref name="biome"/>
    /// at the given chunk, or <c>null</c> if the chunk has no entry.</summary>
    float? GetMaturity(RegionId regionId, int chunkX, int chunkY, BiomeKind biome);

    /// <summary>Fetch the underlying <see cref="ChunkData"/> for the
    /// given chunk in a single lookup, or <c>null</c> when the chunk
    /// has no entry OR when this implementation doesn't expose direct
    /// chunk-data access. Lets perf-critical callers (the per-tile
    /// bilinear sampler) hoist the dict lookup out of the inner read
    /// loop and access the top-biomes hint cached on
    /// <see cref="ChunkData.TopBiomes"/>.
    ///
    /// <para>The string-keyed
    /// <see cref="ChunkValueStoreReader"/> returns <c>null</c> here --
    /// it doesn't sit on top of a <c>ChunkData</c> array. Callers
    /// must tolerate that and fall back to per-read access via
    /// <see cref="GetSuitability"/> / <see cref="GetMaturity"/>.</para></summary>
    ChunkData? GetChunkData(RegionId regionId, int chunkX, int chunkY);

  }

}
