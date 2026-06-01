using System;
using System.Collections.Generic;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Bilinear-interpolated per-tile read of biome channel values via
  /// <see cref="IChunkBiomeValues"/>. Two channels surface today: the
  /// short-term Suitability (<see cref="SampleSuitability"/>) and the
  /// long-term Maturity (<see cref="SampleMaturity"/>). Both share
  /// the same chunk-grid sampling math; they differ only in which
  /// channel they read.
  ///
  /// <para>On top of the per-biome single-channel readers, the
  /// <see cref="SampleDominantBiome"/> entry point answers "which
  /// biome owns this tile right now, and how much Maturity has
  /// it accrued?" via a Suitability-pass gate + max-Suitability
  /// tiebreak. That is the read the rule applier and dominance-gated
  /// visual effects (mist, atmosphere) use.</para>
  ///
  /// <para>Mirrors the shape of <see cref="RegionEcologyField.Sample"/>:
  /// chunk centres are treated as samples on a regular lattice, the
  /// four-corner stencil is edge-clamped, and corners that aren't
  /// represented in the store are dropped from the stencil with
  /// weights renormalised over the remaining ones.</para>
  ///
  /// <para><b>Why bilinear at all.</b> The biome value grid is
  /// coarse (one value per 4x4 chunk). Spawn decisions (Class B
  /// flourishes, Class A markers, ...) are per-tile, so the read
  /// has to smooth across chunk boundaries -- otherwise the player
  /// sees sharp edges along the grid every time a chunk's value
  /// crosses a recipe's threshold.</para>
  ///
  /// <para><b>Layout inputs.</b> The caller supplies the same
  /// metadata that defines a region's
  /// <see cref="RegionEcologyField"/>: the bbox origin (in tile
  /// units, matching <see cref="RegionEcologyField.OriginX"/> /
  /// <see cref="RegionEcologyField.OriginY"/>) and the chunk-grid
  /// extent. Origin is converted to global chunk coordinates here
  /// so the store lookup matches what the ticker writes.</para>
  /// </summary>
  public static class ChunkBiomeSampler {

    /// <summary>Biomes ordered by aggressor tier, most aggressive
    /// first. Used by the dominance argmax in <see cref="SampleDominantBiome"/>
    /// and <see cref="DominantAtChunk"/>: when two biomes tie at the
    /// same Suitability, the one earlier in this list wins (strict
    /// <c>&gt;</c> on the iteration). Tiers mirror the column-default
    /// structure of <see cref="MaturityParameters.DecayHalfLife"/>;
    /// the load-bearing tie is Badwater over Contaminated on a fully-
    /// badwater chunk (both targets saturate to 1 since Contaminated
    /// covers the underlying contamination too).
    /// <para><b>Exposed for downstream consumers</b> that need to
    /// compute dominance over <i>all</i> biomes (e.g.
    /// <see cref="Keystone.Core.Ecology.Clusters.ChunkClusterIndex"/>
    /// needs the true dominant biome -- including non-clusterable ones
    /// like Monoculture / Dry -- to decide whether a chunk's actual
    /// dominance is in its clusterable whitelist). Read-only by
    /// convention; callers must not mutate.</para></summary>
    public static readonly IReadOnlyList<BiomeKind> BiomesByAggressorTier = new[] {
        BiomeKind.Badwater,
        BiomeKind.Contaminated,
        BiomeKind.Dry,
        BiomeKind.River,
        BiomeKind.Wetland,
        BiomeKind.Lake,
        BiomeKind.Forest,
        BiomeKind.Grassland,
        BiomeKind.Monoculture,
        BiomeKind.Cave,
    };

    /// <summary>
    /// Bilinearly sample <paramref name="biome"/>'s Suitability at
    /// tile position (<paramref name="tileX"/>, <paramref name="tileY"/>)
    /// from the per-region chunk grid layout described by the other
    /// parameters.
    ///
    /// <para>Returns 0 when no surrounding chunk has a Suitability
    /// record in <paramref name="store"/>. Out-of-bbox tile positions
    /// are edge-clamped to the nearest in-bbox chunk centre, matching
    /// <see cref="RegionEcologyField.Sample"/>'s behaviour.</para>
    /// </summary>
    /// <param name="values">Biome value accessor.</param>
    /// <param name="regionId">Region whose chunks to read.</param>
    /// <param name="biome">Which biome's Suitability to sample.</param>
    /// <param name="originTileX">Region bbox origin X in tile units. Multiple of <c>ChunkSize</c>.</param>
    /// <param name="originTileY">Region bbox origin Y in tile units. Multiple of <c>ChunkSize</c>.</param>
    /// <param name="chunksX">Chunk-grid width.</param>
    /// <param name="chunksY">Chunk-grid height.</param>
    /// <param name="tileX">Tile X to sample at (continuous; can be sub-tile).</param>
    /// <param name="tileY">Tile Y to sample at.</param>
    public static float SampleSuitability(
        IChunkBiomeValues values,
        RegionId regionId,
        BiomeKind biome,
        int originTileX,
        int originTileY,
        int chunksX,
        int chunksY,
        float tileX,
        float tileY) {
      return SampleChannel(values, regionId, biome, true,
          originTileX, originTileY, chunksX, chunksY, tileX, tileY);
    }

    /// <summary>
    /// Bilinearly sample <paramref name="biome"/>'s Maturity value
    /// (in game-days) at tile position
    /// (<paramref name="tileX"/>, <paramref name="tileY"/>). Same
    /// sampling math as <see cref="SampleSuitability"/>, just reading
    /// the Maturity-prefix kind instead of the Suitability-prefix kind.
    /// </summary>
    public static float SampleMaturity(
        IChunkBiomeValues values,
        RegionId regionId,
        BiomeKind biome,
        int originTileX,
        int originTileY,
        int chunksX,
        int chunksY,
        float tileX,
        float tileY) {
      return SampleChannel(values, regionId, biome, false,
          originTileX, originTileY, chunksX, chunksY, tileX, tileY);
    }

    /// <summary>
    /// Pick the chunk's "dominant" biome at the given tile position
    /// as the biome with the highest bilinearly-sampled Suitability,
    /// and return it together with that biome's Maturity value.
    ///
    /// <para><b>Selection rule.</b>
    /// <list type="number">
    ///   <item>Bilinearly sample each <see cref="BiomeKind"/>'s
    ///         <i>Suitability</i> at the tile.</item>
    ///   <item>Pick the biome with the highest Suitability (tiebreak:
    ///         aggressor tier -- Badwater beats Contaminated on a
    ///         fully-badwater chunk, since the latter's Suitability
    ///         stacks to the same value).</item>
    ///   <item>Sample the winner's Maturity once and return
    ///         <c>(winner, winnerMaturity)</c>. Maturity drives which
    ///         of the winner's levels are active downstream -- a
    ///         freshly-dominant biome with Maturity=0 wins dominance
    ///         but fires no level rules.</item>
    /// </list>
    /// Returns <c>(null, 0)</c> when no biome has positive Suitability
    /// at this tile.</para>
    ///
    /// <para><b>Suitability ranks, Maturity gates levels.</b> The two
    /// channels play different roles. Suitability is short-term,
    /// hour-scale, <c>[0, 1]</c> -- it answers "how well do current
    /// conditions match this biome here?" The dominance vote is the
    /// *only* place that question is asked: a biome whose Suitability
    /// is lower than another's is invisible to dominance no matter how
    /// much Maturity it has accumulated. Maturity is long-term,
    /// day-scale
    /// -- it answers "how stable has this state been?" Used downstream
    /// by <c>BiomeLevelTable</c> to gate level activation
    /// (Maturity ≥ LowerMaturity per level).</para>
    ///
    /// <para><b>Old-forest-floods example.</b> A chunk with Forest
    /// Maturity=30 days that floods drops Forest's Suitability
    /// (drought stress lifted, but moisture and tree-density
    /// predicates collapse). Forest's Suitability falls below the
    /// gate; Forest stops winning dominance even though its Maturity
    /// is still high. Wetland's Suitability rises and passes; Wetland
    /// wins with Maturity≈0, so Wetland L1's spawn rules are eligible
    /// only once Wetland Maturity accrues. No Forest content fires in
    /// the meantime. When the flood recedes, Forest's Suitability
    /// recovers over hours, Forest wins dominance again, and its
    /// accumulated Maturity immediately re-activates its full level
    /// ladder.</para>
    ///
    /// <para><b>Per-tile, not per-chunk.</b> Called once per tile from
    /// the rule applier and visual gates. Per-chunk caching of "this
    /// chunk's dominant biome" would be cheaper but throws away the
    /// bilinear smoothing the suitability field exists to provide --
    /// adjacent chunks with different dominants carve 4-tile hard
    /// edges along the chunk grid, visible as jagged biome boundaries.</para>
    ///
    /// <para><b>Cost.</b> 4 dict lookups (one per corner
    /// <see cref="ChunkData"/>) + bilinear interpolation over the
    /// union of the 4 corners' top-3 biomes — typically 1–4
    /// candidates × 4 array reads each, plus 4 array reads for the
    /// winner's Maturity. The legacy "iterate all 10 biomes × 4
    /// corner dispatches" path (44 dict lookups) is preserved as a
    /// fallback for <see cref="IChunkBiomeValues"/> implementations
    /// that can't expose <see cref="ChunkData"/> directly.</para>
    ///
    /// <para><b>Near-tie stability.</b> Tiles where two biomes'
    /// Suitabilities are close to each other can flap dominance
    /// between cycles when one crosses the other. The bilinear
    /// smoothing softens this by averaging four chunk reads but
    /// doesn't eliminate it. Handlers tolerate flapping: Class A
    /// reconciles every cycle (visuals adjust naturally) and Class
    /// B/C/D persist their spawned entities (a flap doesn't unspawn
    /// anything; new spawns just pause until the flipped tile settles
    /// back).</para>
    ///
    /// <para><b>Per-tile Riparian.</b> Riparian is folded into this same
    /// argmax through the optional <c>riparianSuitability</c> (its
    /// clean-near-water indicator, on the same <c>[0,1]</c> scale as the
    /// per-chunk suitabilities) and <c>riparianMaturity</c> (its per-tile
    /// R). When riparian wins, the returned Maturity is
    /// <c>riparianMaturity</c>, not a per-chunk value -- the winner
    /// brings its own maturity from its own store. On a suitability tie
    /// riparian out-ranks Grassland/Forest/Dry/Monoculture/Cave (it
    /// claims the clean near-water margin from them) and loses to the
    /// water family and the toxics. Callers without per-tile data pass 0
    /// (the default), which keeps riparian out of the argmax entirely --
    /// the per-chunk dominance functions (<see cref="DominantAtChunk"/>
    /// et al.) never pass it, so they stay riparian-free.</para>
    /// </summary>
    public static (BiomeKind? Biome, float Maturity) SampleDominantBiome(
        IChunkBiomeValues values,
        RegionId regionId,
        int originTileX,
        int originTileY,
        int chunksX,
        int chunksY,
        float tileX,
        float tileY,
        float riparianSuitability = 0f,
        float riparianMaturity = 0f) {

      const int chunkSize = RegionEcologyField.ChunkSize;
      const float chunkCentreOffset = (chunkSize - 1) * 0.5f;

      // Bilinear stencil setup (same math as SampleChannel).
      var u = (tileX - originTileX - chunkCentreOffset) / chunkSize;
      var v = (tileY - originTileY - chunkCentreOffset) / chunkSize;
      if (u < 0f) u = 0f;
      if (u > chunksX - 1) u = chunksX - 1;
      if (v < 0f) v = 0f;
      if (v > chunksY - 1) v = chunksY - 1;

      var cxLo = (int)u;
      var cyLo = (int)v;
      var cxHi = cxLo + 1; if (cxHi >= chunksX) cxHi = chunksX - 1;
      var cyHi = cyLo + 1; if (cyHi >= chunksY) cyHi = chunksY - 1;
      var tx = u - cxLo;
      var ty = v - cyLo;

      var w00 = (1f - tx) * (1f - ty);
      var w10 = tx * (1f - ty);
      var w01 = (1f - tx) * ty;
      var w11 = tx * ty;

      var gcx0 = originTileX / chunkSize;
      var gcy0 = originTileY / chunkSize;

      // Hoist the 4 corner ChunkData refs once (Layer 1: 44 dict
      // lookups -> 4). When the implementation can't expose
      // ChunkData (string-keyed reader, test fakes), all refs come
      // back null and we fall through to the legacy per-read path
      // so behavior is preserved end-to-end.
      var d00 = values.GetChunkData(regionId, gcx0 + cxLo, gcy0 + cyLo);
      var d10 = values.GetChunkData(regionId, gcx0 + cxHi, gcy0 + cyLo);
      var d01 = values.GetChunkData(regionId, gcx0 + cxLo, gcy0 + cyHi);
      var d11 = values.GetChunkData(regionId, gcx0 + cxHi, gcy0 + cyHi);

      if (d00 == null && d10 == null && d01 == null && d11 == null) {
        return SampleDominantBiomeViaPerReadFallback(
            values, regionId,
            originTileX, originTileY, chunksX, chunksY, tileX, tileY,
            riparianSuitability, riparianMaturity);
      }

      // Union the per-corner top-3 biomes into a 10-slot bitmask.
      // The candidate set for the argmax is "any biome that ranks in
      // any corner's top-3" — typically 1–4 entries in real terrain,
      // worst-case 10. Iterating BiomesByAggressorTier afterward
      // preserves the existing tiebreak (Badwater > Contaminated on
      // a fully-badwater chunk, etc.).
      Span<bool> seen = stackalloc bool[10];
      MarkTopBiomes(seen, d00);
      MarkTopBiomes(seen, d10);
      MarkTopBiomes(seen, d01);
      MarkTopBiomes(seen, d11);

      BiomeKind? best = null;
      var bestSuitability = 0f;
      for (var i = 0; i < BiomesByAggressorTier.Count; i++) {
        var biome = BiomesByAggressorTier[i];
        if (!seen[(int)biome]) continue;
        var suitOrd = BiomeValueKinds.SuitabilityOrdinal(biome);
        var suit = BilinearFromCorners(
            d00, d10, d01, d11, w00, w10, w01, w11, suitOrd);
        if (suit > bestSuitability) {
          bestSuitability = suit;
          best = biome;
        }
      }
      if (best == null) {
        return FoldRiparian(null, 0f, 0f, riparianSuitability, riparianMaturity);
      }

      var matOrd = BiomeValueKinds.MaturityOrdinal(best.Value);
      var maturity = BilinearFromCorners(
          d00, d10, d01, d11, w00, w10, w01, w11, matOrd);
      return FoldRiparian(best, bestSuitability, maturity, riparianSuitability, riparianMaturity);
    }

    /// <summary>Walk a corner's top-3 biome ordinals and flip the
    /// corresponding <paramref name="seen"/> bits. <c>-1</c> in the
    /// top-3 array marks "no biome at this rank" and ends the walk.
    /// Null <paramref name="data"/> (missing chunk entry) contributes
    /// no candidates -- the bilinear sum will skip the same corner
    /// downstream by zeroing its weight.</summary>
    private static void MarkTopBiomes(Span<bool> seen, ChunkData? data) {
      if (data == null) return;
      var top = data.TopBiomes;
      for (var i = 0; i < top.Length; i++) {
        var ord = top[i];
        if (ord < 0) break;
        seen[ord] = true;
      }
    }

    /// <summary>Bilinear interpolation of one slot value across four
    /// corner <see cref="ChunkData"/> refs, with edge-clamping handled
    /// by skipping null corners and renormalising weights over the
    /// remaining ones. Matches <see cref="SampleChannel"/>'s null-
    /// renorm contract; only the access path changes from
    /// "<see cref="IChunkBiomeValues.GetSuitability"/> /
    /// <see cref="IChunkBiomeValues.GetMaturity"/> dispatch" to
    /// "direct array read by ordinal".</summary>
    private static float BilinearFromCorners(
        ChunkData? d00, ChunkData? d10, ChunkData? d01, ChunkData? d11,
        float w00, float w10, float w01, float w11,
        int slotOrdinal) {
      var sumW = 0f;
      if (d00 != null) sumW += w00;
      if (d10 != null) sumW += w10;
      if (d01 != null) sumW += w01;
      if (d11 != null) sumW += w11;
      if (sumW <= 0f) return 0f;

      var result = 0f;
      if (d00 != null) result += w00 * d00.Get(slotOrdinal);
      if (d10 != null) result += w10 * d10.Get(slotOrdinal);
      if (d01 != null) result += w01 * d01.Get(slotOrdinal);
      if (d11 != null) result += w11 * d11.Get(slotOrdinal);
      return result / sumW;
    }

    /// <summary>Slow-path fallback for <see cref="IChunkBiomeValues"/>
    /// implementations that don't expose <see cref="ChunkData"/>
    /// (string-keyed reader, test fakes). Iterates all biomes via
    /// <see cref="SampleChannel"/>'s per-read dispatch — same shape
    /// the sampler used before the top-3 optimisation landed, kept
    /// here as the contract-preserving fallback rather than the
    /// default path.</summary>
    private static (BiomeKind? Biome, float Maturity) SampleDominantBiomeViaPerReadFallback(
        IChunkBiomeValues values,
        RegionId regionId,
        int originTileX,
        int originTileY,
        int chunksX,
        int chunksY,
        float tileX,
        float tileY,
        float riparianSuitability,
        float riparianMaturity) {
      BiomeKind? best = null;
      var bestSuitability = 0f;
      for (var i = 0; i < BiomesByAggressorTier.Count; i++) {
        var biome = BiomesByAggressorTier[i];
        var suitability = SampleChannel(values, regionId, biome, true,
            originTileX, originTileY, chunksX, chunksY, tileX, tileY);
        if (suitability > bestSuitability) {
          bestSuitability = suitability;
          best = biome;
        }
      }
      if (best == null) {
        return FoldRiparian(null, 0f, 0f, riparianSuitability, riparianMaturity);
      }
      var maturity = SampleChannel(values, regionId, best.Value, false,
          originTileX, originTileY, chunksX, chunksY, tileX, tileY);
      return FoldRiparian(best, bestSuitability, maturity, riparianSuitability, riparianMaturity);
    }

    /// <summary>
    /// Maturity-argmax counterpart to <see cref="SampleDominantBiome"/>:
    /// pick the biome with the highest bilinearly-sampled <i>Maturity</i> at
    /// the tile (ignoring Suitability entirely), and return it with that
    /// Maturity. Answers "which biome has actually established here?" — the
    /// long-term, slow-moving signal — as opposed to
    /// <see cref="SampleDominantBiome"/>'s "which biome do current conditions
    /// favour?".
    ///
    /// <para><b>Candidate set is all biomes, not the suitability top-3.</b>
    /// Unlike <see cref="SampleDominantBiome"/>, this cannot shrink its scan
    /// to <see cref="ChunkData.TopBiomes"/>: that cache ranks by Suitability,
    /// and the maturity winner is frequently a biome with <i>low</i> current
    /// Suitability (an old forest that just flooded still holds its Maturity
    /// while its Suitability has collapsed). So it scans every biome.</para>
    ///
    /// <para><b>Riparian.</b> Folded in by its per-tile Maturity
    /// (<paramref name="riparianMaturity"/>) rather than its suitability — a
    /// tile that accrued riparian R wins as Riparian here even after its
    /// near-water suitability lapsed, which is exactly the persistence
    /// riparian Maturity exists to express. Per-chunk biomes win an exact
    /// Maturity tie (strict <c>&gt;</c>); ties are measure-zero on floats.</para>
    ///
    /// <para>Returns <c>(null, 0)</c> when no biome (and not Riparian) has
    /// positive Maturity at this tile.</para>
    /// </summary>
    public static (BiomeKind? Biome, float Maturity) SampleDominantByMaturity(
        IChunkBiomeValues values,
        RegionId regionId,
        int originTileX,
        int originTileY,
        int chunksX,
        int chunksY,
        float tileX,
        float tileY,
        float riparianMaturity = 0f) {

      const int chunkSize = RegionEcologyField.ChunkSize;
      const float chunkCentreOffset = (chunkSize - 1) * 0.5f;

      var u = (tileX - originTileX - chunkCentreOffset) / chunkSize;
      var v = (tileY - originTileY - chunkCentreOffset) / chunkSize;
      if (u < 0f) u = 0f;
      if (u > chunksX - 1) u = chunksX - 1;
      if (v < 0f) v = 0f;
      if (v > chunksY - 1) v = chunksY - 1;

      var cxLo = (int)u;
      var cyLo = (int)v;
      var cxHi = cxLo + 1; if (cxHi >= chunksX) cxHi = chunksX - 1;
      var cyHi = cyLo + 1; if (cyHi >= chunksY) cyHi = chunksY - 1;
      var tx = u - cxLo;
      var ty = v - cyLo;

      var w00 = (1f - tx) * (1f - ty);
      var w10 = tx * (1f - ty);
      var w01 = (1f - tx) * ty;
      var w11 = tx * ty;

      var gcx0 = originTileX / chunkSize;
      var gcy0 = originTileY / chunkSize;

      var d00 = values.GetChunkData(regionId, gcx0 + cxLo, gcy0 + cyLo);
      var d10 = values.GetChunkData(regionId, gcx0 + cxHi, gcy0 + cyLo);
      var d01 = values.GetChunkData(regionId, gcx0 + cxLo, gcy0 + cyHi);
      var d11 = values.GetChunkData(regionId, gcx0 + cxHi, gcy0 + cyHi);

      BiomeKind? best = null;
      var bestMaturity = 0f;
      if (d00 == null && d10 == null && d01 == null && d11 == null) {
        // Per-read fallback (string-keyed reader / test fakes) — same
        // contract as SampleDominantBiome's fallback, reading the Maturity
        // channel instead of Suitability.
        for (var i = 0; i < BiomesByAggressorTier.Count; i++) {
          var biome = BiomesByAggressorTier[i];
          var m = SampleChannel(values, regionId, biome, false,
              originTileX, originTileY, chunksX, chunksY, tileX, tileY);
          if (m > bestMaturity) { bestMaturity = m; best = biome; }
        }
      } else {
        for (var i = 0; i < BiomesByAggressorTier.Count; i++) {
          var biome = BiomesByAggressorTier[i];
          var matOrd = BiomeValueKinds.MaturityOrdinal(biome);
          var m = BilinearFromCorners(
              d00, d10, d01, d11, w00, w10, w01, w11, matOrd);
          if (m > bestMaturity) { bestMaturity = m; best = biome; }
        }
      }

      if (riparianMaturity > bestMaturity) {
        return (BiomeKind.Riparian, riparianMaturity);
      }
      return (best, bestMaturity);
    }

    /// <summary>
    /// Fold per-tile Riparian into a per-chunk dominance result. Riparian
    /// competes as a <c>[0,1]</c> suitability term against the per-chunk
    /// winner: higher suitability wins; on a tie, <see cref="RiparianOutranks"/>
    /// decides. When riparian wins it carries its own per-tile maturity
    /// (<paramref name="riparianMaturity"/>); otherwise the per-chunk
    /// winner and its maturity pass through unchanged. A non-positive
    /// <paramref name="riparianSuitability"/> means "riparian doesn't
    /// qualify / no per-tile data" and is a no-op.
    /// </summary>
    private static (BiomeKind? Biome, float Maturity) FoldRiparian(
        BiomeKind? perChunkBest, float perChunkBestSuitability, float perChunkMaturity,
        float riparianSuitability, float riparianMaturity) {
      if (riparianSuitability > 0f
          && (perChunkBest == null
              || riparianSuitability > perChunkBestSuitability
              || (riparianSuitability == perChunkBestSuitability
                  && RiparianOutranks(perChunkBest.Value)))) {
        return (BiomeKind.Riparian, riparianMaturity);
      }
      return (perChunkBest, perChunkMaturity);
    }

    /// <summary>Whether per-tile Riparian out-ranks <paramref name="biome"/>
    /// on a suitability tie. Riparian sits above the healthy land biomes
    /// (it claims the clean near-water margin from them) and below the
    /// water family and the toxics. Note riparian's suitability is the
    /// clean-near-water indicator, so it is 0 on water/toxic tiles
    /// anyway -- this tie rule is the belt-and-braces ordering for the
    /// rare exact tie, not the primary mechanism.</summary>
    private static bool RiparianOutranks(BiomeKind biome) => biome switch {
        BiomeKind.Grassland or BiomeKind.Forest or BiomeKind.Dry
            or BiomeKind.Monoculture or BiomeKind.Cave => true,
        _ => false,
    };

    /// <summary>Chunk-resolution counterpart to <see cref="SampleDominantBiome"/>:
    /// argmax over per-chunk Suitability values (no bilinear smoothing),
    /// breaking ties by aggressor tier so Badwater wins over Contaminated
    /// when both saturate to the same value on a fully-badwater chunk.
    /// Returns <c>null</c> when no biome has positive Suitability on
    /// the chunk (degenerate empty-chunk state).
    ///
    /// <para>Used by <see cref="BiomeMaturityUpdater.Tick"/> to pick
    /// the dominant biome the (decaying, dominant) decay matrix
    /// indexes into. Per-tile bilinear dominance is overkill here --
    /// Maturity is integrated per chunk, so a single chunk-level
    /// reading suffices and saves a tile-loop.</para></summary>
    public static BiomeKind? DominantAtChunk(
        IChunkBiomeValues values,
        RegionId regionId,
        int chunkX,
        int chunkY) {
      BiomeKind? best = null;
      var bestSuitability = 0f;
      for (var i = 0; i < BiomesByAggressorTier.Count; i++) {
        var biome = BiomesByAggressorTier[i];
        var s = values.GetSuitability(regionId, chunkX, chunkY, biome) ?? 0f;
        if (s > bestSuitability) {
          bestSuitability = s;
          best = biome;
        }
      }
      return best;
    }

    /// <summary>Argmax over a chunk's per-biome <b>Maturity</b>
    /// values across the given candidate biomes. Returns <c>null</c>
    /// when no candidate has positive Maturity on the chunk.
    ///
    /// <para><b>Why a Maturity-based dominance.</b> Suitability is
    /// the short-term, hour-scale signal — it can flip instantly
    /// when water sim / weather / contamination shifts. Cluster
    /// identity needs a stable signal: a chunk that's been Wetland
    /// for ten game-days shouldn't lose its cluster membership the
    /// moment a Suitability transient elevates a different biome
    /// for a few ticks. Maturity is integrated, slow-rising,
    /// slow-decaying — exactly the property the cluster index wants
    /// for stable identity.</para>
    ///
    /// <para>Ties: when two biomes have identical Maturity, the
    /// candidate listed earlier in <paramref name="candidates"/>
    /// wins (strict <c>&gt;</c>). The
    /// <see cref="ChunkClusterIndex.ClusterableBiomes"/> order is
    /// the conventional pass — pick a deterministic but otherwise
    /// arbitrary tiebreak.</para></summary>
    public static (BiomeKind? Biome, float Maturity) DominantByMaturityAtChunk(
        IChunkBiomeValues values,
        RegionId regionId,
        int chunkX,
        int chunkY,
        IReadOnlyList<BiomeKind> candidates) {
      BiomeKind? best = null;
      var bestMaturity = 0f;
      for (var i = 0; i < candidates.Count; i++) {
        var biome = candidates[i];
        var m = values.GetMaturity(regionId, chunkX, chunkY, biome) ?? 0f;
        if (m > bestMaturity) {
          bestMaturity = m;
          best = biome;
        }
      }
      return (best, bestMaturity);
    }

    /// <summary>Shared bilinear sampler for one biome channel. The
    /// <paramref name="suitability"/> flag selects between the
    /// Suitability and Maturity channels on
    /// <paramref name="values"/>.</summary>
    private static float SampleChannel(
        IChunkBiomeValues values,
        RegionId regionId,
        BiomeKind biome,
        bool suitability,
        int originTileX,
        int originTileY,
        int chunksX,
        int chunksY,
        float tileX,
        float tileY) {

      const int chunkSize = RegionEcologyField.ChunkSize;
      const float chunkCentreOffset = (chunkSize - 1) * 0.5f;  // 1.5 for ChunkSize=4

      // u, v are positions in chunk-centre space: u=0 at the first
      // chunk's centre, u=chunksX-1 at the last chunk's centre.
      var u = (tileX - originTileX - chunkCentreOffset) / chunkSize;
      var v = (tileY - originTileY - chunkCentreOffset) / chunkSize;
      if (u < 0f) u = 0f;
      if (u > chunksX - 1) u = chunksX - 1;
      if (v < 0f) v = 0f;
      if (v > chunksY - 1) v = chunksY - 1;

      var cxLo = (int)u;
      var cyLo = (int)v;
      var cxHi = cxLo + 1; if (cxHi >= chunksX) cxHi = chunksX - 1;
      var cyHi = cyLo + 1; if (cyHi >= chunksY) cyHi = chunksY - 1;
      var tx = u - cxLo;
      var ty = v - cyLo;

      var w00 = (1f - tx) * (1f - ty);
      var w10 = tx * (1f - ty);
      var w01 = (1f - tx) * ty;
      var w11 = tx * ty;

      var globalChunkX0 = originTileX / chunkSize;
      var globalChunkY0 = originTileY / chunkSize;

      var s00 = ReadChannel(values, regionId, globalChunkX0 + cxLo, globalChunkY0 + cyLo, biome, suitability);
      var s10 = ReadChannel(values, regionId, globalChunkX0 + cxHi, globalChunkY0 + cyLo, biome, suitability);
      var s01 = ReadChannel(values, regionId, globalChunkX0 + cxLo, globalChunkY0 + cyHi, biome, suitability);
      var s11 = ReadChannel(values, regionId, globalChunkX0 + cxHi, globalChunkY0 + cyHi, biome, suitability);

      var sumW = 0f;
      if (s00 != null) sumW += w00;
      if (s10 != null) sumW += w10;
      if (s01 != null) sumW += w01;
      if (s11 != null) sumW += w11;
      if (sumW <= 0f) return 0f;

      var result = 0f;
      if (s00 != null) result += w00 * s00.Value;
      if (s10 != null) result += w10 * s10.Value;
      if (s01 != null) result += w01 * s01.Value;
      if (s11 != null) result += w11 * s11.Value;
      return result / sumW;
    }

    private static float? ReadChannel(
        IChunkBiomeValues values, RegionId regionId, int chunkX, int chunkY,
        BiomeKind biome, bool suitability) {
      return suitability
          ? values.GetSuitability(regionId, chunkX, chunkY, biome)
          : values.GetMaturity(regionId, chunkX, chunkY, biome);
    }

  }

}
