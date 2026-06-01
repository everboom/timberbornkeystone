using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;

namespace Keystone.Core.Fauna {

  /// <summary>
  /// Biome-walkability gate on top of any inner topology. A tile is
  /// walkable iff the inner says so AND:
  /// <list type="bullet">
  ///   <item>the tile's <i>dominant</i> biome (via
  ///         <see cref="ChunkBiomeSampler.SampleDominantBiome"/>,
  ///         bilinear across the four surrounding chunk centres)
  ///         matches the configured biome;</item>
  ///   <item>that biome's bilinearly-sampled Maturity at the tile is
  ///         strictly positive (rules out tiles where the biome is
  ///         only nominally suitable but hasn't accumulated any
  ///         Maturity yet);</item>
  ///   <item>that Maturity is &gt;= the configured level threshold (the
  ///         per-recipe gate that natural-spawn agents inherit; zero
  ///         disables the gate for dev-placed agents).</item>
  /// </list>
  ///
  /// <para>Shared between <c>KeystoneFaunaAgent</c>'s
  /// walkability composition and <c>FaunaSpawnDrainer</c>'s
  /// spawn-tile validation so the predicate is identical at spawn
  /// time and at every subsequent stuck-check. Without this sharing,
  /// the drainer's coarser chunk-level check (chunk-centre maturity
  /// vs threshold) accepts tiles that the agent's per-tile bilinear
  /// check rejects — the agent spawns and immediately self-despawns
  /// on its first Update because the same chunk that qualified for
  /// the bucket has tile-level variation the agent doesn't accept.</para>
  /// </summary>
  public sealed class MaturityFilterTopology : IRegionTopologyQuery {

    private readonly IRegionTopologyQuery _inner;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly RegionEcologyField _field;
    private readonly BiomeKind _biome;
    private readonly float _threshold;

    public MaturityFilterTopology(
        IRegionTopologyQuery inner,
        IChunkBiomeValues biomeValues,
        RegionEcologyField field,
        BiomeKind biome,
        float threshold) {
      _inner = inner;
      _biomeValues = biomeValues;
      _field = field;
      _biome = biome;
      _threshold = threshold;
    }

    /// <inheritdoc />
    public bool ContainsTile(RegionId region, int x, int y) {
      if (!_inner.ContainsTile(region, x, y)) return false;
      var (dominant, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          _biomeValues, region,
          _field.OriginX, _field.OriginY, _field.ChunksX, _field.ChunksY,
          x, y);
      if (dominant != _biome) return false;
      if (maturity <= 0f) return false;
      return maturity >= _threshold;
    }

  }

}
