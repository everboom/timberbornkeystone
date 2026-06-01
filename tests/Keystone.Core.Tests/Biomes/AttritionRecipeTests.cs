using System;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Tests for <see cref="AttritionRecipe.EffectiveProbability"/> —
  /// the per-tile probability-scaling math that drives flow-scaled
  /// (and similar) attrition rules. Pure function; tested
  /// exhaustively at the boundaries because off-by-ones in
  /// interpolation are exactly the bug class unit tests catch
  /// cheapest.
  /// </summary>
  [TestClass]
  public class AttritionRecipeTests {

    #region Fixture helpers

    /// <summary>Build a recipe with channel scaling. Other fields
    /// (Biome, LevelId, Action, TargetClasses, Filter) are set to
    /// stable defaults — the scaling math doesn't depend on them.</summary>
    private static AttritionRecipe ScaledRecipe(
        EcologyChannel channel, float scaleMin, float scaleMax,
        float probAtMin, float probAtMax) {
      return new AttritionRecipe(
          Biome: BiomeKind.River,
          LevelId: "L1",
          Action: AttritionAction.Destroy,
          TargetClasses: Array.Empty<string>(),
          Probability: probAtMax,
          Filter: "",
          ScaleBy: channel,
          ScaleMin: scaleMin,
          ScaleMax: scaleMax,
          ProbabilityAtMin: probAtMin,
          ExcludeHabitats: Array.Empty<string>(),
          IncludeHabitats: Array.Empty<string>(),
          VanillaSpecies: Array.Empty<string>());
    }

    /// <summary>Build a recipe with no channel scaling — constant
    /// probability. The sample-value parameter to
    /// <see cref="AttritionRecipe.EffectiveProbability"/> is ignored
    /// for these.</summary>
    private static AttritionRecipe ConstantRecipe(float probability) {
      return new AttritionRecipe(
          Biome: BiomeKind.Dry,
          LevelId: "L1",
          Action: AttritionAction.Kill,
          TargetClasses: Array.Empty<string>(),
          Probability: probability,
          Filter: "",
          ScaleBy: null,
          ScaleMin: 0f,
          ScaleMax: 0f,
          ProbabilityAtMin: 0f,
          ExcludeHabitats: Array.Empty<string>(),
          IncludeHabitats: Array.Empty<string>(),
          VanillaSpecies: Array.Empty<string>());
    }

    private const float Tolerance = 1e-5f;

    #endregion

    #region Constant-probability recipes (no scaling)

    /// <summary>Recipes without <c>ScaleBy</c> return
    /// <c>Probability</c> regardless of sample. Verified at three
    /// arbitrary sample values to confirm the sample is unused.</summary>
    [TestMethod]
    public void EffectiveProbability_NoScaling_ReturnsProbabilityRegardlessOfSample() {
      // Arrange
      var recipe = ConstantRecipe(0.5f);

      // Act + Assert
      Assert.AreEqual(0.5f, recipe.EffectiveProbability(-100f), Tolerance);
      Assert.AreEqual(0.5f, recipe.EffectiveProbability(0f), Tolerance);
      Assert.AreEqual(0.5f, recipe.EffectiveProbability(1000f), Tolerance);
    }

    /// <summary>Zero-probability constant recipes return zero.
    /// (Degenerate but valid — could be authored as a "disabled
    /// placeholder" rule.)</summary>
    [TestMethod]
    public void EffectiveProbability_NoScalingZeroProbability_ReturnsZero() {
      // Arrange
      var recipe = ConstantRecipe(0f);

      // Act
      var result = recipe.EffectiveProbability(0.7f);

      // Assert
      Assert.AreEqual(0f, result, Tolerance);
    }

    #endregion

    #region Scaled recipes — boundary cases

    /// <summary>Samples strictly below <c>ScaleMin</c> return 0 — the
    /// rule never fires. (Distinct from "at exactly <c>ScaleMin</c>,"
    /// which returns <c>ProbabilityAtMin</c>.)</summary>
    [TestMethod]
    public void EffectiveProbability_BelowScaleMin_ReturnsZero() {
      // Arrange: river-style curve, ScaleMin=0.5, ScaleMax=1.0.
      var recipe = ScaledRecipe(EcologyChannel.WaterFlowMagnitude, 0.5f, 1.0f, 0.25f, 1.0f);

      // Act + Assert
      Assert.AreEqual(0f, recipe.EffectiveProbability(0f), Tolerance);
      Assert.AreEqual(0f, recipe.EffectiveProbability(0.49f), Tolerance);
    }

    /// <summary>Sample exactly at <c>ScaleMin</c> returns
    /// <c>ProbabilityAtMin</c>. This is the discontinuity at the
    /// threshold — strictly below = 0, at = ProbabilityAtMin.</summary>
    [TestMethod]
    public void EffectiveProbability_AtScaleMin_ReturnsProbabilityAtMin() {
      // Arrange
      var recipe = ScaledRecipe(EcologyChannel.WaterFlowMagnitude, 0.5f, 1.0f, 0.25f, 1.0f);

      // Act
      var result = recipe.EffectiveProbability(0.5f);

      // Assert
      Assert.AreEqual(0.25f, result, Tolerance);
    }

    /// <summary>Sample at the midpoint of the scaling range returns
    /// the midpoint of the probability range. Catches off-by-ones in
    /// the linear-interp formula.</summary>
    [TestMethod]
    public void EffectiveProbability_AtScalingMidpoint_ReturnsProbabilityMidpoint() {
      // Arrange: river curve, ScaleMin=0.5, ScaleMax=1.0, probs 0.25-1.0.
      var recipe = ScaledRecipe(EcologyChannel.WaterFlowMagnitude, 0.5f, 1.0f, 0.25f, 1.0f);

      // Act: sample at 0.75 (midway between 0.5 and 1.0).
      var result = recipe.EffectiveProbability(0.75f);

      // Assert: midpoint of [0.25, 1.0] = 0.625.
      Assert.AreEqual(0.625f, result, Tolerance);
    }

    /// <summary>Sample exactly at <c>ScaleMax</c> returns
    /// <c>Probability</c> — the high-end clamp boundary.</summary>
    [TestMethod]
    public void EffectiveProbability_AtScaleMax_ReturnsProbability() {
      // Arrange
      var recipe = ScaledRecipe(EcologyChannel.WaterFlowMagnitude, 0.5f, 1.0f, 0.25f, 1.0f);

      // Act
      var result = recipe.EffectiveProbability(1.0f);

      // Assert
      Assert.AreEqual(1.0f, result, Tolerance);
    }

    /// <summary>Samples above <c>ScaleMax</c> clamp to
    /// <c>Probability</c>.</summary>
    [TestMethod]
    public void EffectiveProbability_AboveScaleMax_ClampsToProbability() {
      // Arrange
      var recipe = ScaledRecipe(EcologyChannel.WaterFlowMagnitude, 0.5f, 1.0f, 0.25f, 1.0f);

      // Act + Assert
      Assert.AreEqual(1.0f, recipe.EffectiveProbability(1.01f), Tolerance);
      Assert.AreEqual(1.0f, recipe.EffectiveProbability(5f), Tolerance);
    }

    #endregion

    #region Inverse curve (ProbabilityAtMin > Probability)

    /// <summary>Decreasing curves (probability at min is higher than
    /// probability at max) are mathematically valid. Confirms the
    /// interp formula handles the inverse direction symmetrically.
    /// (Use case: a rule that's more aggressive at low moisture and
    /// less so at high moisture, e.g. "dry crops are most stressed at
    /// 30% moisture, less so above and below.")</summary>
    [TestMethod]
    public void EffectiveProbability_DecreasingCurve_InterpolatesCorrectly() {
      // Arrange: probability starts at 1.0 at sample=0.2 and drops to
      // 0.2 at sample=0.8. Below 0.2 → 0. Above 0.8 → 0.2.
      var recipe = ScaledRecipe(EcologyChannel.Moisture, 0.2f, 0.8f, 1.0f, 0.2f);

      // Act + Assert
      Assert.AreEqual(0f,    recipe.EffectiveProbability(0.1f),  Tolerance);
      Assert.AreEqual(1.0f,  recipe.EffectiveProbability(0.2f),  Tolerance);
      Assert.AreEqual(0.6f,  recipe.EffectiveProbability(0.5f),  Tolerance);  // midpoint
      Assert.AreEqual(0.2f,  recipe.EffectiveProbability(0.8f),  Tolerance);
      Assert.AreEqual(0.2f,  recipe.EffectiveProbability(0.95f), Tolerance);
    }

    #endregion

    #region River-rule fixture (the shipped attrition we're guarding)

    /// <summary>The exact river-attrition curve shipped in
    /// <c>KeystoneRiverRecipes.blueprint.json</c>: <c>ScaleMin=0.5</c>,
    /// <c>ScaleMax=1.0</c>, <c>ProbabilityAtMin=0.25</c>,
    /// <c>Probability=1.0</c>. If anyone changes the formula in a way
    /// that breaks river behaviour, this test catches it.</summary>
    [TestMethod]
    public void EffectiveProbability_RiverFlowCurve_MatchesAuthoringIntent() {
      // Arrange: the literal numbers from KeystoneRiverRecipes.
      var recipe = ScaledRecipe(EcologyChannel.WaterFlowMagnitude, 0.5f, 1.0f, 0.25f, 1.0f);

      // Act + Assert: user-stated points from the design discussion.
      Assert.AreEqual(0f,     recipe.EffectiveProbability(0.4f),  Tolerance);  // below threshold
      Assert.AreEqual(0.25f,  recipe.EffectiveProbability(0.5f),  Tolerance);  // user said "25% at flow 0.5"
      Assert.AreEqual(0.625f, recipe.EffectiveProbability(0.75f), Tolerance);  // midpoint
      Assert.AreEqual(1.0f,   recipe.EffectiveProbability(1.0f),  Tolerance);  // user said "100% at flow 1.0"
      Assert.AreEqual(1.0f,   recipe.EffectiveProbability(1.5f),  Tolerance);  // clamped above
    }

    #endregion

  }

}
