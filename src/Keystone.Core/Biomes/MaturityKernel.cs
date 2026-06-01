namespace Keystone.Core.Biomes {

  /// <summary>
  /// Pure per-step maturity integration math, factored out so every
  /// maturity channel integrates the same way and the model can't fork.
  /// The per-chunk <see cref="BiomeMaturityUpdater"/> uses these to drive
  /// its ten biome channels (with its own dominance/scar/decay-matrix
  /// orchestration around them); the per-tile riparian maturity sweep
  /// uses the same primitives with a binary suitability input.
  ///
  /// <para><b>Hybrid model.</b> Maturity rises toward an
  /// <see cref="Asymptote"/> set by the current suitability via an
  /// exponential <see cref="Accrue"/> step, and falls via a linear
  /// <see cref="DecayLinear"/> step clamped at that same asymptote. The
  /// caller chooses which branch to apply (and, for decay, what rate)
  /// based on whether maturity is above or below the asymptote.</para>
  /// </summary>
  public static class MaturityKernel {

    /// <summary>
    /// The slow-mode asymptote at <paramref name="suitability"/>:
    /// <c>(Alpha * Suitability) / BetaAccrue</c>, or 0 when suitability
    /// is non-positive. Accrual approaches this from below; linear decay
    /// is clamped to it from above, so partial-suitability support halts
    /// a drop at the new sustainable level.
    /// </summary>
    public static float Asymptote(float suitability, float alpha, float betaAccrue) =>
        suitability > 0f ? alpha * suitability / betaAccrue : 0f;

    /// <summary>
    /// One forward-Euler accrue step:
    /// <c>M + (Alpha*Suitability - BetaAccrue*M) * dt</c>, clamped so it
    /// never rises past <see cref="Asymptote"/>. Exponential approach
    /// toward the asymptote; the rise slows as M nears its ceiling.
    ///
    /// <para><b>Why the clamp.</b> Forward-Euler overshoots the asymptote
    /// at large <paramref name="deltaDays"/> (and oscillates once
    /// <c>BetaAccrue*dt &gt;= 2</c>); the true exponential solution never
    /// exceeds the asymptote when rising toward it. The clamp makes a
    /// single big step safe — e.g. the new-game warmup seeding many
    /// game-days of maturity at once can't push a low-ceiling biome
    /// (Monoculture, ceiling 3.5) above its sustainable
    /// level. It is a no-op at the small steady-state dt, where Euler
    /// doesn't overshoot.</para>
    /// </summary>
    public static float Accrue(
        float current, float suitability, float alpha, float betaAccrue, float deltaDays) {
      var next = current + (alpha * suitability - betaAccrue * current) * deltaDays;
      var asymptote = Asymptote(suitability, alpha, betaAccrue);
      return current < asymptote && next > asymptote ? asymptote : next;
    }

    /// <summary>
    /// One linear decay step (<c>M - ratePerDay * dt</c>), clamped so it
    /// never falls below <paramref name="floor"/> -- normally the
    /// <see cref="Asymptote"/>, so support at partial suitability stops
    /// the decay at the sustainable level rather than draining to zero.
    /// </summary>
    public static float DecayLinear(float current, float ratePerDay, float deltaDays, float floor) {
      var next = current - ratePerDay * deltaDays;
      return next < floor ? floor : next;
    }

  }

}
