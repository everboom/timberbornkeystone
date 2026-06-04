using Keystone.Core.Cutting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Cutting {

  /// <summary>
  /// Tests for <see cref="ThinningSelector"/> — the pure per-tile thinning-cut
  /// selection predicate. Pins the design decisions the percentage brush was
  /// built around: per-tile (area-independent) verdicts, seed-driven reroll,
  /// expected-fraction coverage, and cross-session determinism.
  /// </summary>
  [TestClass]
  public class ThinningSelectorTests {

    #region Boundaries

    /// <summary>Fraction &lt;= 0 never marks, regardless of tile or seed — the
    /// "0% thins nothing" floor.</summary>
    [TestMethod]
    public void ShouldMark_FractionZeroOrLess_NeverMarks() {
      // Arrange / Act / Assert
      Assert.IsFalse(ThinningSelector.ShouldMark(0, 0, 0, 0d, 0));
      Assert.IsFalse(ThinningSelector.ShouldMark(7, 13, 2, 0d, 999));
      Assert.IsFalse(ThinningSelector.ShouldMark(7, 13, 2, -0.5d, 999));
    }

    /// <summary>Fraction &gt;= 1 always marks — "100% clear-cuts the selection"
    /// — without consulting the hash (so it's exact at the ceiling).</summary>
    [TestMethod]
    public void ShouldMark_FractionOneOrMore_AlwaysMarks() {
      // Arrange / Act / Assert
      Assert.IsTrue(ThinningSelector.ShouldMark(0, 0, 0, 1d, 0));
      Assert.IsTrue(ThinningSelector.ShouldMark(7, 13, 2, 1d, 999));
      Assert.IsTrue(ThinningSelector.ShouldMark(7, 13, 2, 1.5d, 999));
    }

    /// <summary><see cref="ThinningSelector.Sample"/> stays in <c>[0, 1)</c>
    /// across a spread of coordinates and seeds (so it's a valid threshold
    /// input and never marks at fraction exactly 0 / always at exactly 1).</summary>
    [TestMethod]
    public void Sample_AlwaysInUnitInterval() {
      // Arrange / Act / Assert
      for (var x = -20; x <= 20; x++) {
        for (var y = -20; y <= 20; y++) {
          var s = ThinningSelector.Sample(x, y, x & 3, x * 31 + y);
          Assert.IsTrue(s >= 0f && s < 1f, $"sample {s} out of [0,1) at ({x},{y})");
        }
      }
    }

    #endregion

    #region Determinism

    /// <summary>Same <c>(x, y, z, fraction, seed)</c> always yields the same
    /// verdict — the cross-session determinism the brush relies on (preview ==
    /// commit, stable saves).</summary>
    [TestMethod]
    public void ShouldMark_SameInputs_Deterministic() {
      // Arrange
      var first = ThinningSelector.ShouldMark(5, 7, 0, 0.5d, 42);

      // Act / Assert
      for (var i = 0; i < 100; i++) {
        Assert.AreEqual(first, ThinningSelector.ShouldMark(5, 7, 0, 0.5d, 42));
      }
    }

    /// <summary>Every coordinate axis <em>and</em> the seed is mixed into the
    /// sample, so a tile's selection depends only on its own (x, y, z) + seed —
    /// the basis for area-independent, flicker-free verdicts and for stacked
    /// columns / rerolls not aliasing together. Asserted on the continuous
    /// <see cref="ThinningSelector.Sample"/> rather than the coarse boolean,
    /// which can coincidentally agree across the 0.5 threshold.</summary>
    [TestMethod]
    public void Sample_RespondsToEveryAxisAndSeed() {
      // Arrange
      var baseSample = ThinningSelector.Sample(0, 0, 0, 0);

      // Act / Assert: perturbing any single input changes the sample.
      Assert.AreNotEqual(baseSample, ThinningSelector.Sample(1, 0, 0, 0), "x must affect the hash");
      Assert.AreNotEqual(baseSample, ThinningSelector.Sample(0, 1, 0, 0), "y must affect the hash");
      Assert.AreNotEqual(baseSample, ThinningSelector.Sample(0, 0, 1, 0), "z must affect the hash");
      Assert.AreNotEqual(baseSample, ThinningSelector.Sample(0, 0, 0, 1), "seed must affect the hash");
    }

    #endregion

    #region Coverage (expected fraction)

    /// <summary>Over a large grid the marked share approximates the requested
    /// fraction (≈, not exact — independent per-tile coin flips). Deterministic
    /// given the fixed hash, so this band never flakes once it passes.</summary>
    [TestMethod]
    public void ShouldMark_LargeGrid_ApproximatesRequestedFraction() {
      // Arrange
      const double Fraction = 0.3d;
      const int Side = 80;            // 6,400 tiles
      const int Total = Side * Side;
      const int Seed = 1234;

      // Act
      var marked = 0;
      for (var x = 0; x < Side; x++) {
        for (var y = 0; y < Side; y++) {
          if (ThinningSelector.ShouldMark(x, y, 0, Fraction, Seed)) marked++;
        }
      }

      // Assert: within ±3 percentage points of 30% over 6,400 samples.
      var share = (double)marked / Total;
      Assert.IsTrue(share > 0.27d && share < 0.33d,
          $"marked share {share:P1} should be ≈30% over {Total} tiles");
    }

    #endregion

    #region Reroll

    /// <summary>Changing the seed reshuffles the selection: a substantial share
    /// of tiles flip verdict between two seeds over the same grid. This is the
    /// "re-drag the same area to get a different mix" behavior.</summary>
    [TestMethod]
    public void ShouldMark_DifferentSeeds_ReshuffleSelection() {
      // Arrange
      const double Fraction = 0.5d;
      const int Side = 60;            // 3,600 tiles
      const int Total = Side * Side;

      // Act: count tiles whose verdict differs between consecutive seeds
      // (the tool bumps the seed by one after each drag).
      var flipped = 0;
      for (var x = 0; x < Side; x++) {
        for (var y = 0; y < Side; y++) {
          var a = ThinningSelector.ShouldMark(x, y, 0, Fraction, 7);
          var b = ThinningSelector.ShouldMark(x, y, 0, Fraction, 8);
          if (a != b) flipped++;
        }
      }

      // Assert: at 50%, two independent draws disagree on ~half the tiles;
      // require a healthy minimum so a degenerate (seed-ignoring) hash fails.
      var flipShare = (double)flipped / Total;
      Assert.IsTrue(flipShare > 0.30d,
          $"only {flipShare:P1} of tiles changed between seeds; reroll is too weak");
    }

    #endregion

  }

}
