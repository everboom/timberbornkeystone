using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Pins every parse / validation branch of
  /// <see cref="AttritionRecipeParser.TryParse"/>:
  /// <list type="bullet">
  ///   <item>Biome / Level / Action vocabulary checks.</item>
  ///   <item>Empty Classes AND empty VanillaSpecies = reject.</item>
  ///   <item>Classes filtered to <c>{A, B, C}</c>; unknowns dropped with
  ///         a warning, duplicates collapsed.</item>
  ///   <item>Probability clamps to <c>[0, 1]</c>.</item>
  ///   <item>ScaleBy requires a valid EcologyChannel and
  ///         ScaleMax &gt; ScaleMin; ProbabilityAtMin clamps.</item>
  ///   <item>No ScaleBy → fields stay null/0 even if min/max set.</item>
  ///   <item>Habitat lists filtered against
  ///         <see cref="AttritionRecipeParser.KnownHabitats"/>;
  ///         unknowns dropped, duplicates collapsed.</item>
  ///   <item>Filter and VanillaSpecies pass-through.</item>
  /// </list>
  /// </summary>
  [TestClass]
  public class AttritionRecipeParserTests {

    #region Helpers

    private static AttritionEntryInput Valid(
        string biome = "Grassland",
        string level = "L1",
        string action = "Kill",
        IReadOnlyList<string>? classes = null,
        IReadOnlyList<string>? vanillaSpecies = null,
        float probability = 0.5f,
        string filter = "",
        string scaleBy = "",
        float scaleMin = 0f,
        float scaleMax = 0f,
        float probabilityAtMin = 0f,
        IReadOnlyList<string>? excludeHabitats = null,
        IReadOnlyList<string>? includeHabitats = null) {
      return new AttritionEntryInput(
          Biome: biome,
          Level: level,
          Action: action,
          Classes: classes ?? new[] { "B" },
          VanillaSpecies: vanillaSpecies ?? System.Array.Empty<string>(),
          Probability: probability,
          Filter: filter,
          ScaleBy: scaleBy,
          ScaleMin: scaleMin,
          ScaleMax: scaleMax,
          ProbabilityAtMin: probabilityAtMin,
          ExcludeHabitats: excludeHabitats ?? System.Array.Empty<string>(),
          IncludeHabitats: includeHabitats ?? System.Array.Empty<string>());
    }

    private static (AttritionRecipe? Recipe, List<string> Warnings, bool Result)
        Parse(AttritionEntryInput input) {
      var warnings = new List<string>();
      var ok = AttritionRecipeParser.TryParse(input, "test-book", warnings.Add, out var recipe);
      return (ok ? recipe : null, warnings, ok);
    }

    #endregion

    #region Required field validation

    [TestMethod]
    public void TryParse_EmptyLevel_Rejects() {
      var (_, warnings, ok) = Parse(Valid(level: ""));
      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "empty Level");
    }

    [TestMethod]
    public void TryParse_UnknownBiome_Rejects() {
      var (_, warnings, ok) = Parse(Valid(biome: "NotABiome"));
      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "Biome='NotABiome'");
    }

    [TestMethod]
    public void TryParse_BiomeMatchIsCaseInsensitive() {
      var (recipe, _, ok) = Parse(Valid(biome: "grassland"));
      Assert.IsTrue(ok);
      Assert.AreEqual(BiomeKind.Grassland, recipe!.Biome);
    }

    [TestMethod]
    public void TryParse_UnknownAction_Rejects() {
      var (_, warnings, ok) = Parse(Valid(action: "Maim"));
      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "Action='Maim'");
    }

    [TestMethod]
    public void TryParse_KillAction_Preserved() {
      var (recipe, _, ok) = Parse(Valid(action: "Kill"));
      Assert.IsTrue(ok);
      Assert.AreEqual(AttritionAction.Kill, recipe!.Action);
    }

    [TestMethod]
    public void TryParse_DestroyAction_Preserved() {
      var (recipe, _, ok) = Parse(Valid(action: "Destroy"));
      Assert.IsTrue(ok);
      Assert.AreEqual(AttritionAction.Destroy, recipe!.Action);
    }

    [TestMethod]
    public void TryParse_ActionParseIsCaseSensitive() {
      // The parser only matches "Kill" and "Destroy" exactly. Case-
      // sensitivity is by design (per TryParseAction docstring) so a
      // typo doesn't silently parse.
      var (_, warnings, ok) = Parse(Valid(action: "kill"));
      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "Action='kill'");
    }

    #endregion

    #region Empty target set

    [TestMethod]
    public void TryParse_EmptyClassesAndVanilla_Rejects() {
      var input = Valid(classes: System.Array.Empty<string>(), vanillaSpecies: System.Array.Empty<string>());
      var (_, warnings, ok) = Parse(input);
      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "empty Classes AND empty");
    }

    [TestMethod]
    public void TryParse_AllClassesUnknownAndVanillaEmpty_Rejects() {
      // The .Classes are present in the input but all unknown — after
      // normalisation the recipe has nothing to target, so reject.
      var input = Valid(classes: new[] { "X", "Y" }, vanillaSpecies: System.Array.Empty<string>());
      var (_, warnings, ok) = Parse(input);
      Assert.IsFalse(ok);
      // Two warnings: one per unknown class, plus the final "no recognised
      // Classes" rejection. Verify the rejection message is present.
      var hasReject = warnings.Exists(w => w.Contains("no recognised Classes"));
      Assert.IsTrue(hasReject);
    }

    [TestMethod]
    public void TryParse_AllClassesUnknownButVanillaSpeciesPresent_Accepts() {
      // VanillaSpecies fallback: even with no recognised Classes, the
      // recipe still has targets via the vanilla blueprint-name list.
      var input = Valid(
          classes: new[] { "X" },
          vanillaSpecies: new[] { "Cattail" });
      var (recipe, _, ok) = Parse(input);
      Assert.IsTrue(ok);
      Assert.AreEqual(0, recipe!.TargetClasses.Count);
      Assert.AreEqual(1, recipe.VanillaSpecies.Count);
      Assert.AreEqual("Cattail", recipe.VanillaSpecies[0]);
    }

    #endregion

    #region Classes filter

    [TestMethod]
    public void TryParse_ClassesFiltersUnknownsAndWarnsPerUnknown() {
      var input = Valid(classes: new[] { "A", "X", "B", "Z" });
      var (recipe, warnings, ok) = Parse(input);
      Assert.IsTrue(ok);
      CollectionAssert.AreEqual(new[] { "A", "B" }, (System.Collections.ICollection)recipe!.TargetClasses);
      var unknownWarnings = warnings.FindAll(w => w.Contains("Class='X'") || w.Contains("Class='Z'"));
      Assert.AreEqual(2, unknownWarnings.Count);
    }

    [TestMethod]
    public void TryParse_DuplicateKnownClasses_CollapsedToUnique() {
      var input = Valid(classes: new[] { "B", "B", "C", "B" });
      var (recipe, _, ok) = Parse(input);
      Assert.IsTrue(ok);
      CollectionAssert.AreEqual(new[] { "B", "C" }, (System.Collections.ICollection)recipe!.TargetClasses);
    }

    [TestMethod]
    public void TryParse_AllThreeKnownClassesAccepted() {
      var input = Valid(classes: new[] { "A", "B", "C" });
      var (recipe, _, ok) = Parse(input);
      Assert.IsTrue(ok);
      Assert.AreEqual(3, recipe!.TargetClasses.Count);
    }

    #endregion

    #region Probability clamps

    [TestMethod]
    public void TryParse_ProbabilityBelowZero_ClampedToZeroWithWarning() {
      var input = Valid(probability: -0.5f);
      var (recipe, warnings, _) = Parse(input);
      Assert.AreEqual(0f, recipe!.Probability);
      var hasWarn = warnings.Exists(w => w.Contains("Probability=-0.5") && w.Contains("Clamped to 0"));
      Assert.IsTrue(hasWarn);
    }

    [TestMethod]
    public void TryParse_ProbabilityAboveOne_ClampedToOneWithWarning() {
      var input = Valid(probability: 1.5f);
      var (recipe, warnings, _) = Parse(input);
      Assert.AreEqual(1f, recipe!.Probability);
      var hasWarn = warnings.Exists(w => w.Contains("Probability=1.5") && w.Contains("Clamped to 1"));
      Assert.IsTrue(hasWarn);
    }

    [TestMethod]
    public void TryParse_ProbabilityAtBoundaries_NotClamped() {
      var lower = Parse(Valid(probability: 0f));
      Assert.AreEqual(0f, lower.Recipe!.Probability);
      Assert.AreEqual(0, lower.Warnings.Count);

      var upper = Parse(Valid(probability: 1f));
      Assert.AreEqual(1f, upper.Recipe!.Probability);
      Assert.AreEqual(0, upper.Warnings.Count);
    }

    #endregion

    #region ScaleBy ramp

    [TestMethod]
    public void TryParse_NoScaleBy_ChannelStaysNullEvenWhenMinMaxSet() {
      // Min/Max passed in but ScaleBy is empty → no ramp recorded.
      var input = Valid(scaleBy: "", scaleMin: 0.1f, scaleMax: 0.9f, probabilityAtMin: 0.5f);
      var (recipe, _, _) = Parse(input);
      Assert.IsNull(recipe!.ScaleBy);
      Assert.AreEqual(0f, recipe.ScaleMin);
      Assert.AreEqual(0f, recipe.ScaleMax);
      Assert.AreEqual(0f, recipe.ProbabilityAtMin);
    }

    [TestMethod]
    public void TryParse_UnknownScaleBy_Rejects() {
      var input = Valid(scaleBy: "NotAChannel", scaleMin: 0f, scaleMax: 1f);
      var (_, warnings, ok) = Parse(input);
      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "ScaleBy='NotAChannel'");
    }

    [TestMethod]
    public void TryParse_ScaleByCaseInsensitive() {
      var input = Valid(scaleBy: "waterflowmagnitude", scaleMin: 0f, scaleMax: 1f);
      var (recipe, _, ok) = Parse(input);
      Assert.IsTrue(ok);
      Assert.AreEqual(EcologyChannel.WaterFlowMagnitude, recipe!.ScaleBy);
    }

    [TestMethod]
    public void TryParse_ScaleMaxEqualToScaleMin_Rejects() {
      var input = Valid(scaleBy: "Moisture", scaleMin: 0.5f, scaleMax: 0.5f);
      var (_, warnings, ok) = Parse(input);
      Assert.IsFalse(ok);
      var hasWarn = warnings.Exists(w => w.Contains("ScaleMax=0.5") && w.Contains("ScaleMin=0.5"));
      Assert.IsTrue(hasWarn);
    }

    [TestMethod]
    public void TryParse_ScaleMaxBelowScaleMin_Rejects() {
      var input = Valid(scaleBy: "Moisture", scaleMin: 0.9f, scaleMax: 0.1f);
      var (_, warnings, ok) = Parse(input);
      Assert.IsFalse(ok);
      Assert.IsTrue(warnings.Exists(w => w.Contains("<= ScaleMin")));
    }

    [TestMethod]
    public void TryParse_ProbabilityAtMinBelowZero_ClampsToZero() {
      var input = Valid(
          scaleBy: "WaterDepth", scaleMin: 0f, scaleMax: 1f,
          probabilityAtMin: -0.5f);
      var (recipe, warnings, _) = Parse(input);
      Assert.AreEqual(0f, recipe!.ProbabilityAtMin);
      Assert.IsTrue(warnings.Exists(w => w.Contains("ProbabilityAtMin=-0.5") && w.Contains("Clamped to 0")));
    }

    [TestMethod]
    public void TryParse_ProbabilityAtMinAboveOne_ClampsToOne() {
      var input = Valid(
          scaleBy: "WaterDepth", scaleMin: 0f, scaleMax: 1f,
          probabilityAtMin: 1.5f);
      var (recipe, warnings, _) = Parse(input);
      Assert.AreEqual(1f, recipe!.ProbabilityAtMin);
      Assert.IsTrue(warnings.Exists(w => w.Contains("Clamped to 1")));
    }

    [TestMethod]
    public void TryParse_ValidScaleByRamp_PreservesAllFields() {
      var input = Valid(
          scaleBy: "WaterFlowMagnitude",
          scaleMin: 0.2f, scaleMax: 0.9f, probabilityAtMin: 0.1f);
      var (recipe, warnings, ok) = Parse(input);
      Assert.IsTrue(ok);
      Assert.AreEqual(0, warnings.Count);
      Assert.AreEqual(EcologyChannel.WaterFlowMagnitude, recipe!.ScaleBy);
      Assert.AreEqual(0.2f, recipe.ScaleMin);
      Assert.AreEqual(0.9f, recipe.ScaleMax);
      Assert.AreEqual(0.1f, recipe.ProbabilityAtMin);
    }

    #endregion

    #region Habitat filters

    [TestMethod]
    public void TryParse_KnownHabitatPassesThrough() {
      var input = Valid(includeHabitats: new[] { "Dry" });
      var (recipe, warnings, _) = Parse(input);
      Assert.AreEqual(0, warnings.Count);
      Assert.AreEqual(1, recipe!.IncludeHabitats.Count);
      Assert.AreEqual("Dry", recipe.IncludeHabitats[0]);
    }

    [TestMethod]
    public void TryParse_UnknownHabitat_DroppedWithWarning() {
      var input = Valid(includeHabitats: new[] { "Aquatic" });
      var (recipe, warnings, _) = Parse(input);
      Assert.IsTrue(warnings.Exists(w => w.Contains("IncludeHabitats='Aquatic'")));
      Assert.AreEqual(0, recipe!.IncludeHabitats.Count);
    }

    [TestMethod]
    public void TryParse_HabitatLabelDistinguishesIncludeAndExclude() {
      var input = Valid(
          includeHabitats: new[] { "Aquatic" },
          excludeHabitats: new[] { "Land" });
      var (_, warnings, _) = Parse(input);
      Assert.IsTrue(warnings.Exists(w => w.Contains("IncludeHabitats='Aquatic'")));
      Assert.IsTrue(warnings.Exists(w => w.Contains("ExcludeHabitats='Land'")));
    }

    [TestMethod]
    public void TryParse_DuplicateHabitatsCollapsed() {
      var input = Valid(includeHabitats: new[] { "Dry", "Dry", "Dry" });
      var (recipe, _, _) = Parse(input);
      Assert.AreEqual(1, recipe!.IncludeHabitats.Count);
    }

    [TestMethod]
    public void TryParse_EmptyHabitatListStaysEmpty() {
      var input = Valid();  // habitats default to empty arrays
      var (recipe, _, _) = Parse(input);
      Assert.AreEqual(0, recipe!.IncludeHabitats.Count);
      Assert.AreEqual(0, recipe.ExcludeHabitats.Count);
    }

    #endregion

    #region Filter and VanillaSpecies passthrough

    [TestMethod]
    public void TryParse_NullFilter_NormalisedToEmpty() {
      var input = Valid(filter: null!);
      var (recipe, _, _) = Parse(input);
      Assert.AreEqual("", recipe!.Filter);
    }

    [TestMethod]
    public void TryParse_FilterPassedThroughVerbatim() {
      var input = Valid(filter: "WaterEdge");
      var (recipe, _, _) = Parse(input);
      Assert.AreEqual("WaterEdge", recipe!.Filter);
    }

    [TestMethod]
    public void TryParse_VanillaSpeciesPassedThroughVerbatim() {
      var input = Valid(
          classes: System.Array.Empty<string>(),
          vanillaSpecies: new[] { "Cattail", "Spadderdock" });
      var (recipe, _, _) = Parse(input);
      Assert.AreEqual(2, recipe!.VanillaSpecies.Count);
      Assert.AreEqual("Cattail", recipe.VanillaSpecies[0]);
      Assert.AreEqual("Spadderdock", recipe.VanillaSpecies[1]);
    }

    #endregion

    #region Source attribution

    [TestMethod]
    public void TryParse_WarningsIncludeSourceBookName() {
      var input = Valid(action: "Maim");
      var warnings = new List<string>();
      AttritionRecipeParser.TryParse(input, "MyBook", warnings.Add, out _);
      Assert.IsTrue(warnings.Exists(w => w.Contains("'MyBook'")));
    }

    #endregion

  }

}
