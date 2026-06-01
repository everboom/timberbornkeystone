using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Pins the per-recipe / per-entity decision predicates used by
  /// the Mod-side <c>AttritionHandler</c>'s eval loop:
  /// <list type="bullet">
  ///   <item><see cref="AttritionRecipe.EffectiveProbability"/> —
  ///         the channel-scaled probability ramp.</item>
  ///   <item><see cref="AttritionTargeting.MatchesTarget"/> —
  ///         the two-track class/vanilla targeting.</item>
  /// </list>
  /// Both are pure functions; the Mod-side wires them through real
  /// ecology field samples and real <c>BlockObject</c> walks.
  /// </summary>
  [TestClass]
  public class AttritionDecisionTests {

    #region Helpers

    private static AttritionRecipe MakeRecipe(
        AttritionAction action = AttritionAction.Kill,
        IReadOnlyList<string>? targetClasses = null,
        IReadOnlyList<string>? vanillaSpecies = null,
        float probability = 0.5f,
        string filter = "",
        EcologyChannel? scaleBy = null,
        float scaleMin = 0f,
        float scaleMax = 0f,
        float probabilityAtMin = 0f) {
      return new AttritionRecipe(
          Biome: BiomeKind.Grassland,
          LevelId: "L1",
          Action: action,
          TargetClasses: targetClasses ?? Array.Empty<string>(),
          Probability: probability,
          Filter: filter,
          ScaleBy: scaleBy,
          ScaleMin: scaleMin,
          ScaleMax: scaleMax,
          ProbabilityAtMin: probabilityAtMin,
          ExcludeHabitats: Array.Empty<string>(),
          IncludeHabitats: Array.Empty<string>(),
          VanillaSpecies: vanillaSpecies ?? Array.Empty<string>());
    }

    #endregion

    #region EffectiveProbability — no ScaleBy

    [TestMethod]
    public void EffectiveProbability_NoScaleBy_ReturnsProbabilityIgnoringSample() {
      // Sample value is irrelevant when ScaleBy is null.
      var recipe = MakeRecipe(probability: 0.33f);
      Assert.AreEqual(0.33f, recipe.EffectiveProbability(channelSample: 0f), 1e-6f);
      Assert.AreEqual(0.33f, recipe.EffectiveProbability(channelSample: 0.5f), 1e-6f);
      Assert.AreEqual(0.33f, recipe.EffectiveProbability(channelSample: 999f), 1e-6f);
    }

    #endregion

    #region EffectiveProbability — ScaleBy ramp

    [TestMethod]
    public void EffectiveProbability_BelowScaleMin_ReturnsZero() {
      // Rule never fires below the floor — the recipe's intent is
      // "this only triggers when the channel reads ≥ ScaleMin."
      var recipe = MakeRecipe(
          probability: 1f, scaleBy: EcologyChannel.WaterFlowMagnitude,
          scaleMin: 0.3f, scaleMax: 1f, probabilityAtMin: 0.25f);
      Assert.AreEqual(0f, recipe.EffectiveProbability(channelSample: 0.2f));
      Assert.AreEqual(0f, recipe.EffectiveProbability(channelSample: 0f));
    }

    [TestMethod]
    public void EffectiveProbability_AtOrAboveScaleMax_ReturnsProbability() {
      var recipe = MakeRecipe(
          probability: 1f, scaleBy: EcologyChannel.WaterFlowMagnitude,
          scaleMin: 0.3f, scaleMax: 1f, probabilityAtMin: 0.25f);
      Assert.AreEqual(1f, recipe.EffectiveProbability(channelSample: 1f));
      Assert.AreEqual(1f, recipe.EffectiveProbability(channelSample: 5f));
    }

    [TestMethod]
    public void EffectiveProbability_AtScaleMin_ReturnsProbabilityAtMin() {
      // Boundary: exactly at ScaleMin → ProbabilityAtMin (the ramp's
      // low end). NOT 0 — the floor check is strict `<`, not `≤`.
      var recipe = MakeRecipe(
          probability: 1f, scaleBy: EcologyChannel.WaterFlowMagnitude,
          scaleMin: 0.3f, scaleMax: 1f, probabilityAtMin: 0.25f);
      Assert.AreEqual(0.25f, recipe.EffectiveProbability(channelSample: 0.3f), 1e-6f);
    }

    [TestMethod]
    public void EffectiveProbability_MidRange_LinearlyInterpolates() {
      // Halfway between ScaleMin (0.3) and ScaleMax (1.0) is sample 0.65.
      // Halfway between ProbabilityAtMin (0.25) and Probability (1.0) is 0.625.
      var recipe = MakeRecipe(
          probability: 1f, scaleBy: EcologyChannel.WaterFlowMagnitude,
          scaleMin: 0.3f, scaleMax: 1f, probabilityAtMin: 0.25f);
      Assert.AreEqual(0.625f, recipe.EffectiveProbability(channelSample: 0.65f), 1e-5f);
    }

    [TestMethod]
    public void EffectiveProbability_RampWithZeroProbabilityAtMin_StartsAtZero() {
      // The common case: scale from 0 at the floor to Probability at
      // the ceiling.
      var recipe = MakeRecipe(
          probability: 0.5f, scaleBy: EcologyChannel.WaterFlowMagnitude,
          scaleMin: 0f, scaleMax: 1f, probabilityAtMin: 0f);
      Assert.AreEqual(0f, recipe.EffectiveProbability(channelSample: 0f));
      Assert.AreEqual(0.25f, recipe.EffectiveProbability(channelSample: 0.5f), 1e-5f);
      Assert.AreEqual(0.5f, recipe.EffectiveProbability(channelSample: 1f));
    }

    #endregion

    #region MatchesTarget — Class A/B/C

    [TestMethod]
    public void MatchesTarget_ClassMatchesRecipeTargetClasses_True() {
      var recipe = MakeRecipe(targetClasses: new[] { "B", "C" });
      Assert.IsTrue(AttritionTargeting.MatchesTarget(recipe, classId: "B", vanillaBlueprintName: ""));
      Assert.IsTrue(AttritionTargeting.MatchesTarget(recipe, classId: "C", vanillaBlueprintName: ""));
    }

    [TestMethod]
    public void MatchesTarget_ClassNotInRecipeTargetClasses_False() {
      var recipe = MakeRecipe(targetClasses: new[] { "B" });
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "A", vanillaBlueprintName: ""));
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "C", vanillaBlueprintName: ""));
    }

    [TestMethod]
    public void MatchesTarget_EmptyClassId_NotMatching() {
      var recipe = MakeRecipe(targetClasses: new[] { "B" });
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "", vanillaBlueprintName: ""));
    }

    #endregion

    #region MatchesTarget — Class D / VanillaSpecies

    [TestMethod]
    public void MatchesTarget_ClassDWithMatchingVanillaName_True() {
      // Class D entities don't have a stamp; matched by blueprint name.
      var recipe = MakeRecipe(vanillaSpecies: new[] { "Cattail", "Spadderdock" });
      Assert.IsTrue(AttritionTargeting.MatchesTarget(recipe, classId: "D", vanillaBlueprintName: "Cattail"));
      Assert.IsTrue(AttritionTargeting.MatchesTarget(recipe, classId: "D", vanillaBlueprintName: "Spadderdock"));
    }

    [TestMethod]
    public void MatchesTarget_ClassDWithUnmatchedVanillaName_False() {
      var recipe = MakeRecipe(vanillaSpecies: new[] { "Cattail" });
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "D", vanillaBlueprintName: "Birch"));
    }

    [TestMethod]
    public void MatchesTarget_ClassDWithEmptyVanillaName_False() {
      // Defensive: a "D" classId without a blueprint name (shouldn't
      // happen in production, but the dispatcher should reject it).
      var recipe = MakeRecipe(vanillaSpecies: new[] { "Cattail" });
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "D", vanillaBlueprintName: ""));
    }

    #endregion

    #region MatchesTarget — independence of the two tracks

    [TestMethod]
    public void MatchesTarget_RecipeWithOnlyTargetClasses_DoesNotMatchClassD() {
      // Recipe has TargetClasses=["B"] only; a Class-D entity must not
      // match via the class-string track (D isn't in [B]) and the
      // vanilla track is empty.
      var recipe = MakeRecipe(targetClasses: new[] { "B" });
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "D", vanillaBlueprintName: "Cattail"));
    }

    [TestMethod]
    public void MatchesTarget_RecipeWithOnlyVanillaSpecies_DoesNotMatchClassBCByClassString() {
      // Inverse: recipe has VanillaSpecies=["Cattail"] only; a Class-B
      // entity (whatever its blueprint) must not match.
      var recipe = MakeRecipe(vanillaSpecies: new[] { "Cattail" });
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "B", vanillaBlueprintName: "Cattail"));
    }

    [TestMethod]
    public void MatchesTarget_RecipeWithBothLists_DoesNotConfuseTracks() {
      // A "D" classId only consults VanillaSpecies; a "B" classId only
      // consults TargetClasses. A vanilla blueprint name happening to
      // appear in TargetClasses (which shouldn't happen in practice)
      // doesn't cross over.
      var recipe = MakeRecipe(
          targetClasses: new[] { "B" },
          vanillaSpecies: new[] { "Cattail" });
      Assert.IsTrue(AttritionTargeting.MatchesTarget(recipe, classId: "B", vanillaBlueprintName: ""));
      Assert.IsTrue(AttritionTargeting.MatchesTarget(recipe, classId: "D", vanillaBlueprintName: "Cattail"));
      // Cross-track noise:
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "D", vanillaBlueprintName: "B"));
      Assert.IsFalse(AttritionTargeting.MatchesTarget(recipe, classId: "Cattail", vanillaBlueprintName: ""));
    }

    #endregion

  }

}
