using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Tests for <see cref="BiomeValueKinds"/>. The helper maps
  /// <see cref="BiomeKind"/> &lt;-&gt; persistence string keys. Two
  /// invariants matter: the keys follow the published prefix scheme
  /// (so save migrations and external tooling can pattern-match) and
  /// the round trip survives both directions for every biome.
  /// </summary>
  [TestClass]
  public class BiomeValueKindsTests {

    #region Score channel

    /// <summary>Score keys use <see cref="KnownValueKinds.ChunkSuitabilityPrefix"/>
    /// + the biome name in lowercase. The prefix is part of the
    /// persisted format -- a regression here breaks save load.</summary>
    [TestMethod]
    public void ForSuitability_KeyHasChunkSuitabilityPrefixAndLowercaseName() {
      // Arrange / Act
      var key = BiomeValueKinds.ForSuitability(BiomeKind.Forest);

      // Assert
      Assert.AreEqual(KnownValueKinds.ChunkSuitabilityPrefix + "forest", key);
    }

    /// <summary>Every biome has a stable, distinct Score key.</summary>
    [TestMethod]
    public void For_AllBiomesProduceDistinctKeys() {
      // Arrange
      var biomes = BiomeValueKinds.AllBiomes;
      var seen = new System.Collections.Generic.HashSet<string>();

      // Act / Assert
      foreach (var b in biomes) {
        var key = BiomeValueKinds.ForSuitability(b);
        Assert.IsTrue(seen.Add(key),
            $"Duplicate Score key {key} for biome {b}");
      }
      Assert.AreEqual(biomes.Length, seen.Count);
    }

    /// <summary>Round trip: <c>For(biome)</c> followed by
    /// <c>TryParse</c> recovers the same biome.</summary>
    [TestMethod]
    public void TryParse_RoundTripsEveryBiome() {
      // Arrange
      var biomes = BiomeValueKinds.AllBiomes;

      // Act / Assert
      foreach (var b in biomes) {
        var key = BiomeValueKinds.ForSuitability(b);
        Assert.IsTrue(BiomeValueKinds.TryParseSuitability(key, out var parsed),
            $"TryParse rejected its own For({b}) output {key}");
        Assert.AreEqual(b, parsed);
      }
    }

    /// <summary>Non-Score keys (including Investment keys, arbitrary
    /// strings, and the empty string) are rejected.</summary>
    [TestMethod]
    public void TryParse_RejectsNonScoreKeys() {
      // Arrange / Act / Assert
      Assert.IsFalse(BiomeValueKinds.TryParseSuitability("not-a-keystone-key", out _));
      Assert.IsFalse(BiomeValueKinds.TryParseSuitability(string.Empty, out _));
      Assert.IsFalse(BiomeValueKinds.TryParseSuitability(
          KnownValueKinds.ChunkMaturityPrefix + "forest", out _));
      Assert.IsFalse(BiomeValueKinds.TryParseSuitability(
          KnownValueKinds.ChunkSuitabilityPrefix + "no-such-biome", out _));
    }

    #endregion

    #region Investment channel

    /// <summary>Investment keys use the Investment prefix and the
    /// lowercase biome name -- a separate channel from Score.</summary>
    [TestMethod]
    public void ForMaturity_KeyHasChunkMaturityPrefixAndLowercaseName() {
      // Arrange / Act
      var key = BiomeValueKinds.ForMaturity(BiomeKind.Grassland);

      // Assert
      Assert.AreEqual(KnownValueKinds.ChunkMaturityPrefix + "grassland", key);
    }

    /// <summary>Score and Investment keys are disjoint -- different
    /// channels in the persistence store.</summary>
    [TestMethod]
    public void ScoreAndInvestmentKeys_AreDisjoint() {
      // Arrange
      var biomes = BiomeValueKinds.AllBiomes;

      // Act / Assert
      foreach (var b in biomes) {
        var score = BiomeValueKinds.ForSuitability(b);
        var investment = BiomeValueKinds.ForMaturity(b);
        Assert.AreNotEqual(score, investment,
            $"Biome {b}: Score and Investment keys collided");
      }
    }

    /// <summary>Round trip on the Investment channel.</summary>
    [TestMethod]
    public void TryParseInvestment_RoundTripsEveryBiome() {
      // Arrange
      var biomes = BiomeValueKinds.AllBiomes;

      // Act / Assert
      foreach (var b in biomes) {
        var key = BiomeValueKinds.ForMaturity(b);
        Assert.IsTrue(BiomeValueKinds.TryParseMaturity(key, out var parsed),
            $"TryParseInvestment rejected its own ForInvestment({b}) output {key}");
        Assert.AreEqual(b, parsed);
      }
    }

    /// <summary>Investment parser rejects Score keys -- the two
    /// parsers don't overlap. Catches "we picked the wrong parser"
    /// bugs at the boundary.</summary>
    [TestMethod]
    public void TryParseInvestment_RejectsScoreKeys() {
      // Arrange
      var scoreKey = BiomeValueKinds.ForSuitability(BiomeKind.Forest);

      // Act / Assert
      Assert.IsFalse(BiomeValueKinds.TryParseMaturity(scoreKey, out _));
    }

    /// <summary>Mirror of the Score channel: arbitrary strings and the
    /// empty string are rejected.</summary>
    [TestMethod]
    public void TryParseInvestment_RejectsArbitraryStrings() {
      // Arrange / Act / Assert
      Assert.IsFalse(BiomeValueKinds.TryParseMaturity(string.Empty, out _));
      Assert.IsFalse(BiomeValueKinds.TryParseMaturity("garbage", out _));
      Assert.IsFalse(BiomeValueKinds.TryParseMaturity(
          KnownValueKinds.ChunkMaturityPrefix + "no-such-biome", out _));
    }

    #endregion

    #region Per-tile biome partition

    /// <summary>Riparian must be a valid <see cref="BiomeKind"/> (so it
    /// can be a dominant biome with its own content levels) but MUST be
    /// excluded from the per-chunk-scored set, because its suitability /
    /// maturity are sourced per-tile, not from per-chunk slots. This is
    /// the load-bearing partition invariant the whole partial-biome
    /// approach rests on -- a regression that re-enrols Riparian in
    /// per-chunk scoring (e.g. reverting AllBiomes to Enum.GetValues)
    /// fails here.</summary>
    [TestMethod]
    public void Riparian_IsInEnum_ButExcludedFromPerChunkBiomes() {
      var allEnum = (BiomeKind[])System.Enum.GetValues(typeof(BiomeKind));
      CollectionAssert.Contains(allEnum, BiomeKind.Riparian);
      CollectionAssert.DoesNotContain(BiomeValueKinds.AllBiomes, BiomeKind.Riparian);
    }

    /// <summary>A per-tile biome has no per-chunk persistence key;
    /// asking for one is a programming error and surfaces loudly rather
    /// than returning a bogus key that would imply per-chunk storage.</summary>
    [TestMethod]
    public void ForSuitability_Riparian_Throws_NoPerChunkKey() {
      Assert.ThrowsException<System.Collections.Generic.KeyNotFoundException>(
          () => BiomeValueKinds.ForSuitability(BiomeKind.Riparian));
    }

    #endregion

  }

}
