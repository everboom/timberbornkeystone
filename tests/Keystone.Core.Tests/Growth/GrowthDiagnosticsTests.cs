using Keystone.Core.Biomes;
using Keystone.Core.Growth;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Growth {

  [TestClass]
  public class GrowthDiagnosticsTests {

    #region Helpers

    // Builds a baseline "nothing happening" Forest signal; tests override
    // only the fields relevant to the verdict they pin.
    private static GrowthSignals Forest(
        float suit = 0f, float mat = 0f, float bonus = 0f,
        BiomeKind? domMat = null, float domMatFrac = 0f,
        BiomeKind? domSuit = null,
        float gate = -1f, bool wouldBeForest = false) =>
        new GrowthSignals {
            TargetBiome = BiomeKind.Forest,
            Suitability = suit,
            MaturityFraction = mat,
            BonusFraction = bonus,
            DominantByMaturity = domMat,
            DominantMaturityFraction = domMatFrac,
            DominantBySuitability = domSuit,
            MatureCanopyGate = gate,
            WouldBeForestFavorable = wouldBeForest,
        };

    #endregion

    #region Verdict — Thriving / Benefiting

    [TestMethod]
    public void Classify_EstablishedAndFavorable_Thriving() {
      // Both axes high: established forest in favorable conditions.
      var s = Forest(suit: 0.9f, mat: 0.8f, bonus: 0.9f,
          domMat: BiomeKind.Forest, domMatFrac: 0.8f, domSuit: BiomeKind.Forest);
      Assert.AreEqual(GrowthVerdict.Thriving, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_MeaningfulBonusOneAxisBuilding_Benefiting() {
      // Established maturity but weak current conditions -> still a real
      // bonus from the maturity half. Not Thriving (suit < favorable),
      // not hostile.
      var s = Forest(suit: 0.2f, mat: 0.8f, bonus: 0.5f,
          domMat: BiomeKind.Forest, domMatFrac: 0.8f, domSuit: BiomeKind.Grassland);
      Assert.AreEqual(GrowthVerdict.Benefiting, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_BonusBelowMargin_NotBenefiting() {
      // The premature-positive fix: a tiny blend (8% of max) must NOT
      // read as a positive verdict. Here it falls through to Dormant.
      var s = Forest(suit: 0.05f, mat: 0.05f, bonus: 0.08f);
      Assert.AreNotEqual(GrowthVerdict.Benefiting, GrowthDiagnostics.Classify(s));
      Assert.AreEqual(GrowthVerdict.Dormant, GrowthDiagnostics.Classify(s));
    }

    #endregion

    #region Verdict — Hostile

    [TestMethod]
    public void Classify_ToxicObstacle_HostileEvenWithResidualBonus() {
      // Toxic ground is urgent: it overrides a residual-maturity bonus
      // that would otherwise read as Benefiting.
      var s = Forest(suit: 0f, mat: 0.8f, bonus: 0.4f,
          domMat: BiomeKind.Contaminated, domMatFrac: 0.6f,
          domSuit: BiomeKind.Contaminated);
      Assert.AreEqual(GrowthVerdict.Hostile, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_DryLandForest_Hostile() {
      // Land plant on dry ground (moisture mismatch), no bonus to mask it.
      var s = Forest(suit: 0f, domSuit: BiomeKind.Dry);
      Assert.AreEqual(GrowthVerdict.Hostile, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_DrainedWetlandPlant_Hostile() {
      // Water plant where conditions read as Dry -> hostile mismatch.
      var s = new GrowthSignals {
          TargetBiome = BiomeKind.Wetland,
          DominantBySuitability = BiomeKind.Dry,
          MatureCanopyGate = -1f,
      };
      Assert.AreEqual(GrowthVerdict.Hostile, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_NonToxicBonus_BeatsMoistureMismatch() {
      // A flooded old forest still on a meaningful residual bonus reads as
      // Benefiting (not Hostile): moisture mismatch sits *below* the bonus
      // margin in the cascade; only toxic overrides a bonus.
      var s = Forest(suit: 0.1f, mat: 0.8f, bonus: 0.45f,
          domMat: BiomeKind.Forest, domMatFrac: 0.8f, domSuit: BiomeKind.Wetland);
      Assert.AreEqual(GrowthVerdict.Benefiting, GrowthDiagnostics.Classify(s));
    }

    #endregion

    #region Verdict — Establishing / WrongBiome / Dormant

    [TestMethod]
    public void Classify_YoungDiverseSeedlingsZeroSuitability_Potential() {
      // Diverse dense saplings on grassland: the canopy gate holds Forest
      // suitability at ~0, no maturity yet, but the un-gated score is
      // favorable. It WILL become Forest, but nothing is happening now, so
      // it reads Potential (future tense) — NOT "establishing now". This is
      // the case the suitability split exists to get right.
      var s = Forest(suit: 0.04f, mat: 0f, bonus: 0.02f,
          domMat: BiomeKind.Grassland, domMatFrac: 0.47f,
          domSuit: BiomeKind.Grassland,
          gate: 0f, wouldBeForest: true);
      Assert.AreEqual(GrowthVerdict.Potential, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_CanopyViableWithRealSuitability_Establishing() {
      // Canopy partway grown: real current suitability (>= SuitabilityWeak)
      // but maturity not yet accrued. Present-tense Establishing. (Bonus set
      // artificially below the margin so the cascade reaches this branch
      // rather than Benefiting — in live play a suitability this high would
      // usually read Benefiting first.)
      var s = Forest(suit: 0.3f, mat: 0f, bonus: 0.05f,
          domMat: BiomeKind.Grassland, domMatFrac: 0.2f,
          domSuit: BiomeKind.Forest,
          gate: 0.5f, wouldBeForest: true);
      Assert.AreEqual(GrowthVerdict.Establishing, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_FavorableButNotYetMature_Establishing() {
      // Conditions favor the target (suit high) but maturity hasn't
      // accrued -> on track. (Non-Forest path: no canopy gate involved.)
      var s = new GrowthSignals {
          TargetBiome = BiomeKind.Wetland,
          Suitability = 0.7f,
          MaturityFraction = 0.1f,
          BonusFraction = 0.0f,         // below margin, so not Benefiting
          DominantBySuitability = BiomeKind.Wetland,
          MatureCanopyGate = -1f,
      };
      Assert.AreEqual(GrowthVerdict.Establishing, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_YoungSingleSpeciesFarm_NotEstablishing() {
      // Young, but won't become a forest: the un-gated score is below the
      // favorable bar (mono-suppressed), so WouldBeForestFavorable is false
      // and it is NOT Establishing. Monoculture has established here
      // -> WrongBiome.
      var s = Forest(suit: 0.02f, mat: 0f, bonus: 0.01f,
          domMat: BiomeKind.Monoculture, domMatFrac: 0.6f,
          domSuit: BiomeKind.Monoculture,
          gate: 0f, wouldBeForest: false);
      Assert.AreNotEqual(GrowthVerdict.Establishing, GrowthDiagnostics.Classify(s));
      Assert.AreEqual(GrowthVerdict.WrongBiome, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_OtherBiomeEstablishedNotFavored_WrongBiome() {
      // Established Grassland, target Forest not favored, nothing toxic or
      // on-track -> WrongBiome.
      var s = Forest(suit: 0.1f, mat: 0f, bonus: 0f,
          domMat: BiomeKind.Grassland, domMatFrac: 0.5f,
          domSuit: BiomeKind.Grassland,
          gate: 1f, wouldBeForest: false);   // canopy "mature" so not Establishing
      Assert.AreEqual(GrowthVerdict.WrongBiome, GrowthDiagnostics.Classify(s));
    }

    [TestMethod]
    public void Classify_BareGround_Dormant() {
      // Nothing established, conditions poor, nothing hostile on-tile.
      var s = Forest(suit: 0.1f, mat: 0f, bonus: 0f,
          domMat: null, domSuit: BiomeKind.Grassland, gate: 1f);
      Assert.AreEqual(GrowthVerdict.Dormant, GrowthDiagnostics.Classify(s));
    }

    #endregion

    #region Hostility predicates

    [TestMethod]
    public void IsToxic_OnlyContaminatedAndBadwater() {
      Assert.IsTrue(GrowthDiagnostics.IsToxic(BiomeKind.Contaminated));
      Assert.IsTrue(GrowthDiagnostics.IsToxic(BiomeKind.Badwater));
      Assert.IsFalse(GrowthDiagnostics.IsToxic(BiomeKind.Dry));
      Assert.IsFalse(GrowthDiagnostics.IsToxic(null));
    }

    [TestMethod]
    public void IsMoistureMismatch_LandVsWaterTargets() {
      // Land target (Forest): flooded by water biomes.
      Assert.IsTrue(GrowthDiagnostics.IsMoistureMismatch(BiomeKind.Forest, BiomeKind.Wetland));
      Assert.IsTrue(GrowthDiagnostics.IsMoistureMismatch(BiomeKind.Forest, BiomeKind.Dry));
      Assert.IsFalse(GrowthDiagnostics.IsMoistureMismatch(BiomeKind.Forest, BiomeKind.Grassland));
      Assert.IsFalse(GrowthDiagnostics.IsMoistureMismatch(BiomeKind.Forest, BiomeKind.Monoculture));
      // Water target (Wetland): drained/scoured, but not by standing
      // Wetland conditions themselves.
      Assert.IsTrue(GrowthDiagnostics.IsMoistureMismatch(BiomeKind.Wetland, BiomeKind.Dry));
      Assert.IsTrue(GrowthDiagnostics.IsMoistureMismatch(BiomeKind.Wetland, BiomeKind.River));
      Assert.IsFalse(GrowthDiagnostics.IsMoistureMismatch(BiomeKind.Wetland, BiomeKind.Wetland));
    }

    #endregion

    #region Display tiers

    [TestMethod]
    public void MaturityTierOf_Buckets() {
      Assert.AreEqual(MaturityTier.None, GrowthDiagnostics.MaturityTierOf(0.05f));
      Assert.AreEqual(MaturityTier.Emerging, GrowthDiagnostics.MaturityTierOf(0.2f));
      Assert.AreEqual(MaturityTier.Established, GrowthDiagnostics.MaturityTierOf(0.5f));
      Assert.AreEqual(MaturityTier.Thriving, GrowthDiagnostics.MaturityTierOf(0.9f));
    }

    [TestMethod]
    public void SuitabilityTierOf_Buckets() {
      Assert.AreEqual(SuitabilityTier.Poor, GrowthDiagnostics.SuitabilityTierOf(0.1f));
      Assert.AreEqual(SuitabilityTier.Weak, GrowthDiagnostics.SuitabilityTierOf(0.4f));
      Assert.AreEqual(SuitabilityTier.Good, GrowthDiagnostics.SuitabilityTierOf(0.6f));
      Assert.AreEqual(SuitabilityTier.Ideal, GrowthDiagnostics.SuitabilityTierOf(0.8f));
    }

    #endregion

  }

}
