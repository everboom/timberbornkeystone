using System;
using System.Collections.Generic;
using Keystone.Core.Persistence;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Helpers for mapping <see cref="BiomeKind"/> to and from the
  /// <see cref="ChunkValueStore"/> string keys under which the biome's
  /// per-chunk channels are persisted. Two channels live in the
  /// store today:
  /// <list type="bullet">
  /// <item><c>"keystone.chunk.suitability.&lt;biome&gt;"</c> -- the
  /// short-term Suitability channel.</item>
  /// <item><c>"keystone.chunk.maturity.&lt;biome&gt;"</c> -- the
  /// long-term Maturity channel.</item>
  /// </list>
  ///
  /// <para>Both lookup tables are cached at static-ctor time so the
  /// hot ticker paths don't allocate per call.</para>
  ///
  /// <para><b>Ordinal access.</b> After <see cref="Initialize"/> has
  /// been called (once per session, at startup), the hot-path
  /// accessors <see cref="SuitabilityOrdinal"/> and
  /// <see cref="MaturityOrdinal"/> return pre-cached
  /// <see cref="ChunkValueRegistry"/> ordinals — integer indices into
  /// the parallel data layer's flat <c>float[]</c> arrays. The
  /// string-based accessors (<see cref="ForSuitability"/>,
  /// <see cref="ForMaturity"/>) remain for persistence, debug display,
  /// and the legacy <see cref="ChunkValueStore"/> sync layer.</para>
  /// </summary>
  public static class BiomeValueKinds {

    #region Cached lookups (string-based, static-ctor)

    private static readonly Dictionary<BiomeKind, string> SuitabilityByBiome;
    private static readonly Dictionary<string, BiomeKind> BiomeBySuitability;
    private static readonly Dictionary<BiomeKind, string> MaturityByBiome;
    private static readonly Dictionary<string, BiomeKind> BiomeByMaturity;

    /// <summary>The biomes that carry per-chunk Suitability/Maturity
    /// slots -- the per-chunk-scored set. <b>Not</b> the full
    /// <see cref="BiomeKind"/> enum: per-tile-only biomes
    /// (<see cref="BiomeKind.Riparian"/>) are deliberately excluded,
    /// because their suitability/maturity are sourced per-tile (the
    /// per-surface RiparianMaturity store), not from per-chunk
    /// <see cref="ChunkData"/> slots. Adding a <see cref="BiomeKind"/>
    /// does NOT auto-enrol it here -- add it only if it is per-chunk
    /// scored. <see cref="Initialize"/> sizes its ordinal arrays to this
    /// list and indexes by <c>(int)biome</c>, so a per-tile biome's
    /// ordinal access throws loudly, which is the intended guard.</summary>
    public static readonly BiomeKind[] AllBiomes = {
        BiomeKind.Forest,
        BiomeKind.Grassland,
        BiomeKind.Monoculture,
        BiomeKind.River,
        BiomeKind.Lake,
        BiomeKind.Wetland,
        BiomeKind.Cave,
        BiomeKind.Dry,
        BiomeKind.Contaminated,
        BiomeKind.Badwater,
    };

    static BiomeValueKinds() {
      SuitabilityByBiome = new Dictionary<BiomeKind, string>();
      BiomeBySuitability = new Dictionary<string, BiomeKind>();
      MaturityByBiome = new Dictionary<BiomeKind, string>();
      BiomeByMaturity = new Dictionary<string, BiomeKind>();
      foreach (var biome in AllBiomes) {
        var lower = biome.ToString().ToLowerInvariant();
        var suitabilityKey = KnownValueKinds.ChunkSuitabilityPrefix + lower;
        var maturityKey = KnownValueKinds.ChunkMaturityPrefix + lower;
        SuitabilityByBiome[biome] = suitabilityKey;
        BiomeBySuitability[suitabilityKey] = biome;
        MaturityByBiome[biome] = maturityKey;
        BiomeByMaturity[maturityKey] = biome;
      }
    }

    #endregion

    #region Ordinal access (registry-backed, session-scoped)

    private static int[]? _suitabilityOrdinals;
    private static int[]? _maturityOrdinals;

    /// <summary>Whether <see cref="Initialize"/> has been called for
    /// the current session. Reset implicitly when a new session
    /// creates a fresh <see cref="ChunkValueRegistry"/>.</summary>
    public static bool IsInitialized => _suitabilityOrdinals != null;

    /// <summary>
    /// Register all biome value kinds (suitability + maturity for each
    /// <see cref="BiomeKind"/>) in the given <paramref name="registry"/>
    /// and cache the resulting ordinals for hot-path access via
    /// <see cref="SuitabilityOrdinal"/> and <see cref="MaturityOrdinal"/>.
    ///
    /// <para>Called once per session, before the registry is frozen.
    /// Typically invoked from <c>KeystoneStartupWarmup.PostLoad</c>.</para>
    /// </summary>
    public static void Initialize(ChunkValueRegistry registry) {
      if (registry == null)
        throw new ArgumentNullException(nameof(registry));
      _suitabilityOrdinals = new int[AllBiomes.Length];
      _maturityOrdinals = new int[AllBiomes.Length];
      for (var i = 0; i < AllBiomes.Length; i++) {
        var biome = AllBiomes[i];
        _suitabilityOrdinals[(int)biome] =
            registry.Register(ForSuitability(biome), ChunkValueRole.Suitability);
        _maturityOrdinals[(int)biome] =
            registry.Register(ForMaturity(biome), ChunkValueRole.Maturity);
      }
    }

    /// <summary>
    /// Reset the ordinal cache. Called between sessions (or from tests)
    /// so a stale ordinal set from a previous session doesn't leak into
    /// a new registry.
    /// </summary>
    public static void ResetOrdinals() {
      _suitabilityOrdinals = null;
      _maturityOrdinals = null;
    }

    /// <summary>
    /// The <see cref="ChunkValueRegistry"/> ordinal for the given
    /// biome's per-chunk Suitability slot. Hot-path accessor — no
    /// hashing, no dictionary lookup.
    /// </summary>
    public static int SuitabilityOrdinal(BiomeKind biome) {
      if (_suitabilityOrdinals == null)
        throw new InvalidOperationException(
            "BiomeValueKinds.SuitabilityOrdinal called before Initialize.");
      return _suitabilityOrdinals[(int)biome];
    }

    /// <summary>
    /// The <see cref="ChunkValueRegistry"/> ordinal for the given
    /// biome's per-chunk Maturity slot. Hot-path accessor — no
    /// hashing, no dictionary lookup.
    /// </summary>
    public static int MaturityOrdinal(BiomeKind biome) {
      if (_maturityOrdinals == null)
        throw new InvalidOperationException(
            "BiomeValueKinds.MaturityOrdinal called before Initialize.");
      return _maturityOrdinals[(int)biome];
    }

    /// <summary>
    /// Recompute <see cref="ChunkData.TopBiomes"/> from the chunk's
    /// current Suitability slots. Must be called after <i>any</i>
    /// write to those slots that doesn't go through
    /// <see cref="BiomeSuitabilityUpdater.Tick"/> (which maintains the
    /// cache itself) — the seed/rehydrate path in
    /// <c>ChunkBiomeTicker</c> is the load-bearing example.
    ///
    /// <para>Stale top-3 silently produces wrong dominance answers:
    /// <see cref="ChunkBiomeSampler.SampleDominantBiome"/> shrinks
    /// its candidate set using these ordinals, so an empty cache
    /// (all <c>-1</c>) makes the sampler return <c>(null, 0)</c> for
    /// every tile in the chunk until the next
    /// <see cref="BiomeSuitabilityUpdater.Tick"/> rewrites it.
    /// Downstream effects of that include Class A reconcilers that
    /// see "no dominant biome anywhere" and rip out content that's
    /// actually still valid.</para>
    ///
    /// <para><b>Tie ordering.</b> Iterates
    /// <see cref="ChunkBiomeSampler.BiomesByAggressorTier"/> (not
    /// <see cref="AllBiomes"/>) with strict <c>&gt;</c>, so on a Suitability
    /// tie the aggressor-tier-earlier biome is kept. This MUST match the
    /// argmax tiebreak in <see cref="ChunkBiomeSampler.SampleDominantBiome"/>:
    /// the sampler shrinks its candidate set to the union of corners' top-3,
    /// so a biome evicted from the top-3 here can never win the per-tile
    /// argmax. Iterating enum (<see cref="AllBiomes"/>) order instead would
    /// drop Badwater — last in the enum — from the top-3 on a fully-toxic
    /// chunk where ≥3 biomes co-saturate to 1, silently handing per-tile
    /// dominance to Contaminated and contradicting the per-chunk
    /// <see cref="ChunkBiomeSampler.DominantAtChunk"/> (which scans all 10).</para>
    /// <para>Cost: 10 array reads + ≤30 compares. Cheap enough to
    /// call unconditionally from any seed/rehydrate path.</para>
    /// </summary>
    public static void RecomputeTopBiomes(ChunkData data) {
      if (data == null) throw new ArgumentNullException(nameof(data));
      var values = data.Values;
      int top0 = -1, top1 = -1, top2 = -1;
      float v0 = 0f, v1 = 0f, v2 = 0f;
      foreach (var biome in ChunkBiomeSampler.BiomesByAggressorTier) {
        var value = values[SuitabilityOrdinal(biome)];
        if (value <= 0f) continue;
        var ord = (int)biome;
        if (value > v0) {
          v2 = v1; top2 = top1;
          v1 = v0; top1 = top0;
          v0 = value; top0 = ord;
        } else if (value > v1) {
          v2 = v1; top2 = top1;
          v1 = value; top1 = ord;
        } else if (value > v2) {
          v2 = value; top2 = ord;
        }
      }
      data.SetTopBiomes(top0, top1, top2);
    }

    #endregion

    #region Suitability channel (string-based)

    /// <summary>The <see cref="ChunkValueStore"/> key for the given
    /// biome's per-chunk Suitability (short-term, hour-scale, clamped
    /// <c>[0, 1]</c>).</summary>
    public static string ForSuitability(BiomeKind biome) => SuitabilityByBiome[biome];

    /// <summary>If <paramref name="kind"/> is a biome Suitability key,
    /// returns the corresponding <see cref="BiomeKind"/> via
    /// <paramref name="biome"/> and <c>true</c>. Otherwise
    /// <c>false</c>.</summary>
    public static bool TryParseSuitability(string kind, out BiomeKind biome) {
      return BiomeBySuitability.TryGetValue(kind, out biome);
    }

    #endregion

    #region Maturity channel (string-based)

    /// <summary>The <see cref="ChunkValueStore"/> key for the given
    /// biome's per-chunk Maturity value (long-term, day-scale,
    /// integrates the Suitability channel with asymmetric rise / decay;
    /// in units of in-game days).</summary>
    public static string ForMaturity(BiomeKind biome) => MaturityByBiome[biome];

    /// <summary>If <paramref name="kind"/> is a biome Maturity key,
    /// returns the corresponding <see cref="BiomeKind"/> via
    /// <paramref name="biome"/> and <c>true</c>. Otherwise
    /// <c>false</c>.</summary>
    public static bool TryParseMaturity(string kind, out BiomeKind biome) {
      return BiomeByMaturity.TryGetValue(kind, out biome);
    }

    #endregion

  }

}
