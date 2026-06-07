using Keystone.Core.Biomes;

namespace Keystone.Core.Growth {

  #region Verdict / tier enums

  /// <summary>
  /// Overall at-a-glance verdict for a plant's biome fit, driving the
  /// entity panel's flavor line. Ordered loosely best → worst, but the
  /// selection rule is the priority cascade in
  /// <see cref="GrowthDiagnostics.Classify"/>, not enum order.
  /// </summary>
  public enum GrowthVerdict {
    /// <summary>Established target biome AND favorable current
    /// conditions — the plant is in its element.</summary>
    Thriving,
    /// <summary>A meaningful bonus is being applied, but not the full
    /// established-and-favorable picture (one axis is still building).</summary>
    Benefiting,
    /// <summary>Current conditions are actively hostile (toxic ground,
    /// or a moisture mismatch — flooded land plant / drained water
    /// plant).</summary>
    Hostile,
    /// <summary>Actively establishing <i>now</i>: there is real current
    /// suitability (≥ <see cref="GrowthDiagnostics.SuitabilityWeak"/>) for
    /// the target but maturity hasn't accrued yet. Present tense. Distinct
    /// from <see cref="Potential"/>, which has ~0 current suitability.</summary>
    Establishing,
    /// <summary>Viable but not yet started: a dense, diverse young Forest
    /// planting whose canopy hasn't grown in, so current suitability is
    /// ~0. It <i>will</i> become Forest, but nothing is happening yet —
    /// future tense. Distinct from <see cref="Establishing"/> (real current
    /// suitability) and <see cref="Dormant"/> (won't develop on its own).</summary>
    Potential,
    /// <summary>A different, non-hostile biome owns this ground (e.g. a
    /// tree standing on established Grassland) and the target isn't
    /// favored here.</summary>
    WrongBiome,
    /// <summary>Nothing is happening — no biome established, conditions
    /// not favorable, nothing hostile. Bare or stalled ground.</summary>
    Dormant,
  }

  /// <summary>Coarse bucket for a maturity fraction, for display.</summary>
  public enum MaturityTier { None, Emerging, Established, Thriving }

  /// <summary>Coarse bucket for a suitability value, for display.</summary>
  public enum SuitabilityTier { Poor, Weak, Good, Ideal }

  #endregion

  #region Signal bundle

  /// <summary>
  /// Immutable snapshot of the signals that explain a plant's growth
  /// bonus at one tile, assembled by the Mod layer
  /// (<c>KeystoneGrowthBonus.ComputeSignals</c>) and consumed by
  /// <see cref="GrowthDiagnostics.Classify"/> and the entity-panel
  /// tooltip. Pure value type — primitives + <see cref="BiomeKind"/>
  /// only, so the verdict logic stays unit-testable with no game refs.
  /// </summary>
  public readonly struct GrowthSignals {

    /// <summary>Biome whose health drives this plant's bonus
    /// (Forest / Wetland / Grassland).</summary>
    public BiomeKind TargetBiome { get; init; }

    /// <summary>Gated target-biome suitability at the tile, [0, 1] — the
    /// "current conditions" axis. For Forest this already includes the
    /// mature-canopy gate, so a young planting reads ~0 here even when
    /// diverse (see <see cref="MatureCanopyGate"/>).</summary>
    public float Suitability { get; init; }

    /// <summary>Target-biome maturity fraction (max of chunk-local and
    /// cluster average, over the biome's ceiling), [0, 1] — the
    /// "established" axis.</summary>
    public float MaturityFraction { get; init; }

    /// <summary>Cluster-average maturity fraction for the target biome,
    /// [0, 1]. Surfaced separately so the tooltip can attribute a bonus
    /// to a <i>nearby</i> established stand rather than the local
    /// chunk.</summary>
    public float ClusterMaturityFraction { get; init; }

    /// <summary>Realized bonus as a fraction of the configured maximum,
    /// [0, 1]. The margin gate (<see cref="GrowthDiagnostics.BonusMarginFraction"/>)
    /// reads this so a negligible blend doesn't claim a positive
    /// verdict.</summary>
    public float BonusFraction { get; init; }

    /// <summary>Biome with the highest <i>maturity</i> at the tile — "what
    /// has actually established here." <c>null</c> when nothing has
    /// accrued.</summary>
    public BiomeKind? DominantByMaturity { get; init; }

    /// <summary>Maturity fraction of <see cref="DominantByMaturity"/>
    /// over its own ceiling, [0, 1].</summary>
    public float DominantMaturityFraction { get; init; }

    /// <summary>Biome with the highest <i>suitability</i> at the tile —
    /// "what current conditions most look like." Used to name the
    /// limiting factor when the target isn't favored. <c>null</c> when
    /// no biome scores.</summary>
    public BiomeKind? DominantBySuitability { get; init; }

    /// <summary>Forest only: the mature-canopy gate value, [0, 1], or a
    /// negative sentinel when not applicable / unresolved. Below 1 means
    /// the canopy is still establishing and is holding Forest suitability
    /// down regardless of diversity/density.</summary>
    public float MatureCanopyGate { get; init; }

    /// <summary>Forest only: whether the un-gated Forest score is already
    /// <i>favorable</i> (a genuinely dense, diverse planting — at or above
    /// <see cref="GrowthDiagnostics.SuitabilityFavorable"/>), so once the
    /// canopy matures this chunk would read as real Forest. Deliberately a
    /// favorability bar, not merely "beats Monoculture": a lone or sparse
    /// tree clears the latter (its un-gated score is a small positive vs a
    /// near-zero Monoculture) but is not a forest-in-progress, and the
    /// near-zero comparison flickers frame to frame.</summary>
    public bool WouldBeForestFavorable { get; init; }

    /// <summary>True when the target is Forest, the canopy hasn't matured
    /// (<see cref="MatureCanopyGate"/> in [0, 1)), and the un-gated score
    /// is already favorable — the "diverse, dense saplings, just young"
    /// state that should read as <see cref="GrowthVerdict.Establishing"/>
    /// rather than a problem. A sparse planting fails the favorability bar
    /// and instead reads as WrongBiome / Dormant, which is correct: a few
    /// scattered trees on grassland are not an establishing forest.</summary>
    public bool CanopyViableButImmature =>
        TargetBiome == BiomeKind.Forest
        && MatureCanopyGate >= 0f && MatureCanopyGate < 1f
        && WouldBeForestFavorable;
  }

  #endregion

  /// <summary>
  /// Pure classification of <see cref="GrowthSignals"/> into a
  /// <see cref="GrowthVerdict"/> plus display tiers. All thresholds live
  /// here as named constants so the entity panel's wording and the tests
  /// share one source of truth.
  /// </summary>
  public static class GrowthDiagnostics {

    #region Thresholds

    /// <summary>Maturity fraction at or above which a biome counts as
    /// "established" — for both the target (Thriving) and a competing
    /// dominant (WrongBiome).</summary>
    public const float EstablishedMinFraction = 0.30f;

    /// <summary>Suitability at or above which current conditions count as
    /// "favorable" for the target.</summary>
    public const float SuitabilityFavorable = 0.50f;

    /// <summary>Suitability below which conditions read as "poor" (the
    /// boundary between the Poor and Weak display tiers).</summary>
    public const float SuitabilityWeak = 0.25f;

    /// <summary>Minimum bonus (as a fraction of the configured max) for a
    /// positive verdict (Thriving / Benefiting). Below this, a tiny
    /// suitability/maturity blend no longer claims the plant is doing
    /// well — the fix for premature positives.</summary>
    public const float BonusMarginFraction = 0.10f;

    #endregion

    #region Classification

    /// <summary>
    /// Map a signal bundle to its <see cref="GrowthVerdict"/>. Priority
    /// cascade (first match wins):
    /// <list type="number">
    ///   <item><b>Hostile (toxic)</b> — obstacle is Contaminated/Badwater.
    ///         Trumps a residual bonus: toxic ground is urgent.</item>
    ///   <item><b>Thriving</b> — established AND favorable.</item>
    ///   <item><b>Benefiting</b> — a meaningful bonus is applied (one axis
    ///         still building). A residual-maturity bonus under a fresh
    ///         non-toxic stress lands here; the tooltip still shows the
    ///         poor current conditions.</item>
    ///   <item><b>Hostile (moisture)</b> — flooded land plant / drained
    ///         water plant, when no meaningful bonus masks it.</item>
    ///   <item><b>Establishing / Potential</b> — on track: conditions favor
    ///         the target, or (Forest) a diverse/dense planting is still
    ///         young. Split by current suitability — <b>Establishing</b>
    ///         (real suitability now) vs <b>Potential</b> (canopy gated to
    ///         ~0 suitability; will establish but hasn't started).</item>
    ///   <item><b>WrongBiome</b> — a different non-hostile biome is
    ///         established here.</item>
    ///   <item><b>Dormant</b> — none of the above.</item>
    /// </list>
    /// </summary>
    public static GrowthVerdict Classify(in GrowthSignals s) {
      if (IsToxic(s.DominantBySuitability)) return GrowthVerdict.Hostile;

      var established = s.MaturityFraction >= EstablishedMinFraction;
      var favorable = s.Suitability >= SuitabilityFavorable;
      if (established && favorable) return GrowthVerdict.Thriving;

      if (s.BonusFraction >= BonusMarginFraction) return GrowthVerdict.Benefiting;

      if (IsMoistureMismatch(s.TargetBiome, s.DominantBySuitability))
        return GrowthVerdict.Hostile;

      if (s.CanopyViableButImmature || (favorable && !established)) {
        // Split "establishing now" from "viable but not started." With a
        // meaningful bonus already caught by Benefiting above, the cases
        // that reach here with ~0 current suitability are diverse young
        // saplings whose canopy gate holds Forest suitability at 0 — they
        // WILL become Forest but nothing is happening yet, so they read as
        // Potential (future tense), not Establishing (present tense).
        return s.Suitability >= SuitabilityWeak
            ? GrowthVerdict.Establishing
            : GrowthVerdict.Potential;
      }

      if (s.DominantByMaturity.HasValue
          && s.DominantByMaturity.Value != s.TargetBiome
          && s.DominantMaturityFraction >= EstablishedMinFraction)
        return GrowthVerdict.WrongBiome;

      return GrowthVerdict.Dormant;
    }

    #endregion

    #region Hostility

    /// <summary>Toxic ground — hostile to every plant, urgent enough to
    /// override a residual bonus.</summary>
    public static bool IsToxic(BiomeKind? obstacle) =>
        obstacle == BiomeKind.Contaminated || obstacle == BiomeKind.Badwater;

    /// <summary>Moisture mismatch between the target and the dominant
    /// current conditions: a land target (Forest / Grassland) flooded by
    /// River / Lake / Wetland, or a water target (Wetland) drained (Dry)
    /// or scoured (River) or drowned (Lake). Excludes toxic, which
    /// <see cref="IsToxic"/> handles.</summary>
    public static bool IsMoistureMismatch(BiomeKind target, BiomeKind? obstacle) {
      if (!obstacle.HasValue) return false;
      var o = obstacle.Value;
      if (target == BiomeKind.Wetland)
        return o == BiomeKind.Dry || o == BiomeKind.River || o == BiomeKind.Lake;
      return o == BiomeKind.Dry || o == BiomeKind.River
          || o == BiomeKind.Lake || o == BiomeKind.Wetland;
    }

    /// <summary>Convenience union: toxic OR moisture mismatch.</summary>
    public static bool IsHostileObstacle(BiomeKind target, BiomeKind? obstacle) =>
        IsToxic(obstacle) || IsMoistureMismatch(target, obstacle);

    #endregion

    #region Display tiers

    /// <summary>Bucket a maturity fraction for display.</summary>
    public static MaturityTier MaturityTierOf(float fraction) =>
        fraction < 0.10f ? MaturityTier.None
      : fraction < EstablishedMinFraction ? MaturityTier.Emerging
      : fraction < 0.70f ? MaturityTier.Established
      : MaturityTier.Thriving;

    /// <summary>Bucket a suitability value for display.</summary>
    public static SuitabilityTier SuitabilityTierOf(float suitability) =>
        suitability < SuitabilityWeak ? SuitabilityTier.Poor
      : suitability < SuitabilityFavorable ? SuitabilityTier.Weak
      : suitability < 0.75f ? SuitabilityTier.Good
      : SuitabilityTier.Ideal;

    #endregion

  }

}
