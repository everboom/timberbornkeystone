using System.Collections.Generic;
using System.Linq;
using System.Text;
using Keystone.Mod.HarmonyPatches;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Asserts the load-time invariant of
  /// <see cref="TemplateCollectionServicePatch"/>: every blueprint
  /// the patch dropped must have been inside its declared
  /// mutation scope (the <c>crossFactionBlueprintNames</c> set --
  /// blueprints loaded via <c>NaturalResources.&lt;OtherFaction&gt;</c>
  /// collections we explicitly cross-loaded). A drop outside that
  /// scope is the failure mode the 0.4.2 hotfix was issued for, when
  /// the predicate was inverted ("not in nativeBlueprintNames" instead
  /// of "in crossFactionBlueprintNames") and we silently removed
  /// vanilla <c>Path</c> from <c>AllTemplates</c>, crashing the
  /// tutorial deserializer downstream.
  ///
  /// <para><b>How it works.</b> The patch publishes two pieces of
  /// state on each postfix run:
  /// <see cref="TemplateCollectionServicePatch.LastDroppedBuildingNames"/>
  /// (every blueprint name the most recent run dropped) and
  /// <see cref="TemplateCollectionServicePatch.LastCrossFactionBlueprintNames"/>
  /// (the cross-faction scope at run time). The test loads both and
  /// asserts <c>dropped ⊆ scope</c>. Any out-of-scope name fails the
  /// test loudly with the offending names in the detail.</para>
  ///
  /// <para><b>Pre-conditions.</b>
  /// <see cref="TemplateCollectionServicePatch.HasRun"/> must be true
  /// (the Harmony postfix actually executed). If it's false the test
  /// is skipped, not passed: a "passed-without-running" result would
  /// silently mask the regression the test exists to catch.</para>
  /// </summary>
  internal sealed class PatchScopeInvariantTest : IKeystoneSelfTest {

    /// <inheritdoc />
    public string Name => "Patch drop scope";

    /// <inheritdoc />
    public string Category => "Wiring";

    /// <inheritdoc />
    public SelfTestResult Run() {
      if (!TemplateCollectionServicePatch.HasRun) {
        return SelfTestResult.Skipped(
            "TemplateCollectionServicePatch postfix hasn't fired yet -- " +
            "Harmony attach may have failed, or the host method wasn't called.");
      }

      var dropped = TemplateCollectionServicePatch.LastDroppedBuildingNames;
      var scope = TemplateCollectionServicePatch.LastCrossFactionBlueprintNames;
      var scopeSet = scope as ISet<string>
          ?? new HashSet<string>(scope, System.StringComparer.Ordinal);

      var outOfScope = dropped.Where(n => !scopeSet.Contains(n)).ToList();

      var detail = new StringBuilder();
      detail.Append("Cross-faction scope: ").Append(scope.Count)
            .Append(" name(s). Dropped: ").Append(dropped.Count)
            .AppendLine(" name(s).");
      if (dropped.Count > 0) {
        detail.AppendLine("Dropped names:");
        foreach (var n in dropped.OrderBy(s => s, System.StringComparer.Ordinal)) {
          var marker = scopeSet.Contains(n) ? "[OK]" : "[OUT-OF-SCOPE]";
          detail.Append("  ").Append(marker).Append(' ').Append(n).AppendLine();
        }
      }

      if (outOfScope.Count > 0) {
        return SelfTestResult.Fail(
            $"{outOfScope.Count} drop(s) outside cross-faction scope: " +
            string.Join(", ", outOfScope.OrderBy(s => s, System.StringComparer.Ordinal)) +
            ". The patch's drop predicate has regressed -- it's removing " +
            "blueprints it shouldn't be touching.",
            detail.ToString());
      }

      return new SelfTestResult(
          SelfTestStatus.Pass,
          $"{dropped.Count} drop(s) all within {scope.Count}-name cross-faction scope",
          detail.ToString());
    }

  }

}
