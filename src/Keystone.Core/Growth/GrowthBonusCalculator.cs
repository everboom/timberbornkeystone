using System;
using Keystone.Core.Biomes;

namespace Keystone.Core.Growth {

  /// <summary>
  /// Pure calculation for the biome-driven plant growth bonus.
  /// Plants in qualifying biomes grow faster: trees benefit from
  /// Forest, water plants from Wetland. The bonus is a 50/50 blend
  /// of chunk-level Suitability (immediate environmental signal) and
  /// cluster-level Maturity fraction (established ecosystem signal).
  /// </summary>
  public static class GrowthBonusCalculator {

    #region Constants

    /// <summary>Default maximum growth-rate bonus at full Suitability
    /// + full Maturity. 0.30 = 30% faster growth (rate multiplier
    /// 1.3×). Overridable via the mod settings slider.</summary>
    public const float DefaultMaxBonus = 0.30f;

    /// <summary>Weight of the Suitability component in the blend.
    /// Suitability is the fast-moving signal that rewards the player
    /// for creating the right conditions NOW (diverse planting, good
    /// moisture). Complements the Maturity weight to sum to 1.</summary>
    public const float SuitabilityWeight = 0.5f;

    /// <summary>Weight of the cluster Maturity component in the
    /// blend. Maturity is the slow-moving signal that rewards
    /// sustained ecosystem investment.</summary>
    public const float MaturityWeight = 1f - SuitabilityWeight;

    #endregion

    #region Public API

    /// <summary>
    /// Compute the growth-rate bonus fraction for a plant at a given
    /// tile, in <c>[0, maxBonus]</c>.
    /// </summary>
    /// <param name="suitability">Chunk-level Suitability of the target
    /// biome at the plant's tile, in [0, 1].</param>
    /// <param name="clusterAvgMaturity">Average Maturity of the target
    /// biome across the cluster the plant's chunk belongs to. 0 when
    /// the chunk isn't part of a qualifying cluster.</param>
    /// <param name="maturityCeiling">Per-biome Maturity ceiling from
    /// <see cref="MaturityParameters.Ceiling"/>.</param>
    /// <param name="maxBonus">Player-configurable cap on the bonus
    /// fraction. <see cref="DefaultMaxBonus"/> when unspecified.</param>
    /// <returns>Growth-rate bonus in [0, maxBonus]. Multiply by
    /// <c>intervalDays / growthTimeInDays</c> to get the per-check
    /// normalised progress to feed
    /// <c>Growable.IncreaseGrowthProgress</c>.</returns>
    public static float ComputeBonus(
        float suitability,
        float clusterAvgMaturity,
        float maturityCeiling,
        float maxBonus = DefaultMaxBonus) {
      var suitabilityTerm = Math.Max(0f, Math.Min(1f, suitability));
      var maturityFraction = maturityCeiling > 0f
          ? Math.Max(0f, Math.Min(1f, clusterAvgMaturity / maturityCeiling))
          : 0f;
      return maxBonus * (SuitabilityWeight * suitabilityTerm
                       + MaturityWeight * maturityFraction);
    }

    /// <summary>
    /// Returns the target <see cref="BiomeKind"/> whose health drives
    /// the growth bonus for a plant, based on its characteristics.
    /// <c>null</c> when no qualifying biome applies (the plant gets
    /// no bonus).
    ///
    /// <para>Priority order is deliberate: aquatic beats tree beats
    /// crop. An aquatic crop (Spadderdock, Cattail) is routed to
    /// Wetland, not Grassland — the water requirement dominates its
    /// ecology. Land crops fall through to Grassland.</para>
    /// </summary>
    /// <param name="isAquatic">True when the entity requires standing
    /// water (<c>FloodableNaturalResourceSpec.MinWaterHeight &gt; 0</c>).
    /// Note: most trees also carry <c>FloodableNaturalResourceSpec</c>
    /// (to define their flood-death threshold), but with
    /// <c>MinWaterHeight = 0</c> — they tolerate flooding but don't
    /// require water. That's not aquatic.</param>
    /// <param name="isTree">True when the entity has a
    /// <c>TreeComponentSpec</c>.</param>
    /// <param name="isCrop">True when the entity qualifies as a land
    /// crop and should key off Grassland. The caller is responsible
    /// for the full qualification — non-aquatic, has <c>CropSpec</c>,
    /// and is plantable by the active faction — so that wild bushes,
    /// modded non-crops, water crops, and cross-faction /
    /// faction-disabled crops are all excluded before this point (see
    /// <c>KeystoneGrowthBonus.StartTickable</c>). Because the Grassland
    /// score is suppressed by Monoculture (dense single-species
    /// planting), the realized bonus rewards crop diversity / crops
    /// grown amid natural grassland — a dense monocrop field reads as
    /// Monoculture and gets little to no bonus.</param>
    public static BiomeKind? TargetBiome(bool isAquatic, bool isTree, bool isCrop) {
      if (isAquatic) return BiomeKind.Wetland;
      if (isTree) return BiomeKind.Forest;
      if (isCrop) return BiomeKind.Grassland;
      return null;
    }

    #endregion

  }

}
