using Keystone.Core.Biomes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  [TestClass]
  public class BiomeLevelTableTests {

    private static BiomeLevelTable Make() => new();

    #region Define + lookup round-trip

    [TestMethod]
    public void Define_Find_RoundTrips() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);

      var level = table.Find(BiomeKind.Grassland, "L1");

      Assert.IsNotNull(level);
      Assert.AreEqual("L1", level!.LevelId);
      Assert.AreEqual(0.5f, level.LowerMaturity);
      Assert.AreEqual(1.0f, level.UpperMaturity);
    }

    [TestMethod]
    public void Find_UnknownBiome_ReturnsNull() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);

      Assert.IsNull(table.Find(BiomeKind.Forest, "L1"));
    }

    [TestMethod]
    public void Find_UnknownLevel_ReturnsNull() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);

      Assert.IsNull(table.Find(BiomeKind.Grassland, "L2"));
    }

    [TestMethod]
    public void LevelsFor_NoEntries_ReturnsEmpty() {
      var table = Make();
      Assert.AreEqual(0, table.LevelsFor(BiomeKind.Forest).Count);
    }

    [TestMethod]
    public void LevelsFor_MultipleEntries_SortedByLowerBound() {
      var table = Make();
      // Insert out of order; expect sorted by lower bound on read.
      table.Define(BiomeKind.Grassland, "L3", 3.0f, 10.0f);
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);
      table.Define(BiomeKind.Grassland, "L2", 1.0f, 3.0f);

      var levels = table.LevelsFor(BiomeKind.Grassland);

      Assert.AreEqual(3, levels.Count);
      Assert.AreEqual("L1", levels[0].LevelId);
      Assert.AreEqual("L2", levels[1].LevelId);
      Assert.AreEqual("L3", levels[2].LevelId);
    }

    #endregion

    #region Override semantics

    [TestMethod]
    public void Define_SameLevelTwice_OverwritesRange() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);
      table.Define(BiomeKind.Grassland, "L1", 0.2f, 0.8f);  // override

      var level = table.Find(BiomeKind.Grassland, "L1");

      Assert.AreEqual(0.2f, level!.LowerMaturity);
      Assert.AreEqual(0.8f, level.UpperMaturity);
    }

    [TestMethod]
    public void Define_OverrideAcrossBiomes_DoesNotInterfere() {
      // Per-biome tables are independent: overriding L1 for Grassland
      // does not change Forest's L1.
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);
      table.Define(BiomeKind.Forest, "L1", 0.5f, 1.0f);
      table.Define(BiomeKind.Grassland, "L1", 0.2f, 0.8f);

      Assert.AreEqual(0.2f, table.Find(BiomeKind.Grassland, "L1")!.LowerMaturity);
      Assert.AreEqual(0.5f, table.Find(BiomeKind.Forest, "L1")!.LowerMaturity);
    }

    #endregion

    #region Progress

    [TestMethod]
    public void ProgressIn_BelowLower_ReturnsZero() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);

      Assert.AreEqual(0f, table.ProgressIn(BiomeKind.Grassland, "L1", 0.3f));
      Assert.AreEqual(0f, table.ProgressIn(BiomeKind.Grassland, "L1", 0.5f));
    }

    [TestMethod]
    public void ProgressIn_AtUpper_ReturnsOne() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);

      Assert.AreEqual(1f, table.ProgressIn(BiomeKind.Grassland, "L1", 1.0f));
      Assert.AreEqual(1f, table.ProgressIn(BiomeKind.Grassland, "L1", 5.0f));
    }

    [TestMethod]
    public void ProgressIn_Midway_LerpsLinearly() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);

      Assert.AreEqual(0.5f, table.ProgressIn(BiomeKind.Grassland, "L1", 0.75f), 1e-5f);
    }

    [TestMethod]
    public void ProgressIn_UnknownLevel_ReturnsZero() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);

      Assert.AreEqual(0f, table.ProgressIn(BiomeKind.Grassland, "L2", 5f));
      Assert.AreEqual(0f, table.ProgressIn(BiomeKind.Forest, "L1", 5f));
    }

    [TestMethod]
    public void ProgressIn_StackedLevels_BothActiveAtMidStack() {
      // L1: 0.5-1.0, L2: 1.0-3.0. At investment=2.0:
      //   L1 progress = 1.0 (saturated)
      //   L2 progress = 0.5 (ramping)
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);
      table.Define(BiomeKind.Grassland, "L2", 1.0f, 3.0f);

      Assert.AreEqual(1.0f, table.ProgressIn(BiomeKind.Grassland, "L1", 2.0f), 1e-5f);
      Assert.AreEqual(0.5f, table.ProgressIn(BiomeKind.Grassland, "L2", 2.0f), 1e-5f);
    }

    #endregion

    #region Argument validation

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Define_EmptyLevelId_Throws() {
      Make().Define(BiomeKind.Grassland, "", 0.5f, 1.0f);
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Define_UpperNotGreaterThanLower_Throws() {
      Make().Define(BiomeKind.Grassland, "L1", 1.0f, 1.0f);
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Define_NegativeLower_Throws() {
      Make().Define(BiomeKind.Grassland, "L1", -0.1f, 1.0f);
    }

    /// <summary>
    /// Pins that <see cref="BiomeLevelTable.Define"/> rejects negative
    /// density. Density encodes per-recipe tile-fraction in <c>[0, 1]</c>
    /// and a negative value indicates a catalog bug; failing loudly here
    /// surfaces it at catalog load rather than producing nonsense
    /// densities downstream.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Define_NegativeDensity_Throws() {
      Make().Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f, density: -0.01f);
    }

    /// <summary>
    /// Pins that <see cref="BiomeLevelTable.Define"/> rejects density
    /// greater than 1. Density is a tile-fraction in <c>[0, 1]</c>; a
    /// value above 1 cannot represent a coherent fraction and is
    /// almost certainly a unit-confusion bug in the calling catalog.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Define_DensityAboveOne_Throws() {
      Make().Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f, density: 1.01f);
    }

    /// <summary>
    /// Pins that <see cref="BiomeLevelTable.Define"/> rejects negative
    /// <c>faunaCapacityAtSaturation</c>. The field is a count of fauna
    /// slots at full saturation; negatives are nonsense and the catalog
    /// must fail loudly rather than silently clamp.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Define_NegativeFaunaCapacity_Throws() {
      Make().Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f,
          faunaCapacityAtSaturation: -1);
    }

    /// <summary>
    /// Pins that <see cref="BiomeLevelTable.Define"/> rejects negative
    /// <c>faunaMinScore</c>. The field is a Suitability gate in
    /// <c>[0, 1]</c>; a negative value would let any tile pass and is a
    /// catalog bug.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Define_NegativeFaunaMinScore_Throws() {
      Make().Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f, faunaMinScore: -0.01f);
    }

    /// <summary>
    /// Pins that <see cref="BiomeLevelTable.Define"/> rejects
    /// <c>faunaMinScore</c> above 1. Above-1 Suitability gates would
    /// reject every tile; the field's <c>[0, 1]</c> contract must hold.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Define_FaunaMinScoreAboveOne_Throws() {
      Make().Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f, faunaMinScore: 1.01f);
    }

    #endregion

    #region Override semantics — iteration past non-matching entries

    /// <summary>
    /// Pins that <see cref="BiomeLevelTable.Define"/>'s override-search
    /// walks past non-matching entries and overwrites the *correct* one,
    /// not the first one. The lookup loop's "skip when LevelId doesn't
    /// match" branch must be exercised against a biome with multiple
    /// pre-existing levels, then overriding a later one — the other test
    /// (<c>Define_SameLevelTwice_OverwritesRange</c>) hits the loop at
    /// the first index only.
    /// </summary>
    [TestMethod]
    public void Define_OverrideThirdLevelOfThree_OverwritesOnlyMatchingEntry() {
      // Arrange — three levels for one biome.
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);
      table.Define(BiomeKind.Grassland, "L2", 1.0f, 3.0f);
      table.Define(BiomeKind.Grassland, "L3", 3.0f, 10.0f);

      // Act — override the *last* entry; the loop must walk past L1, L2.
      table.Define(BiomeKind.Grassland, "L3", 4.0f, 12.0f);

      // Assert — L3 updated, L1 and L2 untouched.
      Assert.AreEqual(3, table.Count, "Override must not add a new entry.");
      Assert.AreEqual(0.5f, table.Find(BiomeKind.Grassland, "L1")!.LowerMaturity);
      Assert.AreEqual(1.0f, table.Find(BiomeKind.Grassland, "L2")!.LowerMaturity);
      Assert.AreEqual(4.0f, table.Find(BiomeKind.Grassland, "L3")!.LowerMaturity);
      Assert.AreEqual(12.0f, table.Find(BiomeKind.Grassland, "L3")!.UpperMaturity);
    }

    /// <summary>
    /// Pins that <see cref="BiomeLevelTable.Define"/>'s override path
    /// re-sorts the level list when the override changes the entry's
    /// lower bound to a value that should reposition it within the
    /// list. The other override tests overwrite with values that keep
    /// the original ordering (or operate on a single-entry list), so
    /// the <i>sort actually swaps positions</i> path on the override
    /// branch is not otherwise exercised. If a future refactor drops
    /// the post-override sort and relies on insertion order, this
    /// test fails because <see cref="BiomeLevelTable.LevelsFor"/>
    /// will return the entries in the wrong order.
    /// </summary>
    [TestMethod]
    public void Define_OverrideMovesEntryToDifferentSortPosition_LevelsForReflectsNewOrder() {
      // Arrange — three levels with monotonically increasing lower bounds.
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);
      table.Define(BiomeKind.Grassland, "L2", 1.0f, 3.0f);
      table.Define(BiomeKind.Grassland, "L3", 3.0f, 10.0f);

      // Sanity — pre-override order is L1, L2, L3.
      var preLevels = table.LevelsFor(BiomeKind.Grassland);
      Assert.AreEqual("L1", preLevels[0].LevelId);
      Assert.AreEqual("L2", preLevels[1].LevelId);
      Assert.AreEqual("L3", preLevels[2].LevelId);

      // Act — override L3's lower bound to 0.0 so it should sort to the
      // front. This is the load-bearing case for the post-override
      // sort: without the re-sort, L3 would stay at index 2 despite
      // having the smallest lower bound.
      table.Define(BiomeKind.Grassland, "L3", 0.0f, 0.4f);

      // Assert — LevelsFor must return L3, L1, L2 (sorted by lower bound).
      var postLevels = table.LevelsFor(BiomeKind.Grassland);
      Assert.AreEqual(3, postLevels.Count, "Override must not add or drop entries.");
      Assert.AreEqual("L3", postLevels[0].LevelId,
          "Override that lowers the bound must re-sort the level to the front.");
      Assert.AreEqual("L1", postLevels[1].LevelId);
      Assert.AreEqual("L2", postLevels[2].LevelId);
      Assert.AreEqual(0.0f, postLevels[0].LowerMaturity);
      Assert.AreEqual(0.4f, postLevels[0].UpperMaturity);
    }

    #endregion

    #region Clear / Count

    [TestMethod]
    public void Clear_RemovesAllEntries() {
      var table = Make();
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);
      table.Define(BiomeKind.Forest, "L1", 0.5f, 1.0f);

      table.Clear();

      Assert.AreEqual(0, table.Count);
      Assert.AreEqual(0, table.LevelsFor(BiomeKind.Grassland).Count);
    }

    [TestMethod]
    public void Count_ReflectsTotalEntries() {
      var table = Make();
      Assert.AreEqual(0, table.Count);
      table.Define(BiomeKind.Grassland, "L1", 0.5f, 1.0f);
      Assert.AreEqual(1, table.Count);
      table.Define(BiomeKind.Grassland, "L2", 1.0f, 3.0f);
      Assert.AreEqual(2, table.Count);
      table.Define(BiomeKind.Forest, "L1", 0.5f, 1.0f);
      Assert.AreEqual(3, table.Count);
      // Override doesn't increment count.
      table.Define(BiomeKind.Grassland, "L1", 0.2f, 0.8f);
      Assert.AreEqual(3, table.Count);
    }

    #endregion

  }

}
