using Keystone.Core.Biomes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  [TestClass]
  public class MaturityParametersTests {

    #region Ceiling table

    [TestMethod]
    public void Ceiling_HealthyBiomes_ReturnDefault() {
      Assert.AreEqual(30f, MaturityParameters.Ceiling(BiomeKind.Forest));
      Assert.AreEqual(30f, MaturityParameters.Ceiling(BiomeKind.Grassland));
      Assert.AreEqual(30f, MaturityParameters.Ceiling(BiomeKind.Wetland));
      Assert.AreEqual(30f, MaturityParameters.Ceiling(BiomeKind.River));
      Assert.AreEqual(30f, MaturityParameters.Ceiling(BiomeKind.Lake));
      Assert.AreEqual(30f, MaturityParameters.Ceiling(BiomeKind.Cave));
    }

    [TestMethod]
    public void Ceiling_Badwater_Is15() {
      Assert.AreEqual(15f, MaturityParameters.Ceiling(BiomeKind.Badwater));
    }

    [TestMethod]
    public void Ceiling_Dry_Is10() {
      Assert.AreEqual(10f, MaturityParameters.Ceiling(BiomeKind.Dry));
    }

    [TestMethod]
    public void Ceiling_Contaminated_Is12p5() {
      // Deep toxic ceiling (raised from 3.5): an entrenched contamination
      // scar is a ~12.5-day reclamation, not a trivially-cleared blemish.
      // Decoupled from Monoculture, which keeps the old 3.5 (it is not a
      // toxic scar -- IsNegative(Monoculture) is false).
      Assert.AreEqual(12.5f, MaturityParameters.Ceiling(BiomeKind.Contaminated));
    }

    [TestMethod]
    public void Ceiling_Monoculture_Is3p5() {
      // Monoculture stays shallow -- it reflects prompt player planting,
      // not a persistent scar. It only ever shared the literal 3.5 with
      // Contaminated by coincidence; the toxic-ceiling deepening does not
      // touch it.
      Assert.AreEqual(3.5f, MaturityParameters.Ceiling(BiomeKind.Monoculture));
    }

    #endregion

    #region For() derives BetaAccrue from ceiling

    [TestMethod]
    public void For_HealthyBiome_BetaAccrueIsOneOverThirty() {
      var (alpha, betaAccrue) = MaturityParameters.For(BiomeKind.Forest);
      Assert.AreEqual(1f, alpha);
      Assert.AreEqual(1f / 30f, betaAccrue, 1e-6f);
    }

    [TestMethod]
    public void For_Badwater_BetaAccrueIsOneOver15() {
      var (_, betaAccrue) = MaturityParameters.For(BiomeKind.Badwater);
      Assert.AreEqual(1f / 15f, betaAccrue, 1e-6f);
    }

    [TestMethod]
    public void For_Contaminated_BetaAccrueIsOneOver12p5() {
      var (_, betaAccrue) = MaturityParameters.For(BiomeKind.Contaminated);
      Assert.AreEqual(1f / 12.5f, betaAccrue, 1e-6f);
    }

    [TestMethod]
    public void For_Asymptote_EqualsCeiling() {
      // Asymptote at Suitability=1 is Alpha / BetaAccrue, which by
      // construction equals Ceiling. Validate the round-trip across
      // every biome.
      foreach (BiomeKind biome in BiomeValueKinds.AllBiomes) {
        var (alpha, betaAccrue) = MaturityParameters.For(biome);
        var asymptote = alpha / betaAccrue;
        Assert.AreEqual(MaturityParameters.Ceiling(biome), asymptote, 1e-4f,
            $"{biome}: asymptote {asymptote} != ceiling {MaturityParameters.Ceiling(biome)}");
      }
    }

    #endregion

    #region Polarity + fallback

    [TestMethod]
    public void IsNegative_Negative_True() {
      Assert.IsTrue(MaturityParameters.IsNegative(BiomeKind.Dry));
      Assert.IsTrue(MaturityParameters.IsNegative(BiomeKind.Contaminated));
      Assert.IsTrue(MaturityParameters.IsNegative(BiomeKind.Badwater));
    }

    [TestMethod]
    public void IsNegative_Positive_False() {
      Assert.IsFalse(MaturityParameters.IsNegative(BiomeKind.Forest));
      Assert.IsFalse(MaturityParameters.IsNegative(BiomeKind.Grassland));
      Assert.IsFalse(MaturityParameters.IsNegative(BiomeKind.Wetland));
      Assert.IsFalse(MaturityParameters.IsNegative(BiomeKind.Monoculture));
    }

    [TestMethod]
    public void FallbackDecayDays_Positive_IsSeven() {
      Assert.AreEqual(7f, MaturityParameters.FallbackDecayDays(BiomeKind.Forest));
      Assert.AreEqual(7f, MaturityParameters.FallbackDecayDays(BiomeKind.Monoculture));
    }

    [TestMethod]
    public void FallbackDecayDays_BadwaterAndContaminated_DerivedFromBaselineRate() {
      // Badwater and Contaminated decay at BaselineDecayRatePerDay
      // (1/day) when not self-dominant. Clear time = ceiling / rate.
      // Badwater ceiling 15 -> 15d; Contaminated 12.5 -> 12.5d.
      Assert.AreEqual(15f, MaturityParameters.FallbackDecayDays(BiomeKind.Badwater));
      Assert.AreEqual(12.5f, MaturityParameters.FallbackDecayDays(BiomeKind.Contaminated));
    }

    [TestMethod]
    public void FallbackDecayDays_Dry_KeepsOriginalScarPersistence() {
      // Dry's self/null-dominant fallback intentionally stays at 70d
      // (slow scar persistence). The matrix's per-dominant row is the
      // design path for Dry decay (aggressor acceleration at 0.5d,
      // healthy-dominant fast clear at 1d); the fallback fires only
      // in the degenerate no-aggressor case and is kept slow so
      // internal dynamics don't wipe a scar with nothing actively
      // clearing it.
      Assert.AreEqual(70f, MaturityParameters.FallbackDecayDays(BiomeKind.Dry));
    }

    #endregion

    #region Scar-gate thresholds

    [TestMethod]
    public void BadwaterScarGateThreshold_Is0p1() {
      Assert.AreEqual(0.1f, MaturityParameters.BadwaterScarGateThreshold);
    }

    [TestMethod]
    public void ContaminatedScarGateThreshold_Is0p5() {
      Assert.AreEqual(0.5f, MaturityParameters.ContaminatedScarGateThreshold);
    }

    #endregion

    #region Drought-intensity floor

    [TestMethod]
    public void DroughtFloor_WaterFamily_Is0p1() {
      // Water-family biomes are defined by the water input -- when the
      // water leaves they begin to feel it immediately, regardless of
      // how deeply Dry has set in. Floor 0.1 means the matrix rate
      // kicks in at 10% strength from tick 1 (1 M/day for ceiling-30
      // biomes at the (X, Dry) matrix rate of 10/day), climbing to
      // 100% as Dry's Maturity reaches its ceiling. The takeover
      // duration for a mature river ends up around 8 days under a
      // fresh drought, vs the 3-day matrix nominal under a mature one.
      Assert.AreEqual(0.1f, MaturityParameters.DroughtFloor(BiomeKind.River));
      Assert.AreEqual(0.1f, MaturityParameters.DroughtFloor(BiomeKind.Wetland));
      Assert.AreEqual(0.1f, MaturityParameters.DroughtFloor(BiomeKind.Lake));
    }

    [TestMethod]
    public void DroughtFloor_GrasslandAndForest_IsZero() {
      // Grassland/Forest buffer nascent drought via root systems and
      // soil banking; decay only kicks in once Dry's Maturity has
      // climbed. Floor 0 makes that explicit.
      Assert.AreEqual(0f, MaturityParameters.DroughtFloor(BiomeKind.Grassland));
      Assert.AreEqual(0f, MaturityParameters.DroughtFloor(BiomeKind.Forest));
    }

    [TestMethod]
    public void DroughtFloor_PassiveBiomes_IsZero() {
      // Cave and Monoculture are passive in the current design; floor
      // 0 is the placeholder pending real treatment, not a final
      // statement about their drought response.
      Assert.AreEqual(0f, MaturityParameters.DroughtFloor(BiomeKind.Cave));
      Assert.AreEqual(0f, MaturityParameters.DroughtFloor(BiomeKind.Monoculture));
    }

    [TestMethod]
    public void DroughtSaturationMaturity_Is3p33() {
      // Drought saturates at ~33% of Dry's own ceiling. Decoupled from
      // Ceiling(Dry) to avoid the intensity ramp inheriting Dry's
      // slow time constant. Under Dry Suitability=1, M_dry reaches
      // this value at t ~ 4 days.
      Assert.AreEqual(3.33f, MaturityParameters.DroughtSaturationMaturity, 1e-4f);
    }

    #endregion

    #region Dry-column per-biome clear times

    [TestMethod]
    public void DecayClearTime_RiverDecaying_DryDominant_0p7d() {
      // Saturated 0.7d, fresh ~2d. Rivers are defined by their water
      // input -- once the source is gone they die fast.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.River, BiomeKind.Dry);
      Assert.AreEqual(0.7f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_LakeDecaying_DryDominant_0p7d() {
      // Same as River: defined by inflow, dies fast without it.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Lake, BiomeKind.Dry);
      Assert.AreEqual(0.7f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_WetlandDecaying_DryDominant_1p8d() {
      // Saturated 1.8d, fresh ~3.5d. Wetlands hold a buffer of standing
      // moisture but break down fast once that buffer drains.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Wetland, BiomeKind.Dry);
      Assert.AreEqual(1.8f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_GrasslandDecaying_DryDominant_2p1d() {
      // Saturated 2.1d, fresh ~4d. Grasses bank moisture in deep roots
      // but eventually surrender to sustained drought.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Grassland, BiomeKind.Dry);
      Assert.AreEqual(2.1f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_ForestDecaying_DryDominant_4p1d() {
      // Saturated 4.1d, fresh ~6d. Forests have the slowest drought
      // kill time among healthy biomes -- deep root systems and shade
      // canopy maintain microclimates longer.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Forest, BiomeKind.Dry);
      Assert.AreEqual(4.1f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_CaveDecaying_DryDominant_FallsToDefault3d() {
      // Cave is moisture-independent in lore but has no biome content
      // yet; the Dry-column lookup falls through to the column default
      // (3d) until Cave is actively designed.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Cave, BiomeKind.Dry);
      Assert.AreEqual(3f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_MonocultureDecaying_DryDominant_FallsToDefault3d() {
      // Monoculture is structurally an unhealthy biome (depleted soil,
      // low biodiversity) and is deferred pending a dedicated pass.
      // For now the Dry-column lookup falls through to the column
      // default (3d), matching the pre-redesign behavior.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Monoculture, BiomeKind.Dry);
      Assert.AreEqual(3f, clearTime);
    }

    #endregion

    #region Decay-rate matrix (clear-time interpretation)

    // Column defaults (one cell per dominant biome that hits the
    // column default with no row/cell override applying).

    [TestMethod]
    public void DecayClearTime_ForestDecaying_GrasslandDominant_LandFamily7d() {
      var (coPresent, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Forest, BiomeKind.Grassland);
      Assert.IsFalse(coPresent);
      Assert.AreEqual(7f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_ForestDecaying_RiverDominant_WaterFamily5d() {
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Forest, BiomeKind.River);
      Assert.AreEqual(5f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_ForestDecaying_BadwaterDominant_HalfDay() {
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Forest, BiomeKind.Badwater);
      Assert.AreEqual(0.5f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_ForestDecaying_ContaminatedDominant_OneDay() {
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Forest, BiomeKind.Contaminated);
      Assert.AreEqual(1f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_ForestDecaying_CaveDominant_FourteenDays() {
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Forest, BiomeKind.Cave);
      Assert.AreEqual(14f, clearTime);
    }

    // Row overrides.

    [TestMethod]
    public void DecayClearTime_BadwaterDecaying_AnyDominant_DerivedFromBaselineRate() {
      // Badwater row is uniform: ceiling 15 / 1-per-day rate = 15d.
      // Independent of dominant -- the rate is a property of the
      // decaying biome alone.
      var (_, hlContaminated) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Badwater, BiomeKind.Contaminated);
      var (_, hlForest) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Badwater, BiomeKind.Forest);
      var (_, hlDry) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Badwater, BiomeKind.Dry);
      Assert.AreEqual(15f, hlContaminated);
      Assert.AreEqual(15f, hlForest);
      Assert.AreEqual(15f, hlDry);
    }

    [TestMethod]
    public void DecayClearTime_ContaminatedDecaying_BadwaterDominant_CoPresent() {
      var (coPresent, _) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Contaminated, BiomeKind.Badwater);
      Assert.IsTrue(coPresent);
    }

    [TestMethod]
    public void DecayClearTime_ContaminatedDecaying_NonToxicDominant_12p5d() {
      var (_, hlForest) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Contaminated, BiomeKind.Forest);
      var (_, hlDry) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Contaminated, BiomeKind.Dry);
      Assert.AreEqual(12.5f, hlForest);
      Assert.AreEqual(12.5f, hlDry);
    }

    [TestMethod]
    public void DecayClearTime_DryDecaying_BadwaterDominant_ColumnDefault0p5() {
      // Design intent: toxic biomes structurally kill dry land fast.
      // 0.5d at ceiling 10 = rate 20/day -- aggressively cleared.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Dry, BiomeKind.Badwater);
      Assert.AreEqual(0.5f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_DryDecaying_HealthyDominant_LowCeilingFastClear1d() {
      // Design intent: dry land is easily cleared once moisture or
      // shade returns. 1d clear time at ceiling 10 = rate 10/day.
      var (_, hlContaminated) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Dry, BiomeKind.Contaminated);
      var (_, hlRiver) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Dry, BiomeKind.River);
      var (_, hlForest) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Dry, BiomeKind.Forest);
      var (_, hlCave) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Dry, BiomeKind.Cave);
      Assert.AreEqual(1f, hlContaminated);
      Assert.AreEqual(1f, hlRiver);
      Assert.AreEqual(1f, hlForest);
      Assert.AreEqual(1f, hlCave);
    }

    [TestMethod]
    public void DecayClearTime_MonocultureDecaying_GrasslandOrForestDominant_3d() {
      var (_, hlGra) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Monoculture, BiomeKind.Grassland);
      var (_, hlFor) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Monoculture, BiomeKind.Forest);
      Assert.AreEqual(3f, hlGra);
      Assert.AreEqual(3f, hlFor);
    }

    [TestMethod]
    public void DecayClearTime_MonocultureDecaying_RiverDominant_FallsToColumnDefault5d() {
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Monoculture, BiomeKind.River);
      Assert.AreEqual(5f, clearTime);
    }

    // Cell overrides.

    [TestMethod]
    public void DecayClearTime_WetlandDecaying_RiverDominant_FlowErosion3d() {
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Wetland, BiomeKind.River);
      Assert.AreEqual(3f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_LakeDecaying_RiverDominant_FlowErosion3d() {
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Lake, BiomeKind.River);
      Assert.AreEqual(3f, clearTime);
    }

    // Succession-free peer drift cells. Design intent: Lake/River are
    // successors over Wetland. The successor side hits the matrix
    // fast rate; the peer side drifts at BaselineDecayRatePerDay
    // (1/day), giving a 30d clear time on Wetland-family ceilings.
    // Grassland-under-Forest is no longer in this group (see the
    // half-aggression test below).

    [TestMethod]
    public void DecayClearTime_GrasslandDecaying_ForestDominant_HalfAggression15d() {
      // Forest pushes back against Grassland at half the rate
      // Grassland gets to use against Forest (which hits the 7d
      // land-family default). Rate = 2 * baseline = 2/day ->
      // ceiling 30 / 2 = 15d. Preserves the Grassland-is-successor
      // asymmetry while removing the indefinite-drift behaviour the
      // original peer-drift cell had.
      var (coPresent, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Grassland, BiomeKind.Forest);
      Assert.IsFalse(coPresent);
      Assert.AreEqual(15f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_RiverDecaying_WetlandDominant_HalfAggression15d() {
      // Wetlands reclaim river channels back to slow-flow vegetative
      // water at twice baseline (2/day, 15d clear). Reverse direction
      // (Wetland under River) stays at 3d flow erosion -- the
      // successional asymmetry is preserved.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.River, BiomeKind.Wetland);
      Assert.AreEqual(15f, clearTime);
    }

    [TestMethod]
    public void DecayClearTime_LakeDecaying_WetlandDominant_PeerBaseline30d() {
      // Wetlands don't drain lakes; Lake drifts at 1/day.
      // Reverse direction (Wetland under Lake) stays at the 5d
      // water-family column default.
      var (_, clearTime) = MaturityParameters.DecayClearTimeDays(
          BiomeKind.Lake, BiomeKind.Wetland);
      Assert.AreEqual(30f, clearTime);
    }

    // Self-dominant is invalid input.

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void DecayClearTime_SameBiomeOnBothAxes_Throws() {
      MaturityParameters.DecayClearTimeDays(BiomeKind.Forest, BiomeKind.Forest);
    }

    // DecayRate derives linear rate from clear time and ceiling.

    [TestMethod]
    public void DecayRate_ForestUnderGrassland_4p29PerDay() {
      // Forest ceiling 30, clear time 7d -> rate 30/7.
      var (_, rate) = MaturityParameters.DecayRate(
          BiomeKind.Forest, BiomeKind.Grassland);
      Assert.AreEqual(30f / 7f, rate, 1e-4f);
    }

    [TestMethod]
    public void DecayRate_ForestUnderBadwater_60PerDay() {
      // Forest ceiling 30, clear time 0.5d -> rate 60.
      var (_, rate) = MaturityParameters.DecayRate(
          BiomeKind.Forest, BiomeKind.Badwater);
      Assert.AreEqual(60f, rate, 1e-4f);
    }

    [TestMethod]
    public void DecayRate_BadwaterUnderForest_1PerDay_Baseline() {
      // Badwater decays at BaselineDecayRatePerDay (1/day)
      // independent of the dominant biome. Ceiling 15 / 15d clear = 1.
      var (_, rate) = MaturityParameters.DecayRate(
          BiomeKind.Badwater, BiomeKind.Forest);
      Assert.AreEqual(1f, rate, 1e-4f);
    }

    [TestMethod]
    public void DecayRate_ContaminatedUnderForest_1PerDay_ScarFade() {
      // Contaminated ceiling 12.5, clear time 12.5d -> rate 1.0.
      var (_, rate) = MaturityParameters.DecayRate(
          BiomeKind.Contaminated, BiomeKind.Forest);
      Assert.AreEqual(1f, rate, 1e-4f);
    }

    [TestMethod]
    public void DecayRate_CoPresent_RateIsZero() {
      var (coPresent, rate) = MaturityParameters.DecayRate(
          BiomeKind.Contaminated, BiomeKind.Badwater);
      Assert.IsTrue(coPresent);
      Assert.AreEqual(0f, rate);
    }

    // Complete-row spot check: every dominant-biome column reachable
    // from Forest (no row override) returns its declared column default
    // or cell override. Anchors all 10 non-self columns.

    [TestMethod]
    public void DecayClearTime_ForestRow_FullSpec() {
      // Anchors Forest's clear time across every dominant biome.
      // Column defaults for Badwater/Contaminated/River/Wetland/Lake/
      // Grassland/Monoculture/Cave; Dry uses the per-biome
      // Dry-column override at 4.1d (saturated; ~6d under fresh
      // drought after the intensity ramp).
      var expected = new (BiomeKind dominant, float clearTimeDays)[] {
          (BiomeKind.Badwater, 0.5f),
          (BiomeKind.Contaminated, 1f),
          (BiomeKind.Dry, 4.1f),
          (BiomeKind.River, 5f),
          (BiomeKind.Wetland, 5f),
          (BiomeKind.Lake, 5f),
          (BiomeKind.Grassland, 7f),
          (BiomeKind.Monoculture, 7f),
          (BiomeKind.Cave, 14f),
      };
      foreach (var (dominant, expectedDays) in expected) {
        var (coPresent, clearTime) = MaturityParameters.DecayClearTimeDays(
            BiomeKind.Forest, dominant);
        Assert.IsFalse(coPresent, $"({dominant}) should not be co-present for Forest decaying");
        Assert.AreEqual(expectedDays, clearTime,
            $"Forest decaying under {dominant}: expected {expectedDays}d, got {clearTime}d");
      }
    }

    #endregion

  }

}
