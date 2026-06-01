using Keystone.Core.Biomes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Pins per-tile riparian maturity dynamics. The load-bearing design
  /// statement is that a *brief* spell of near-water accrues almost
  /// nothing (so a transient flood can't trip the flourish), while
  /// *sustained* near-water climbs to the ceiling — i.e. the time axis
  /// the instantaneous water check dropped is restored here.
  /// </summary>
  [TestClass]
  public class RiparianMaturityUpdaterTests {

    [TestMethod]
    public void Step_NearWater_FromZero_RisesAboutAlphaPerDay() {
      // 0 + (1*1 - Beta*0) * 1 = 1 maturity-day after one near-water day.
      Assert.AreEqual(1f, RiparianMaturityUpdater.Step(0f, nearWater: true, toxic: false, deltaDays: 1f), 1e-4f);
    }

    [TestMethod]
    public void Step_BriefNearWaterCycle_BarelyAccrues_FarBelowCeiling() {
      // One ~game-hour cycle of near-water from zero. The whole point:
      // a momentary flood adds a sliver, nowhere near the ceiling.
      var afterOneHour = RiparianMaturityUpdater.Step(0f, nearWater: true, toxic: false, deltaDays: 1f / 24f);
      Assert.IsTrue(afterOneHour > 0f, "should accrue something while near water");
      Assert.IsTrue(afterOneHour < 0.1f,
          $"one hour of near-water should be a sliver, got {afterOneHour}");
      Assert.IsTrue(afterOneHour < RiparianMaturityParameters.Ceiling * 0.05f,
          "one hour must be far below the ceiling");
    }

    [TestMethod]
    public void Step_SustainedNearWater_AsymptotesAtCeiling() {
      var m = 0f;
      for (var i = 0; i < 365 * 24; i++) {
        m = RiparianMaturityUpdater.Step(m, nearWater: true, toxic: false, deltaDays: 1f / 24f);
      }
      Assert.IsTrue(m > RiparianMaturityParameters.Ceiling - 0.1f,
          $"expected near ceiling {RiparianMaturityParameters.Ceiling}, got {m}");
      Assert.IsTrue(m <= RiparianMaturityParameters.Ceiling + 1e-3f,
          $"must not exceed ceiling, got {m}");
    }

    [TestMethod]
    public void Step_NotNearWater_DissipatesAtDecayRate() {
      // 5 - DecayRatePerDay(1) * 1 = 4.
      Assert.AreEqual(
          5f - RiparianMaturityParameters.DecayRatePerDay,
          RiparianMaturityUpdater.Step(5f, nearWater: false, toxic: false, deltaDays: 1f),
          1e-4f);
    }

    [TestMethod]
    public void Step_NotNearWater_FloorsAtZero() {
      Assert.AreEqual(0f, RiparianMaturityUpdater.Step(0.3f, nearWater: false, toxic: false, deltaDays: 1f), 1e-4f);
    }

    [TestMethod]
    public void Step_Toxic_DecaysFast_EvenWhileNearWater() {
      // Toxic overrides near-water: no accrual, fast decay instead.
      // 5 - ToxicDecayRatePerDay(10) * 0.1 = 4.
      Assert.AreEqual(
          5f - RiparianMaturityParameters.ToxicDecayRatePerDay * 0.1f,
          RiparianMaturityUpdater.Step(5f, nearWater: true, toxic: true, deltaDays: 0.1f),
          1e-4f);
    }

    [TestMethod]
    public void Step_Toxic_FloorsAtZero() {
      // A toxic step that would overshoot clamps at 0, never negative.
      Assert.AreEqual(0f, RiparianMaturityUpdater.Step(0.5f, nearWater: true, toxic: true, deltaDays: 1f), 1e-4f);
    }

    [TestMethod]
    public void Step_Toxic_DecaysFasterThanPlainDissipation() {
      var toxic = RiparianMaturityUpdater.Step(8f, nearWater: false, toxic: true, deltaDays: 0.1f);
      var dry = RiparianMaturityUpdater.Step(8f, nearWater: false, toxic: false, deltaDays: 0.1f);
      Assert.IsTrue(toxic < dry,
          $"toxic decay ({toxic}) must outpace plain dissipation ({dry})");
    }

    [TestMethod]
    public void SeededValue_ZeroOrNegativeDays_IsZero() {
      Assert.AreEqual(0f, RiparianMaturityUpdater.SeededValue(0f), 0f);
      Assert.AreEqual(0f, RiparianMaturityUpdater.SeededValue(-5f), 0f);
    }

    [TestMethod]
    public void SeededValue_LongElapsed_ApproachesCeilingFromBelow() {
      var v = RiparianMaturityUpdater.SeededValue(500f);
      Assert.IsTrue(v > RiparianMaturityParameters.Ceiling - 0.01f, $"expected near ceiling, got {v}");
      // Mathematically asymptotic; in float it saturates to exactly the
      // ceiling at large t. The invariant is that it never *exceeds* it.
      Assert.IsTrue(v <= RiparianMaturityParameters.Ceiling, $"must never exceed ceiling, got {v}");
    }

    [TestMethod]
    public void SeededValue_IsMonotonicInDays() {
      var a = RiparianMaturityUpdater.SeededValue(2f);
      var b = RiparianMaturityUpdater.SeededValue(5f);
      Assert.IsTrue(a > 0f && b > a && b < RiparianMaturityParameters.Ceiling,
          $"expected 0 < {a} < {b} < {RiparianMaturityParameters.Ceiling}");
    }

    [TestMethod]
    public void IsNearWater_OnlyPositiveBand_CountsAsRiparianLand() {
      Assert.IsTrue(RiparianMaturityParameters.IsNearWater(1f), "borders water");
      Assert.IsTrue(RiparianMaturityParameters.IsNearWater(2f), "within band");
      Assert.IsFalse(RiparianMaturityParameters.IsNearWater(0f), "uninitialised / not computed");
      Assert.IsFalse(RiparianMaturityParameters.IsNearWater(3f), "too far");
      Assert.IsFalse(RiparianMaturityParameters.IsNearWater(-1f), "water tile, not land");
    }

  }

}
