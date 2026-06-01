namespace Keystone.Core.Biomes {

  /// <summary>
  /// Tuning constants for per-tile riparian maturity -- the
  /// sustained-near-water signal that gates Grassland's riparian
  /// flourishes. Riparian is no longer a <see cref="BiomeKind"/> (removed
  /// in v0.6 and folded into Grassland), so these live apart from
  /// <see cref="MaturityParameters"/>.
  ///
  /// <para>Uses the shared <see cref="MaturityKernel"/>: accrue toward
  /// <see cref="Ceiling"/> while near water (binary suitability = 1),
  /// linear decay at <see cref="DecayRatePerDay"/> otherwise. With
  /// <see cref="Alpha"/> = 1 the near-zero accrual is ~1 maturity-day per
  /// game-day, so early maturity tracks "days of sustained near-water" --
  /// a momentary flood adds almost nothing, which is the entire point of
  /// reintroducing the time axis the instantaneous water check dropped.</para>
  ///
  /// <para><b>Provisional.</b> These values are first-cut defaults, not
  /// yet tuned in-game, and are good candidates to become player-facing
  /// settings later (cf. <c>KeystoneFloraSettings.GrowthBonusPercent</c>).</para>
  /// </summary>
  public static class RiparianMaturityParameters {

    /// <summary>Rise constant per game-day at full (near-water) suitability.</summary>
    public const float Alpha = 1f;

    /// <summary>Asymptotic ceiling in maturity-days at sustained near-water.
    /// Synced with the per-chunk biome default (<see cref="MaturityParameters.DefaultCeiling"/>
    /// = 30) so riparian L2 ramps over the same 30-day window as Grassland L2
    /// rather than a riparian-only short scale.</summary>
    public const float Ceiling = 30f;

    /// <summary>Accrue rate constant. <c>Beta = Alpha / Ceiling</c> so the
    /// asymptote at suitability 1 equals <see cref="Ceiling"/>.</summary>
    public const float BetaAccrue = Alpha / Ceiling;

    /// <summary>Linear dissipation per game-day once no longer near water.</summary>
    public const float DecayRatePerDay = 1f;

    /// <summary>Linear decay per game-day while a destructive factor
    /// (badwater or soil contamination) is present at the tile. Fast:
    /// riparian is a healthy biome and toxics destroy it, so it builds
    /// only in clean sustained near-water. This is the per-tile analogue
    /// of the badwater/contaminated rows of the per-chunk decay matrix.
    /// Provisional (~1 game-day to clear the full <see cref="Ceiling"/>).</summary>
    public const float ToxicDecayRatePerDay = 10f;

    /// <summary>Upper cap on the riparian maturity granted to near-water
    /// surfaces when loading a save from before the per-tile store existed
    /// (the one-time migration). Deliberately small: an existing
    /// settlement's shorelines start just at the riparian L1 threshold and
    /// grow in over play, rather than instantly sprouting full riparian
    /// bands across a map the player already knows. New games seed by
    /// <see cref="RiparianMaturityUpdater.SeededValue"/> (an established
    /// fresh map); the migration takes the smaller of that and this cap.</summary>
    public const float MigrationSeedCap = 0.5f;

    /// <summary>Inclusive minimum Chebyshev distance to water that counts
    /// as "near water" (1 = borders water).</summary>
    public const int NearWaterMinDistance = 1;

    /// <summary>Inclusive maximum Chebyshev distance to water that counts
    /// as "near water". Mirrors <c>NearWaterRecipeFilter</c>'s
    /// <c>IsNearWater(.., 2)</c> band -- keep the two in sync.</summary>
    public const int NearWaterMaxDistance = 2;

    /// <summary>
    /// Whether a stored signed water-distance counts as near-water land.
    /// Positive distances within the band are near-water land; water
    /// tiles (negative distance) and far/uninitialised tiles (0 or large)
    /// are not riparian land and so dissipate rather than accrue.
    /// </summary>
    public static bool IsNearWater(float waterDistance) =>
        waterDistance >= NearWaterMinDistance && waterDistance <= NearWaterMaxDistance;

  }

}
