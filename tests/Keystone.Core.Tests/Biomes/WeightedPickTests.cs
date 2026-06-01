using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Tests for <see cref="WeightedPick"/>. The helper is pure: given a
  /// weight list and a uniform sample in <c>[0, 1)</c>, it picks an
  /// index proportional to weight, with <c>-1</c> as the sentinel for
  /// "no positive weight to pick from."
  /// </summary>
  [TestClass]
  public class WeightedPickTests {

    #region Empty / degenerate inputs

    /// <summary>Empty weight list -> <c>-1</c> regardless of hash.</summary>
    [TestMethod]
    public void Pick_EmptyList_ReturnsNegativeOne() {
      // Arrange
      var weights = new List<float>();

      // Act / Assert
      Assert.AreEqual(-1, WeightedPick.Pick(weights, 0.0f));
      Assert.AreEqual(-1, WeightedPick.Pick(weights, 0.999f));
    }

    /// <summary>All-zero list -> <c>-1</c>: no candidate has positive
    /// weight.</summary>
    [TestMethod]
    public void Pick_AllZeroWeights_ReturnsNegativeOne() {
      // Arrange
      var weights = new List<float> { 0f, 0f, 0f };

      // Act
      var result = WeightedPick.Pick(weights, 0.5f);

      // Assert
      Assert.AreEqual(-1, result);
    }

    /// <summary>All-negative weights -> <c>-1</c>: non-positive
    /// values count as zero per the contract.</summary>
    [TestMethod]
    public void Pick_AllNegativeWeights_ReturnsNegativeOne() {
      // Arrange
      var weights = new List<float> { -1f, -0.5f };

      // Act
      var result = WeightedPick.Pick(weights, 0.5f);

      // Assert
      Assert.AreEqual(-1, result);
    }

    #endregion

    #region Single-element list (short-circuit path)

    /// <summary>Single positive weight -> always index 0.</summary>
    [TestMethod]
    public void Pick_SingletonPositive_ReturnsZero() {
      // Arrange
      var weights = new List<float> { 1f };

      // Act / Assert
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.0f));
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.5f));
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.9999f));
    }

    /// <summary>Single non-positive weight -> <c>-1</c>.</summary>
    [TestMethod]
    public void Pick_SingletonZero_ReturnsNegativeOne() {
      // Arrange
      var weights = new List<float> { 0f };

      // Act / Assert
      Assert.AreEqual(-1, WeightedPick.Pick(weights, 0.5f));
    }

    #endregion

    #region Proportional selection

    /// <summary>Uniform two-element pick: hash &lt; 0.5 -> 0;
    /// hash &gt;= 0.5 -> 1. The half-open boundary follows from the
    /// <c>t &lt; accum</c> check.</summary>
    [TestMethod]
    public void Pick_TwoEvenWeights_SplitsAtHalf() {
      // Arrange
      var weights = new List<float> { 1f, 1f };

      // Act / Assert
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.0f));
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.499f));
      Assert.AreEqual(1, WeightedPick.Pick(weights, 0.5f));
      Assert.AreEqual(1, WeightedPick.Pick(weights, 0.9999f));
    }

    /// <summary>Asymmetric weights: 3:1 -> first index covers 75% of
    /// the hash range, second covers 25%.</summary>
    [TestMethod]
    public void Pick_AsymmetricWeights_BoundaryAtCumulativeFraction() {
      // Arrange: weights 3 and 1, total 4. Boundary at hash * 4 = 3,
      // i.e. hash = 0.75.
      var weights = new List<float> { 3f, 1f };

      // Act / Assert
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.0f));
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.7499f));
      Assert.AreEqual(1, WeightedPick.Pick(weights, 0.75f));
      Assert.AreEqual(1, WeightedPick.Pick(weights, 0.9999f));
    }

    /// <summary>Three-element list: each bucket's hash range matches
    /// its share of the total weight.</summary>
    [TestMethod]
    public void Pick_ThreeUnevenWeights_BucketsByCumulativeShare() {
      // Arrange: 1, 2, 1 -> total 4. Cumulative thresholds 0.25, 0.75.
      var weights = new List<float> { 1f, 2f, 1f };

      // Act / Assert
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.0f));
      Assert.AreEqual(0, WeightedPick.Pick(weights, 0.2499f));
      Assert.AreEqual(1, WeightedPick.Pick(weights, 0.25f));
      Assert.AreEqual(1, WeightedPick.Pick(weights, 0.7499f));
      Assert.AreEqual(2, WeightedPick.Pick(weights, 0.75f));
      Assert.AreEqual(2, WeightedPick.Pick(weights, 0.9999f));
    }

    /// <summary>Non-positive weights are skipped, but the surrounding
    /// positive weights still pick correctly. 0:1:0 -> always index 1.</summary>
    [TestMethod]
    public void Pick_ZeroWeightsAmongPositive_SkippedNotPicked() {
      // Arrange
      var weights = new List<float> { 0f, 1f, 0f };

      // Act / Assert
      for (var h = 0f; h < 1f; h += 0.1f) {
        Assert.AreEqual(1, WeightedPick.Pick(weights, h),
            $"hash {h}: only index 1 has positive weight");
      }
    }

    /// <summary>Negative weights treated as zero -- the boundary
    /// shifts to reflect only the positive total. -2:1:1 behaves like
    /// 0:1:1 with two equal halves.</summary>
    [TestMethod]
    public void Pick_NegativeWeightsTreatedAsZero() {
      // Arrange: positive total = 2; boundary at hash = 0.5.
      var weights = new List<float> { -2f, 1f, 1f };

      // Act / Assert
      Assert.AreEqual(1, WeightedPick.Pick(weights, 0.0f));
      Assert.AreEqual(1, WeightedPick.Pick(weights, 0.4999f));
      Assert.AreEqual(2, WeightedPick.Pick(weights, 0.5f));
      Assert.AreEqual(2, WeightedPick.Pick(weights, 0.9999f));
    }

    #endregion

    #region Distribution check

    /// <summary>Floating-point rounding can leave the accumulator
    /// just shy of the total at the end of the proportional pass when
    /// the hash is essentially 1. The fallback walks the list from the
    /// end and returns the last positive-weight index. Trigger it with
    /// a hash that, multiplied by an irrational-ish total, accumulates
    /// FP drift. The trailing zero entries exercise the
    /// "skip non-positive on the way back" branch in the fallback.</summary>
    [TestMethod]
    public void Pick_FloatRoundingNearOne_ReturnsLastPositiveIndex() {
      // Arrange: a long list of fractional weights whose cumulative
      // sum drifts in float. With hash so close to 1 that t ~= total
      // but float comparison can fall short, the main loop may not
      // trip the t < accum check, dropping into the fallback path.
      var weights = new List<float> { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.0f, 0.0f };
      var hash = MathF.BitDecrement(1f);  // largest float strictly less than 1.

      // Act
      var result = WeightedPick.Pick(weights, hash);

      // Assert: must be a positive-weight index. The fallback walks
      // back from the end, skipping zeros; expected answer is index 6
      // (the last positive). The normal path would return 6 too, so
      // this test passes regardless of which branch executes -- the
      // value of the assertion is the coverage / lack-of-throw, not
      // the index value per se.
      Assert.AreEqual(6, result);
    }

    /// <summary>Coarse Monte-Carlo: 3:1 weights over many evenly-spaced
    /// hashes recover the 75/25 split within sampling tolerance. Guards
    /// against off-by-one boundary regressions that wouldn't show up
    /// in single-hash boundary tests.</summary>
    [TestMethod]
    public void Pick_ManySamples_RecoversWeightDistribution() {
      // Arrange
      var weights = new List<float> { 3f, 1f };
      var counts = new int[2];
      const int Samples = 1000;

      // Act: evenly-spaced hashes in [0, 1).
      for (var i = 0; i < Samples; i++) {
        var hash = (float)i / Samples;
        counts[WeightedPick.Pick(weights, hash)]++;
      }

      // Assert: 750 / 250 ± 1 (deterministic given evenly-spaced hashes).
      Assert.AreEqual(750, counts[0]);
      Assert.AreEqual(250, counts[1]);
    }

    #endregion

  }

}
