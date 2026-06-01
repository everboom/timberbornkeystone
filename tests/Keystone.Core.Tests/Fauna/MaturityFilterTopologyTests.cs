using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Fauna;
using Keystone.Core.Persistence;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Fauna {

  /// <summary>
  /// Pins the layered predicate of
  /// <see cref="MaturityFilterTopology"/>:
  /// <list type="number">
  ///   <item>Inner topology must say yes.</item>
  ///   <item>Tile's <i>dominant</i> biome (bilinearly sampled across
  ///         the four surrounding chunk centres via
  ///         <see cref="ChunkBiomeSampler.SampleDominantBiome"/>) must
  ///         match the configured biome.</item>
  ///   <item>That biome's bilinearly-sampled Maturity at the tile
  ///         must be strictly positive (so a nominally-eligible chunk
  ///         that hasn't accumulated any Maturity yet is rejected).</item>
  ///   <item>That Maturity must be ≥ the configured level threshold.</item>
  /// </list>
  /// </summary>
  [TestClass]
  public class MaturityFilterTopologyTests {

    #region Helpers

    private sealed class AcceptAllInner : IRegionTopologyQuery {
      public bool ContainsTile(RegionId region, int x, int y) => true;
    }

    private sealed class RejectAllInner : IRegionTopologyQuery {
      public bool ContainsTile(RegionId region, int x, int y) => false;
    }

    private static readonly RegionId Region = new(1);

    /// <summary>Build a 2x2-chunk store with the given Suitability and
    /// Maturity values set uniformly across all 4 chunks for
    /// <paramref name="biome"/>. Other biomes' channels left
    /// untouched.</summary>
    private static ChunkValueStore Store(BiomeKind biome, float suitability, float maturity) {
      var store = new ChunkValueStore();
      var sKind = BiomeValueKinds.ForSuitability(biome);
      var mKind = BiomeValueKinds.ForMaturity(biome);
      for (var cx = 0; cx < 2; cx++) {
        for (var cy = 0; cy < 2; cy++) {
          store.Set(Region, cx, cy, sKind, suitability);
          store.Set(Region, cx, cy, mKind, maturity);
        }
      }
      return store;
    }

    private static RegionEcologyField Field2x2() =>
        new(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);

    #endregion

    #region Layered predicate

    [TestMethod]
    public void Contains_InnerRejects_FilterRejectsImmediately() {
      // Inner rejection short-circuits — biome / maturity not consulted.
      var store = Store(BiomeKind.Grassland, suitability: 1f, maturity: 10f);
      var filter = new MaturityFilterTopology(
          inner: new RejectAllInner(),
          biomeValues: new ChunkValueStoreReader(store), field: Field2x2(),
          biome: BiomeKind.Grassland, threshold: 1f);

      Assert.IsFalse(filter.ContainsTile(Region, 4, 4));
    }

    [TestMethod]
    public void Contains_InnerAcceptsAndAllGatesPass_True() {
      var store = Store(BiomeKind.Grassland, suitability: 1f, maturity: 10f);
      var filter = new MaturityFilterTopology(
          inner: new AcceptAllInner(),
          biomeValues: new ChunkValueStoreReader(store), field: Field2x2(),
          biome: BiomeKind.Grassland, threshold: 1f);

      Assert.IsTrue(filter.ContainsTile(Region, 4, 4));
    }

    [TestMethod]
    public void Contains_DominantBiomeMismatch_False() {
      // Grassland is dominant but the filter wants Forest tiles.
      var store = Store(BiomeKind.Grassland, suitability: 1f, maturity: 10f);
      var filter = new MaturityFilterTopology(
          inner: new AcceptAllInner(),
          biomeValues: new ChunkValueStoreReader(store), field: Field2x2(),
          biome: BiomeKind.Forest, threshold: 1f);

      Assert.IsFalse(filter.ContainsTile(Region, 4, 4));
    }

    [TestMethod]
    public void Contains_DominantBiomeMatchesButMaturityZero_False() {
      // Suitability set for Grassland, but Maturity hasn't accumulated.
      // The "strictly positive maturity" gate rejects this case so that
      // nominally-eligible chunks don't host fauna before any
      // Maturity has built up.
      var store = Store(BiomeKind.Grassland, suitability: 1f, maturity: 0f);
      var filter = new MaturityFilterTopology(
          inner: new AcceptAllInner(),
          biomeValues: new ChunkValueStoreReader(store), field: Field2x2(),
          biome: BiomeKind.Grassland, threshold: 0f);

      Assert.IsFalse(filter.ContainsTile(Region, 4, 4),
          "Maturity == 0 should reject even when the threshold is 0 — the strict-positive gate is independent.");
    }

    [TestMethod]
    public void Contains_MaturityBelowThreshold_False() {
      var store = Store(BiomeKind.Grassland, suitability: 1f, maturity: 4.999f);
      var filter = new MaturityFilterTopology(
          inner: new AcceptAllInner(),
          biomeValues: new ChunkValueStoreReader(store), field: Field2x2(),
          biome: BiomeKind.Grassland, threshold: 5f);

      Assert.IsFalse(filter.ContainsTile(Region, 4, 4));
    }

    [TestMethod]
    public void Contains_MaturityAtThreshold_True() {
      // Threshold check is inclusive (>= threshold).
      var store = Store(BiomeKind.Grassland, suitability: 1f, maturity: 5f);
      var filter = new MaturityFilterTopology(
          inner: new AcceptAllInner(),
          biomeValues: new ChunkValueStoreReader(store), field: Field2x2(),
          biome: BiomeKind.Grassland, threshold: 5f);

      Assert.IsTrue(filter.ContainsTile(Region, 4, 4));
    }

    [TestMethod]
    public void Contains_ThresholdZero_StillRequiresStrictlyPositiveMaturity() {
      // The "zero disables the threshold for dev-placed agents" docstring
      // means the threshold check passes trivially — but the strict-
      // positive maturity check still applies.
      var nonZero = Store(BiomeKind.Grassland, suitability: 1f, maturity: 0.001f);
      var filter = new MaturityFilterTopology(
          inner: new AcceptAllInner(),
          biomeValues: new ChunkValueStoreReader(nonZero), field: Field2x2(),
          biome: BiomeKind.Grassland, threshold: 0f);

      Assert.IsTrue(filter.ContainsTile(Region, 4, 4),
          "Any positive maturity passes threshold=0.");

      var zero = Store(BiomeKind.Grassland, suitability: 1f, maturity: 0f);
      var filterZero = new MaturityFilterTopology(
          inner: new AcceptAllInner(),
          biomeValues: new ChunkValueStoreReader(zero), field: Field2x2(),
          biome: BiomeKind.Grassland, threshold: 0f);

      Assert.IsFalse(filterZero.ContainsTile(Region, 4, 4),
          "Zero maturity still rejects even with threshold=0.");
    }

    #endregion

  }

}
