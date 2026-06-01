using Keystone.Core.Biomes;
using Keystone.Core.Growth;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Growth {

  [TestClass]
  public class GrowthBonusCalculatorTests {

    #region ComputeBonus

    [TestMethod]
    public void ComputeBonus_ZeroSuitabilityZeroMaturity_ReturnsZero() {
      var result = GrowthBonusCalculator.ComputeBonus(0f, 0f, 30f);
      Assert.AreEqual(0f, result);
    }

    [TestMethod]
    public void ComputeBonus_FullSuitabilityFullMaturity_ReturnsMaxBonus() {
      var result = GrowthBonusCalculator.ComputeBonus(1f, 30f, 30f);
      Assert.AreEqual(GrowthBonusCalculator.DefaultMaxBonus, result, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_FullSuitabilityZeroMaturity_ReturnsHalfMaxBonus() {
      var result = GrowthBonusCalculator.ComputeBonus(1f, 0f, 30f);
      var expected = GrowthBonusCalculator.DefaultMaxBonus * 0.5f;
      Assert.AreEqual(expected, result, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_ZeroSuitabilityFullMaturity_ReturnsHalfMaxBonus() {
      var result = GrowthBonusCalculator.ComputeBonus(0f, 30f, 30f);
      var expected = GrowthBonusCalculator.DefaultMaxBonus * 0.5f;
      Assert.AreEqual(expected, result, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_HalfSuitabilityHalfMaturity_ReturnsHalfMaxBonus() {
      var result = GrowthBonusCalculator.ComputeBonus(0.5f, 15f, 30f);
      var expected = GrowthBonusCalculator.DefaultMaxBonus * 0.5f;
      Assert.AreEqual(expected, result, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_SuitabilityClampsAboveOne() {
      var result = GrowthBonusCalculator.ComputeBonus(1.5f, 0f, 30f);
      var clamped = GrowthBonusCalculator.ComputeBonus(1f, 0f, 30f);
      Assert.AreEqual(clamped, result, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_MaturityClampsAtCeiling() {
      var result = GrowthBonusCalculator.ComputeBonus(0f, 60f, 30f);
      var clamped = GrowthBonusCalculator.ComputeBonus(0f, 30f, 30f);
      Assert.AreEqual(clamped, result, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_NegativeSuitabilityClampedToZero() {
      var result = GrowthBonusCalculator.ComputeBonus(-0.5f, 0f, 30f);
      Assert.AreEqual(0f, result);
    }

    [TestMethod]
    public void ComputeBonus_ZeroCeiling_MaturityTermIsZero() {
      var result = GrowthBonusCalculator.ComputeBonus(1f, 10f, 0f);
      var expected = GrowthBonusCalculator.DefaultMaxBonus * 0.5f;
      Assert.AreEqual(expected, result, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_BadwaterCeiling15_ScalesCorrectly() {
      // Uses Badwater's real ceiling as a sample: maturity == ceiling
      // drives the maturity term to 1, so with suitability 0 the bonus
      // is half of max. The specific ceiling value isn't the pinned
      // design here -- the ceiling-relative scaling is.
      var ceiling = MaturityParameters.Ceiling(BiomeKind.Badwater);
      Assert.AreEqual(15f, ceiling);
      var result = GrowthBonusCalculator.ComputeBonus(0f, 15f, ceiling);
      var expected = GrowthBonusCalculator.DefaultMaxBonus * 0.5f;
      Assert.AreEqual(expected, result, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_CustomMaxBonus_ScalesLinearly() {
      var halfResult = GrowthBonusCalculator.ComputeBonus(1f, 30f, 30f, maxBonus: 0.15f);
      var fullResult = GrowthBonusCalculator.ComputeBonus(1f, 30f, 30f, maxBonus: 0.30f);
      Assert.AreEqual(halfResult * 2f, fullResult, 0.0001f);
    }

    [TestMethod]
    public void ComputeBonus_ZeroMaxBonus_ReturnsZero() {
      var result = GrowthBonusCalculator.ComputeBonus(1f, 30f, 30f, maxBonus: 0f);
      Assert.AreEqual(0f, result);
    }

    #endregion

    #region TargetBiome

    [TestMethod]
    public void TargetBiome_Aquatic_ReturnsWetland() {
      var result = GrowthBonusCalculator.TargetBiome(isAquatic: true, isTree: false, isCrop: false);
      Assert.AreEqual(BiomeKind.Wetland, result);
    }

    [TestMethod]
    public void TargetBiome_Tree_ReturnsForest() {
      var result = GrowthBonusCalculator.TargetBiome(isAquatic: false, isTree: true, isCrop: false);
      Assert.AreEqual(BiomeKind.Forest, result);
    }

    [TestMethod]
    public void TargetBiome_LandCrop_ReturnsGrassland() {
      // A farmed crop with no water requirement (Carrot, Potato, ...)
      // keys off Grassland.
      var result = GrowthBonusCalculator.TargetBiome(isAquatic: false, isTree: false, isCrop: true);
      Assert.AreEqual(BiomeKind.Grassland, result);
    }

    [TestMethod]
    public void TargetBiome_AquaticCrop_ReturnsWetland() {
      // Aquatic beats crop: Spadderdock/Cattail carry CropSpec AND
      // MinWaterHeight > 0, but the water requirement dominates their
      // ecology, so they route to Wetland, not Grassland.
      var result = GrowthBonusCalculator.TargetBiome(isAquatic: true, isTree: false, isCrop: true);
      Assert.AreEqual(BiomeKind.Wetland, result);
    }

    [TestMethod]
    public void TargetBiome_AquaticTree_ReturnsWetland() {
      // Aquatic takes priority (e.g. Mangrove).
      var result = GrowthBonusCalculator.TargetBiome(isAquatic: true, isTree: true, isCrop: false);
      Assert.AreEqual(BiomeKind.Wetland, result);
    }

    [TestMethod]
    public void TargetBiome_TreeBeatsCrop_ReturnsForest() {
      // Defensive: nothing is both a tree and a crop today, but if a
      // spec ever carried both, tree wins (Forest) per priority order.
      var result = GrowthBonusCalculator.TargetBiome(isAquatic: false, isTree: true, isCrop: true);
      Assert.AreEqual(BiomeKind.Forest, result);
    }

    [TestMethod]
    public void TargetBiome_NeitherAquaticNorTreeNorCrop_ReturnsNull() {
      // Wild gatherable bushes (NaturalResource + Growable, but no
      // CropSpec/TreeComponentSpec) get no bonus.
      var result = GrowthBonusCalculator.TargetBiome(isAquatic: false, isTree: false, isCrop: false);
      Assert.IsNull(result);
    }

    #endregion

  }

}
