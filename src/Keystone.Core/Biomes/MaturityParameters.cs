namespace Keystone.Core.Biomes {

  /// <summary>
  /// Per-biome integration constants for the Maturity channel.
  /// Hybrid model: <b>exponential accrue, linear decay</b>.
  /// <list type="bullet">
  /// <item><b>Accrue</b> -- Maturity at or below the slow-mode
  /// asymptote <c>(Alpha * Suitability) / BetaAccrue</c>. Integrate
  /// <c>dM/dt = Alpha * Suitability - BetaAccrue * Maturity</c>:
  /// exponential approach toward the asymptote. The "growth feels
  /// ongoing" feel comes from never quite reaching the ceiling.</item>
  /// <item><b>Decay</b> -- Maturity above the asymptote. Integrate
  /// <c>dM/dt = -RatePerDay</c> linearly, clamped at the asymptote
  /// so partial-Suitability support halts decay at the new sustainable
  /// level. Linear gives predictable cleanup timing (clear time =
  /// <c>ceiling / rate</c>) rather than the exponential tail.</item>
  /// </list>
  ///
  /// <para><b>Per-biome ceilings.</b> Asymptote at
  /// <c>Suitability = 1</c> is <c>Alpha / BetaAccrue</c>. With
  /// <c>Alpha = 1</c> universal, <c>BetaAccrue = 1 / Ceiling(biome)</c>:
  /// Forest / Grassland / Wetland / River / Lake / Cave 30d,
  /// Badwater 15, Dry 10, Contaminated 12.5, Monoculture 3.5.</para>
  ///
  /// <para><b>Decay-rate matrix.</b> See <see cref="DecayClearTimeDays"/>
  /// for the <c>(decaying, dominant) -> clear-time-in-days</c> table
  /// (clear time = days for Maturity to drop from ceiling to 0 under
  /// this dominant, at full intensity). <see cref="DecayRate"/> converts
  /// to <c>rate per day = ceiling / clearTimeDays</c>. The integration
  /// layer may scale this rate further -- see <see cref="DroughtFloor"/>
  /// for the Dry-dominant intensity ramp that buffers grassland and
  /// forest against nascent droughts while leaving water-family biomes
  /// near-fully exposed. The matrix encodes the pecking order:</para>
  /// <list type="bullet">
  /// <item>Badwater dominant column 0.5d -- destroys neighbours in
  /// ~12 hours.</item>
  /// <item>Contaminated dominant column 1d.</item>
  /// <item>Dry dominant 3d.</item>
  /// <item>Water-family dominant (Riv/Wet/Lak) 5d, land-family 7d, Cave 14d.</item>
  /// <item>Toxic scar fade: Badwater and Contaminated decay at a
  /// flat <see cref="BaselineDecayRatePerDay"/> (1/day) once
  /// they're no longer the chunk's dominant signal. Clear time =
  /// ceiling / rate, so Badwater clears in 15d, Contaminated in 12.5d.
  /// While one of them is itself dominant the scar holds; the short-
  /// circuit lives in <see cref="BiomeMaturityUpdater.Tick"/>.</item>
  /// </list>
  ///
  /// <para>One co-present cell -- Contaminated decaying under Badwater
  /// dominant -- zeroes the rate so Maturity stays in place. In
  /// practice unreachable post-Stage-D since Contaminated Suitability
  /// stacks with Badwater and the asymptote stays at the ceiling.</para>
  ///
  /// <para><b>Scar gate.</b> Every biome <i>except the toxic scars
  /// themselves</i> (so the seven healthy biomes -- Forest, Grassland,
  /// Monoculture, Wetland, River, Lake, Cave -- plus Dry)
  /// cannot accrue Maturity while
  /// <c>BadwaterMaturity &gt; <see cref="BadwaterScarGateThreshold"/></c>
  /// or
  /// <c>ContaminatedMaturity &gt; <see cref="ContaminatedScarGateThreshold"/></c>.
  /// Dry is gated alongside healthy biomes because the input-side
  /// <c>ContaminationFactor</c> cancellation in
  /// <see cref="BiomeTargets"/> only suppresses Dry while the
  /// contamination <i>input</i> is present; without this scar gate,
  /// Dry Maturity would spring back during the cleanup tail (after
  /// fluid removal but before scar Maturity drained), out of pace
  /// with the toxic biomes that own the chunk.
  /// Enforced in <see cref="BiomeMaturityUpdater.Tick"/>: in accrue
  /// mode the per-biome integration is skipped when blocked; decay
  /// mode is unaffected (the toxic biome is presumably dominant and
  /// the matrix handles the rate). Cleanup pacing under the 1/day
  /// baseline: Badwater ceiling 15 reaches threshold 0.1 in ~15d;
  /// Contaminated ceiling 12.5 reaches threshold 0.5 in ~12d. So a
  /// scarred chunk is uninhabitable to non-toxic-scar biomes for
  /// ~15 days after Badwater cleanup, ~12 days after plain
  /// Contamination cleanup. The deep toxic ceilings make a fully-
  /// entrenched scar a multi-week reclamation; because the scars
  /// accrue at ~1/day too, a fresh spill cleaned promptly clears
  /// fast (cleanup cost is proportional to how long it was left).</para>
  /// </summary>
  public static class MaturityParameters {

    #region Constants

    /// <summary>Default rise constant: at <c>Suitability = 1</c>,
    /// Maturity accrues 1 point per game-day before the
    /// <c>-Beta * M</c> taper kicks in.</summary>
    public const float DefaultAlpha = 1f;

    /// <summary>Default per-biome Maturity ceiling at
    /// <c>Suitability = 1</c>, in game-days.</summary>
    public const float DefaultCeiling = 30f;

    /// <summary>Linear-decay clear-time fallback for positive biomes
    /// in decay mode when no external biome dominates (the decaying
    /// biome is itself dominant, or all Suitabilities are 0). Days
    /// from ceiling to 0 at the linear rate <c>ceiling / 7</c>.</summary>
    public const float PositiveFallbackDecayDays = 7f;

    /// <summary>Flat per-day decay rate used in two distinct contexts
    /// that share the "no aggression" semantic — the dominant biome
    /// is NOT actively destroying the decaying biome, so the decaying
    /// biome just drifts back at the base rate of 1 unit per game-day.
    /// Clear time always follows from the decaying biome's ceiling.
    ///
    /// <para><b>Toxic scar fade.</b> Badwater / Contaminated decay at
    /// this rate once they're no longer the chunk's dominant signal —
    /// Badwater (ceiling 15) clears in 15 days, Contaminated (12.5) in
    /// 12.5 days. The self-dominant case is short-circuited in
    /// <see cref="BiomeMaturityUpdater.Tick"/>: while the negative
    /// biome is itself present the scar holds (rate 0).</para>
    ///
    /// <para><b>Succession-free peer drift.</b> Some healthy-biome
    /// pairs have an asymmetric successional relationship: Grassland
    /// is a "winning" state over Forest (Grassland succeeds Forest);
    /// Lake and River are "winning" states over Wetland. The
    /// successor's dominance kills the predecessor's Maturity at the
    /// matrix's faster rate (Forest under Grassland 7 d, Wetland under
    /// River 3 d). The Lake under Wetland cell is "no aggression": the
    /// dominant biome doesn't destroy its peer, so the peer drifts at
    /// this baseline rate. The corresponding cell override lives in
    /// <see cref="DecayClearTimeDays"/>.</para>
    ///
    /// <para>Half-aggression cells — Grassland under Forest and River
    /// under Wetland — sit between peer drift and full succession.
    /// The dominant biome pushes back at twice the baseline rate
    /// (2/day, 15 d clear on a 30-ceiling biome), still slower than
    /// the successor's matrix rate in the reverse direction so the
    /// successional asymmetry holds, but the predecessor no longer
    /// drifts indefinitely under a mature dominant. Cell overrides
    /// in <see cref="DecayClearTimeDays"/>.</para>
    ///
    /// <para>Rate is the design constant — clear times are derived,
    /// never hardcoded.</para></summary>
    public const float BaselineDecayRatePerDay = 1f;

    /// <summary>Badwater Maturity above this value blocks healthy
    /// biomes from accruing on the same chunk. With the 1/day baseline
    /// decay under non-Badwater dominance (ceiling 15 / 1/day = 15d
    /// clear), the gate opens roughly 15 game-days after the last
    /// badwater fluid clears.</summary>
    public const float BadwaterScarGateThreshold = 0.1f;

    /// <summary>Contaminated Maturity above this value blocks healthy
    /// biomes from accruing. With the matrix's 12.5-day scar fade,
    /// the gate opens roughly 12 game-days after contamination clears.</summary>
    public const float ContaminatedScarGateThreshold = 0.5f;

    #endregion

    #region Drought intensity

    /// <summary>
    /// Per-biome floor for the drought-intensity scalar applied to the
    /// decay rate when Dry is the dominant biome. The integration layer
    /// (<see cref="BiomeMaturityUpdater.Tick"/>) multiplies the matrix
    /// rate by <c>floor + (1 - floor) * (DryMaturity /
    /// Ceiling(Dry))</c>, so a floor of 0 means decay is gated entirely
    /// by Dry's Maturity depth (no decay until drought has built), and
    /// a floor of 1 disables the scaling entirely (pure matrix rate
    /// from tick 1).
    ///
    /// <para><b>Design intent.</b> Water-family biomes (River, Wetland,
    /// Lake) are defined by the water input -- the moment the
    /// water leaves they are already in trouble, regardless of how
    /// "deep" the drought has become. Grassland and Forest have root
    /// systems and soil banking that buffer transient dry spells, so
    /// they should only start decaying once the drought is genuinely
    /// established (Dry Maturity climbs toward its ceiling). Monoculture
    /// and Cave currently take the default-0 floor too; they're passive
    /// in the current design and their floors are placeholders pending
    /// real treatment.</para>
    ///
    /// <para>The scalar applies only when Dry is the chunk's dominant
    /// biome. Decay rates under any other dominant are unaffected. Dry
    /// itself decaying (e.g. under Forest dominant) uses its own row
    /// override and is also unaffected.</para>
    /// </summary>
    public static float DroughtFloor(BiomeKind decaying) => decaying switch {
        BiomeKind.River => 0.1f,
        BiomeKind.Wetland => 0.1f,
        BiomeKind.Lake => 0.1f,
        _ => 0f,
    };

    /// <summary>
    /// Dry Maturity at which drought intensity reaches its saturation
    /// value of 1.0. Below this, the integration layer scales the decay
    /// rate linearly from <see cref="DroughtFloor"/> up to 1.0.
    ///
    /// <para><b>Decoupled from Dry's own ceiling on purpose.</b> Dry
    /// asymptotes at <c>Ceiling(Dry) = 10</c> under saturated
    /// Suitability, but drought "feels fully bitten in" much earlier --
    /// around 33% of that ceiling. With Dry's accrue time constant of
    /// 10 days, this threshold is reached at roughly day 4 of a fresh
    /// drought (M_dry(t) = 10*(1 - exp(-t/10)) hits 3.33 at t ~ 4.05d).
    /// Without this decoupling, intensity would ramp on Dry's own slow
    /// time constant and total fresh-drought clear times would balloon
    /// to two to three times the matrix nominals.</para>
    ///
    /// <para>The visible meaning shift worth knowing about: any UI or
    /// debug surface that wants to read "drought intensity at this
    /// chunk" should use <c>min(1, M_dry / DroughtSaturationMaturity)</c>,
    /// not <c>M_dry / Ceiling(Dry)</c>.</para>
    /// </summary>
    public const float DroughtSaturationMaturity = 3.33f;

    #endregion

    #region Per-biome lookups

    /// <summary>Maturity ceiling (asymptote at <c>Suitability = 1</c>)
    /// for <paramref name="biome"/>, in game-days. The accrue-mode
    /// rate <c>BetaAccrue</c> is derived as <c>DefaultAlpha / Ceiling</c>,
    /// which means lower-ceiling biomes also reach their ceiling
    /// faster (time constant equals ceiling in days).</summary>
    public static float Ceiling(BiomeKind biome) => biome switch {
        BiomeKind.Badwater => 15f,
        BiomeKind.Dry => 10f,
        BiomeKind.Contaminated => 12.5f,
        BiomeKind.Monoculture => 3.5f,
        _ => DefaultCeiling,
    };

    /// <summary>Tuple of <c>(Alpha, BetaAccrue)</c> for the accrue
    /// branch of the integration. BetaAccrue is derived from the
    /// biome's ceiling (<see cref="Ceiling"/>).</summary>
    public static (float Alpha, float BetaAccrue) For(BiomeKind biome) {
      var betaAccrue = DefaultAlpha / Ceiling(biome);
      return (DefaultAlpha, betaAccrue);
    }

    /// <summary>Decay clear-time fallback for null-dominant decay mode
    /// and for self-dominant positive biomes. Badwater and Contaminated
    /// derive their clear time from
    /// <see cref="BaselineDecayRatePerDay"/> (rate is the
    /// design constant; clear time is ceiling / rate). Dry keeps its
    /// original 70d scar-persistence value -- the matrix's per-
    /// dominant row override is the design path for Dry decay, so its
    /// fallback is left intact pending an explicit redesign. Positives
    /// use the legacy 7d.
    ///
    /// <para>The self-dominant case for Badwater / Contaminated is
    /// handled separately in <see cref="BiomeMaturityUpdater.Tick"/>
    /// -- while one of those biomes is itself the chunk's dominant
    /// signal its scar Maturity holds (rate 0) rather than decays,
    /// so this fallback isn't consulted for them in the self-dominant
    /// branch.</para></summary>
    public static float FallbackDecayDays(BiomeKind biome) => biome switch {
        BiomeKind.Badwater => Ceiling(BiomeKind.Badwater) / BaselineDecayRatePerDay,
        BiomeKind.Contaminated => Ceiling(BiomeKind.Contaminated) / BaselineDecayRatePerDay,
        BiomeKind.Dry => DryFallbackDecayDays,
        _ => PositiveFallbackDecayDays,
    };

    /// <summary>Dry's original self/null-dominant fallback (pre-1/day-
    /// baseline). Slow drain so internal-dynamic decay doesn't wipe
    /// scar Maturity without something actively clearing it. Restored
    /// after a flatten-to-1/day pass overreached its scope -- the
    /// baseline-rate redesign covered Badwater/Contaminated but Dry
    /// has its own row dynamics (aggressor acceleration, low-ceiling
    /// fast clear under healthy dominants) that the matrix encodes.</summary>
    private const float DryFallbackDecayDays = 70f;

    /// <summary>True if <paramref name="biome"/> is one of the
    /// "negative" biomes the design treats as stress states. Used to
    /// pick the polarity-based decay fallback. The scar gate's
    /// membership predicate lives separately (in
    /// <see cref="BiomeMaturityUpdater"/>'s <c>IsToxicScar</c>) and
    /// excludes Dry, so the gate blocks Dry accrual too even though
    /// Dry is "negative" -- they're orthogonal axes.</summary>
    public static bool IsNegative(BiomeKind biome) => biome switch {
        BiomeKind.Dry => true,
        BiomeKind.Contaminated => true,
        BiomeKind.Badwater => true,
        _ => false,
    };

    #endregion

    #region Decay-rate matrix

    /// <summary>Decay-mode <i>clear time</i> for the
    /// (<paramref name="decaying"/>, <paramref name="dominant"/>) pair,
    /// in game-days. "Clear time" means: at the linear decay rate
    /// <c>Ceiling(decaying) / clearTimeDays</c>, Maturity drops from
    /// the decaying biome's ceiling to 0 in <c>clearTimeDays</c> days.
    /// Same biome on both axes is an error -- callers handle the
    /// self-dominant case via <see cref="FallbackDecayDays"/>.
    ///
    /// <para><b>Return shape.</b> <c>CoPresent = true</c> signals a
    /// stacking pair where the decaying biome's Maturity should stay
    /// in place (Badwater dominant implies Contaminated presence; the
    /// matrix cell zeroes the decay rate). <c>ClearTimeDays</c> is
    /// undefined for CoPresent cells.</para>
    ///
    /// <para>See DESIGN.md § Maturity for the full table and
    /// structural pattern (column defaults by dominant biome, plus
    /// row + cell overrides). Spot summary: Badwater dominant 0.5d,
    /// Contaminated dominant 1d, Dry dominant 3d, water-family
    /// dominant (Riv/Wet/Lak) 5d, land-family dominant
    /// (Gra/For/Mon) 7d, Cave dominant 14d -- with row overrides for
    /// Dry / Monoculture / Badwater / Contaminated decaying, and cell
    /// overrides for Wetland/Lake against a dominant River.</para></summary>
    public static (bool CoPresent, float ClearTimeDays) DecayClearTimeDays(
        BiomeKind decaying, BiomeKind dominant) {
      if (decaying == dominant) {
        throw new System.ArgumentException(
            $"DecayClearTimeDays is for cross-biome decay; got same biome on both axes ({decaying}). " +
            "Self-dominant decay uses MaturityParameters.FallbackDecayDays.");
      }

      // Co-present stacking: Badwater carries Contaminated.
      if (decaying == BiomeKind.Contaminated && dominant == BiomeKind.Badwater) {
        return (true, 0f);
      }

      // --- Row overrides (more specific than column defaults) ---

      // Badwater / Contaminated decay: flat BaselineDecayRatePerDay
      // (1/day) regardless of dominant. Clear time derived from the
      // biome's ceiling. Self-dominant case is short-circuited by
      // BiomeMaturityUpdater before this matrix is consulted; while a
      // negative biome is itself dominant the scar holds (rate 0).
      // Contaminated-under-Badwater is the exception, handled by the
      // co-present stacking above.
      if (decaying == BiomeKind.Badwater || decaying == BiomeKind.Contaminated) {
        return (false, Ceiling(decaying) / BaselineDecayRatePerDay);
      }

      // Dry decaying keeps its own dominance-dependent design:
      //   BW dominant 0.5d (toxic structurally kills dry land fast).
      //   Con dominant 1d (column default).
      //   Any other dominant 1d (low ceiling, easily cleared once
      //   moisture or shade returns).
      if (decaying == BiomeKind.Dry) {
        if (dominant == BiomeKind.Badwater) return (false, 0.5f);
        if (dominant == BiomeKind.Contaminated) return (false, 1f);
        return (false, 1f);
      }

      // Monoculture decaying:
      //   Grassland/Forest dominant -> 3d (replaceable with effort).
      //   Otherwise falls through to column defaults.
      if (decaying == BiomeKind.Monoculture
          && (dominant == BiomeKind.Grassland || dominant == BiomeKind.Forest)) {
        return (false, 3f);
      }

      // --- Cell overrides (specific (decaying, dominant) pairs) ---

      // Rivers wash wetland/lake plantings faster than the water-family
      // default of 5d.
      if (dominant == BiomeKind.River
          && (decaying == BiomeKind.Wetland || decaying == BiomeKind.Lake)) {
        return (false, 3f);
      }

      // Half-aggression cells. The dominant biome pushes back against
      // the decaying biome at 2x the baseline rate -- half the matrix
      // rate the decaying biome gets to use in the reverse direction,
      // preserving the successional asymmetry while no longer leaving
      // the predecessor drifting indefinitely. Clear time = ceiling
      // / (2 * baseline) = 15 d on a 30-ceiling biome.
      //   - Grassland under Forest: mature forests visibly suppress
      //     nearby grass. Reverse (Forest under Grassland) stays at
      //     the 7 d land-family default.
      //   - River under Wetland: wetlands reclaim river channels back
      //     to slow-flow vegetative water at twice baseline. Reverse
      //     (Wetland under River) stays at 3 d (flow erosion).
      if ((decaying == BiomeKind.Grassland && dominant == BiomeKind.Forest)
          || (decaying == BiomeKind.River && dominant == BiomeKind.Wetland)) {
        return (false, Ceiling(decaying) / (2f * BaselineDecayRatePerDay));
      }

      // Succession-free peer drift cell. The dominant biome doesn't
      // aggressively destroy the decaying one, so it drifts at
      // BaselineDecayRatePerDay (1 unit per game-day) instead of the
      // faster successor-kills-predecessor matrix rate.
      //   - Lake under Wetland dominant -- wetlands don't fill lakes;
      //     they peacefully coexist and the lake drifts at base rate.
      //     The reverse (Wetland under Lake) is still 5 d (water-
      //     family default).
      // Clear time = Ceiling(decaying) / BaselineDecayRatePerDay; the
      // rate is the constant, not the clear time.
      if (decaying == BiomeKind.Lake && dominant == BiomeKind.Wetland) {
        return (false, Ceiling(decaying) / BaselineDecayRatePerDay);
      }

      // --- Per-biome Dry column ---
      // Drought has a per-biome kill order (rivers go fastest, forest
      // hangs on longest) rather than the uniform column default. See
      // DryColumnClearTimeDays for the table. Cave + Monoculture fall
      // through to the column default via the lookup's _ arm.
      if (dominant == BiomeKind.Dry) {
        return (false, DryColumnClearTimeDays(decaying));
      }

      // --- Column defaults by dominant biome (aggressor tier) ---

      return (false, ColumnDefaultClearTimeDays(dominant));
    }

    /// <summary>Convenience wrapper that converts
    /// <see cref="DecayClearTimeDays"/> into the linear decay rate
    /// applied at integration time: <c>rate = Ceiling(decaying) /
    /// clearTimeDays</c>. CoPresent flag propagates; RatePerDay is 0
    /// when CoPresent.</summary>
    public static (bool CoPresent, float RatePerDay) DecayRate(
        BiomeKind decaying, BiomeKind dominant) {
      var (coPresent, clearTimeDays) = DecayClearTimeDays(decaying, dominant);
      if (coPresent) return (true, 0f);
      return (false, Ceiling(decaying) / clearTimeDays);
    }

    /// <summary>
    /// Per-biome saturated-drought clear time when Dry is the chunk's
    /// dominant biome. Encodes the "drought kill order" for healthy
    /// biomes -- water-family clears fast, forest clings on longest.
    /// Values are the *saturated* clear times (intensity = 1.0); the
    /// integration layer's drought-intensity ramp
    /// (<see cref="DroughtFloor"/> + <see cref="DroughtSaturationMaturity"/>)
    /// stretches the effective fresh-drought clear time roughly
    /// according to:
    /// <list type="table">
    /// <listheader><term>Biome</term><description>Saturated → Fresh (M=30)</description></listheader>
    /// <item><term>River / Lake</term><description>0.7 d → ~2 d</description></item>
    /// <item><term>Wetland</term><description>1.8 d → ~3.5 d</description></item>
    /// <item><term>Grassland</term><description>2.1 d → ~4 d</description></item>
    /// <item><term>Forest</term><description>4.1 d → ~6 d</description></item>
    /// </list>
    /// Cave (moisture-independent placeholder) and Monoculture (deferred
    /// pending an "unhealthy biome" pass) fall through to the 3d column
    /// default. Negative biomes never appear as decaying-under-Dry --
    /// Badwater and Contaminated have their own row override at the
    /// baseline rate, and Dry-decaying-under-itself isn't a valid input.
    /// </summary>
    private static float DryColumnClearTimeDays(BiomeKind decaying) => decaying switch {
        BiomeKind.River => 0.7f,
        BiomeKind.Lake => 0.7f,
        BiomeKind.Wetland => 1.8f,
        BiomeKind.Grassland => 2.1f,
        BiomeKind.Forest => 4.1f,
        _ => 3f,
    };

    private static float ColumnDefaultClearTimeDays(BiomeKind dominant) => dominant switch {
        BiomeKind.Badwater => 0.5f,
        BiomeKind.Contaminated => 1f,
        BiomeKind.Dry => 3f,
        BiomeKind.River => 5f,
        BiomeKind.Wetland => 5f,
        BiomeKind.Lake => 5f,
        BiomeKind.Forest => 7f,
        BiomeKind.Grassland => 7f,
        BiomeKind.Monoculture => 7f,
        BiomeKind.Cave => 14f,
        _ => 14f,
    };

    #endregion

  }

}
