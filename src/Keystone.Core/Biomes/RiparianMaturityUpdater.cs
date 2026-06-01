namespace Keystone.Core.Biomes {

  /// <summary>
  /// Single-channel per-tile maturity integrator for riparian
  /// (sustained-near-water) state -- the per-tile counterpart to
  /// <see cref="BiomeMaturityUpdater"/>'s per-biome loop, but with a
  /// binary suitability input (near water: yes/no) and none of the
  /// dominance / scar-gate / decay-matrix orchestration. Just the shared
  /// <see cref="MaturityKernel"/> primitives driven by
  /// <see cref="RiparianMaturityParameters"/>.
  /// </summary>
  public static class RiparianMaturityUpdater {

    /// <summary>
    /// Advance one tile's riparian maturity by <paramref name="deltaDays"/>.
    /// <list type="bullet">
    ///   <item><paramref name="toxic"/> (badwater or soil contamination
    ///   present) — fast decay toward 0 at
    ///   <see cref="RiparianMaturityParameters.ToxicDecayRatePerDay"/>,
    ///   regardless of water proximity. Riparian builds only in clean
    ///   water-adjacency, so a destructive factor resets it.</item>
    ///   <item>else near water — accrue toward
    ///   <see cref="RiparianMaturityParameters.Ceiling"/>.</item>
    ///   <item>else — slow dissipate toward 0 at
    ///   <see cref="RiparianMaturityParameters.DecayRatePerDay"/>.</item>
    /// </list>
    /// Floored at 0.
    /// </summary>
    public static float Step(float current, bool nearWater, bool toxic, float deltaDays) {
      if (toxic) {
        return MaturityKernel.DecayLinear(
            current, RiparianMaturityParameters.ToxicDecayRatePerDay, deltaDays, 0f);
      }

      var suitability = nearWater ? 1f : 0f;
      var asymptote = MaturityKernel.Asymptote(
          suitability,
          RiparianMaturityParameters.Alpha,
          RiparianMaturityParameters.BetaAccrue);

      var next = current <= asymptote
          ? MaturityKernel.Accrue(
              current, suitability,
              RiparianMaturityParameters.Alpha,
              RiparianMaturityParameters.BetaAccrue,
              deltaDays)
          : MaturityKernel.DecayLinear(
              current,
              RiparianMaturityParameters.DecayRatePerDay,
              deltaDays,
              asymptote);

      return next < 0f ? 0f : next;
    }

    /// <summary>
    /// The maturity a tile reaches after <paramref name="days"/> of
    /// sustained near-water starting from zero -- the closed form of the
    /// accrue ODE (<c>M' = Alpha - Beta*M</c> at suitability 1):
    /// <c>Ceiling * (1 - e^(-Beta * days))</c>. Used by the new-game
    /// warmup to seed near-water surfaces "as if water had been there for
    /// <paramref name="days"/>", the per-tile analogue of the per-chunk
    /// biome-maturity seed. Returns 0 for <paramref name="days"/> &lt;= 0.
    /// </summary>
    public static float SeededValue(float days) {
      if (days <= 0f) return 0f;
      return RiparianMaturityParameters.Ceiling
          * (1f - (float)System.Math.Exp(-RiparianMaturityParameters.BetaAccrue * days));
    }

  }

}
