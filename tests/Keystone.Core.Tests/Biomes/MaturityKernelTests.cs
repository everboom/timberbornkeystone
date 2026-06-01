using Keystone.Core.Biomes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Pins the shared maturity-integration primitives. These are the
  /// math the per-chunk <see cref="BiomeMaturityUpdater"/> and the
  /// per-tile riparian sweep both call, so a change here moves every
  /// maturity channel at once.
  /// </summary>
  [TestClass]
  public class MaturityKernelTests {

    // Standard healthy-biome constants (Alpha=1, ceiling=30 => Beta=1/30).
    private const float Alpha = 1f;
    private const float Beta = 1f / 30f;

    #region Asymptote

    [TestMethod]
    public void Asymptote_FullSuitability_EqualsCeiling() {
      // (Alpha * 1) / (1/30) = 30.
      Assert.AreEqual(30f, MaturityKernel.Asymptote(1f, Alpha, Beta), 1e-4f);
    }

    [TestMethod]
    public void Asymptote_HalfSuitability_HalvesCeiling() {
      Assert.AreEqual(15f, MaturityKernel.Asymptote(0.5f, Alpha, Beta), 1e-4f);
    }

    [TestMethod]
    public void Asymptote_NonPositiveSuitability_IsZero() {
      Assert.AreEqual(0f, MaturityKernel.Asymptote(0f, Alpha, Beta), 0f);
      Assert.AreEqual(0f, MaturityKernel.Asymptote(-1f, Alpha, Beta), 0f);
    }

    #endregion

    #region Accrue

    [TestMethod]
    public void Accrue_FromZero_FullSuitability_RisesAtAlphaPerDay() {
      // 0 + (1*1 - (1/30)*0) * 1 = 1.
      Assert.AreEqual(1f, MaturityKernel.Accrue(0f, 1f, Alpha, Beta, deltaDays: 1f), 1e-4f);
    }

    [TestMethod]
    public void Accrue_StepsApproachAsymptoteFromBelowAndNeverOvershoot() {
      // Repeated small steps converge toward (but do not exceed) 30.
      var m = 0f;
      for (var i = 0; i < 200 * 24; i++) {
        m = MaturityKernel.Accrue(m, 1f, Alpha, Beta, deltaDays: 1f / 24f);
      }
      Assert.IsTrue(m > 29.9f, $"expected near 30, got {m}");
      Assert.IsTrue(m <= 30f, $"expected at or below asymptote, got {m}");
    }

    [TestMethod]
    public void Accrue_ZeroSuitability_DecaysTowardZero() {
      // With no support the accrue step is pure -Beta*M relaxation.
      // (current 10 > asymptote 0, so the upward clamp doesn't touch it.)
      var next = MaturityKernel.Accrue(10f, 0f, Alpha, Beta, deltaDays: 1f);
      Assert.AreEqual(10f - Beta * 10f, next, 1e-4f);
    }

    [TestMethod]
    public void Accrue_LargeStepOvershoot_ClampedToAsymptote() {
      // A single big step (the new-game warmup seeds many game-days at once)
      // must not Euler-overshoot the asymptote. At S=1 the asymptote is 30;
      // a 100-day raw Euler step would reach 100. The clamp holds it at 30.
      var m = MaturityKernel.Accrue(0f, 1f, Alpha, Beta, deltaDays: 100f);
      Assert.AreEqual(30f, m, 1e-4f);
    }

    [TestMethod]
    public void Accrue_LowCeilingBiome_BigWarmupStep_DoesNotExceedCeiling() {
      // Monoculture: ceiling 3.5 => Beta = 1/3.5. A 7-day warmup step at
      // full suitability previously seeded 7 (2x ceiling); now clamps to
      // the 3.5 ceiling.
      const float lowBeta = 1f / 3.5f;
      var m = MaturityKernel.Accrue(0f, 1f, Alpha, lowBeta, deltaDays: 7f);
      Assert.AreEqual(3.5f, m, 1e-4f);
    }

    #endregion

    #region DecayLinear

    [TestMethod]
    public void DecayLinear_AboveFloor_SubtractsRateTimesDelta() {
      Assert.AreEqual(8f, MaturityKernel.DecayLinear(10f, ratePerDay: 2f, deltaDays: 1f, floor: 0f), 1e-4f);
    }

    [TestMethod]
    public void DecayLinear_ClampsAtFloor_NeverUndershoots() {
      // A step that would cross the floor stops exactly at it.
      Assert.AreEqual(5f, MaturityKernel.DecayLinear(6f, ratePerDay: 10f, deltaDays: 1f, floor: 5f), 1e-4f);
    }

    [TestMethod]
    public void DecayLinear_AlreadyAtFloor_Holds() {
      Assert.AreEqual(5f, MaturityKernel.DecayLinear(5f, ratePerDay: 10f, deltaDays: 1f, floor: 5f), 1e-4f);
    }

    #endregion

  }

}
