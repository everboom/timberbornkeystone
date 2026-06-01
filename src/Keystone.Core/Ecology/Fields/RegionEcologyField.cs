using System;

namespace Keystone.Core.Ecology.Fields {

  /// <summary>
  /// Per-region scalar field over a chunked grid. Each region in the world
  /// owns one of these; they hold smoothed local conditions
  /// (moisture, contamination, water flow, plant and animal densities) at
  /// chunk-scale resolution and answer per-tile queries via bilinear
  /// interpolation.
  ///
  /// <para><b>Why per-region.</b> A regular global grid laid over the map
  /// would have chunks straddling region boundaries, mixing wet-plateau
  /// tiles with dry-plateau tiles in a single chunk's mean. That's the
  /// wrong model for ecology -- we want each region to have its own view
  /// of itself, never coloured by neighbouring regions. Each field is
  /// scoped to its region's bounding box, and only in-region tiles
  /// contribute to chunk values.</para>
  ///
  /// <para><b>Why chunks.</b> Per-tile Gaussian blur over a meaningful
  /// radius is O(N x R^2) per refresh -- too expensive at typical-map
  /// sizes. A regular chunk grid with bilinear lookup is the cheap
  /// approximation: O(N) refresh, O(1) per-tile query, and the bilinear
  /// blend gives a smooth gradient field at sub-chunk granularity.</para>
  ///
  /// <para><b>Channel layout.</b> Every field carries the four fixed
  /// channels enumerated by <see cref="EcologyChannel"/> (two continuous
  /// scalars -- water depth, water flow magnitude -- and two boolean-
  /// predicate fractions -- moisture and contamination), plus N integer-
  /// indexed <i>entity channels</i> (index 0..N-1). Entity channels
  /// store raw counts (per chunk) of an entity blueprint at each chunk --
  /// flora today, fauna once Phase 2 lands, both populated the same way.
  /// The channel index map is owned by a higher layer (the catalog at
  /// mod load); the field type knows nothing about which blueprint is
  /// at which index.</para>
  ///
  /// <para><b>Validity and bilinear at edges.</b> A chunk that contains
  /// no in-region tiles is flagged invalid -- it had nothing to average.
  /// At sample time the bilinear stencil drops invalid corners and
  /// renormalises the remaining weights. So at a region's interior the
  /// query is full 4-point bilinear; near the boundary it gracefully
  /// degrades to 3, 2, or 1 corner. If all four corners are invalid the
  /// query returns 0 (caller is sampling outside meaningful data).</para>
  ///
  /// <para><b>Construction and mutation.</b> Production code allocates an
  /// empty field via the public <c>(originX, originY, chunksX, chunksY,
  /// entityChannelCount)</c> constructor and overwrites individual
  /// chunks in place via <see cref="WriteChunk"/> as the polling cycle
  /// progresses. Each chunk write is independent: consumers reading
  /// during a cycle see a mix of freshly-recomputed and last-cycle
  /// chunks, which is intentional -- it both spreads cost evenly and
  /// surfaces new data as soon as it's available without waiting for an
  /// atomic full-region flip.</para>
  ///
  /// <para><see cref="RegionEcologyFieldBuilder"/> is the alternative
  /// construction path for tests and one-shot bulk builds.</para>
  /// </summary>
  public sealed class RegionEcologyField {

    #region Constants

    /// <summary>Edge length in tiles of one chunk. Square. Power of two for cheap divisions.</summary>
    public const int ChunkSize = 4;

    /// <summary>Number of fixed scalar channels (mirrors <see cref="EcologyChannel"/>'s value count).</summary>
    public const int FixedChannelCount = 5;

    #endregion

    #region Fields

    /// <summary>
    /// Flat per-channel storage. Layout: <c>_data[channelIndex * ChunksX * ChunksY + cy * ChunksX + cx]</c>.
    /// Channels 0..3 are fixed (<see cref="EcologyChannel"/>); 4..4+EntityChannelCount-1 are entity channels.
    /// </summary>
    private readonly float[] _data;

    /// <summary>Per-chunk validity flag. <c>_valid[cy * ChunksX + cx]</c>.</summary>
    private readonly bool[] _valid;

    /// <summary>Per-chunk sample count — number of in-region surfaces
    /// that contributed to the chunk's averaged scalar values. Used by
    /// <see cref="Clusters.ChunkClusterIndex"/> for tile-weighted
    /// cluster aggregates. Zero for invalid chunks. Layout matches
    /// <see cref="_valid"/>.</summary>
    private readonly int[] _sampleCounts;

    #endregion

    #region Properties

    /// <summary>Tile-space X of the bounding box's lower-left corner.</summary>
    public int OriginX { get; }

    /// <summary>Tile-space Y of the bounding box's lower-left corner.</summary>
    public int OriginY { get; }

    /// <summary>Number of chunks along X.</summary>
    public int ChunksX { get; }

    /// <summary>Number of chunks along Y.</summary>
    public int ChunksY { get; }

    /// <summary>
    /// Number of entity channels carried by this field (one per
    /// catalogued blueprint -- flora, fauna, etc.).
    /// </summary>
    public int EntityChannelCount { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Allocate an empty field of the given dimensions. All chunks start
    /// invalid; their values are zero. Production code calls this on
    /// region introduction and then overwrites individual chunks via
    /// <see cref="WriteChunk"/> as the polling cycle reaches them.
    /// </summary>
    public RegionEcologyField(int originX, int originY, int chunksX, int chunksY, int entityChannelCount) {
      if (chunksX <= 0 || chunksY <= 0) {
        throw new ArgumentException("Field must contain at least one chunk along each axis.");
      }
      if (entityChannelCount < 0) {
        throw new ArgumentOutOfRangeException(nameof(entityChannelCount));
      }
      OriginX = originX;
      OriginY = originY;
      ChunksX = chunksX;
      ChunksY = chunksY;
      EntityChannelCount = entityChannelCount;
      _data = new float[(FixedChannelCount + entityChannelCount) * chunksX * chunksY];
      _valid = new bool[chunksX * chunksY];
      _sampleCounts = new int[chunksX * chunksY];
    }

    /// <summary>
    /// Internal constructor used by the builder and by tests. Takes
    /// ownership of the supplied arrays (does not copy).
    /// </summary>
    internal RegionEcologyField(
        int originX, int originY,
        int chunksX, int chunksY,
        int entityChannelCount,
        float[] data,
        bool[] valid,
        int[] sampleCounts) {
      if (chunksX <= 0 || chunksY <= 0) {
        throw new ArgumentException("Field must contain at least one chunk along each axis.");
      }
      var expectedDataLength = (FixedChannelCount + entityChannelCount) * chunksX * chunksY;
      if (data.Length != expectedDataLength) {
        throw new ArgumentException(
            $"Data length {data.Length} does not match expected {expectedDataLength}.", nameof(data));
      }
      if (valid.Length != chunksX * chunksY) {
        throw new ArgumentException(
            $"Valid-flag length {valid.Length} does not match expected {chunksX * chunksY}.", nameof(valid));
      }
      if (sampleCounts.Length != chunksX * chunksY) {
        throw new ArgumentException(
            $"Sample-counts length {sampleCounts.Length} does not match expected {chunksX * chunksY}.", nameof(sampleCounts));
      }
      OriginX = originX;
      OriginY = originY;
      ChunksX = chunksX;
      ChunksY = chunksY;
      EntityChannelCount = entityChannelCount;
      _data = data;
      _valid = valid;
      _sampleCounts = sampleCounts;
    }

    #endregion

    #region Mutation

    /// <summary>
    /// Overwrite one chunk's contents in place. Used by the polling
    /// updater as it processes chunks across ticks; each call is
    /// independent and visible immediately to subsequent samplers.
    ///
    /// <para><paramref name="scalarValues"/> must contain exactly
    /// <see cref="FixedChannelCount"/> entries in
    /// <see cref="EcologyChannel"/> ordinal order; <paramref name="entityCounts"/>
    /// must contain exactly <see cref="EntityChannelCount"/> entries.</para>
    /// </summary>
    public void WriteChunk(int cx, int cy, bool valid, int sampleCount,
                           ReadOnlySpan<float> scalarValues,
                           ReadOnlySpan<float> entityCounts) {
      WriteScalars(cx, cy, valid, sampleCount, scalarValues);
      WriteEntities(cx, cy, entityCounts);
    }

    /// <summary>Write only the fixed scalar channels, validity flag,
    /// and sample count for one chunk. Entity channels are left
    /// untouched.</summary>
    public void WriteScalars(int cx, int cy, bool valid, int sampleCount,
                             ReadOnlySpan<float> scalarValues) {
      if (cx < 0 || cx >= ChunksX) throw new ArgumentOutOfRangeException(nameof(cx));
      if (cy < 0 || cy >= ChunksY) throw new ArgumentOutOfRangeException(nameof(cy));
      if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
      if (scalarValues.Length != FixedChannelCount) {
        throw new ArgumentException(
            $"Expected {FixedChannelCount} scalar values, got {scalarValues.Length}.", nameof(scalarValues));
      }
      var chunkIdx = ChunkIndex(cx, cy);
      _valid[chunkIdx] = valid;
      _sampleCounts[chunkIdx] = sampleCount;
      for (var ch = 0; ch < FixedChannelCount; ch++) {
        _data[ch * ChunksX * ChunksY + chunkIdx] = scalarValues[ch];
      }
    }

    /// <summary>Write only the entity channels for one chunk. Scalar
    /// channels, validity, and sample count are left untouched.</summary>
    public void WriteEntities(int cx, int cy, ReadOnlySpan<float> entityCounts) {
      if (cx < 0 || cx >= ChunksX) throw new ArgumentOutOfRangeException(nameof(cx));
      if (cy < 0 || cy >= ChunksY) throw new ArgumentOutOfRangeException(nameof(cy));
      if (entityCounts.Length != EntityChannelCount) {
        throw new ArgumentException(
            $"Expected {EntityChannelCount} entity counts, got {entityCounts.Length}.", nameof(entityCounts));
      }
      var chunkIdx = ChunkIndex(cx, cy);
      for (var e = 0; e < EntityChannelCount; e++) {
        _data[(FixedChannelCount + e) * ChunksX * ChunksY + chunkIdx] = entityCounts[e];
      }
    }

    #endregion

    #region Sample API

    /// <summary>
    /// Bilinear-interpolated value of <paramref name="channel"/> at tile
    /// position <paramref name="tileX"/>, <paramref name="tileY"/>.
    /// Invalid chunks are dropped from the stencil and weights renormalise
    /// over the remaining ones. Returns 0 if no surrounding chunk is
    /// valid.
    /// </summary>
    public float Sample(EcologyChannel channel, float tileX, float tileY) =>
        SampleByChannelIndex((int)channel, tileX, tileY);

    /// <summary>
    /// Bilinear-interpolated value of entity channel
    /// <paramref name="entityIndex"/> at tile position. Same edge-
    /// renormalisation behaviour as <see cref="Sample"/>.
    /// </summary>
    public float SampleEntity(int entityIndex, float tileX, float tileY) {
      if (entityIndex < 0 || entityIndex >= EntityChannelCount) {
        throw new ArgumentOutOfRangeException(nameof(entityIndex));
      }
      return SampleByChannelIndex(FixedChannelCount + entityIndex, tileX, tileY);
    }

    #endregion

    #region Raw access (tooling, debug)

    /// <summary>Exact stored value for <paramref name="channel"/> at chunk <c>(cx, cy)</c>. No interpolation.</summary>
    public float ChunkValue(EcologyChannel channel, int cx, int cy) =>
        _data[Offset((int)channel, cx, cy)];

    /// <summary>Exact stored value for entity channel <paramref name="entityIndex"/> at chunk <c>(cx, cy)</c>.</summary>
    public float ChunkValueEntity(int entityIndex, int cx, int cy) {
      if (entityIndex < 0 || entityIndex >= EntityChannelCount) {
        throw new ArgumentOutOfRangeException(nameof(entityIndex));
      }
      return _data[Offset(FixedChannelCount + entityIndex, cx, cy)];
    }

    /// <summary>True iff chunk <c>(cx, cy)</c> had at least one in-region tile contribute.</summary>
    public bool ChunkValid(int cx, int cy) =>
        _valid[ChunkIndex(cx, cy)];

    /// <summary>Number of in-region surfaces that contributed to chunk
    /// <c>(cx, cy)</c>'s averaged values at the last
    /// <see cref="WriteChunk"/>. Zero for invalid chunks. Used as
    /// "tile count" for cluster-aggregate weighting — multi-Z chunks
    /// (cliff faces) count each Z surface, which approximates "biome
    /// area visible from above" for natural-resource terrain at one
    /// Z level (the common case).</summary>
    public int ChunkSampleCount(int cx, int cy) =>
        _sampleCounts[ChunkIndex(cx, cy)];

    /// <summary>
    /// Read-only view of one channel's flat chunk grid. Length is
    /// <see cref="ChunksX"/> * <see cref="ChunksY"/>, indexed
    /// <c>cy * ChunksX + cx</c>. For tooling and debug overlays.
    /// </summary>
    public ReadOnlySpan<float> RawGrid(EcologyChannel channel) =>
        RawGridByChannelIndex((int)channel);

    /// <summary>Read-only view of one entity channel's flat chunk grid.</summary>
    public ReadOnlySpan<float> RawGridEntity(int entityIndex) {
      if (entityIndex < 0 || entityIndex >= EntityChannelCount) {
        throw new ArgumentOutOfRangeException(nameof(entityIndex));
      }
      return RawGridByChannelIndex(FixedChannelCount + entityIndex);
    }

    /// <summary>Read-only view of the per-chunk validity flags.</summary>
    public ReadOnlySpan<bool> ValidFlags => _valid;

    #endregion

    #region Internals

    private int ChunkIndex(int cx, int cy) => cy * ChunksX + cx;

    private int Offset(int channelIndex, int cx, int cy) =>
        (channelIndex * ChunksX * ChunksY) + ChunkIndex(cx, cy);

    private ReadOnlySpan<float> RawGridByChannelIndex(int channelIndex) {
      var start = channelIndex * ChunksX * ChunksY;
      return new ReadOnlySpan<float>(_data, start, ChunksX * ChunksY);
    }

    /// <summary>
    /// Core bilinear sampler. Treats chunk centres as samples on a regular
    /// lattice (chunk <c>(cx, cy)</c>'s centre lies at tile-space
    /// <c>(OriginX + cx*ChunkSize + (ChunkSize-1)/2,</c> ...<c>)</c>).
    /// Clamps so the four-corner stencil always lands on existing chunks;
    /// renormalises weights when corners are invalid.
    /// </summary>
    private float SampleByChannelIndex(int channelIndex, float tileX, float tileY) {
      const float chunkCentreOffset = (ChunkSize - 1) * 0.5f;  // 1.5 for ChunkSize=4
      // u, v are positions in chunk-centre space. u = 0 at first chunk's centre,
      // u = ChunksX-1 at last chunk's centre. Tiles outside the centre band get
      // clamped, which gives "edge tile reads its nearest chunk's value" -- the
      // standard image-resampling edge clamp.
      var u = (tileX - OriginX - chunkCentreOffset) / ChunkSize;
      var v = (tileY - OriginY - chunkCentreOffset) / ChunkSize;
      if (u < 0f) u = 0f;
      if (u > ChunksX - 1) u = ChunksX - 1;
      if (v < 0f) v = 0f;
      if (v > ChunksY - 1) v = ChunksY - 1;

      var cxLo = (int)u;
      var cyLo = (int)v;
      var cxHi = cxLo + 1; if (cxHi >= ChunksX) cxHi = ChunksX - 1;
      var cyHi = cyLo + 1; if (cyHi >= ChunksY) cyHi = ChunksY - 1;
      var tx = u - cxLo;
      var ty = v - cyLo;

      // Standard bilinear weights.
      var w00 = (1f - tx) * (1f - ty);
      var w10 = tx * (1f - ty);
      var w01 = (1f - tx) * ty;
      var w11 = tx * ty;

      var v00 = _valid[ChunkIndex(cxLo, cyLo)];
      var v10 = _valid[ChunkIndex(cxHi, cyLo)];
      var v01 = _valid[ChunkIndex(cxLo, cyHi)];
      var v11 = _valid[ChunkIndex(cxHi, cyHi)];

      var sumW = 0f;
      if (v00) sumW += w00;
      if (v10) sumW += w10;
      if (v01) sumW += w01;
      if (v11) sumW += w11;
      if (sumW <= 0f) return 0f;

      var result = 0f;
      if (v00) result += w00 * _data[Offset(channelIndex, cxLo, cyLo)];
      if (v10) result += w10 * _data[Offset(channelIndex, cxHi, cyLo)];
      if (v01) result += w01 * _data[Offset(channelIndex, cxLo, cyHi)];
      if (v11) result += w11 * _data[Offset(channelIndex, cxHi, cyHi)];
      return result / sumW;
    }

    #endregion

  }

}
