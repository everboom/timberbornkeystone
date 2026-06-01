using System.Collections.Generic;
using Keystone.Core.Biomes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Pins the rules that <see cref="BiomeLevelEntryValidator.TryApply"/>
  /// enforces on incoming spec entries before they reach
  /// <see cref="BiomeLevelTable.Define"/>:
  /// <list type="bullet">
  ///   <item>Empty <c>LevelId</c> rejects.</item>
  ///   <item>Upper-not-greater-than-lower rejects.</item>
  ///   <item>Negative <c>LowerMaturity</c> rejects.</item>
  ///   <item>Density &lt; 0 silently falls back to
  ///         <see cref="BiomeLevelEntryValidator.DefaultDensity"/> (the
  ///         sentinel-for-omitted-field semantic).</item>
  ///   <item>Density &gt; 1 clamps to 1 with a warning.</item>
  ///   <item>Density of exactly 0 is a valid value and is preserved
  ///         (distinguishing "level fires no recipes" from "unset").</item>
  ///   <item>Empty <c>Mode</c> silently defaults to Deterministic.</item>
  ///   <item>Unknown <c>Mode</c> warns and falls back to Deterministic.</item>
  ///   <item>Mode parsing is case-insensitive.</item>
  /// </list>
  /// </summary>
  [TestClass]
  public class BiomeLevelEntryValidatorTests {

    #region Helpers

    private static BiomeLevelInput Valid(
        string levelId = "L1",
        float lower = 0f,
        float upper = 10f,
        float density = 0.1f,
        bool runAtStartup = false,
        string mode = "",
        int faunaCapacityAtSaturation = 0,
        float faunaMinScore = 0f) {
      return new BiomeLevelInput(
          LevelId: levelId,
          LowerMaturity: lower,
          UpperMaturity: upper,
          Density: density,
          RunAtStartup: runAtStartup,
          Mode: mode,
          FaunaCapacityAtSaturation: faunaCapacityAtSaturation,
          FaunaMinScore: faunaMinScore);
    }

    private static (BiomeLevelTable Table, List<string> Warnings, bool Result)
        Apply(BiomeLevelInput input, BiomeKind biome = BiomeKind.Grassland) {
      var table = new BiomeLevelTable();
      var warnings = new List<string>();
      var ok = BiomeLevelEntryValidator.TryApply(table, biome, input, "test", warnings.Add);
      return (table, warnings, ok);
    }

    private static BiomeLevel? GetLevel(BiomeLevelTable table, BiomeKind biome, string levelId) {
      foreach (var lvl in table.LevelsFor(biome)) {
        if (lvl.LevelId == levelId) return lvl;
      }
      return null;
    }

    #endregion

    #region Empty LevelId

    [TestMethod]
    public void TryApply_EmptyLevelId_RejectsAndWarns() {
      var input = Valid(levelId: "");
      var (table, warnings, ok) = Apply(input);

      Assert.IsFalse(ok);
      Assert.AreEqual(0, table.Count, "Rejected input must not define a level.");
      Assert.AreEqual(1, warnings.Count);
      StringAssert.Contains(warnings[0], "empty LevelId");
    }

    [TestMethod]
    public void TryApply_NullLevelId_RejectsAndWarns() {
      var input = Valid(levelId: null!);
      var (_, warnings, ok) = Apply(input);

      Assert.IsFalse(ok);
      Assert.AreEqual(1, warnings.Count);
      StringAssert.Contains(warnings[0], "empty LevelId");
    }

    #endregion

    #region Range ordering

    [TestMethod]
    public void TryApply_UpperEqualsLower_RejectsBecauseRangeIsEmpty() {
      var input = Valid(lower: 5f, upper: 5f);
      var (_, warnings, ok) = Apply(input);

      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "invalid range");
    }

    [TestMethod]
    public void TryApply_UpperLessThanLower_RejectsAndIncludesValuesInWarning() {
      var input = Valid(lower: 10f, upper: 5f);
      var (_, warnings, ok) = Apply(input);

      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "upper=5");
      StringAssert.Contains(warnings[0], "lower=10");
    }

    [TestMethod]
    public void TryApply_NegativeLowerMaturity_Rejects() {
      // Even with upper > lower, a negative lower is invalid (maturity
      // is a non-negative accumulator).
      var input = Valid(lower: -1f, upper: 5f);
      var (_, warnings, ok) = Apply(input);

      Assert.IsFalse(ok);
      StringAssert.Contains(warnings[0], "negative LowerMaturity");
    }

    #endregion

    #region Density semantics

    [TestMethod]
    public void TryApply_NegativeDensity_FallsBackToDefaultSilently() {
      // The -1f sentinel for "field omitted" must yield the default
      // density without producing a warning (it's an expected init value).
      var input = Valid(density: -1f);
      var (table, warnings, ok) = Apply(input);

      Assert.IsTrue(ok);
      Assert.AreEqual(0, warnings.Count, "Negative density is the unset sentinel — no warning expected.");
      var level = GetLevel(table, BiomeKind.Grassland, "L1");
      Assert.IsNotNull(level);
      Assert.AreEqual(BiomeLevelEntryValidator.DefaultDensity, level!.Density);
    }

    [TestMethod]
    public void TryApply_ZeroDensity_PreservedAsExplicitValue() {
      // 0 means "level activates nothing" — semantically distinct from
      // "unset". Must not be rewritten to the default.
      var input = Valid(density: 0f);
      var (table, warnings, ok) = Apply(input);

      Assert.IsTrue(ok);
      Assert.AreEqual(0, warnings.Count);
      var level = GetLevel(table, BiomeKind.Grassland, "L1");
      Assert.AreEqual(0f, level!.Density);
    }

    [TestMethod]
    public void TryApply_DensityInRange_PreservedExactly() {
      var input = Valid(density: 0.37f);
      var (table, _, _) = Apply(input);

      var level = GetLevel(table, BiomeKind.Grassland, "L1");
      Assert.AreEqual(0.37f, level!.Density, 1e-6f);
    }

    [TestMethod]
    public void TryApply_DensityAboveOne_ClampsToOneWithWarning() {
      var input = Valid(density: 1.5f);
      var (table, warnings, ok) = Apply(input);

      Assert.IsTrue(ok);
      Assert.AreEqual(1, warnings.Count);
      StringAssert.Contains(warnings[0], "exceeds 1");
      Assert.AreEqual(1f, GetLevel(table, BiomeKind.Grassland, "L1")!.Density);
    }

    [TestMethod]
    public void TryApply_DensityExactlyOne_NotWarned() {
      var input = Valid(density: 1f);
      var (table, warnings, _) = Apply(input);

      Assert.AreEqual(0, warnings.Count, "1.0 is in-range, no warning.");
      Assert.AreEqual(1f, GetLevel(table, BiomeKind.Grassland, "L1")!.Density);
    }

    #endregion

    #region Mode parsing

    [TestMethod]
    public void TryApply_EmptyMode_DefaultsToDeterministicSilently() {
      var input = Valid(mode: "");
      var (table, warnings, _) = Apply(input);

      Assert.AreEqual(0, warnings.Count, "Empty mode is treated as omitted, no warning.");
      Assert.AreEqual(LevelDispatchMode.Deterministic,
          GetLevel(table, BiomeKind.Grassland, "L1")!.Mode);
    }

    [TestMethod]
    public void TryApply_DeterministicMode_Preserved() {
      var input = Valid(mode: "Deterministic");
      var (table, _, _) = Apply(input);

      Assert.AreEqual(LevelDispatchMode.Deterministic,
          GetLevel(table, BiomeKind.Grassland, "L1")!.Mode);
    }

    [TestMethod]
    public void TryApply_StochasticMode_Preserved() {
      var input = Valid(mode: "Stochastic");
      var (table, _, _) = Apply(input);

      Assert.AreEqual(LevelDispatchMode.Stochastic,
          GetLevel(table, BiomeKind.Grassland, "L1")!.Mode);
    }

    [TestMethod]
    public void TryApply_ModeParsingIsCaseInsensitive() {
      var input = Valid(mode: "stochastic");
      var (table, warnings, _) = Apply(input);

      Assert.AreEqual(0, warnings.Count);
      Assert.AreEqual(LevelDispatchMode.Stochastic,
          GetLevel(table, BiomeKind.Grassland, "L1")!.Mode);
    }

    [TestMethod]
    public void TryApply_UnknownMode_WarnsAndFallsBackToDeterministic() {
      var input = Valid(mode: "Bogus");
      var (table, warnings, ok) = Apply(input);

      Assert.IsTrue(ok, "Unknown mode still defines the level, falling back.");
      Assert.AreEqual(1, warnings.Count);
      StringAssert.Contains(warnings[0], "unknown Mode");
      Assert.AreEqual(LevelDispatchMode.Deterministic,
          GetLevel(table, BiomeKind.Grassland, "L1")!.Mode);
    }

    #endregion

    #region Successful define

    [TestMethod]
    public void TryApply_ValidInput_DefinesLevelOnTable() {
      var input = Valid(levelId: "L2", lower: 2f, upper: 30f, density: 0.5f);
      var (table, _, ok) = Apply(input);

      Assert.IsTrue(ok);
      var level = GetLevel(table, BiomeKind.Grassland, "L2");
      Assert.IsNotNull(level);
      Assert.AreEqual("L2", level!.LevelId);
      Assert.AreEqual(2f, level.LowerMaturity);
      Assert.AreEqual(30f, level.UpperMaturity);
      Assert.AreEqual(0.5f, level.Density);
    }

    [TestMethod]
    public void TryApply_PreservesRunAtStartupAndFaunaFields() {
      var input = Valid(
          runAtStartup: true,
          faunaCapacityAtSaturation: 8,
          faunaMinScore: 0.5f);
      var (table, _, _) = Apply(input);

      var level = GetLevel(table, BiomeKind.Grassland, "L1");
      Assert.IsTrue(level!.RunAtStartup);
      Assert.AreEqual(8, level.FaunaCapacityAtSaturation);
      Assert.AreEqual(0.5f, level.FaunaMinScore);
    }

    #endregion

    #region Source attribution

    [TestMethod]
    public void TryApply_WarningsIncludeSourceLabel() {
      // Multiple specs feed the catalog (default ladder + per-biome
      // overrides); the source label has to thread through so authors
      // can tell which spec produced a malformed entry.
      var input = Valid(levelId: "");
      var table = new BiomeLevelTable();
      var warnings = new List<string>();
      BiomeLevelEntryValidator.TryApply(table, BiomeKind.Grassland, input,
          "override for Grassland", warnings.Add);

      Assert.AreEqual(1, warnings.Count);
      StringAssert.Contains(warnings[0], "override for Grassland");
    }

    #endregion

  }

}
