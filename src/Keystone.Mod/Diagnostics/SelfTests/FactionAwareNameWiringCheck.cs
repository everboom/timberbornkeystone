using System.Collections.Generic;
using System.Text;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Shared check for "every name in a Keystone-curated blueprint
  /// list resolves to a template in the active faction." Used by
  /// <see cref="EcologyTransparentBuildingWiringTest"/> and
  /// <see cref="EcologyNoAuraBuildingWiringTest"/>; the helper
  /// extracts the per-faction reasoning so the two tests can share
  /// the warning logic verbatim.
  ///
  /// <para><b>Heuristic for "intended for this faction".</b> Names
  /// follow the <c>Blueprint.Faction</c> suffix convention
  /// (<c>".Folktails"</c>, <c>".IronTeeth"</c>, <c>".LeafCoats"</c>,
  /// etc.). We don't have the active faction id without going through
  /// vanilla services we'd rather not depend on at the self-test
  /// level, but we DO have <see cref="TemplateCollectionService.AllTemplates"/>
  /// — the templates loaded for the active faction. Suffix-extract
  /// the unique faction tags present in those templates' names; treat
  /// listed names whose suffix matches one of those tags as "expected
  /// to resolve in this faction." Names with no recognised faction
  /// suffix (the rare unfactioned blueprints — there aren't any in
  /// vanilla today) are also expected to resolve.</para>
  /// </summary>
  internal static class FactionAwareNameWiringCheck {

    /// <summary>Run the check. Returns:
    /// <list type="bullet">
    ///   <item>Pass — every "expected here" name resolves.</item>
    ///   <item>Skipped — no listed name targets the active faction
    ///         (everything in the list belongs to other factions, so
    ///         there's nothing for this test to verify in the current
    ///         game state).</item>
    ///   <item>Warning (partial) — at least one suffix-matched name
    ///         failed to resolve, with details listing the absent
    ///         names so the developer can verify (typo vs.
    ///         game-version change).</item>
    /// </list>
    /// <para>Cross-faction absences are silent at every level. They
    /// contribute nothing to the signal the test is trying to surface
    /// (typos / drift on the active faction's slice of the list) and
    /// previously crowded the output with "(N absent — other factions,
    /// expected)" noise that the developer had to filter past every
    /// time.</para></summary>
    public static SelfTestResult Run(
        string listLabel,
        IReadOnlyCollection<string> listedNames,
        TemplateCollectionService templates) {
      var presentNames = new HashSet<string>();
      foreach (var bp in templates.AllTemplates) {
        presentNames.Add(bp.Name);
      }
      // Faction suffixes present in the live templates — the active
      // faction's tag plus any cross-faction donor tags (e.g.
      // Folktails primary + the IronTeeth tag still showing up on
      // donor blueprints we patched in via TemplateCollectionService).
      var activeFactionSuffixes = new HashSet<string>();
      foreach (var name in presentNames) {
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1) {
          activeFactionSuffixes.Add(name.Substring(dot + 1));
        }
      }

      var resolved = new List<string>();
      var expectedButAbsent = new List<string>();
      var inScopeCount = 0;

      foreach (var name in listedNames) {
        var dot = name.LastIndexOf('.');
        var suffix = (dot > 0 && dot < name.Length - 1)
            ? name.Substring(dot + 1) : string.Empty;
        var expectedHere = suffix.Length == 0
            || activeFactionSuffixes.Contains(suffix);
        if (!expectedHere) continue;
        inScopeCount++;
        if (presentNames.Contains(name)) {
          resolved.Add(name);
        } else {
          expectedButAbsent.Add(name);
        }
      }

      if (expectedButAbsent.Count > 0) {
        var detail = new StringBuilder();
        detail.Append(resolved.Count).Append(" resolved, ")
              .Append(expectedButAbsent.Count).AppendLine(" missing.");
        detail.AppendLine("Missing names (likely typos, or removed by a Timberborn update):");
        foreach (var n in expectedButAbsent) {
          detail.Append("  ").AppendLine(n);
        }
        return SelfTestResult.Warn(
            $"{expectedButAbsent.Count} {listLabel} name(s) targeting this faction failed to resolve",
            detail.ToString());
      }

      if (inScopeCount == 0) {
        return SelfTestResult.Skipped(
            $"No {listLabel} names target the active faction "
            + "(every entry in the list belongs to another faction).");
      }

      return SelfTestResult.Pass(
          $"{resolved.Count}/{inScopeCount} {listLabel} name(s) resolved");
    }

  }

}
