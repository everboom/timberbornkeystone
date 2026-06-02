using System;
using Keystone.Core.Flourish;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Flourish {

  /// <summary>
  /// Pins the cadence-correctness rule of
  /// <see cref="DeadFlourishDecay.PerCycleProbability"/>: a one-day
  /// cycle yields exactly the per-day chance, longer cycles compound to
  /// preserve the per-day rate, and a zero-dt cycle (the first sweep
  /// after a load) deletes nothing.
  /// </summary>
  [TestClass]
  public class DeadFlourishDecayTests {

    private const float Tolerance = 1e-5f;

    #region Default constant

    [TestMethod]
    public void DefaultDailyDeleteChance_IsTenPercent() {
      // The "~10% per day" design figure. Pin so a stealth move doesn't
      // silently change the decay rate dead flourishes rot at.
      Assert.AreEqual(0.10f, DeadFlourishDecay.DefaultDailyDeleteChance, Tolerance);
    }

    #endregion

    #region One-day cycle reads as the per-day chance

    [TestMethod]
    public void PerCycleProbability_OneDayCycle_EqualsDailyChance() {
      // Arrange / Act
      var p = DeadFlourishDecay.PerCycleProbability(0.10f, cycleDtDays: 1f);

      // Assert — a normal once-per-day cycle is exactly the daily rate.
      Assert.AreEqual(0.10f, p, Tolerance);
    }

    #endregion

    #region Longer cycles compound the per-day rate

    [TestMethod]
    public void PerCycleProbability_TwoDayCycle_CompoundsToNineteenPercent() {
      // Arrange / Act — a cycle that spanned two game-days (starved
      // sweep, fast-forward) is two days' worth of 10%.
      var p = DeadFlourishDecay.PerCycleProbability(0.10f, cycleDtDays: 2f);

      // Assert — 1 - 0.9^2 = 0.19.
      Assert.AreEqual(0.19f, p, Tolerance);
    }

    [TestMethod]
    public void PerCycleProbability_FractionalDay_IsLessThanDailyChance() {
      // Arrange / Act — half a day is less than a full day's chance.
      var p = DeadFlourishDecay.PerCycleProbability(0.10f, cycleDtDays: 0.5f);

      // Assert — 1 - 0.9^0.5 ≈ 0.0513.
      Assert.AreEqual(1f - (float)Math.Sqrt(0.9), p, Tolerance);
      Assert.IsTrue(p < 0.10f);
    }

    #endregion

    #region Zero-dt cycle deletes nothing (no load-time cull)

    [TestMethod]
    public void PerCycleProbability_ZeroDt_ReturnsZero() {
      // The rolling sweep reports dt = 0 on its first cycle after a
      // load; nothing should be culled until real game-time elapses.
      var p = DeadFlourishDecay.PerCycleProbability(0.10f, cycleDtDays: 0f);

      Assert.AreEqual(0f, p, Tolerance);
    }

    #endregion

    #region Certain / impossible daily chances

    [TestMethod]
    public void PerCycleProbability_ZeroDailyChance_ReturnsZero() {
      Assert.AreEqual(0f, DeadFlourishDecay.PerCycleProbability(0f, 1f), Tolerance);
    }

    [TestMethod]
    public void PerCycleProbability_CertainDailyChance_ReturnsOneForAnyPositiveDt() {
      // A daily chance of 1 means "gone within the day" — stays 1 even
      // for a fractional cycle, where float pow would otherwise undershoot.
      Assert.AreEqual(1f, DeadFlourishDecay.PerCycleProbability(1f, 0.25f), Tolerance);
      Assert.AreEqual(1f, DeadFlourishDecay.PerCycleProbability(1f, 1f), Tolerance);
    }

    #endregion

    #region Invalid arguments fail loudly

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void PerCycleProbability_DailyChanceAboveOne_Throws() {
      DeadFlourishDecay.PerCycleProbability(1.5f, 1f);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void PerCycleProbability_NegativeDailyChance_Throws() {
      DeadFlourishDecay.PerCycleProbability(-0.1f, 1f);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void PerCycleProbability_NegativeDt_Throws() {
      DeadFlourishDecay.PerCycleProbability(0.10f, -1f);
    }

    #endregion

  }

}
