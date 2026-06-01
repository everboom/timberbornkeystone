using Keystone.Core.Biomes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  [TestClass]
  public class FlourishThresholdTests {

    #region Determinism

    [TestMethod]
    public void ComputeActivation_SameInputs_ReturnsSameValue() {
      var a = FlourishThreshold.ComputeActivation(5, 7, BiomeKind.Forest, "L1");
      var b = FlourishThreshold.ComputeActivation(5, 7, BiomeKind.Forest, "L1");
      Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void ComputePick_SameInputs_ReturnsSameValue() {
      var a = FlourishThreshold.ComputePick(5, 7, BiomeKind.Forest, "L1");
      var b = FlourishThreshold.ComputePick(5, 7, BiomeKind.Forest, "L1");
      Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void ComputeActivation_StableLiteralValue() {
      // Pinned literal so a future refactor of the hash function shows
      // up loudly. If you change the hash on purpose, just re-run and
      // paste the new value.
      Assert.AreEqual(0.6774827f,
          FlourishThreshold.ComputeActivation(0, 0, BiomeKind.Forest, "L1"), 1e-6f);
    }

    [TestMethod]
    public void ComputePick_StableLiteralValue() {
      Assert.AreEqual(0.9513936f,
          FlourishThreshold.ComputePick(0, 0, BiomeKind.Forest, "L1"), 1e-6f);
    }

    [TestMethod]
    public void ActivationAndPick_DifferentSalts_DifferentValues() {
      // The whole point of the two-hash design: same inputs produce
      // independent activation and pick values, so adding density doesn't
      // shift which recipe gets picked at activated tiles.
      var act = FlourishThreshold.ComputeActivation(5, 7, BiomeKind.Forest, "L1");
      var pick = FlourishThreshold.ComputePick(5, 7, BiomeKind.Forest, "L1");
      Assert.AreNotEqual(act, pick);
    }

    #endregion

    #region Range

    [TestMethod]
    public void ComputeActivation_AlwaysInUnitRange() {
      for (var x = -50; x < 50; x++) {
        for (var y = -50; y < 50; y++) {
          var v = FlourishThreshold.ComputeActivation(x, y, BiomeKind.Forest, "L1");
          Assert.IsTrue(v >= 0f && v < 1f, $"out of range at ({x}, {y}): {v}");
        }
      }
    }

    [TestMethod]
    public void ComputePick_AlwaysInUnitRange() {
      for (var x = -50; x < 50; x++) {
        for (var y = -50; y < 50; y++) {
          var v = FlourishThreshold.ComputePick(x, y, BiomeKind.Forest, "L1");
          Assert.IsTrue(v >= 0f && v < 1f, $"out of range at ({x}, {y}): {v}");
        }
      }
    }

    #endregion

    #region Decorrelation

    [TestMethod]
    public void ComputeActivation_DifferentBiomes_DifferentValues() {
      var forest = FlourishThreshold.ComputeActivation(5, 7, BiomeKind.Forest, "L1");
      var grass = FlourishThreshold.ComputeActivation(5, 7, BiomeKind.Grassland, "L1");
      Assert.AreNotEqual(forest, grass);
    }

    [TestMethod]
    public void ComputeActivation_DifferentLevels_DifferentValues() {
      // Same tile + biome, different levels -> different activation hashes.
      // Lets L1 and L2 fire on independent spatial patterns within a biome.
      var l1 = FlourishThreshold.ComputeActivation(5, 7, BiomeKind.Grassland, "L1");
      var l2 = FlourishThreshold.ComputeActivation(5, 7, BiomeKind.Grassland, "L2");
      Assert.AreNotEqual(l1, l2);
    }

    [TestMethod]
    public void ComputeActivation_AdjacentTiles_DifferentValues() {
      var a = FlourishThreshold.ComputeActivation(5, 7, BiomeKind.Forest, "L1");
      var b = FlourishThreshold.ComputeActivation(6, 7, BiomeKind.Forest, "L1");
      var c = FlourishThreshold.ComputeActivation(5, 8, BiomeKind.Forest, "L1");
      Assert.AreNotEqual(a, b);
      Assert.AreNotEqual(a, c);
      Assert.AreNotEqual(b, c);
    }

    #endregion

    #region Distribution

    [TestMethod]
    public void ComputeActivation_RoughlyUniform_Over10kSamples() {
      var counts = new int[4];
      var n = 0;
      for (var x = 0; x < 100; x++) {
        for (var y = 0; y < 100; y++) {
          var v = FlourishThreshold.ComputeActivation(x, y, BiomeKind.Forest, "L1");
          counts[(int)(v * 4f)]++;
          n++;
        }
      }
      foreach (var c in counts) {
        Assert.IsTrue(c > 2250 && c < 2750,
            $"quartile count out of band: {c} (expected ~2500)");
      }
    }

    #endregion

  }

}
