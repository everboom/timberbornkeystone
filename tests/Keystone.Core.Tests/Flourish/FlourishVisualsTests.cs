using Keystone.Core.Ecology.Fields;
using Keystone.Core.Flourish;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Flourish {

  /// <summary>
  /// Pins the pure rules of <see cref="FlourishVisuals"/>:
  /// <list type="bullet">
  ///   <item><see cref="FlourishVisuals.LeafFor"/> mapping for every
  ///         (phase × lifeStatus × health) combination, including the
  ///         two special cases: Stump phase short-circuits, Dead
  ///         life-status routes to the phase's <c>#Dead</c> leaf
  ///         regardless of health.</item>
  ///   <item><see cref="FlourishVisuals.ShouldDieFromBadwater"/>
  ///         predicate with explicit boundary handling for water depth
  ///         and contamination thresholds.</item>
  /// </list>
  /// </summary>
  [TestClass]
  public class FlourishVisualsTests {

    #region Threshold value

    [TestMethod]
    public void BadwaterContaminationThreshold_IsExactly0p1() {
      // Distinct from WaterContamination.Threshold (0.05) — this is the
      // per-tile self-kill cutoff, stricter than the chunk aggregate.
      // Pin so a stealth move doesn't silently change the per-entity
      // kill behaviour.
      Assert.AreEqual(0.1f, FlourishVisuals.BadwaterContaminationThreshold);
    }

    #endregion

    #region LeafFor — Stump phase

    [TestMethod]
    public void LeafFor_StumpPhase_AlwaysReturnsStumpRegardlessOfOtherAxes() {
      Assert.AreEqual(FlourishVisualLeaf.Stump,
          FlourishVisuals.LeafFor(FlourishPhase.Stump, FlourishLifeStatus.Alive, FlourishHealth.Healthy));
      Assert.AreEqual(FlourishVisualLeaf.Stump,
          FlourishVisuals.LeafFor(FlourishPhase.Stump, FlourishLifeStatus.Alive, FlourishHealth.Dry));
      Assert.AreEqual(FlourishVisualLeaf.Stump,
          FlourishVisuals.LeafFor(FlourishPhase.Stump, FlourishLifeStatus.Dead, FlourishHealth.Healthy));
      Assert.AreEqual(FlourishVisualLeaf.Stump,
          FlourishVisuals.LeafFor(FlourishPhase.Stump, FlourishLifeStatus.Dead, FlourishHealth.Dry));
    }

    #endregion

    #region LeafFor — Dead lifeStatus

    [TestMethod]
    public void LeafFor_DeadStatusOnSeedling_RoutesToSeedlingDeadRegardlessOfHealth() {
      Assert.AreEqual(FlourishVisualLeaf.SeedlingDead,
          FlourishVisuals.LeafFor(FlourishPhase.Seedling, FlourishLifeStatus.Dead, FlourishHealth.Healthy));
      Assert.AreEqual(FlourishVisualLeaf.SeedlingDead,
          FlourishVisuals.LeafFor(FlourishPhase.Seedling, FlourishLifeStatus.Dead, FlourishHealth.Dry));
    }

    [TestMethod]
    public void LeafFor_DeadStatusOnMature_RoutesToMatureDeadRegardlessOfHealth() {
      Assert.AreEqual(FlourishVisualLeaf.MatureDead,
          FlourishVisuals.LeafFor(FlourishPhase.Mature, FlourishLifeStatus.Dead, FlourishHealth.Healthy));
      Assert.AreEqual(FlourishVisualLeaf.MatureDead,
          FlourishVisuals.LeafFor(FlourishPhase.Mature, FlourishLifeStatus.Dead, FlourishHealth.Dry));
    }

    #endregion

    #region LeafFor — Alive routing

    [TestMethod]
    public void LeafFor_AliveSeedlingHealthy_ReturnsSeedlingAlive() {
      Assert.AreEqual(FlourishVisualLeaf.SeedlingAlive,
          FlourishVisuals.LeafFor(FlourishPhase.Seedling, FlourishLifeStatus.Alive, FlourishHealth.Healthy));
    }

    [TestMethod]
    public void LeafFor_AliveSeedlingDry_ReturnsSeedlingDying() {
      Assert.AreEqual(FlourishVisualLeaf.SeedlingDying,
          FlourishVisuals.LeafFor(FlourishPhase.Seedling, FlourishLifeStatus.Alive, FlourishHealth.Dry));
    }

    [TestMethod]
    public void LeafFor_AliveMatureHealthy_ReturnsMatureAlive() {
      Assert.AreEqual(FlourishVisualLeaf.MatureAlive,
          FlourishVisuals.LeafFor(FlourishPhase.Mature, FlourishLifeStatus.Alive, FlourishHealth.Healthy));
    }

    [TestMethod]
    public void LeafFor_AliveMatureDry_ReturnsMatureDying() {
      Assert.AreEqual(FlourishVisualLeaf.MatureDying,
          FlourishVisuals.LeafFor(FlourishPhase.Mature, FlourishLifeStatus.Alive, FlourishHealth.Dry));
    }

    #endregion

    #region LeafFor — unknown-enum defensive arms

    /// <summary>
    /// Pins that <see cref="FlourishVisuals.LeafFor"/> returns
    /// <c>null</c> when the Dead-status switch encounters a phase
    /// it doesn't enumerate. Stump is filtered upstream at the
    /// method's first guard, so the only way to land in this arm is
    /// a future <see cref="FlourishPhase"/> enum addition that
    /// hasn't been wired into the switch yet (or a corrupted save
    /// supplying an unknown value). The contract is "return null and
    /// let the Mod-side caller fall back to a default visual rather
    /// than crash"; this test pins that contract against a refactor
    /// that throws on unknown values.
    /// </summary>
    [TestMethod]
    public void LeafFor_DeadStatusWithUnknownPhase_ReturnsNull() {
      // Arrange — fabricate an out-of-range phase value. Cast bypasses
      // the enum's value-set check, mirroring what a corrupted save or
      // future enum extension produces.
      var unknownPhase = (FlourishPhase)999;

      // Act
      var leaf = FlourishVisuals.LeafFor(
          unknownPhase, FlourishLifeStatus.Dead, FlourishHealth.Healthy);

      // Assert — defensive arm returns null rather than throwing.
      Assert.IsNull(leaf,
          "Dead status with unknown phase must return null (defensive "
          + "fallback), not throw.");
    }

    /// <summary>
    /// Pins that <see cref="FlourishVisuals.LeafFor"/> returns
    /// <c>null</c> when the Alive-state (phase, health) switch
    /// encounters a combination it doesn't enumerate. Same
    /// rationale as the Dead-status defensive test: unknown enum
    /// values must fall back to null rather than throw.
    /// </summary>
    [TestMethod]
    public void LeafFor_AliveStatusWithUnknownPhase_ReturnsNull() {
      // Arrange
      var unknownPhase = (FlourishPhase)999;

      // Act
      var leaf = FlourishVisuals.LeafFor(
          unknownPhase, FlourishLifeStatus.Alive, FlourishHealth.Healthy);

      // Assert
      Assert.IsNull(leaf,
          "Alive status with unknown phase must return null (defensive "
          + "fallback), not throw.");
    }

    /// <summary>
    /// Pins that <see cref="FlourishVisuals.LeafFor"/> returns
    /// <c>null</c> when the Alive-state switch encounters an unknown
    /// health value (e.g. a future <see cref="FlourishHealth"/> enum
    /// addition that hasn't been mapped). Pins the second defensive
    /// arm of the <c>(phase, health)</c> switch.
    /// </summary>
    [TestMethod]
    public void LeafFor_AliveStatusWithUnknownHealth_ReturnsNull() {
      // Arrange
      var unknownHealth = (FlourishHealth)999;

      // Act
      var leaf = FlourishVisuals.LeafFor(
          FlourishPhase.Mature, FlourishLifeStatus.Alive, unknownHealth);

      // Assert
      Assert.IsNull(leaf);
    }

    #endregion

    #region ShouldDieFromBadwater — early-outs

    [TestMethod]
    public void ShouldDieFromBadwater_AlreadyDead_NeverRecomputes() {
      // Once Dead, the predicate is a no-op — no point re-evaluating.
      Assert.IsFalse(FlourishVisuals.ShouldDieFromBadwater(
          FlourishLifeStatus.Dead, waterDepth: 5f, waterContamination: 1f, threshold: 0.1f));
    }

    [TestMethod]
    public void ShouldDieFromBadwater_NoWater_DoesNotKill() {
      // The kill mechanic requires standing in water. A flourish on
      // dry land is unaffected by contamination (soil contamination
      // is a separate kill path via AttritionHandler).
      Assert.IsFalse(FlourishVisuals.ShouldDieFromBadwater(
          FlourishLifeStatus.Alive, waterDepth: 0f, waterContamination: 1f, threshold: 0.1f));
    }

    [TestMethod]
    public void ShouldDieFromBadwater_NegativeWaterDepth_DoesNotKill() {
      // Defensive against impossible inputs from a misbehaving water
      // query; treat ≤ 0 the same as 0.
      Assert.IsFalse(FlourishVisuals.ShouldDieFromBadwater(
          FlourishLifeStatus.Alive, waterDepth: -0.5f, waterContamination: 1f, threshold: 0.1f));
    }

    #endregion

    #region ShouldDieFromBadwater — contamination threshold

    [TestMethod]
    public void ShouldDieFromBadwater_ContaminationBelowThreshold_DoesNotKill() {
      Assert.IsFalse(FlourishVisuals.ShouldDieFromBadwater(
          FlourishLifeStatus.Alive, waterDepth: 1f, waterContamination: 0.05f, threshold: 0.1f));
    }

    [TestMethod]
    public void ShouldDieFromBadwater_ContaminationExactlyAtThreshold_DoesNotKill() {
      // Comparison is strict (>), so exactly-at-threshold survives.
      // This is the inverse of WaterContamination.IsBadwater's
      // inclusive comparison — that's intentional: the chunk-aggregate
      // counts trace as badwater, the per-tile kill requires a clear
      // exceedance.
      Assert.IsFalse(FlourishVisuals.ShouldDieFromBadwater(
          FlourishLifeStatus.Alive, waterDepth: 1f, waterContamination: 0.1f, threshold: 0.1f));
    }

    [TestMethod]
    public void ShouldDieFromBadwater_ContaminationJustAboveThreshold_Kills() {
      Assert.IsTrue(FlourishVisuals.ShouldDieFromBadwater(
          FlourishLifeStatus.Alive, waterDepth: 1f, waterContamination: 0.11f, threshold: 0.1f));
    }

    [TestMethod]
    public void ShouldDieFromBadwater_FullySaturatedContamination_Kills() {
      Assert.IsTrue(FlourishVisuals.ShouldDieFromBadwater(
          FlourishLifeStatus.Alive, waterDepth: 1f, waterContamination: 1f, threshold: 0.1f));
    }

    #endregion

    #region ShouldDieFromBadwater — cross-system boundary matrix vs WaterContamination

    /// <summary>
    /// Pins the <i>cross-system</i> divergence between
    /// <see cref="FlourishVisuals.ShouldDieFromBadwater"/> and
    /// <see cref="WaterContamination.IsBadwater"/>: the two systems
    /// use <b>different thresholds</b> (<c>0.1</c> vs <c>0.05</c>)
    /// <b>and different operators</b> (strict <c>&gt;</c> vs inclusive
    /// <c>&gt;=</c>). Both choices are deliberate -- see
    /// <see cref="FlourishVisuals.BadwaterContaminationThreshold"/>'s
    /// docstring and <see cref="WaterContamination"/>'s class docstring
    /// for the rationale (per-tile self-kill is stricter than the
    /// chunk-aggregate cutoff). The per-axis boundary tests pin each
    /// system in isolation; this test crosses them simultaneously
    /// across the full <c>(waterDepth, waterContamination)</c> plane
    /// so a future "let's make these consistent" refactor that picks
    /// either threshold or either operator will fail loudly here and
    /// surface both deliberate values for the refactor's author to
    /// reckon with before unifying them.
    ///
    /// <para>The matrix below tabulates expected
    /// <c>ShouldDieFromBadwater</c> and <c>IsBadwater</c> outcomes
    /// at the four cardinal contamination values: 0, exactly
    /// <c>WaterContamination.Threshold</c> (0.05), strictly between
    /// the two thresholds (0.075), exactly
    /// <c>BadwaterContaminationThreshold</c> (0.1), and above (0.2).
    /// Water depth is varied across "dry land" (0), "barely wet"
    /// (0.01), and "deep" (5).</para>
    /// </summary>
    [TestMethod]
    public void BadwaterBoundaryMatrix_PinsBothThresholdsAndOperatorsAcrossSystems() {
      // Arrange — sanity-check the two constants we depend on so a
      // change to either surfaces here first.
      Assert.AreEqual(0.1f, FlourishVisuals.BadwaterContaminationThreshold,
          "FlourishVisuals threshold must remain 0.1 (per-tile self-kill).");
      Assert.AreEqual(0.05f, WaterContamination.Threshold,
          "WaterContamination threshold must remain 0.05 (chunk aggregate).");

      // Each row: (waterDepth, contamination, expectedShouldDie, expectedIsBadwater).
      // For ShouldDie, depth must be > 0 to even engage; IsBadwater
      // ignores depth entirely (it's a per-surface predicate).
      var rows = new (float depth, float contam, bool expectDie, bool expectBadwater, string label)[] {
          // Dry land row: ShouldDie is false everywhere (depth guard);
          // IsBadwater still depends on contamination only.
          (0f,    0f,    false, false, "dry, contam=0"),
          (0f,    0.05f, false, true,  "dry, contam=0.05 (IsBadwater boundary, inclusive)"),
          (0f,    0.10f, false, true,  "dry, contam=0.10 (Flourish boundary; dry skips kill)"),
          (0f,    0.20f, false, true,  "dry, contam=0.20 (above both thresholds; depth=0)"),

          // Barely-wet row: depth>0 engages the predicate.
          (0.01f, 0f,    false, false, "wet, contam=0 (clean water leaves alive)"),
          // ContaminationExactlyAt WaterContamination.Threshold (0.05):
          //   - IsBadwater: TRUE (inclusive >=)
          //   - ShouldDie: FALSE (below FlourishVisuals threshold of 0.1)
          //   This row pins the inter-system gap deliberately: a tile
          //   in clearly-badwater water by the chunk-aggregate rule is
          //   still alive per-flourish.
          (0.01f, 0.05f, false, true,  "wet, contam=0.05 (badwater per chunk; alive per flourish)"),
          // Strictly between the two thresholds:
          (0.01f, 0.075f, false, true, "wet, contam=0.075 (between thresholds: badwater but alive)"),
          // ContaminationExactlyAt FlourishVisuals.BadwaterContaminationThreshold (0.1):
          //   - IsBadwater: TRUE
          //   - ShouldDie: FALSE -- strict > on the flourish side means
          //     exactly-at-threshold survives. Pins the operator divergence:
          //     IsBadwater uses >=, ShouldDie uses >.
          (0.01f, 0.10f, false, true,  "wet, contam=0.10 (flourish boundary, strict >; survives)"),
          // Just above both thresholds:
          (0.01f, 0.11f, true,  true,  "wet, contam=0.11 (above both; dies, badwater)"),
          (0.01f, 0.20f, true,  true,  "wet, contam=0.20 (clearly toxic)"),

          // Deep water -- depth value shouldn't change either outcome.
          (5f,    0.05f, false, true,  "deep, contam=0.05 (depth-independent: badwater, alive)"),
          (5f,    0.10f, false, true,  "deep, contam=0.10 (strict >: survives)"),
          (5f,    0.11f, true,  true,  "deep, contam=0.11 (dies)"),
      };

      foreach (var row in rows) {
        // Act
        var actualDie = FlourishVisuals.ShouldDieFromBadwater(
            FlourishLifeStatus.Alive,
            waterDepth: row.depth,
            waterContamination: row.contam,
            threshold: FlourishVisuals.BadwaterContaminationThreshold);
        var actualBadwater = WaterContamination.IsBadwater(row.contam);

        // Assert
        Assert.AreEqual(row.expectDie, actualDie,
            $"ShouldDieFromBadwater divergence at row [{row.label}]: "
            + $"depth={row.depth}, contam={row.contam}.");
        Assert.AreEqual(row.expectBadwater, actualBadwater,
            $"IsBadwater divergence at row [{row.label}]: "
            + $"contam={row.contam}.");
      }
    }

    /// <summary>
    /// Pins the <i>operator</i> halves of the divergence in isolation:
    /// at each system's own threshold value, the comparison direction
    /// is the load-bearing detail. <see cref="WaterContamination.IsBadwater"/>
    /// is inclusive (<c>&gt;=</c>) so the exact threshold counts as
    /// badwater; <see cref="FlourishVisuals.ShouldDieFromBadwater"/> is
    /// strict (<c>&gt;</c>) so the exact threshold leaves the flourish
    /// alive. A refactor that swaps either operator while keeping the
    /// constant the same would slip past the value-only pins -- this
    /// test catches such a swap.
    /// </summary>
    [TestMethod]
    public void Operators_AtEachThresholdExactly_DivergeByDesign() {
      // Arrange — exact-threshold inputs.
      const float waterTh = WaterContamination.Threshold;          // 0.05
      const float flourishTh = FlourishVisuals.BadwaterContaminationThreshold; // 0.1

      // Act / Assert — IsBadwater is inclusive: exactly-at-threshold
      // counts as badwater. A strict-> refactor would flip this to false.
      Assert.IsTrue(WaterContamination.IsBadwater(waterTh),
          "IsBadwater(0.05) must be true (inclusive >=). A strict > "
          + "refactor would silently flip this to false.");

      // ShouldDieFromBadwater is strict: exactly-at-threshold survives.
      // An inclusive >= refactor would flip this to true.
      Assert.IsFalse(FlourishVisuals.ShouldDieFromBadwater(
              FlourishLifeStatus.Alive,
              waterDepth: 1f,
              waterContamination: flourishTh,
              threshold: flourishTh),
          "ShouldDieFromBadwater at exactly the threshold must be false "
          + "(strict >). An inclusive >= refactor would silently flip "
          + "this to true and start killing flourishes at the boundary.");
    }

    #endregion

  }

}
