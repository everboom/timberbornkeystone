using Keystone.Core.Biomes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  [TestClass]
  public class BiomeTargetsTests {

    #region Forest

    [TestMethod]
    public void Forest_FullyIrrigatedDiverseDenseTrees_TargetIsOne() {
      // All trees mature (MatureTreeCount == TreeCount) so the mature-
      // canopy gate is 1 and this test keeps pinning the irrigation ×
      // diversity × density product, not the gate.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 10,
          TreeSpeciesCount = 3,
          MatureTreeCount = 10,
      };
      Assert.AreEqual(1f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    [TestMethod]
    public void Forest_NoIrrigation_TargetIsZero() {
      // Trees fully mature so the zero is unambiguously from irrigation,
      // not the mature-canopy gate.
      var inputs = new ChunkBiomeInputs {
          TreeCount = 10, TreeSpeciesCount = 3, MatureTreeCount = 10,
      };
      Assert.AreEqual(0f, BiomeTargets.Forest(inputs));
    }

    [TestMethod]
    public void Forest_SingleSpecies_TargetIsZeroBecauseDiversityZero() {
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 10,
          TreeSpeciesCount = 1,
          MatureTreeCount = 10,
      };
      // diversity = saturate(1/2) = 0.5; density = saturate(10/5) = 1;
      // mature gate = 1 (all mature)
      Assert.AreEqual(0.5f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    [TestMethod]
    public void Forest_LandContaminationDoesNotZeroTarget() {
      // Land contamination no longer disables Forest accumulation --
      // Forest can rise alongside Contaminated on the same chunk.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 10,
          TreeSpeciesCount = 3,
          MatureTreeCount = 10,
          ContaminatedFraction = 0.2f,
      };
      Assert.AreEqual(1f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    #endregion

    #region Forest mature-canopy gate

    [TestMethod]
    public void Forest_AllSeedlings_TargetIsZero() {
      // The exploit this gate closes: a chunk carpeted with dense,
      // diverse saplings (0 mature) must not read as established forest.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 16,
          TreeSpeciesCount = 3,
          MatureTreeCount = 0,
      };
      Assert.AreEqual(0f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    [TestMethod]
    public void Forest_MatureFractionAtSaturation_GateIsFull() {
      // 25% mature is the saturation point -> gate = 1 -> full credit.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 16,
          TreeSpeciesCount = 3,
          MatureTreeCount = 4,
      };
      Assert.AreEqual(1f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    [TestMethod]
    public void Forest_MatureFractionAboveSaturation_GateStaysFull() {
      // Beyond 25% the gate clamps at 1 (no extra credit for more
      // maturity); the other factors are already saturated here.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 16,
          TreeSpeciesCount = 3,
          MatureTreeCount = 12,
      };
      Assert.AreEqual(1f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    [TestMethod]
    public void Forest_MatureFractionHalfOfSaturation_GateIsHalf() {
      // 12.5% mature = half of the 25% saturation -> linear gate = 0.5.
      // Other factors saturated (density 16/5 -> 1, diversity 3/2 -> 1),
      // so Forest == the gate value.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 16,
          TreeSpeciesCount = 3,
          MatureTreeCount = 2,
      };
      Assert.AreEqual(0.5f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    [TestMethod]
    public void Forest_NoTrees_MatureFractionZero_NoDivideByZero() {
      // Defensive: MatureTreeFraction is 0 when TreeCount is 0 (and
      // density is 0 anyway), so Forest is a clean 0, not NaN.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 0,
          TreeSpeciesCount = 0,
          MatureTreeCount = 0,
      };
      Assert.AreEqual(0f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    #endregion

    #region Grassland

    [TestMethod]
    public void Grassland_IrrigatedNoTreesNoCrops_TargetIsIrrigatedFraction() {
      var inputs = new ChunkBiomeInputs { IrrigatedFraction = 0.8f };
      Assert.AreEqual(0.8f, BiomeTargets.Grassland(inputs), 1e-4f);
    }

    [TestMethod]
    public void Grassland_FullDensityMatureForest_TargetIsZero() {
      // With *mature* trees at the density saturation point (5+), the
      // linear mature-canopy reduction takes Grassland to 0.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 5,
          TreeSpeciesCount = 2,
          MatureTreeCount = 5,
      };
      Assert.AreEqual(0f, BiomeTargets.Grassland(inputs));
    }

    [TestMethod]
    public void Grassland_PartialMatureTrees_TargetReducedProportionally() {
      // 2 mature trees at density saturation 5 -> mature-canopy
      // presence = 0.4. Grassland target = irrigated * (1 - 0.4) = 0.6.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 2,
          TreeSpeciesCount = 2,
          MatureTreeCount = 2,
      };
      Assert.AreEqual(0.6f, BiomeTargets.Grassland(inputs), 1e-4f);
    }

    [TestMethod]
    public void Grassland_OneMatureTreePartialIrrigation_ScalesBoth() {
      // 1 mature tree -> mature-canopy presence = 0.2. Half-irrigated ->
      // 0.5. Grassland target = 0.5 * (1 - 0.2) = 0.4.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 0.5f,
          TreeCount = 1,
          TreeSpeciesCount = 1,
          MatureTreeCount = 1,
      };
      Assert.AreEqual(0.4f, BiomeTargets.Grassland(inputs), 1e-4f);
    }

    [TestMethod]
    public void Grassland_DenseSeedlingsDoNotSuppress_FallsBackToFull() {
      // The corollary to Forest's mature-canopy gate: a chunk densely
      // planted with *immature* trees reads as ~0 Forest (gate) AND must
      // NOT be suppressed as Grassland -- it falls back to full Grassland
      // until the canopy establishes, rather than sitting in a
      // low-everything limbo. Mature-canopy presence is 0 here, so
      // Grassland == irrigated.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 10,
          TreeSpeciesCount = 3,
          MatureTreeCount = 0,
      };
      Assert.AreEqual(1f, BiomeTargets.Grassland(inputs), 1e-4f);
      // ...and Forest is gated off, so the chunk genuinely reads as
      // Grassland, not Forest.
      Assert.AreEqual(0f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    [TestMethod]
    public void Grassland_FullMonoculture_TargetIsZero() {
      // 16 plantables / 16-tile chunk -> sat 1; D = 1 -> dom 1.
      // Mono = 1 * 1 * 1 = 1. Grassland = irrigated * (1 - 1) = 0.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 16,
          PlantableSpeciesCount = 1,
          PlantableDominance = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Grassland(inputs), 1e-4f);
    }

    [TestMethod]
    public void Grassland_EvenThreeSpeciesMix_NoMonocultureSuppression() {
      // 3 species perfectly even -> D = 1/3, threshold cuts to 0.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 0.6f,
          PlantableCount = 15,
          PlantableSpeciesCount = 3,
          PlantableDominance = 1f / 3f,
      };
      Assert.AreEqual(0.6f, BiomeTargets.Grassland(inputs), 1e-4f);
    }

    [TestMethod]
    public void Grassland_DominatedThreeSpeciesMix_PartialSuppression() {
      // 14:1:1 (3 species but Birch dominates). D = (196+1+1)/256 = 0.773.
      // sat 1, lin ≈ 0.66; concave dom = sqrt(0.66) ≈ 0.81.
      // Mono ≈ 0.6 * 0.81 = 0.49. Grassland = 0.6 * (1 - 0.49) ≈ 0.31.
      // Distinguishes from the even 3-species case above.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 0.6f,
          PlantableCount = 16,
          PlantableSpeciesCount = 3,
          PlantableDominance = 0.773f,
      };
      var expectedDom = (float)System.Math.Sqrt((0.773f - 1f / 3f) / (2f / 3f));
      var expectedMono = 0.6f * 1f * expectedDom;
      Assert.AreEqual(0.6f * (1f - expectedMono),
          BiomeTargets.Grassland(inputs), 1e-3f);
    }

    #endregion

    #region Monoculture

    [TestMethod]
    public void Monoculture_FullChunkSingleSpecies_TargetIsIrrigatedFraction() {
      // 16 plantables / 16-tile chunk -> sat 1; D=1 -> dom 1.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 0.7f,
          PlantableCount = 16,
          PlantableSpeciesCount = 1,
          PlantableDominance = 1f,
      };
      Assert.AreEqual(0.7f, BiomeTargets.Monoculture(inputs), 1e-4f);
    }

    [TestMethod]
    public void Monoculture_BelowMinCount_TargetIsZero() {
      // 2 < MonocultureMinCount (3) -> 0 regardless of dominance.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 2,
          PlantableSpeciesCount = 1,
          PlantableDominance = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Monoculture(inputs));
    }

    [TestMethod]
    public void Monoculture_PartialSaturationSingleSpecies_ScalesLinearly() {
      // 3 plantables / 16-tile chunk -> sat 0.1875; D=1 -> dom 1.
      // mono = 1 * 0.1875 * 1 = 0.1875.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 3,
          PlantableSpeciesCount = 1,
          PlantableDominance = 1f,
      };
      Assert.AreEqual(3f / 16f, BiomeTargets.Monoculture(inputs), 1e-3f);
    }

    [TestMethod]
    public void Monoculture_EvenTwoSpeciesFullChunk_HalfMono() {
      // 8:8 -> D = 0.5; threshold remap -> lin = (0.5 - 1/3) / (2/3) = 0.25;
      // concave (sqrt) -> dom = sqrt(0.25) = 0.5. mono = irrigated * 1 * 0.5.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 16,
          PlantableSpeciesCount = 2,
          PlantableDominance = 0.5f,
      };
      Assert.AreEqual(0.5f, BiomeTargets.Monoculture(inputs), 1e-3f);
    }

    [TestMethod]
    public void Monoculture_DominatedTwoSpeciesFullChunk_HighMono() {
      // 14:2 -> D = (196+4)/256 ≈ 0.781. lin = (0.781 - 1/3) / (2/3) ≈ 0.67;
      // concave (sqrt) -> dom ≈ 0.82. mono ≈ 0.82. Distinguishes from the
      // 8:8 case (0.5) -- distribution matters, not just species count.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 16,
          PlantableSpeciesCount = 2,
          PlantableDominance = 0.781f,
      };
      var expected = (float)System.Math.Sqrt((0.781f - 1f / 3f) / (2f / 3f));
      Assert.AreEqual(expected, BiomeTargets.Monoculture(inputs), 1e-3f);
    }

    [TestMethod]
    public void Monoculture_EvenThreeSpeciesMix_TargetIsZero() {
      // 5:5:5 -> D = 1/3 exactly -> dom = 0 -> mono = 0.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 15,
          PlantableSpeciesCount = 3,
          PlantableDominance = 1f / 3f,
      };
      Assert.AreEqual(0f, BiomeTargets.Monoculture(inputs), 1e-4f);
    }

    [TestMethod]
    public void Monoculture_DominatedThreeSpeciesMix_StillFires() {
      // 14:1:1 -> D = (196+1+1)/256 ≈ 0.773. lin ≈ 0.66; concave (sqrt) ->
      // dom ≈ 0.81. Still rates as ~0.81 mono because Birch dominates --
      // 3 species alone isn't enough to clear monoculture if one species
      // owns the chunk.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 16,
          PlantableSpeciesCount = 3,
          PlantableDominance = 0.773f,
      };
      var expected = (float)System.Math.Sqrt((0.773f - 1f / 3f) / (2f / 3f));
      Assert.AreEqual(expected, BiomeTargets.Monoculture(inputs), 1e-3f);
    }

    [TestMethod]
    public void Monoculture_EvenFourSpeciesMix_TargetIsZero() {
      // 4:4:4:4 -> D = 0.25 < threshold -> dom 0 -> mono 0.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 16,
          PlantableSpeciesCount = 4,
          PlantableDominance = 0.25f,
      };
      Assert.AreEqual(0f, BiomeTargets.Monoculture(inputs), 1e-4f);
    }

    [TestMethod]
    public void Monoculture_NoIrrigation_TargetIsZero() {
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 0f,
          PlantableCount = 16,
          PlantableSpeciesCount = 1,
          PlantableDominance = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Monoculture(inputs));
    }

    [TestMethod]
    public void Monoculture_OverflowedSaturation_ClampedToOne() {
      // Defensive: more plantables than tiles shouldn't blow up the
      // saturation factor.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          PlantableCount = 32,
          PlantableSpeciesCount = 1,
          PlantableDominance = 1f,
      };
      Assert.AreEqual(1f, BiomeTargets.Monoculture(inputs), 1e-4f);
    }

    #endregion

    #region Forest suppression by Monoculture

    [TestMethod]
    public void Forest_TreePlantationMonoculture_Suppressed() {
      // 16-tree single-species plantation: Forest's own factors saturate
      // (TreeCount/5 saturated, single-species diversity 0.5), but mono
      // is 1.0 -> Forest * (1 - 1) = 0.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 16,
          TreeSpeciesCount = 1,
          MatureTreeCount = 16,
          PlantableCount = 16,
          PlantableSpeciesCount = 1,
          PlantableDominance = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    [TestMethod]
    public void Forest_NaturalDiverseGrove_NotSuppressed() {
      // 3 evenly-mixed species -> D = 1/3 -> mono 0 -> no suppression.
      // All mature so the mature-canopy gate is 1 and doesn't intrude.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 15,
          TreeSpeciesCount = 3,
          MatureTreeCount = 15,
          PlantableCount = 15,
          PlantableSpeciesCount = 3,
          PlantableDominance = 1f / 3f,
      };
      Assert.AreEqual(1f, BiomeTargets.Forest(inputs), 1e-4f);
    }

    #endregion

    #region Forest limited by immaturity (vs genuine monoculture)

    // ForestLimitedByImmaturity is the discriminator behind the entity
    // panel's "still maturing" vs "lacks species diversity" message split.
    // It answers: is Forest being out-scored by Monoculture *only* because
    // its canopy hasn't grown up yet (so the area would read as Forest once
    // established), as opposed to being a genuine low-diversity planting?
    // Contract: MatureCanopyGate < 1  AND  ForestUngated > Monoculture.

    [TestMethod]
    public void ForestLimitedByImmaturity_YoungDiverseGrove_WouldBecomeForest_True() {
      // The false-positive case the split exists to fix: a dense, multi-
      // species planting of seedlings. The mature-canopy gate pins Forest
      // at 0 (all seedlings), so Monoculture out-scores it and the old code
      // showed "lacks diversity" -- but the un-gated Forest score already
      // beats Monoculture, so this WILL be a forest once it grows. The
      // honest message is "still maturing", so the discriminator is true.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 10,
          TreeSpeciesCount = 3,
          MatureTreeCount = 0,            // all seedlings -> gate = 0 < 1
          PlantableCount = 10,
          PlantableSpeciesCount = 3,
          PlantableDominance = 0.5f,      // skewed enough that Monoculture > 0
      };
      // Sanity: Forest is genuinely suppressed here (gate zeroes it) while
      // Monoculture is positive -- the suppression the message reacts to.
      Assert.AreEqual(0f, BiomeTargets.Forest(inputs), 1e-4f);
      Assert.IsTrue(BiomeTargets.Monoculture(inputs) > 0f);
      Assert.IsTrue(BiomeTargets.ForestUngated(inputs) > BiomeTargets.Monoculture(inputs));

      Assert.IsTrue(BiomeTargets.ForestLimitedByImmaturity(inputs));
    }

    [TestMethod]
    public void ForestLimitedByImmaturity_MatureCanopy_False() {
      // Same diverse/dense grove but the canopy is established (gate = 1).
      // Immaturity can't be the limiter once the trees are grown -- here
      // un-gated Forest already beats Monoculture so the chunk reads as
      // Forest outright (no suppression, no message at all).
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 10,
          TreeSpeciesCount = 3,
          MatureTreeCount = 10,           // fully mature -> gate = 1
          PlantableCount = 10,
          PlantableSpeciesCount = 3,
          PlantableDominance = 0.5f,
      };
      Assert.IsFalse(BiomeTargets.ForestLimitedByImmaturity(inputs));
    }

    [TestMethod]
    public void ForestLimitedByImmaturity_YoungSingleSpeciesFarm_StaysMonoculture_False() {
      // A young single-species tree farm: seedlings (gate < 1) but it will
      // NOT become a diverse forest -- un-gated Forest (diversity 0.5,
      // fully mono-suppressed) loses to Monoculture even once grown. The
      // diversity message is correct, not the maturity one, so the
      // discriminator is false despite the young canopy.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 16,
          TreeSpeciesCount = 1,
          MatureTreeCount = 0,            // young -> gate < 1
          PlantableCount = 16,
          PlantableSpeciesCount = 1,
          PlantableDominance = 1f,        // pure monoculture
      };
      Assert.IsTrue(BiomeTargets.MatureCanopyGate(inputs) < 1f);
      Assert.IsFalse(BiomeTargets.ForestUngated(inputs) > BiomeTargets.Monoculture(inputs));

      Assert.IsFalse(BiomeTargets.ForestLimitedByImmaturity(inputs));
    }

    [TestMethod]
    public void ForestLimitedByImmaturity_EmptyChunk_False() {
      // Nothing planted: no canopy to mature into. Gate is 0 (< 1) but
      // un-gated Forest is 0 and Monoculture is 0, so 0 > 0 is false --
      // the discriminator must not fire on an empty chunk.
      var inputs = new ChunkBiomeInputs { IrrigatedFraction = 1f };
      Assert.IsFalse(BiomeTargets.ForestLimitedByImmaturity(inputs));
    }

    [TestMethod]
    public void Forest_FactoredAsUngatedTimesGate_MatchesInlinedProduct() {
      // The Forest() split is behaviour-preserving: the product of the two
      // exposed factors equals the single Forest score. Partial gate so
      // both factors are non-trivial (12.5% mature -> gate 0.5).
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 16,
          TreeSpeciesCount = 3,
          MatureTreeCount = 2,            // gate = 0.5
          PlantableCount = 8,
          PlantableSpeciesCount = 2,
          PlantableDominance = 0.5f,
      };
      Assert.AreEqual(
          BiomeTargets.ForestUngated(inputs) * BiomeTargets.MatureCanopyGate(inputs),
          BiomeTargets.Forest(inputs), 1e-6f);
    }

    #endregion

    #region Water (clean)

    [TestMethod]
    public void River_HighFlowAnyDepth_TargetIsHighFlowFraction() {
      // River fires on any depth so long as flow is above the
      // high-flow threshold (which the adapter encodes into the
      // ShallowHighFlow / DeepHighFlow sub-fractions).
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          ShallowHighFlowWaterFraction = 0.6f,
          DeepHighFlowWaterFraction = 0.4f,
      };
      Assert.AreEqual(1f, BiomeTargets.River(inputs), 1e-4f);
    }

    [TestMethod]
    public void River_OnlyLowFlowWater_TargetIsZero() {
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          DeepSlowWaterFraction = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.River(inputs));
    }

    [TestMethod]
    public void River_ContaminatedWaterDoesNotZeroTarget() {
      // Water contamination is continuous; River and Badwater
      // accumulate in parallel.
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          ShallowHighFlowWaterFraction = 1f,
          ContaminatedWaterFraction = 0.5f,
      };
      Assert.AreEqual(1f, BiomeTargets.River(inputs), 1e-4f);
      Assert.AreEqual(0.5f, BiomeTargets.Badwater(inputs), 1e-4f);
    }

    [TestMethod]
    public void Lake_DeepSlow_TargetIsDeepSlowFraction() {
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          DeepSlowWaterFraction = 0.9f,
      };
      Assert.AreEqual(0.9f, BiomeTargets.Lake(inputs), 1e-4f);
    }

    [TestMethod]
    public void Lake_ShallowSlow_TargetIsZero() {
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          ShallowSlowWaterFraction = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Lake(inputs));
    }

    [TestMethod]
    public void Lake_DeepHighFlow_TargetIsZero() {
      // Deep but fast-flowing = River, not Lake.
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          DeepHighFlowWaterFraction = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Lake(inputs));
    }

    [TestMethod]
    public void Wetland_ShallowSlow_TargetIsShallowSlowFraction() {
      // No diversity gate any more -- shallow + slow water reads
      // as Wetland regardless of what plants are present.
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          ShallowSlowWaterFraction = 1f,
      };
      Assert.AreEqual(1f, BiomeTargets.Wetland(inputs), 1e-4f);
    }

    [TestMethod]
    public void Wetland_ShallowHighFlow_TargetIsZero() {
      // Fast-flowing shallow = River, not Wetland.
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          ShallowHighFlowWaterFraction = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Wetland(inputs));
    }

    [TestMethod]
    public void Wetland_DeepSlow_TargetIsZero() {
      // Deep slow = Lake, not Wetland.
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          DeepSlowWaterFraction = 1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Wetland(inputs));
    }

    #endregion

    #region Negative biomes

    [TestMethod]
    public void Dry_AllDryLand_TargetIsOne() {
      var inputs = new ChunkBiomeInputs { DryLandFraction = 1f };
      Assert.AreEqual(1f, BiomeTargets.Dry(inputs), 1e-4f);
    }

    [TestMethod]
    public void Dry_ContaminationDoesNotZeroTarget() {
      // Dry no longer gates on contamination. The chunk can have
      // both Dry and Contaminated scores rising in parallel.
      var inputs = new ChunkBiomeInputs {
          DryLandFraction = 1f,
          ContaminatedFraction = 0.5f,
      };
      Assert.AreEqual(1f, BiomeTargets.Dry(inputs), 1e-4f);
    }

    [TestMethod]
    public void Contaminated_TracksTotalContamination_IncludingWater() {
      // Contaminated stacks: its positive predicate covers ALL
      // contamination on the chunk, including the badwater-water
      // portion that also drives Badwater. Dominance between the two
      // is broken by the aggressor tiebreak in ChunkBiomeSampler.
      var inputs = new ChunkBiomeInputs {
          ContaminatedFraction = 0.7f,
          ContaminatedWaterFraction = 0.2f,
      };
      Assert.AreEqual(0.7f, BiomeTargets.Contaminated(inputs), 1e-4f);
    }

    [TestMethod]
    public void Contaminated_PureBadwaterChunk_FullSuitability() {
      // All-badwater chunk: ContaminatedFraction = ContaminatedWater = 1.
      // Contaminated and Badwater both target 1; tiebreak handled
      // downstream.
      var inputs = new ChunkBiomeInputs {
          ContaminatedFraction = 1f,
          ContaminatedWaterFraction = 1f,
      };
      Assert.AreEqual(1f, BiomeTargets.Contaminated(inputs), 1e-4f);
      Assert.AreEqual(1f, BiomeTargets.Badwater(inputs), 1e-4f);
    }

    [TestMethod]
    public void Badwater_ContaminatedWaterFraction() {
      var inputs = new ChunkBiomeInputs {
          ContaminatedWaterFraction = 0.6f,
      };
      Assert.AreEqual(0.6f, BiomeTargets.Badwater(inputs), 1e-4f);
    }

    #endregion

    #region Cave

    [TestMethod]
    public void Cave_FractionPassedThrough() {
      var inputs = new ChunkBiomeInputs { CaveFraction = 0.3f };
      Assert.AreEqual(0.3f, BiomeTargets.Cave(inputs), 1e-4f);
    }

    #endregion

    #region Dispatcher

    [TestMethod]
    public void Compute_DispatchesByKind() {
      var inputs = new ChunkBiomeInputs { CaveFraction = 0.4f };
      Assert.AreEqual(0.4f, BiomeTargets.Compute(BiomeKind.Cave, inputs), 1e-4f);
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Forest, inputs));
    }

    // Suitability is stateless and clamped to [0, 1]. Drought and
    // inundation are enforced naturally by the positive predicates
    // (IrrigatedFraction -> 0 when dry or flooded). Contamination
    // gets an explicit multiplicative cancellation factor inside
    // Compute, since contaminated land otherwise reads as
    // "irrigated land" from the moisture channel's perspective.

    [TestMethod]
    public void Compute_HealthyLand_FullDrought_TargetIsZero() {
      // Drought is handled by the positive predicate's IrrigatedFraction:
      // no irrigation -> positive=0. No explicit drought stress.
      var inputs = new ChunkBiomeInputs { DryLandFraction = 1f };
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Forest, inputs), 1e-6f);
    }

    [TestMethod]
    public void Compute_HealthyLand_FullInundation_TargetIsZero() {
      // Inundation is handled by IrrigatedFraction -> 0 when flooded.
      var inputs = new ChunkBiomeInputs { WaterFraction = 1f };
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Forest, inputs), 1e-6f);
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Grassland, inputs), 1e-6f);
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Monoculture, inputs), 1e-6f);
    }

    [TestMethod]
    public void Compute_HealthyLand_PartialInundation_ScalesProportionally() {
      // 50% irrigated + 50% flooded: positive predicate scales linearly
      // with IrrigatedFraction. No explicit inundation stress -- the
      // positive predicate IS the inundation signal.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 0.5f,
          WaterFraction = 0.5f,
          TreeCount = 10,
          TreeSpeciesCount = 3,
          MatureTreeCount = 10,
      };
      Assert.AreEqual(0.5f, BiomeTargets.Compute(BiomeKind.Forest, inputs), 1e-4f);
    }

    [TestMethod]
    public void Compute_HealthyLand_AnyContamination_CancelsTarget() {
      // 5% contamination already saturates the cancellation factor
      // (scale=20). A fully-suitable Forest reads as 0 the moment
      // contamination crosses ~5%.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 10,
          TreeSpeciesCount = 3,
          MatureTreeCount = 10,
          ContaminatedFraction = 0.5f,
      };
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Forest, inputs), 1e-6f);
    }

    [TestMethod]
    public void Compute_HealthyLand_SubThresholdContamination_PartialReduction() {
      // 2.5% contamination -> cancellation factor = 1 - 20*0.025 = 0.5.
      // Forest positive at 1.0 -> Compute returns 0.5.
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 10,
          TreeSpeciesCount = 3,
          MatureTreeCount = 10,
          ContaminatedFraction = 0.025f,
      };
      Assert.AreEqual(0.5f, BiomeTargets.Compute(BiomeKind.Forest, inputs), 1e-4f);
    }

    [TestMethod]
    public void Compute_HealthyLand_MultipleStressesAllProduceZero() {
      // Drought, inundation, and contamination all act independently
      // on the positive predicate. None of them stack in any
      // interesting way under the new model -- each is enough on its
      // own to zero out a healthy land biome. Verifying mixed inputs
      // still produce 0, just by virtue of IrrigatedFraction=0 (the
      // partition forces it when other fractions are present).
      var inputs = new ChunkBiomeInputs {
          DryLandFraction = 0.3f,
          WaterFraction = 0.5f,
          ContaminatedFraction = 0.2f,
          // IrrigatedFraction defaults to 0 -- positive predicate is 0.
      };
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Grassland, inputs), 1e-6f);
    }

    [TestMethod]
    public void Compute_Cave_ContaminationCancels() {
      var clean = new ChunkBiomeInputs { CaveFraction = 0.3f };
      Assert.AreEqual(0.3f, BiomeTargets.Compute(BiomeKind.Cave, clean), 1e-4f);

      var contam = new ChunkBiomeInputs {
          CaveFraction = 0.3f,
          ContaminatedFraction = 0.1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Cave, contam), 1e-6f);
    }

    [TestMethod]
    public void Compute_Dry_ContaminationCancels() {
      // Dry isn't itself a contamination state, so the cancellation
      // applies to it. Without it, Dry Suitability would stay high
      // in contaminated chunks and Dry Maturity wouldn't decay.
      var clean = new ChunkBiomeInputs { DryLandFraction = 1f };
      Assert.AreEqual(1f, BiomeTargets.Compute(BiomeKind.Dry, clean), 1e-4f);

      var contam = new ChunkBiomeInputs {
          DryLandFraction = 1f,
          ContaminatedFraction = 0.1f,
      };
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Dry, contam), 1e-6f);
    }

    [TestMethod]
    public void Compute_WaterBiome_WaterContamination_CancelsTarget() {
      // Lake uses ContaminatedWaterFraction (not total contamination).
      // 30% water contamination -> factor = 0 (well above kill threshold).
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          DeepSlowWaterFraction = 1f,
          ContaminatedWaterFraction = 0.3f,
      };
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Lake, inputs), 1e-6f);
    }

    [TestMethod]
    public void Compute_WaterBiome_LandContaminationDoesNotCancel() {
      // Land contamination on the shore doesn't make the river dirty:
      // water biomes use ContaminatedWaterFraction, not total.
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          DeepSlowWaterFraction = 1f,
          ContaminatedFraction = 0.5f,         // contaminated land on the same chunk
          ContaminatedWaterFraction = 0f,      // but the water itself is clean
      };
      Assert.AreEqual(1f, BiomeTargets.Compute(BiomeKind.Lake, inputs), 1e-4f);
    }

    [TestMethod]
    public void Compute_Contaminated_NoCancellation() {
      // Contaminated is the contamination state; cancellation doesn't
      // apply. Reads its positive predicate directly.
      var inputs = new ChunkBiomeInputs { ContaminatedFraction = 1f };
      Assert.AreEqual(1f, BiomeTargets.Compute(BiomeKind.Contaminated, inputs), 1e-4f);
    }

    [TestMethod]
    public void Compute_Badwater_NoCancellation() {
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          ShallowSlowWaterFraction = 1f,
          ContaminatedFraction = 1f,
          ContaminatedWaterFraction = 1f,
      };
      Assert.AreEqual(1f, BiomeTargets.Compute(BiomeKind.Badwater, inputs), 1e-4f);
      Assert.AreEqual(1f, BiomeTargets.Compute(BiomeKind.Contaminated, inputs), 1e-4f);
      // And the stacking-cancellation effect: the same chunk's
      // Wetland reads as 0 (contamination cancellation via the water
      // contamination fraction, which the badwater contributes to).
      Assert.AreEqual(0f, BiomeTargets.Compute(BiomeKind.Wetland, inputs), 1e-6f);
    }

    [TestMethod]
    public void Compute_AllResultsInRangeZeroToOne() {
      // Sanity: across a range of inputs, no biome returns out of [0, 1].
      var stressful = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 100, TreeSpeciesCount = 100,
          ContaminatedFraction = 0.01f,
      };
      foreach (BiomeKind biome in BiomeValueKinds.AllBiomes) {
        var v = BiomeTargets.Compute(biome, stressful);
        Assert.IsTrue(v >= 0f && v <= 1f, $"{biome} = {v} out of [0,1]");
      }
    }

    #endregion

  }

}
