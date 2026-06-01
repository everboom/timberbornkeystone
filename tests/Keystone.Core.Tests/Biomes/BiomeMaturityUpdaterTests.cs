using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  [TestClass]
  public class BiomeMaturityUpdaterTests {

    private ChunkValueRegistry _registry = null!;
    private int _slotCount;

    private static BiomeMaturityUpdater MakeUpdater() => new();

    private ChunkData MakeData() => new(_slotCount);

    private static float Maturity(ChunkData data, BiomeKind biome) {
      return data.Get(BiomeValueKinds.MaturityOrdinal(biome));
    }

    private static void SetSuitability(ChunkData data, BiomeKind biome, float value) {
      data.Set(BiomeValueKinds.SuitabilityOrdinal(biome), value);
    }

    private static void SetMaturity(ChunkData data, BiomeKind biome, float value) {
      data.Set(BiomeValueKinds.MaturityOrdinal(biome), value);
    }

    [TestInitialize]
    public void Setup() {
      _registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(_registry);
      _registry.Freeze();
      _slotCount = _registry.SlotCount;
    }

    [TestCleanup]
    public void Cleanup() {
      BiomeValueKinds.ResetOrdinals();
    }

    #region Accrue dynamics (exponential, unchanged)

    [TestMethod]
    public void Tick_FullSuitability_FromZero_AccruesAtAlphaRate() {
      // Alpha=1, BetaAccrue=1/30. Starting from 0 with Suitability=1,
      // after 1 game-day we have ~ alpha * deltaDays = 1 day of accrual.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(1f, Maturity(data, BiomeKind.Forest), 1e-4f);
    }

    [TestMethod]
    public void Tick_FullSuitability_AsymptotesNearThirtyDays() {
      // Forest ceiling 30. Asymptote = 30 at Suitability=1.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);

      for (var i = 0; i < 200 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var value = Maturity(data, BiomeKind.Forest);
      Assert.IsTrue(value > 29.9f, $"expected near 30, got {value}");
      Assert.IsTrue(value <= 30f, $"expected at or below asymptote, got {value}");
    }

    [TestMethod]
    public void Tick_PartialSuitability_AsymptotesAtScaledLevel() {
      // Suitability=0.5 -> asymptote = (1 * 0.5) / (1/30) = 15 days.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 0.5f);

      for (var i = 0; i < 200 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var value = Maturity(data, BiomeKind.Forest);
      Assert.IsTrue(value > 14.9f, $"expected near 15, got {value}");
      Assert.IsTrue(value <= 15f, $"expected at or below asymptote, got {value}");
    }

    [TestMethod]
    public void Tick_BelowAsymptote_UsesAccrueExponential() {
      // Suitability=1, current=10 days, asymptote=30 days. Accrue
      // mode: dM/dt = 1*1 - (1/30)*10 = 0.667. After 1d, expect ~10.667.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);
      SetMaturity(data, BiomeKind.Forest, 10f);

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(10.667f, Maturity(data, BiomeKind.Forest), 1e-3f);
    }

    [TestMethod]
    public void Tick_NoSuitabilityNoMaturity_StaysAtZero() {
      var updater = MakeUpdater();
      var data = MakeData();

      updater.Tick(data, deltaDays: 5f);

      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-6f);
    }

    [TestMethod]
    public void Tick_ZeroDeltaDays_IsNoop() {
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Forest, 12.5f);

      updater.Tick(data, deltaDays: 0f);

      Assert.AreEqual(12.5f, Maturity(data, BiomeKind.Forest), 1e-6f);
    }

    #endregion

    #region Decay dynamics (linear, clamped at asymptote)

    [TestMethod]
    public void Tick_NullDominant_LinearDecayAtFallbackRate() {
      // Forest M=30, all Suitabilities=0 -> null dominant. Fallback
      // positive decay 7d clear time, rate = 30/7 ~ 4.286/day. After
      // 7d the linear decay clamps at asymptote=0.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Forest, 30f);

      for (var i = 0; i < 7 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-4f);
    }

    [TestMethod]
    public void Tick_AboveAsymptote_LinearDecay_ClampsAtAsymptote() {
      // Forest Suitability=0.1, asymptote=3. M starts at 10, decay
      // mode. Self-dominant -> polarity fallback 7d clear, rate 30/7.
      // From 10 to asymptote 3 = 7 units lost at 4.286/day = 1.633d.
      // After 1d, M = 10 - 4.286 = 5.714.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 0.1f);
      SetMaturity(data, BiomeKind.Forest, 10f);

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(10f - 30f / 7f, Maturity(data, BiomeKind.Forest), 1e-3f);
    }

    [TestMethod]
    public void Tick_DecayClampedAtAsymptote_StopsAtPartialSupport() {
      // Forest Suitability=0.1, asymptote=3. M=10. After enough ticks,
      // linear decay should halt at 3, not overshoot to 0.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 0.1f);
      SetMaturity(data, BiomeKind.Forest, 10f);

      for (var i = 0; i < 10 * 24; i++) {  // 10 days, well past linear cleanup
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(3f, Maturity(data, BiomeKind.Forest), 1e-4f);
    }

    #endregion

    #region Per-biome ceilings

    [TestMethod]
    public void Tick_Contaminated_AsymptotesNear12p5() {
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Contaminated, 1f);

      // Ceiling 12.5, time constant 12.5d -> needs many time constants to
      // converge within 0.1 (e^-8 at 100d).
      for (var i = 0; i < 100 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var value = Maturity(data, BiomeKind.Contaminated);
      Assert.IsTrue(value > 12.4f && value <= 12.5f,
          $"expected near 12.5, got {value}");
    }

    [TestMethod]
    public void Tick_Badwater_AsymptotesNear15() {
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Badwater, 1f);

      // Ceiling 15, time constant 15d (e^-10 at 150d).
      for (var i = 0; i < 150 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var value = Maturity(data, BiomeKind.Badwater);
      Assert.IsTrue(value > 14.9f && value <= 15f,
          $"expected near 15, got {value}");
    }

    [TestMethod]
    public void Tick_Dry_AsymptotesNear10() {
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Dry, 1f);

      for (var i = 0; i < 150 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var value = Maturity(data, BiomeKind.Dry);
      Assert.IsTrue(value > 9.9f && value <= 10f,
          $"expected near 10, got {value}");
    }

    [TestMethod]
    public void Tick_Monoculture_AsymptotesNear3p5() {
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Monoculture, 1f);

      for (var i = 0; i < 60 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var value = Maturity(data, BiomeKind.Monoculture);
      Assert.IsTrue(value > 3.4f && value <= 3.5f,
          $"expected near 3.5, got {value}");
    }

    #endregion

    #region Decay matrix (linear)

    [TestMethod]
    public void Tick_ForestDecayingUnderGrasslandDominant_Matrix7dLinear() {
      // (Forest, Grassland) clear time 7d. Rate = 30/7 = 4.286/day.
      // From M=30, after 7 days M should be 0 (clamped at asymptote=0).
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Grassland, 1f);
      SetMaturity(data, BiomeKind.Forest, 30f);

      for (var i = 0; i < 7 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-3f);
    }

    [TestMethod]
    public void Tick_ForestDecayingUnderGrasslandDominant_HalfwayAfterHalfClearTime() {
      // Linear linearity sanity: at 3.5 days (half the 7d clear time),
      // Forest M should be at 15 (half of starting 30).
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Grassland, 1f);
      SetMaturity(data, BiomeKind.Forest, 30f);

      for (var i = 0; i < (int)(3.5 * 24); i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(15f, Maturity(data, BiomeKind.Forest), 0.1f);
    }

    [TestMethod]
    public void Tick_ForestDecayingUnderBadwaterDominant_Matrix0p5dLinear() {
      // (Forest, Badwater) clear time 0.5d. Rate = 30/0.5 = 60/day.
      // After 0.5d, M = 0 (clamped at asymptote=0).
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Badwater, 1f);
      SetMaturity(data, BiomeKind.Forest, 30f);

      for (var i = 0; i < 12; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-3f);
    }

    [TestMethod]
    public void Tick_ContaminatedUnderBadwaterDominant_CoPresent_Stays() {
      // (Contaminated, Badwater) is co-present: rate=0, M holds.
      // Contaminated Suitability=0, so asymptote=0; Maturity above
      // asymptote -> decay branch, but co-present zeroes the rate.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Badwater, 1f);
      SetMaturity(data, BiomeKind.Contaminated, 12.5f);

      for (var i = 0; i < 30 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(12.5f, Maturity(data, BiomeKind.Contaminated), 1e-4f);
    }

    [TestMethod]
    public void Tick_BadwaterDecayingUnderForestDominant_ScarFadeAt1PerDay() {
      // Badwater decays at the negative baseline 1/day under any non-
      // self dominant. Ceiling 15 -> clears in 15 days. After 7.5d
      // (halfway), Maturity = 7.5.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);
      SetMaturity(data, BiomeKind.Badwater, 15f);

      for (var i = 0; i < (int)(7.5 * 24); i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(7.5f, Maturity(data, BiomeKind.Badwater), 0.1f);
    }

    [TestMethod]
    public void Tick_BadwaterScar_ClearsScarGateInFifteenDays() {
      // From Badwater ceiling 15, at baseline rate 1/day under healthy
      // dominance, threshold 0.1 is reached at t = (15-0.1)/1 = 14.9d.
      // Allow 15d for slack.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);
      SetMaturity(data, BiomeKind.Badwater, 15f);

      for (var i = 0; i < 15 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var value = Maturity(data, BiomeKind.Badwater);
      Assert.IsTrue(value <= MaturityParameters.BadwaterScarGateThreshold,
          $"expected Badwater <= gate threshold {MaturityParameters.BadwaterScarGateThreshold} after 15d, got {value}");
    }

    [TestMethod]
    public void Tick_ContaminatedDecayingUnderForestDominant_ScarFade12p5dLinear() {
      // (Contaminated, Forest) clear time 12.5d. Rate = 12.5/12.5 = 1/day.
      // After 12.5d, M = 0.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);
      SetMaturity(data, BiomeKind.Contaminated, 12.5f);

      for (var i = 0; i < (int)(12.5 * 24); i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Contaminated), 0.1f);
    }

    [TestMethod]
    public void Tick_ContaminatedScar_ClearsScarGateInTwelveDays() {
      // From Contaminated ceiling 12.5, at scar rate 1/day under
      // healthy dominance, threshold 0.5 is reached at t = (12.5-0.5)/1 = 12d.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);
      SetMaturity(data, BiomeKind.Contaminated, 12.5f);

      for (var i = 0; i < 12 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var value = Maturity(data, BiomeKind.Contaminated);
      Assert.IsTrue(value <= MaturityParameters.ContaminatedScarGateThreshold + 0.05f,
          $"expected Contaminated <= ~0.5 after 12d, got {value}");
    }

    [TestMethod]
    public void Tick_DryDecayingUnderForestDominant_LowCeilingFastClear1d() {
      // (Dry, Forest) clear time 1d. Rate = ceiling/1 = 10/day.
      // From ceiling 10, reaches 0 in 1 day.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);
      SetMaturity(data, BiomeKind.Dry, 10f);

      for (var i = 0; i < 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Dry), 1e-3f);
    }

    [TestMethod]
    public void Tick_MonocultureUnderGrasslandDominant_RowOverride3dLinear() {
      // (Monoculture, Grassland) clear time 3d. Rate = 3.5/3 = 1.17/day.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Grassland, 1f);
      SetMaturity(data, BiomeKind.Monoculture, 3.5f);

      for (var i = 0; i < 3 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Monoculture), 1e-3f);
    }

    [TestMethod]
    public void Tick_WetlandDecayingUnderRiverDominant_CellOverride3dLinear() {
      // Cell override (Wetland, River) 3d. Rate = 30/3 = 10/day.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.River, 1f);
      SetMaturity(data, BiomeKind.Wetland, 30f);

      for (var i = 0; i < 3 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Wetland), 1e-3f);
    }

    #endregion

    #region Drought intensity (Dry-dominant decay scaling)

    [TestMethod]
    public void Tick_RiverDecayingUnderDry_AtFullDroughtMatch_MatrixRate() {
      // Dry M = 10 is well above DroughtSaturationMaturity (3.33), so
      // intensity = 1 and the matrix rate applies in full. Per-biome
      // Dry-column entry for River is 0.7d, so rate = 30/0.7 ~ 42.9/day.
      // River fully clears in well under 1 day; 3 days of ticks is
      // far past that, M ends clamped at 0.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Dry, 1f);
      SetMaturity(data, BiomeKind.Dry, 10f);    // saturated drought
      SetMaturity(data, BiomeKind.River, 30f);

      for (var i = 0; i < 3 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.River), 1e-3f);
    }

    [TestMethod]
    public void Tick_RiverDecayingUnderDry_AtZeroDryM_FloorRate() {
      // Dry M = 0 -> intensity = floor + (1-floor)*0 = floor = 0.1.
      // Matrix rate for River-under-Dry is 30/0.7 ~ 42.857/day, scaled
      // to 0.1 * 42.857 ~ 4.286/day at the floor. The pre-pass freezes
      // droughtDepth at the start-of-tick value (Dry M = 0), so the
      // scaling here uses exactly the floor for the whole tick even
      // though Dry's own M rises a bit during the integration.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Dry, 1f);
      SetMaturity(data, BiomeKind.Dry, 0f);
      SetMaturity(data, BiomeKind.River, 30f);

      updater.Tick(data, deltaDays: 0.25f);

      // Expected: River = 30 - (30/0.7) * 0.1 * 0.25 ~ 28.929.
      var expected = 30f - (30f / 0.7f) * 0.1f * 0.25f;
      Assert.AreEqual(expected, Maturity(data, BiomeKind.River), 1e-3f);
    }

    [TestMethod]
    public void Tick_GrasslandDecayingUnderDry_AtZeroDryM_HoldsCompletely() {
      // Grassland floor = 0 -> intensity = 0 + 1*0 = 0. No decay at all
      // until Dry has built up. Pre-pass freezes droughtDepth at 0 for
      // this tick.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Dry, 1f);
      SetMaturity(data, BiomeKind.Dry, 0f);
      SetMaturity(data, BiomeKind.Grassland, 30f);

      updater.Tick(data, deltaDays: 0.25f);

      Assert.AreEqual(30f, Maturity(data, BiomeKind.Grassland), 1e-4f);
    }

    [TestMethod]
    public void Tick_GrasslandDecayingUnderDry_AtFullDroughtMatch_MatrixRate() {
      // Dry M = 10 is well above DroughtSaturationMaturity (3.33), so
      // intensity = 1 and the matrix rate applies in full. Per-biome
      // Dry-column entry for Grassland is 2.1d, so rate = 30/2.1 ~
      // 14.286/day. Fully clears in ~2.1 days; 3 days of ticks is past
      // that, M ends clamped at 0.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Dry, 1f);
      SetMaturity(data, BiomeKind.Dry, 10f);
      SetMaturity(data, BiomeKind.Grassland, 30f);

      for (var i = 0; i < 3 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Grassland), 1e-3f);
    }

    [TestMethod]
    public void Tick_WetlandUnderDry_HasFloorScaling_GrasslandDoesNot() {
      // Same chunk: both Wetland (floor 0.1) and Grassland (floor 0)
      // are decaying under Dry-dominant with Dry M = 0. After one
      // quarter-day tick, Wetland has dropped, Grassland has not.
      // Pins the per-biome floor distinction in a single observation.
      // Wetland matrix rate is 30/1.8 ~ 16.667/day; floor lops it to
      // 1.667/day, total loss over 0.25d = ~0.417.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Dry, 1f);
      SetMaturity(data, BiomeKind.Dry, 0f);
      SetMaturity(data, BiomeKind.Wetland, 30f);
      SetMaturity(data, BiomeKind.Grassland, 30f);

      updater.Tick(data, deltaDays: 0.25f);

      var expectedWetland = 30f - (30f / 1.8f) * 0.1f * 0.25f;
      Assert.AreEqual(expectedWetland, Maturity(data, BiomeKind.Wetland), 1e-3f,
          "Wetland (floor 0.1, matrix 16.67/day) should lose ~0.417 over 0.25d at Dry M=0");
      Assert.AreEqual(30f, Maturity(data, BiomeKind.Grassland), 1e-4f,
          "Grassland (floor 0) should hold at Dry M=0");
    }

    [TestMethod]
    public void Tick_GrasslandUnderFreshDry_TrajectoryBuffersThenAccelerates() {
      // Coupled dynamics under a fresh drought (Dry building from 0
      // with Suitability=1, alpha=1, beta=1/10):
      //   M_dry(t)        = 10 * (1 - exp(-t/10))
      //   droughtDepth(t) = min(1, M_dry / 3.33) saturates near t=4d
      // Grassland matrix rate (Grassland, Dry) = 30/2.1 ~ 14.286/day,
      // floor = 0. Effective rate = 14.286 * droughtDepth(t). For
      // t < 4d the closed form (before saturation) is:
      //   M_grass(t) = 30 - 14.286 * (10/3.33) * (t + 10*exp(-t/10) - 10)
      //              = 30 - 42.9 * (t + 10*exp(-t/10) - 10)
      // Target: ~4 days fresh clear for M=30, vs 2.1d saturated nominal.
      // The "buffer then accelerate" arc is preserved (slow day 1)
      // while the total time matches the design target.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Dry, 1f);
      SetMaturity(data, BiomeKind.Grassland, 30f);

      void TickHours(int hours) {
        for (var i = 0; i < hours; i++) {
          updater.Tick(data, deltaDays: 1f / 24f);
        }
      }

      TickHours(24);  // t = 1d, exact M_grass ~ 27.92
      Assert.AreEqual(27.92f, Maturity(data, BiomeKind.Grassland), 0.3f,
          "day 1: ~2 lost; drought ramps faster than the old M_dry/Ceiling form");

      TickHours(24);  // t = 2d, exact M_grass ~ 21.98
      Assert.AreEqual(21.98f, Maturity(data, BiomeKind.Grassland), 0.4f,
          "day 2: ~8 lost cumulatively");

      TickHours(24);  // t = 3d, exact M_grass ~ 12.49
      Assert.AreEqual(12.49f, Maturity(data, BiomeKind.Grassland), 0.5f,
          "day 3: ~17.5 lost; intensity nearing saturation");

      TickHours(24);  // t = 4d, drought saturated, Grassland cleared
      Assert.IsTrue(Maturity(data, BiomeKind.Grassland) < 0.5f,
          $"day 4: Grassland should have cleared, got {Maturity(data, BiomeKind.Grassland)}");
    }

    [TestMethod]
    public void Tick_ForestUnderFreshDry_SlowestKillTrajectory() {
      // Forest matrix rate 30/4.1 ~ 7.317/day, floor = 0. Pre-saturation
      // closed form:
      //   M_forest(t) = 30 - 7.317 * (10/3.33) * (t + 10*exp(-t/10) - 10)
      //               = 30 - 21.97 * (t + 10*exp(-t/10) - 10)
      // After drought saturates (t > ~4.05d), rate is a constant 7.317/day
      // and the remaining ~14 Maturity is shed linearly. Total fresh
      // clear time ~6 days, the longest kill time among healthy biomes.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Dry, 1f);
      SetMaturity(data, BiomeKind.Forest, 30f);

      void TickHours(int hours) {
        for (var i = 0; i < hours; i++) {
          updater.Tick(data, deltaDays: 1f / 24f);
        }
      }

      TickHours(24);       // t = 1d, exact ~ 28.94
      Assert.AreEqual(28.94f, Maturity(data, BiomeKind.Forest), 0.3f);

      TickHours(2 * 24);   // t = 3d, exact ~ 21.04
      Assert.AreEqual(21.04f, Maturity(data, BiomeKind.Forest), 0.5f);

      TickHours(1 * 24);   // t = 4d, exact ~ 14.56 (drought just saturated)
      Assert.AreEqual(14.56f, Maturity(data, BiomeKind.Forest), 0.5f);

      TickHours(2 * 24);   // t = 6d, expected near 0
      Assert.IsTrue(Maturity(data, BiomeKind.Forest) < 0.5f,
          $"day 6: Forest should have cleared, got {Maturity(data, BiomeKind.Forest)}");
    }

    [TestMethod]
    public void Tick_NonDryDominant_DroughtScalingNotApplied() {
      // Drought scaling fires only when Dry is the chunk's dominant
      // biome. Under Grassland dominant, Forest decays at the matrix
      // rate (30/7d ~ 4.286/day) regardless of Dry's Maturity.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Grassland, 1f);
      SetMaturity(data, BiomeKind.Dry, 10f);     // saturated drought, but
      SetMaturity(data, BiomeKind.Forest, 30f);  // Grassland is dominant

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(30f - 30f / 7f, Maturity(data, BiomeKind.Forest), 1e-3f);
    }

    #endregion

    #region Stacking (Stage D + linear)

    [TestMethod]
    public void Tick_StackedBadwaterAndContaminated_BadwaterWins_ForestDecaysAt0p5dLinear() {
      // Both Suitabilities saturate to 1; aggressor tiebreak picks
      // Badwater dominant. Forest decays at the BW column rate
      // (60/day), reaches 0 in 0.5d.
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Contaminated, 1f);
      SetSuitability(data, BiomeKind.Badwater, 1f);
      SetMaturity(data, BiomeKind.Forest, 30f);

      for (var i = 0; i < 12; i++) {  // 0.5 days
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-3f);
    }

    [TestMethod]
    public void Tick_StackedBadwaterAndContaminated_BothMaturitiesAccrueTogether() {
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Contaminated, 1f);
      SetSuitability(data, BiomeKind.Badwater, 1f);

      // Deep toxic ceilings (12.5 / 15) need many time constants to
      // converge; 100 days puts both within 0.1 of their asymptote.
      for (var i = 0; i < 100 * 24; i++) {
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      var contaminated = Maturity(data, BiomeKind.Contaminated);
      var badwater = Maturity(data, BiomeKind.Badwater);
      Assert.IsTrue(contaminated > 12.4f && contaminated <= 12.5f,
          $"Contaminated expected ~12.5, got {contaminated}");
      Assert.IsTrue(badwater > 14.9f && badwater <= 15f,
          $"Badwater expected ~15, got {badwater}");
    }

    #endregion

    #region Scar gate

    [TestMethod]
    public void Tick_BadwaterMaturityAboveGate_BlocksHealthyAccrue() {
      // Badwater Maturity = 0.5 (above threshold 0.1). Forest has
      // Suitability = 1 trying to accrue from 0. Gate should block,
      // Forest M stays at 0.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Badwater, 0.5f);
      SetSuitability(data, BiomeKind.Forest, 1f);

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-4f);
    }

    [TestMethod]
    public void Tick_ContaminatedMaturityAboveGate_BlocksHealthyAccrue() {
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Contaminated, 1f);  // above 0.5 threshold
      SetSuitability(data, BiomeKind.Forest, 1f);

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-4f);
    }

    [TestMethod]
    public void Tick_ToxicMaturitiesBelowGate_HealthyAccrueResumes() {
      // Badwater M = 0.05 (below 0.1), Contaminated M = 0.3 (below 0.5).
      // Gate open. Forest accrues normally.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Badwater, 0.05f);
      SetMaturity(data, BiomeKind.Contaminated, 0.3f);
      SetSuitability(data, BiomeKind.Forest, 1f);

      updater.Tick(data, deltaDays: 1f);

      // Forest dM/dt = 1*1 - (1/30)*0 = 1. After 1d, M ~ 1.
      Assert.AreEqual(1f, Maturity(data, BiomeKind.Forest), 1e-4f);
    }

    [TestMethod]
    public void Tick_GateBlocksAllHealthyBiomes() {
      // All non-negative biomes should be blocked.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Badwater, 1f);  // gate closed
      foreach (BiomeKind biome in BiomeValueKinds.AllBiomes) {
        if (!MaturityParameters.IsNegative(biome)) {
          SetSuitability(data, biome, 1f);
        }
      }

      updater.Tick(data, deltaDays: 1f);

      foreach (BiomeKind biome in BiomeValueKinds.AllBiomes) {
        if (!MaturityParameters.IsNegative(biome)) {
          Assert.AreEqual(0f, Maturity(data, biome), 1e-4f,
              $"{biome} should be blocked from accruing while Badwater M > gate");
        }
      }
    }

    [TestMethod]
    public void Tick_GateDoesNotBlockToxicBiomes() {
      // Toxic biomes accrue normally regardless of their own scar gate state.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Badwater, 1f);  // gate closed
      SetSuitability(data, BiomeKind.Contaminated, 1f);

      updater.Tick(data, deltaDays: 1f);

      // Contaminated should accrue: dM/dt = 1*1 - (1/12.5)*0 = 1.
      Assert.AreEqual(1f, Maturity(data, BiomeKind.Contaminated), 1e-4f);
    }

    [TestMethod]
    public void Tick_BadwaterScarClosed_BlocksDryAccrue() {
      // Dry is gated alongside healthy biomes -- the contamination-
      // input cancellation in BiomeTargets only suppresses Dry while
      // the input is present; without the scar gate, Dry Maturity
      // would spring back as soon as the contamination input cleared,
      // before the scar Maturity had finished draining. Pin Badwater
      // direction: scar M = 1 (above 0.1 gate) with Dry Suitability
      // = 1 must NOT produce Dry accrual.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Badwater, 1f);  // gate closed
      SetSuitability(data, BiomeKind.Dry, 1f);

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(0f, Maturity(data, BiomeKind.Dry), 1e-4f);
    }

    [TestMethod]
    public void Tick_ContaminatedScarClosed_BlocksDryAccrue() {
      // Pin Contaminated direction of the same rule.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Contaminated, 1f);  // above 0.5 gate
      SetSuitability(data, BiomeKind.Dry, 1f);

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(0f, Maturity(data, BiomeKind.Dry), 1e-4f);
    }

    [TestMethod]
    public void Tick_ToxicMaturitiesBelowGate_DryAccrueResumes() {
      // Mirror of Tick_ToxicMaturitiesBelowGate_HealthyAccrueResumes
      // for Dry: once both scars sit below their gates, Dry accrues
      // at its normal alpha=1, ceiling=10 rate.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Badwater, 0.05f);
      SetMaturity(data, BiomeKind.Contaminated, 0.3f);
      SetSuitability(data, BiomeKind.Dry, 1f);

      updater.Tick(data, deltaDays: 1f);

      // Dry dM/dt = 1*1 - (1/10)*0 = 1. After 1d, M ~ 1.
      Assert.AreEqual(1f, Maturity(data, BiomeKind.Dry), 1e-4f);
    }

    [TestMethod]
    public void Tick_GateDoesNotBlockDecay_OnlyAccrue() {
      // Forest has accumulated Maturity, then toxic appears; gate is
      // closed but Forest is in DECAY mode (asymptote = 0 because its
      // Suitability is 0). Decay should proceed normally.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Badwater, 1f);  // gate closed
      SetSuitability(data, BiomeKind.Badwater, 1f);  // Badwater is dominant
      SetMaturity(data, BiomeKind.Forest, 30f);
      // (Forest, Badwater) clear time 0.5d -> rate 60/day.

      for (var i = 0; i < 12; i++) {  // 0.5 days
        updater.Tick(data, deltaDays: 1f / 24f);
      }
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-3f);
    }

    [TestMethod]
    public void Tick_GateUsesPreTickToxicMaturity() {
      // Gate state is computed at the start of the tick, before any
      // updates land. If Badwater starts at 0.11 (above gate 0.1) and
      // decays during this tick to below 0.1, the gate stays closed
      // for THIS tick's accrue checks. Healthy biomes don't sneak in
      // a partial accrual on the same tick the scar drops.
      var updater = MakeUpdater();
      var data = MakeData();
      SetMaturity(data, BiomeKind.Badwater, 0.11f);
      SetSuitability(data, BiomeKind.Forest, 1f);  // healthy dominant -> Badwater decays at 1/day
      // After 1d, Badwater would drop by 1 to 0 (clamped). But the
      // gate was closed at tick start, so Forest doesn't accrue this tick.

      updater.Tick(data, deltaDays: 1f);

      Assert.AreEqual(0f, Maturity(data, BiomeKind.Forest), 1e-4f);
    }

    #endregion

    #region Cross-biome isolation

    [TestMethod]
    public void Tick_SuitabilityInOneBiome_DoesNotAffectAnother() {
      var updater = MakeUpdater();
      var data = MakeData();
      SetSuitability(data, BiomeKind.Forest, 1f);

      updater.Tick(data, deltaDays: 1f);

      Assert.IsTrue(Maturity(data, BiomeKind.Forest) > 0f);
      Assert.AreEqual(0f, Maturity(data, BiomeKind.Grassland), 1e-6f);
    }

    #endregion

    #region Argument validation

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentNullException))]
    public void Tick_NullData_Throws() {
      MakeUpdater().Tick(null!, deltaDays: 0.1f);
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentOutOfRangeException))]
    public void Tick_NegativeDeltaDays_Throws() {
      MakeUpdater().Tick(MakeData(), deltaDays: -0.5f);
    }

    #endregion

  }

}
