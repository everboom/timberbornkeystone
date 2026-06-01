using System.Collections.Generic;
using System.Text;
using Keystone.Mod.Wellbeing;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Verifies the outcome of <see cref="KeystoneNatureModifierProvider"/>
  /// for every building listed in
  /// <see cref="Keystone.Core.Buildings.Factions.FactionRegistry.AllNatureFactions"/>:
  /// the blueprint must carry <see cref="KeystoneNatureSourceSpec"/>
  /// (with biome count matching the entry), and exactly one of
  /// <see cref="KeystoneEcologyTransparentSpec"/> /
  /// <see cref="KeystoneEcologyNoAuraSpec"/> / neither, matching the
  /// entry's <see cref="NatureBuilding.Transparent"/> /
  /// <see cref="NatureBuilding.NoAura"/> flags.
  ///
  /// <para><b>What this catches.</b> Faction-name drift on the Keystone
  /// side (a building renamed in <see cref="KeystoneNatureFactions"/> but
  /// the vanilla blueprint kept its old name, or vice versa), modifier-
  /// provider suffix-matching regressions, and any mismatch between
  /// the entry's footprint flags and the actually-attached specs (a
  /// missing-when-expected marker, an unexpected marker, or the wrong
  /// marker — transparent attached where no-aura was expected).</para>
  ///
  /// <para><b>Cross-faction note.</b> Only the active faction's buildings
  /// appear in <see cref="TemplateCollectionService.AllTemplates"/>;
  /// buildings belonging to other factions are absent by design and
  /// are simply skipped over. They contribute nothing to the pass /
  /// warn signal and don't appear in any output. The pass criterion is
  /// "every present building has its expected specs wired correctly."
  /// If no building in the registry targets the active faction, the
  /// test returns <see cref="SelfTestStatus.Skipped"/> rather than a
  /// noise warning.</para>
  /// </summary>
  internal sealed class NatureBuildingWiringTest : IKeystoneSelfTest {

    private readonly TemplateCollectionService _templates;

    public NatureBuildingWiringTest(TemplateCollectionService templates) {
      _templates = templates;
    }

    /// <inheritdoc />
    public string Name => "Nature building wiring";

    /// <inheritdoc />
    public string Category => "Wiring";

    /// <inheritdoc />
    public SelfTestResult Run() {
      // Collect template names to identify which faction's content
      // is live, so we can distinguish "intended for this faction
      // but didn't resolve" (a real concern, likely typo or
      // game-version drift) from "belongs to another faction"
      // (expected absence).
      var presentTemplateNames = new HashSet<string>();
      foreach (var bp in _templates.AllTemplates) {
        presentTemplateNames.Add(bp.Name);
      }
      var activeFactionSuffixes = new HashSet<string>();
      foreach (var name in presentTemplateNames) {
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1) {
          activeFactionSuffixes.Add(name.Substring(dot + 1));
        }
      }

      var present = 0;
      var wired = 0;
      var inScopeCount = 0;
      var expectedButAbsent = new List<string>();
      var misconfigured = new List<string>();

      foreach (var faction in Keystone.Core.Buildings.Factions.FactionRegistry.AllNatureFactions) {
        foreach (var building in faction.Buildings) {
          var suffix = SuffixOf(building.BlueprintName);
          var expectedHere = suffix.Length == 0 || activeFactionSuffixes.Contains(suffix);
          // Cross-faction entries are silently skipped: they don't
          // belong to the active faction and contribute nothing to
          // the wiring signal we're trying to surface.
          if (!expectedHere) continue;
          inScopeCount++;

          var blueprint = FindBlueprint(building.BlueprintName);
          if (blueprint == null) {
            expectedButAbsent.Add(building.BlueprintName);
            continue;
          }
          present++;

          var sourceSpec = blueprint.HasSpec<KeystoneNatureSourceSpec>()
              ? blueprint.GetSpec<KeystoneNatureSourceSpec>()
              : null;
          var hasTransparent = blueprint.HasSpec<KeystoneEcologyTransparentSpec>();
          var hasNoAura = blueprint.HasSpec<KeystoneEcologyNoAuraSpec>();

          if (sourceSpec == null) {
            misconfigured.Add($"{building.BlueprintName}: missing KeystoneNatureSourceSpec");
            continue;
          }
          if (building.Transparent && !hasTransparent) {
            misconfigured.Add($"{building.BlueprintName}: missing KeystoneEcologyTransparentSpec (Transparent=true)");
            continue;
          }
          if (!building.Transparent && hasTransparent) {
            misconfigured.Add($"{building.BlueprintName}: unexpected KeystoneEcologyTransparentSpec (Transparent=false)");
            continue;
          }
          if (building.NoAura && !hasNoAura) {
            misconfigured.Add($"{building.BlueprintName}: missing KeystoneEcologyNoAuraSpec (NoAura=true)");
            continue;
          }
          if (!building.NoAura && hasNoAura) {
            misconfigured.Add($"{building.BlueprintName}: unexpected KeystoneEcologyNoAuraSpec (NoAura=false)");
            continue;
          }
          if (sourceSpec.Sources.IsDefault || sourceSpec.Sources.Length == 0) {
            misconfigured.Add($"{building.BlueprintName}: Sources array is empty");
            continue;
          }
          if (sourceSpec.Sources.Length != building.Biomes.Count) {
            misconfigured.Add(
                $"{building.BlueprintName}: Sources length {sourceSpec.Sources.Length} " +
                $"does not match expected {building.Biomes.Count}");
            continue;
          }

          wired++;
        }
      }

      // Misconfiguration on a present building is a real failure —
      // we attempted to inject specs and they didn't land. Distinct
      // from "absent" cases, which are at-most warnings.
      if (misconfigured.Count > 0) {
        var detail = new StringBuilder();
        detail.Append(present).Append(" present, ")
              .Append(wired).AppendLine(" wired correctly.");
        foreach (var p in misconfigured) {
          detail.Append("  ").AppendLine(p);
        }
        return SelfTestResult.Fail(
            $"{misconfigured.Count} of {present} present Nature building(s) misconfigured",
            detail.ToString());
      }

      // Faction-suffixed entries that didn't resolve. Probably typos,
      // a Timberborn rename, or a load-order issue. Advisory.
      if (expectedButAbsent.Count > 0) {
        var detail = new StringBuilder();
        detail.Append(present).Append(" wired correctly, ")
              .Append(expectedButAbsent.Count).AppendLine(" missing.");
        detail.AppendLine("Missing (likely typos, or removed by a Timberborn update):");
        foreach (var n in expectedButAbsent) {
          detail.Append("  ").AppendLine(n);
        }
        return SelfTestResult.Warn(
            $"{expectedButAbsent.Count} Nature building name(s) targeting this faction failed to resolve",
            detail.ToString());
      }

      if (inScopeCount == 0) {
        return SelfTestResult.Skipped(
            "No Nature buildings in the registry target the active faction "
            + "(every entry belongs to another faction).");
      }

      return SelfTestResult.Pass(
          $"{wired}/{present} present buildings wired correctly");
    }

    /// <summary>Return the dot-suffix of <paramref name="name"/>, or
    /// empty if there is none. Used to detect the faction tag on a
    /// Blueprint name without resorting to a vanilla service call.</summary>
    private static string SuffixOf(string name) {
      var dot = name.LastIndexOf('.');
      return (dot > 0 && dot < name.Length - 1) ? name.Substring(dot + 1) : string.Empty;
    }

    /// <summary>Lookup by exact <see cref="Timberborn.BlueprintSystem.Blueprint.Name"/>
    /// match. The Name is the asset's filename without the
    /// <c>.blueprint</c> extension (e.g. <c>"ContemplationSpot.Folktails"</c>),
    /// which is exactly the form
    /// <see cref="KeystoneNatureFactions"/> stores. Returns the first
    /// matching blueprint or <c>null</c>.</summary>
    private Timberborn.BlueprintSystem.Blueprint? FindBlueprint(string blueprintName) {
      foreach (var bp in _templates.AllTemplates) {
        if (string.Equals(bp.Name, blueprintName, System.StringComparison.Ordinal)) {
          return bp;
        }
      }
      return null;
    }

  }

}
