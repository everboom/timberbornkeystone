using System;
using Keystone.Core.Persistence;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Advances a chunk's per-biome Maturity values by integrating the
  /// long-term channel. Reads Suitability and Maturity from the
  /// chunk's <see cref="ChunkData"/> array; constants come from
  /// <see cref="MaturityParameters"/>.
  ///
  /// <para><b>Hybrid model -- exponential accrue, linear decay.</b>
  /// The slow-mode asymptote at the current Suitability is
  /// <c>(Alpha * Suitability) / BetaAccrue</c> = <c>Suitability *
  /// Ceiling(biome)</c>. The mode branches on whether Maturity is at
  /// or below this asymptote:</para>
  /// <list type="bullet">
  /// <item><b>Accrue.</b> <c>dM/dt = Alpha * Suitability - BetaAccrue
  /// * Maturity</c>. Exponential approach toward the asymptote; the
  /// rise slows as Maturity nears its ceiling, which is the "growth
  /// keeps feeling ongoing" feel.</item>
  /// <item><b>Decay.</b> <c>dM/dt = -rate</c> where <c>rate</c> is
  /// linear (units of Maturity per game-day), picked from
  /// <see cref="MaturityParameters.DecayRate"/> when an external
  /// biome dominates and from <see cref="MaturityParameters.FallbackDecayDays"/>
  /// when self-dominant or null-dominant. Linear decay is clamped at
  /// the asymptote so partial-Suitability support halts the drop at
  /// the new sustainable level. Co-present cells (Contaminated under
  /// Badwater) get rate = 0 so Maturity stays in place.
  /// <para>When Dry is the chunk's dominant biome the matrix rate is
  /// further multiplied by a drought-intensity scalar
  /// <c>floor + (1 - floor) * (DryMaturity / Ceiling(Dry))</c>, where
  /// the floor is per-decaying-biome (<see cref="MaturityParameters.DroughtFloor"/>).
  /// Water-family biomes get a small non-zero floor (light immediate
  /// decay, ramping substantially as drought deepens). Grassland/Forest
  /// get floor 0 (no decay at all until Dry has built up). The scalar
  /// fires only under Dry dominance; other dominants use the raw
  /// matrix rate.</para></item>
  /// </list>
  ///
  /// <para><b>Scar gate.</b> Before the per-biome loop runs, the
  /// updater reads Badwater and Contaminated Maturity at the chunk
  /// and checks whether either exceeds
  /// <see cref="MaturityParameters.BadwaterScarGateThreshold"/> /
  /// <see cref="MaturityParameters.ContaminatedScarGateThreshold"/>.
  /// When the gate is closed, every non-toxic-scar biome (every
  /// <c>!IsToxicScar(biome)</c> -- i.e. the eight healthy biomes plus
  /// Dry) has its accrue branch skipped: its Maturity holds whatever
  /// value it has. Only Badwater and Contaminated themselves are
  /// exempt -- they <i>are</i> the scar so blocking their accrual
  /// would defeat the gate's purpose. Decay is unaffected: while a
  /// toxic biome actively dominates the chunk, peer Maturity is
  /// being killed by the matrix anyway. The gate matters during the
  /// cleanup tail, after toxic Suitability has dropped but Maturity
  /// is still draining -- Dry in particular would otherwise spring
  /// back as soon as the contamination input cleared, before the
  /// scar Maturity had finished draining.</para>
  ///
  /// <para>Maturity has no upper clamp (the dynamics asymptote
  /// naturally) but is floored at 0; the integration step can briefly
  /// drive it negative numerically, which we clip.</para>
  ///
  /// <para><b>Order of operations.</b> The ticker is expected to call
  /// <see cref="BiomeSuitabilityUpdater.Tick"/> first and then this
  /// updater for the same <see cref="ChunkData"/> in the same
  /// processing step, so Maturity integrates the freshest Suitability
  /// (and dominance reflects post-update Suitability values).</para>
  /// </summary>
  public sealed class BiomeMaturityUpdater {

    /// <summary>
    /// Advance the chunk's Maturity values by
    /// <paramref name="deltaDays"/>. Reads each biome's current
    /// Suitability and Maturity from <paramref name="data"/>,
    /// determines the chunk's dominant biome and scar-gate state,
    /// integrates one forward-Euler step per biome, and writes the
    /// result back into the maturity ordinal slots.
    /// </summary>
    public void Tick(ChunkData data, float deltaDays) {
      if (data == null) throw new ArgumentNullException(nameof(data));
      if (deltaDays < 0f) {
        throw new ArgumentOutOfRangeException(nameof(deltaDays),
            $"deltaDays must be non-negative; got {deltaDays}.");
      }

      var values = data.Values;

      // Pre-pass: chunk's dominant biome (argmax Suitability, with
      // aggressor tiebreak so Badwater wins over Contaminated on
      // stacked chunks). Null when no biome has positive Suitability.
      var dominantBiome = DominantAtChunk(values);

      // Pre-pass: scar gate. Read toxic Maturities BEFORE the loop
      // mutates anything.
      var badwaterMaturity = values[BiomeValueKinds.MaturityOrdinal(BiomeKind.Badwater)];
      var contaminatedMaturity = values[BiomeValueKinds.MaturityOrdinal(BiomeKind.Contaminated)];
      var scarGateClosed =
          badwaterMaturity > MaturityParameters.BadwaterScarGateThreshold
          || contaminatedMaturity > MaturityParameters.ContaminatedScarGateThreshold;

      // Pre-pass: drought intensity input.
      var dryMaturity = values[BiomeValueKinds.MaturityOrdinal(BiomeKind.Dry)];
      var droughtDepth = System.Math.Min(1f,
          dryMaturity / MaturityParameters.DroughtSaturationMaturity);

      foreach (var biome in BiomeValueKinds.AllBiomes) {
        var suitability = values[BiomeValueKinds.SuitabilityOrdinal(biome)];
        var current = values[BiomeValueKinds.MaturityOrdinal(biome)];
        var (alpha, betaAccrue) = MaturityParameters.For(biome);

        var asymptote = MaturityKernel.Asymptote(suitability, alpha, betaAccrue);

        float next;
        if (current <= asymptote) {
          if (scarGateClosed && !IsToxicScar(biome)) {
            next = current;
          } else {
            next = MaturityKernel.Accrue(current, suitability, alpha, betaAccrue, deltaDays);
          }
        } else {
          float ratePerDay;
          if (IsToxicScar(biome) && dominantBiome == biome) {
            ratePerDay = 0f;
          } else if (dominantBiome == null || dominantBiome == biome) {
            ratePerDay = MaturityParameters.Ceiling(biome)
                / MaturityParameters.FallbackDecayDays(biome);
          } else {
            var (coPresent, matrixRate) = MaturityParameters.DecayRate(
                biome, dominantBiome.Value);
            ratePerDay = coPresent ? 0f : matrixRate;
            if (dominantBiome.Value == BiomeKind.Dry) {
              var floor = MaturityParameters.DroughtFloor(biome);
              ratePerDay *= floor + (1f - floor) * droughtDepth;
            }
          }
          next = MaturityKernel.DecayLinear(current, ratePerDay, deltaDays, asymptote);
        }

        if (next < 0f) next = 0f;

        values[BiomeValueKinds.MaturityOrdinal(biome)] = next;
      }
    }

    /// <summary>Argmax Suitability from the chunk's flat array, with
    /// aggressor tiebreak matching
    /// <see cref="ChunkBiomeSampler.DominantAtChunk"/>.</summary>
    private static BiomeKind? DominantAtChunk(float[] values) {
      BiomeKind? best = null;
      var bestSuitability = 0f;
      for (var i = 0; i < ChunkBiomeSampler.BiomesByAggressorTier.Count; i++) {
        var biome = ChunkBiomeSampler.BiomesByAggressorTier[i];
        var s = values[BiomeValueKinds.SuitabilityOrdinal(biome)];
        if (s > bestSuitability) {
          bestSuitability = s;
          best = biome;
        }
      }
      return best;
    }

    private static bool IsToxicScar(BiomeKind biome) =>
        biome == BiomeKind.Badwater || biome == BiomeKind.Contaminated;

  }

}
