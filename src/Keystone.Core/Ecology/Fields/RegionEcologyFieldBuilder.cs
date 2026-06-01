using System;

namespace Keystone.Core.Ecology.Fields {

  /// <summary>
  /// Accumulates per-tile contributions and produces a finalised
  /// <see cref="RegionEcologyField"/>. The expected use is one builder per
  /// region per refresh: configure with the region's bounding box and
  /// entity-channel count, walk every in-region tile feeding scalar
  /// values + entity hits, then call <see cref="Build"/> once.
  ///
  /// <para><b>Channel semantics on aggregation.</b></para>
  /// <list type="bullet">
  ///   <item>Fixed scalar channels (moisture, water depth, ...) are
  ///         accumulated as sum + sample-count and finalised to the
  ///         per-chunk mean. A chunk that received no tile contributions
  ///         is flagged invalid, value 0.</item>
  ///   <item>Entity channels are accumulated as raw counts (number of
  ///         entities of that channel within the chunk). Entity
  ///         contributions don't affect the chunk's validity by themselves
  ///         -- a chunk is valid iff at least one scalar tile contributed,
  ///         since entity counts on a chunk with no in-region surface are
  ///         meaningless anyway.</item>
  /// </list>
  ///
  /// <para><b>Out-of-bbox tiles are silently dropped.</b> A caller that
  /// asks the builder to record a tile outside its configured bbox is
  /// almost certainly buggy, but throwing inside a per-tile loop would be
  /// expensive and produces no real safety -- the builder validates its
  /// own bbox at construction. Callers are expected to feed only
  /// in-region tiles.</para>
  /// </summary>
  public sealed class RegionEcologyFieldBuilder {

    #region Fields

    private readonly int _originX;
    private readonly int _originY;
    private readonly int _chunksX;
    private readonly int _chunksY;
    private readonly int _entityChannelCount;

    /// <summary>Per-channel sums over each chunk. Same layout as the field's <c>_data</c>.</summary>
    private readonly float[] _sums;

    /// <summary>Per-chunk count of scalar contributions, used both for averaging fixed channels and for the validity flag.</summary>
    private readonly int[] _scalarCounts;

    #endregion

    #region Construction

    /// <summary>
    /// Configure a builder for a region whose bounding box is
    /// <c>[originX, originX + chunksX * ChunkSize)</c> by
    /// <c>[originY, originY + chunksY * ChunkSize)</c> in tile space,
    /// carrying <paramref name="entityChannelCount"/> entity channels
    /// (one per catalogued blueprint -- flora, fauna, ...).
    /// </summary>
    public RegionEcologyFieldBuilder(int originX, int originY, int chunksX, int chunksY, int entityChannelCount) {
      if (chunksX <= 0 || chunksY <= 0) {
        throw new ArgumentException("Field must contain at least one chunk along each axis.");
      }
      if (entityChannelCount < 0) {
        throw new ArgumentOutOfRangeException(nameof(entityChannelCount));
      }
      _originX = originX;
      _originY = originY;
      _chunksX = chunksX;
      _chunksY = chunksY;
      _entityChannelCount = entityChannelCount;
      _sums = new float[(RegionEcologyField.FixedChannelCount + entityChannelCount) * chunksX * chunksY];
      _scalarCounts = new int[chunksX * chunksY];
    }

    #endregion

    #region Public API

    /// <summary>
    /// Record one tile's scalar contributions. Increments the chunk's
    /// scalar-tile count (which both feeds per-channel averaging and
    /// flags the chunk as valid) and accumulates the four fixed-channel
    /// sums.
    ///
    /// <para>Water depth and flow magnitude are continuous (tile units)
    /// and aggregate as chunk-means. Moisture, contamination, and
    /// water-contamination are boolean predicates and aggregate as
    /// the fraction of in-chunk tiles where the predicate holds.</para>
    /// </summary>
    public void AddTile(int tileX, int tileY,
                        float waterDepth, float waterFlowMagnitude,
                        bool isMoist, bool isContaminated,
                        bool isBadwater = false) {
      if (!TryGetChunk(tileX, tileY, out var cx, out var cy)) return;
      var chunkIdx = ChunkIndex(cx, cy);
      _scalarCounts[chunkIdx]++;
      _sums[Offset((int)EcologyChannel.WaterDepth, cx, cy)] += waterDepth;
      _sums[Offset((int)EcologyChannel.WaterFlowMagnitude, cx, cy)] += waterFlowMagnitude;
      if (isMoist) _sums[Offset((int)EcologyChannel.Moisture, cx, cy)] += 1f;
      if (isContaminated) _sums[Offset((int)EcologyChannel.Contamination, cx, cy)] += 1f;
      if (isBadwater) _sums[Offset((int)EcologyChannel.WaterContamination, cx, cy)] += 1f;
    }

    /// <summary>
    /// Record one entity at <c>(tileX, tileY)</c> as one count on the
    /// given entity channel. Entity counts are raw (unaveraged); the
    /// chunk's <see cref="RegionEcologyField.ChunkValueEntity"/> at
    /// finalisation equals the number of times <c>AddEntity</c> was
    /// called for that chunk and entity index.
    /// </summary>
    public void AddEntity(int tileX, int tileY, int entityIndex) {
      if (entityIndex < 0 || entityIndex >= _entityChannelCount) {
        throw new ArgumentOutOfRangeException(nameof(entityIndex));
      }
      if (!TryGetChunk(tileX, tileY, out var cx, out var cy)) return;
      _sums[Offset(RegionEcologyField.FixedChannelCount + entityIndex, cx, cy)] += 1f;
    }

    /// <summary>
    /// Finalise the accumulated state into an immutable
    /// <see cref="RegionEcologyField"/>. Chunks with no scalar
    /// contributions become invalid (and their values fall to 0).
    /// </summary>
    public RegionEcologyField Build() {
      var totalChannels = RegionEcologyField.FixedChannelCount + _entityChannelCount;
      var totalChunks = _chunksX * _chunksY;
      var data = new float[totalChannels * totalChunks];
      var valid = new bool[totalChunks];
      var sampleCounts = new int[totalChunks];

      for (var cy = 0; cy < _chunksY; cy++) {
        for (var cx = 0; cx < _chunksX; cx++) {
          var idx = ChunkIndex(cx, cy);
          var count = _scalarCounts[idx];
          if (count == 0) {
            valid[idx] = false;
            continue;
          }
          valid[idx] = true;
          sampleCounts[idx] = count;
          var inv = 1f / count;
          // Fixed channels: divide accumulated sum by sample count.
          for (var ch = 0; ch < RegionEcologyField.FixedChannelCount; ch++) {
            data[ch * totalChunks + idx] = _sums[ch * totalChunks + idx] * inv;
          }
          // Entity channels: copy raw counts unchanged.
          for (var e = 0; e < _entityChannelCount; e++) {
            var ch = RegionEcologyField.FixedChannelCount + e;
            data[ch * totalChunks + idx] = _sums[ch * totalChunks + idx];
          }
        }
      }

      return new RegionEcologyField(_originX, _originY, _chunksX, _chunksY, _entityChannelCount, data, valid, sampleCounts);
    }

    #endregion

    #region Internals

    private int ChunkIndex(int cx, int cy) => cy * _chunksX + cx;

    private int Offset(int channelIndex, int cx, int cy) =>
        (channelIndex * _chunksX * _chunksY) + ChunkIndex(cx, cy);

    /// <summary>
    /// Resolve <c>(tileX, tileY)</c> to a chunk index. Returns false if
    /// the tile is outside the configured bbox -- caller's responsibility
    /// not to feed out-of-bbox tiles in the first place; this is just a
    /// final guard.
    /// </summary>
    private bool TryGetChunk(int tileX, int tileY, out int cx, out int cy) {
      cx = (tileX - _originX) / RegionEcologyField.ChunkSize;
      cy = (tileY - _originY) / RegionEcologyField.ChunkSize;
      if (cx < 0 || cx >= _chunksX || cy < 0 || cy >= _chunksY) return false;
      // The integer division above truncates toward zero, which produces
      // wrong chunk indices for negative remainders just inside the bbox.
      // Re-check the post-division position is still in-range.
      return tileX >= _originX && tileY >= _originY;
    }

    #endregion

  }

}
