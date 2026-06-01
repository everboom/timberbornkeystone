using System.Collections.Generic;
using System.Text;
using Keystone.Mod.Recipes;
using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Walks every <see cref="KeystoneRecipeBookSpec"/> instance loaded
  /// in this game and validates each entry's required string fields
  /// before <see cref="FlourishCatalog"/> parses them. Catches the
  /// silent-skip failure class where a typo in the recipe-book JSON
  /// (e.g. <c>"Class": "b"</c> instead of <c>"B"</c>, or omitting
  /// <c>Category</c> on a Class D entry) makes the parser drop the
  /// recipe with a warning but no visible game-side symptom — the
  /// affected content just stops spawning.
  ///
  /// <para><b>What we check.</b> For each <see cref="RecipeEntry"/>:
  /// the <c>Class</c> string must be one of <c>{A, B, C, D, E}</c>;
  /// Class D and Class E entries must have a non-empty <c>Category</c>;
  /// either <c>BlueprintName</c> or <c>BlueprintNames</c> must be set.
  /// For each <see cref="AttritionEntry"/>: the <c>Action</c> string
  /// must be <c>Kill</c> or <c>Destroy</c>.</para>
  ///
  /// <para><b>Why the raw spec, not the catalog.</b> By the time
  /// <see cref="FlourishCatalog"/>'s post-load has run, malformed
  /// recipes have already been dropped — the catalog is the
  /// <i>filtered</i> output. We want to flag the <i>input</i> so the
  /// author sees the typo, not just the missing content.</para>
  /// </summary>
  internal sealed class RecipeBookValidityTest : IKeystoneSelfTest {

    private static readonly HashSet<string> ValidClasses = new(System.StringComparer.Ordinal) {
        "A", "B", "C", "D", "E",
    };

    private static readonly HashSet<string> ValidAttritionActions = new(System.StringComparer.Ordinal) {
        "Kill", "Destroy",
    };

    private readonly ISpecService _specs;

    public RecipeBookValidityTest(ISpecService specs) {
      _specs = specs;
    }

    /// <inheritdoc />
    public string Name => "Recipe book validity";

    /// <inheritdoc />
    public string Category => "Specs";

    /// <inheritdoc />
    public SelfTestResult Run() {
      var problems = new List<string>();
      var recipesChecked = 0;
      var attritionsChecked = 0;
      var booksChecked = 0;

      foreach (var book in _specs.GetSpecs<KeystoneRecipeBookSpec>()) {
        booksChecked++;
        if (!book.Recipes.IsDefault) {
          for (var i = 0; i < book.Recipes.Length; i++) {
            recipesChecked++;
            CheckRecipe(book.Recipes[i], i, problems);
          }
        }
        if (!book.Attritions.IsDefault) {
          for (var i = 0; i < book.Attritions.Length; i++) {
            attritionsChecked++;
            CheckAttrition(book.Attritions[i], i, problems);
          }
        }
      }

      if (booksChecked == 0) {
        return SelfTestResult.Skipped(
            "No KeystoneRecipeBookSpec instances loaded. " +
            "Templates may not have completed loading yet.");
      }

      if (problems.Count > 0) {
        var detail = new StringBuilder();
        detail.Append(booksChecked).Append(" recipe book(s), ")
              .Append(recipesChecked).Append(" recipe(s), ")
              .Append(attritionsChecked).AppendLine(" attrition(s) checked");
        foreach (var p in problems) {
          detail.Append("  ").AppendLine(p);
        }
        return SelfTestResult.Fail(
            $"{problems.Count} malformed entry(ies) across {booksChecked} books",
            detail.ToString());
      }

      return SelfTestResult.Pass(
          $"{recipesChecked} recipe(s), {attritionsChecked} attrition(s) " +
          $"OK across {booksChecked} books");
    }

    private static void CheckRecipe(RecipeEntry entry, int index, List<string> problems) {
      var label = $"Recipe[{index}] (Class='{entry.Class}', Blueprint='{entry.BlueprintName}')";

      if (string.IsNullOrEmpty(entry.Class)) {
        problems.Add(label + ": Class is empty");
        return;  // can't run class-conditional checks below
      }
      if (!ValidClasses.Contains(entry.Class)) {
        problems.Add(label + $": Class '{entry.Class}' not in {{A,B,C,D,E}}");
      }

      var hasBlueprintName = !string.IsNullOrEmpty(entry.BlueprintName);
      var hasBlueprintNames = !entry.BlueprintNames.IsDefault && entry.BlueprintNames.Length > 0;
      if (!hasBlueprintName && !hasBlueprintNames) {
        problems.Add(label + ": neither BlueprintName nor BlueprintNames is set");
      }

      // Category is required only for Class D and Class E (the
      // user-facing slider buckets). Class A/B/C ignore it.
      if ((entry.Class == "D" || entry.Class == "E") && string.IsNullOrEmpty(entry.Category)) {
        problems.Add(label + ": Class " + entry.Class + " requires non-empty Category");
      }
    }

    private static void CheckAttrition(AttritionEntry entry, int index, List<string> problems) {
      var label = $"Attrition[{index}] (Biome='{entry.Biome}', Level='{entry.Level}')";

      if (string.IsNullOrEmpty(entry.Action)) {
        problems.Add(label + ": Action is empty");
      } else if (!ValidAttritionActions.Contains(entry.Action)) {
        problems.Add(label + $": Action '{entry.Action}' not in {{Kill, Destroy}}");
      }

      // Either Classes or VanillaSpecies must be set; otherwise the
      // rule has no targets and will silently fire on nothing.
      var hasClasses = !entry.Classes.IsDefault && entry.Classes.Length > 0;
      var hasVanilla = !entry.VanillaSpecies.IsDefault && entry.VanillaSpecies.Length > 0;
      if (!hasClasses && !hasVanilla) {
        problems.Add(label + ": neither Classes nor VanillaSpecies is set (rule has no targets)");
      }
    }

  }

}
