using System.Collections.Generic;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Weighted-random sampling helper for the per-bucket recipe pick
  /// in spawn handlers. Pure function: takes a list of weights
  /// and a pick hash in <c>[0, 1)</c>, returns the chosen index.
  ///
  /// <para>Used by Class A/B/C handlers when a <c>(biome, level)</c>
  /// bucket activates at a tile and the handler needs to choose
  /// which recipe in the bucket fires. The hash is deterministic per
  /// <c>(tile, biome, level)</c> via <see cref="FlourishThreshold.ComputePick"/>,
  /// so the same tile always picks the same recipe across runs.</para>
  /// </summary>
  public static class WeightedPick {

    /// <summary>Choose an index in <c>[0, weights.Count)</c> with
    /// probability proportional to each weight. Returns <c>-1</c> if
    /// the weights list is empty or all weights are non-positive.</summary>
    /// <param name="weights">Per-candidate weights. Non-positive
    /// values count as zero.</param>
    /// <param name="pickHash">Uniform sample in <c>[0, 1)</c>; the
    /// caller is expected to use <see cref="FlourishThreshold.ComputePick"/>
    /// for determinism.</param>
    public static int Pick(IReadOnlyList<float> weights, float pickHash) {
      if (weights.Count == 0) return -1;
      if (weights.Count == 1) return weights[0] > 0f ? 0 : -1;
      var total = 0f;
      for (var i = 0; i < weights.Count; i++) {
        if (weights[i] > 0f) total += weights[i];
      }
      if (total <= 0f) return -1;
      var t = pickHash * total;
      var accum = 0f;
      for (var i = 0; i < weights.Count; i++) {
        if (weights[i] <= 0f) continue;
        accum += weights[i];
        if (t < accum) return i;
      }
      // Floating-point rounding fallback: return the last positive-weight index.
      for (var i = weights.Count - 1; i >= 0; i--) {
        if (weights[i] > 0f) return i;
      }
      return -1;
    }

  }

}
