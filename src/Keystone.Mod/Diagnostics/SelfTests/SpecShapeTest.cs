using System.Collections.Generic;
using System.Text;
using Keystone.Mod.Flourish;
using Keystone.Mod.Recipes;
using Keystone.Mod.Wellbeing;
using Timberborn.BlueprintSystem;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Walks every blueprint in
  /// <see cref="TemplateCollectionService.AllTemplates"/> and, for each
  /// Keystone-side spec it carries, asserts that required fields were
  /// populated by deserialization. Catches the failure class where a
  /// JSON-property rename, a type signature change (notoriously
  /// <c>List&lt;T&gt;</c> vs <c>ImmutableArray&lt;T&gt;</c> on a record
  /// list), or a silent <see cref="Timberborn.BlueprintSystem"/>
  /// deserializer drop produces a spec instance whose fields are all
  /// at their default values.
  ///
  /// <para><b>What we check.</b> Per spec type, the fields whose absence
  /// is load-bearing for downstream behaviour. We don't sanity-check
  /// every <c>[Serialize]</c> field — only the ones whose default value
  /// would silently break the system that consumes them. Specs whose
  /// only role is a marker (no fields) are skipped.</para>
  ///
  /// <para><b>One row per spec type.</b> The report rolls per-blueprint
  /// findings into a single fail with details, rather than emitting one
  /// failure per blueprint — the developer wants to see "3 of 8 blueprints
  /// with KeystoneNatureSourceSpec have an empty Sources array", not
  /// eight separate red rows.</para>
  /// </summary>
  internal sealed class SpecShapeTest : IKeystoneSelfTest {

    private readonly TemplateCollectionService _templates;

    public SpecShapeTest(TemplateCollectionService templates) {
      _templates = templates;
    }

    /// <inheritdoc />
    public string Name => "Spec shape";

    /// <inheritdoc />
    public string Category => "Specs";

    /// <inheritdoc />
    public SelfTestResult Run() {
      var problems = new List<string>();
      var checkedBlueprintCount = 0;
      var specChecks = 0;

      foreach (var bp in _templates.AllTemplates) {
        var hadAnyKeystoneSpec = false;

        if (bp.HasSpec<KeystoneNatureSourceSpec>()) {
          hadAnyKeystoneSpec = true;
          specChecks++;
          CheckNatureSourceSpec(bp, bp.GetSpec<KeystoneNatureSourceSpec>(), problems);
        }

        if (bp.HasSpec<KeystoneFlourishSpec>()) {
          hadAnyKeystoneSpec = true;
          specChecks++;
          // KeystoneFlourishSpec is a marker today (no required fields);
          // having it attached is sufficient. Bump the check counter so
          // the rollup reflects the coverage, but no per-field
          // assertions.
        }

        if (bp.HasSpec<KeystoneBiomeLevelsSpec>()) {
          hadAnyKeystoneSpec = true;
          specChecks++;
          CheckBiomeLevelsSpec(bp, bp.GetSpec<KeystoneBiomeLevelsSpec>(), problems);
        }

        if (bp.HasSpec<KeystoneRecipeBookSpec>()) {
          hadAnyKeystoneSpec = true;
          specChecks++;
          CheckRecipeBookSpec(bp, bp.GetSpec<KeystoneRecipeBookSpec>(), problems);
        }

        if (hadAnyKeystoneSpec) checkedBlueprintCount++;
      }

      if (specChecks == 0) {
        return SelfTestResult.Skipped(
            "No Keystone-side specs found on any loaded blueprint. " +
            "Either templates have not yet loaded, or no Keystone-decorated " +
            "blueprints are present in the active faction.");
      }

      if (problems.Count > 0) {
        var detail = new StringBuilder();
        detail.Append(specChecks).Append(" spec instances checked across ")
              .Append(checkedBlueprintCount).AppendLine(" blueprints");
        foreach (var p in problems) {
          detail.Append("  ").AppendLine(p);
        }
        return SelfTestResult.Fail(
            $"{problems.Count} of {specChecks} spec instance(s) malformed",
            detail.ToString());
      }

      return SelfTestResult.Pass(
          $"{specChecks} spec instances OK across {checkedBlueprintCount} blueprints");
    }

    private static void CheckNatureSourceSpec(
        Blueprint bp, KeystoneNatureSourceSpec spec, List<string> problems) {
      var label = bp.Name + ".KeystoneNatureSourceSpec";
      if (spec.Sources.IsDefault) {
        problems.Add(label + ": Sources is default (deserializer dropped the list)");
        return;
      }
      if (spec.Sources.Length == 0) {
        problems.Add(label + ": Sources is empty");
        return;
      }
      for (var i = 0; i < spec.Sources.Length; i++) {
        var entry = spec.Sources[i];
        if (string.IsNullOrEmpty(entry.Biome)) {
          problems.Add(label + $": Sources[{i}].Biome is empty");
        }
        if (string.IsNullOrEmpty(entry.NeedId)) {
          problems.Add(label + $": Sources[{i}].NeedId is empty");
        }
        if (entry.PointsPerHour <= 0f) {
          problems.Add(label + $": Sources[{i}].PointsPerHour is {entry.PointsPerHour} (must be > 0)");
        }
      }
    }

    private static void CheckBiomeLevelsSpec(
        Blueprint bp, KeystoneBiomeLevelsSpec spec, List<string> problems) {
      var label = bp.Name + ".KeystoneBiomeLevelsSpec";
      if (spec.Levels.IsDefault) {
        problems.Add(label + ": Levels is default (deserializer dropped the list)");
        return;
      }
      if (spec.Levels.Length == 0) {
        problems.Add(label + ": Levels is empty");
      }
    }

    private static void CheckRecipeBookSpec(
        Blueprint bp, KeystoneRecipeBookSpec spec, List<string> problems) {
      var label = bp.Name + ".KeystoneRecipeBookSpec";
      // The catalog reads this off; both lists default with an empty
      // book on a deployed blueprint is almost certainly a deserializer
      // drop, not an intentional empty. (Either Recipes or Attritions
      // alone being empty is legitimate -- some books carry only one
      // kind.)
      if (spec.Recipes.IsDefault && spec.Attritions.IsDefault) {
        problems.Add(label + ": both Recipes and Attritions are default (deserializer dropped them)");
      }
    }

  }

}
