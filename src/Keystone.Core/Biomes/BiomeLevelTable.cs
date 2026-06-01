using System;
using System.Collections.Generic;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Per-biome ladder of <see cref="BiomeLevel"/> entries plus
  /// lookup helpers consumed by the rule-handler tier.
  /// Populated at PostLoad by the Mod-side catalog from discovered
  /// <c>KeystoneBiomeLevelsSpec</c> instances; consumers treat it as
  /// read-only afterwards.
  ///
  /// <para><b>Gating model.</b> The table holds <i>only</i> the
  /// Maturity-driven level ladder. The orthogonal Suitability-pass +
  /// dominance question (which biome is this tile in?) is answered
  /// by <see cref="ChunkBiomeSampler.SampleDominantBiome"/> before
  /// any of these levels are consulted. Once a biome has won
  /// dominance at a tile, the rule applier walks
  /// <see cref="LevelsFor"/> and gates each level on
  /// <c>maturity ≥ LowerMaturity</c>. So the full per-tile
  /// activation check is two-stage: Suitability-pass selects the
  /// biome, then Maturity selects the levels within it.</para>
  ///
  /// <para><b>Override semantics.</b> <see cref="Define"/> applies
  /// in-order: calling it twice for the same <c>(biome, levelId)</c>
  /// pair overwrites the earlier entry's range. The catalog uses
  /// this to apply default-ladder entries first, then per-biome
  /// override entries on top. Adding levels not in the default
  /// ladder is supported; removing levels is not (a default-ladder
  /// level always carries through to every biome that doesn't
  /// override it).</para>
  ///
  /// <para><b>Cumulative levels.</b> Levels for a biome do not
  /// have to partition the maturity timeline. They typically
  /// stack: <c>L1: 0.5-1.0</c>, <c>L2: 1.0-3.0</c>, <c>L3: 3.0-10.0</c>.
  /// A chunk at maturity 2.0 has L1 at progress=1 (saturated) and
  /// L2 at progress=0.5 (ramping). Both are "active" -- their
  /// actions all fire, each at its own per-level progress.</para>
  /// </summary>
  public sealed class BiomeLevelTable {

    private readonly Dictionary<BiomeKind, List<BiomeLevel>> _byBiome = new();

    /// <summary>
    /// Add or replace a level entry for <paramref name="biome"/>.
    /// If a level with <paramref name="levelId"/> already exists
    /// for this biome, its range and density are overwritten
    /// (override semantics). Otherwise it's appended and the biome's
    /// level list is re-sorted by lower bound.
    /// </summary>
    public void Define(
        BiomeKind biome,
        string levelId,
        float lowerMaturity,
        float upperMaturity,
        float density = 0.10f,
        LevelDispatchMode mode = LevelDispatchMode.Deterministic,
        bool runAtStartup = false,
        int faunaCapacityAtSaturation = 0,
        float faunaMinScore = 0f) {
      if (string.IsNullOrEmpty(levelId)) {
        throw new ArgumentException("levelId must be non-empty.", nameof(levelId));
      }
      if (!(upperMaturity > lowerMaturity)) {
        throw new ArgumentException(
            $"upperMaturity ({upperMaturity}) must be strictly greater than " +
            $"lowerMaturity ({lowerMaturity}) for level '{levelId}' on {biome}.",
            nameof(upperMaturity));
      }
      if (lowerMaturity < 0f) {
        throw new ArgumentException(
            $"lowerMaturity must be non-negative; got {lowerMaturity} for " +
            $"level '{levelId}' on {biome}.",
            nameof(lowerMaturity));
      }
      if (density < 0f || density > 1f) {
        throw new ArgumentException(
            $"density must be in [0, 1]; got {density} for " +
            $"level '{levelId}' on {biome}.",
            nameof(density));
      }
      if (faunaCapacityAtSaturation < 0) {
        throw new ArgumentException(
            $"faunaCapacityAtSaturation must be non-negative; got {faunaCapacityAtSaturation} " +
            $"for level '{levelId}' on {biome}.",
            nameof(faunaCapacityAtSaturation));
      }
      if (faunaMinScore < 0f || faunaMinScore > 1f) {
        throw new ArgumentException(
            $"faunaMinScore must be in [0, 1]; got {faunaMinScore} " +
            $"for level '{levelId}' on {biome}.",
            nameof(faunaMinScore));
      }

      var entry = new BiomeLevel(biome, levelId, lowerMaturity, upperMaturity,
          density, mode, runAtStartup, faunaCapacityAtSaturation, faunaMinScore);

      if (!_byBiome.TryGetValue(biome, out var list)) {
        list = new List<BiomeLevel>();
        _byBiome[biome] = list;
      }
      for (var i = 0; i < list.Count; i++) {
        if (list[i].LevelId == levelId) {
          list[i] = entry;
          list.Sort(CompareByLowerBound);
          return;
        }
      }
      list.Add(entry);
      list.Sort(CompareByLowerBound);
    }

    /// <summary>Remove all entries (test setup, mod reload).</summary>
    public void Clear() => _byBiome.Clear();

    /// <summary>All level entries for <paramref name="biome"/>, sorted
    /// ascending by <see cref="BiomeLevel.LowerMaturity"/>. Returns
    /// an empty list when the biome has no levels defined.</summary>
    public IReadOnlyList<BiomeLevel> LevelsFor(BiomeKind biome) {
      if (_byBiome.TryGetValue(biome, out var list)) return list;
      return Array.Empty<BiomeLevel>();
    }

    /// <summary>The level entry with the given id for the given
    /// biome, or <c>null</c> if no such entry exists.</summary>
    public BiomeLevel? Find(BiomeKind biome, string levelId) {
      if (!_byBiome.TryGetValue(biome, out var list)) return null;
      for (var i = 0; i < list.Count; i++) {
        if (list[i].LevelId == levelId) return list[i];
      }
      return null;
    }

    /// <summary>
    /// Progress through the named level for the given maturity
    /// value. <c>0</c> when maturity is at or below the level's
    /// lower bound; <c>1</c> when at or above the upper bound;
    /// linear in between. Returns <c>0</c> if the level isn't
    /// defined for the biome.
    /// </summary>
    public float ProgressIn(BiomeKind biome, string levelId, float maturity) {
      var level = Find(biome, levelId);
      if (level == null) return 0f;
      return ProgressFor(level, maturity);
    }

    /// <summary>Same shape as <see cref="ProgressIn"/> but takes a
    /// pre-resolved <see cref="BiomeLevel"/>. Avoids the lookup
    /// when the caller is already iterating <see cref="LevelsFor"/>.</summary>
    public static float ProgressFor(BiomeLevel level, float maturity) {
      if (maturity <= level.LowerMaturity) return 0f;
      if (maturity >= level.UpperMaturity) return 1f;
      return (maturity - level.LowerMaturity) / (level.UpperMaturity - level.LowerMaturity);
    }

    /// <summary>Total number of level entries across all biomes.
    /// Diagnostic surface for catalog logging.</summary>
    public int Count {
      get {
        var n = 0;
        foreach (var kv in _byBiome) n += kv.Value.Count;
        return n;
      }
    }

    private static int CompareByLowerBound(BiomeLevel a, BiomeLevel b) =>
        a.LowerMaturity.CompareTo(b.LowerMaturity);

  }

}
