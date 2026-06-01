using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Tests for <see cref="BiomeValueKinds"/>'s ordinal-access layer:
  /// <see cref="BiomeValueKinds.Initialize"/>,
  /// <see cref="BiomeValueKinds.SuitabilityOrdinal"/>,
  /// <see cref="BiomeValueKinds.MaturityOrdinal"/>.
  /// </summary>
  [TestClass]
  public class BiomeValueKindsOrdinalTests {

    [TestCleanup]
    public void Cleanup() {
      BiomeValueKinds.ResetOrdinals();
    }

    #region Initialize

    [TestMethod]
    public void Initialize_RegistersAllBiomes() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act
      BiomeValueKinds.Initialize(registry);

      // Assert — 11 biomes × 2 channels = 22 slots
      Assert.AreEqual(BiomeValueKinds.AllBiomes.Length * 2, registry.SlotCount);
    }

    [TestMethod]
    public void Initialize_SetsIsInitialized() {
      // Arrange
      var registry = new ChunkValueRegistry();
      Assert.IsFalse(BiomeValueKinds.IsInitialized);

      // Act
      BiomeValueKinds.Initialize(registry);

      // Assert
      Assert.IsTrue(BiomeValueKinds.IsInitialized);
    }

    #endregion

    #region SuitabilityOrdinal

    [TestMethod]
    public void SuitabilityOrdinal_AfterInit_ReturnsValidOrdinal() {
      // Arrange
      var registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(registry);

      // Act / Assert
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        var ordinal = BiomeValueKinds.SuitabilityOrdinal(biome);
        Assert.IsTrue(ordinal >= 0 && ordinal < registry.SlotCount,
            $"SuitabilityOrdinal({biome}) = {ordinal} out of range [0, {registry.SlotCount}).");
      }
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void SuitabilityOrdinal_BeforeInit_Throws() {
      BiomeValueKinds.SuitabilityOrdinal(BiomeKind.Forest);
    }

    #endregion

    #region MaturityOrdinal

    [TestMethod]
    public void MaturityOrdinal_AfterInit_ReturnsValidOrdinal() {
      // Arrange
      var registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(registry);

      // Act / Assert
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        var ordinal = BiomeValueKinds.MaturityOrdinal(biome);
        Assert.IsTrue(ordinal >= 0 && ordinal < registry.SlotCount,
            $"MaturityOrdinal({biome}) = {ordinal} out of range [0, {registry.SlotCount}).");
      }
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void MaturityOrdinal_BeforeInit_Throws() {
      BiomeValueKinds.MaturityOrdinal(BiomeKind.Forest);
    }

    #endregion

    #region Distinctness

    [TestMethod]
    public void Ordinals_AreDistinct() {
      // Arrange
      var registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(registry);

      // Act
      var seen = new HashSet<int>();
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        Assert.IsTrue(seen.Add(BiomeValueKinds.SuitabilityOrdinal(biome)),
            $"SuitabilityOrdinal({biome}) collides with a previously seen ordinal.");
        Assert.IsTrue(seen.Add(BiomeValueKinds.MaturityOrdinal(biome)),
            $"MaturityOrdinal({biome}) collides with a previously seen ordinal.");
      }
    }

    #endregion

    #region Round-trip through registry

    [TestMethod]
    public void OrdinalRoundTrips_ThroughRegistry() {
      // Arrange
      var registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(registry);

      // Act / Assert
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        var suitOrdinal = BiomeValueKinds.SuitabilityOrdinal(biome);
        Assert.AreEqual(BiomeValueKinds.ForSuitability(biome), registry.NameFor(suitOrdinal),
            $"Suitability name mismatch for {biome}.");

        var matOrdinal = BiomeValueKinds.MaturityOrdinal(biome);
        Assert.AreEqual(BiomeValueKinds.ForMaturity(biome), registry.NameFor(matOrdinal),
            $"Maturity name mismatch for {biome}.");
      }
    }

    #endregion

    #region RecomputeTopBiomes tiebreak

    [TestMethod]
    public void RecomputeTopBiomes_TieAtTop_KeepsAggressorTierWinner() {
      // On a fully-toxic chunk several biomes saturate to the same
      // Suitability. The top-3 must keep the aggressor-tier winner
      // (Badwater) so the per-tile sampler's argmax can still pick it —
      // iterating enum order would evict Badwater (enum-last) and silently
      // hand per-tile dominance to Contaminated. Four biomes tied at 1.0
      // exceeds the 3 top slots, forcing an eviction.
      // Arrange
      var registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(registry);
      var data = new ChunkData(registry.SlotCount);
      data.Set(BiomeValueKinds.SuitabilityOrdinal(BiomeKind.Badwater), 1f);
      data.Set(BiomeValueKinds.SuitabilityOrdinal(BiomeKind.Contaminated), 1f);
      data.Set(BiomeValueKinds.SuitabilityOrdinal(BiomeKind.Dry), 1f);
      data.Set(BiomeValueKinds.SuitabilityOrdinal(BiomeKind.River), 1f);

      // Act
      BiomeValueKinds.RecomputeTopBiomes(data);

      // Assert
      CollectionAssert.Contains(data.TopBiomes, (int)BiomeKind.Badwater,
          "Badwater (top aggressor tier) must survive the top-3 tie, not be evicted by enum order");
    }

    #endregion

    #region ResetOrdinals

    [TestMethod]
    public void ResetOrdinals_ClearsIsInitialized() {
      // Arrange
      var registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(registry);
      Assert.IsTrue(BiomeValueKinds.IsInitialized);

      // Act
      BiomeValueKinds.ResetOrdinals();

      // Assert
      Assert.IsFalse(BiomeValueKinds.IsInitialized);
    }

    #endregion

  }

}
