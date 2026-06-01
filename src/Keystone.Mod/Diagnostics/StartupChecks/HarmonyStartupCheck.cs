using System.Collections.Generic;

namespace Keystone.Mod.Diagnostics.StartupChecks {

  /// <summary>
  /// Surfaces Harmony patch-application results captured at
  /// <c>IModStarter</c> time. Reads the static state on
  /// <see cref="KeystoneModStarter"/> rather than re-running anything
  /// at PostLoad -- <c>PatchAll</c> runs exactly once, before DI is
  /// up, and we can't replay it.
  ///
  /// <para><b>What this catches.</b> A Timberborn update that renames
  /// or removes a Harmony target leaves <c>PatchAll</c> applying fewer
  /// methods than expected; the count mismatch surfaces here. A
  /// <c>PatchAll</c> exception (e.g. an unresolvable type) takes
  /// every patch down, which is louder still.</para>
  /// </summary>
  public sealed class HarmonyStartupCheck : IStartupCheck {

    /// <inheritdoc />
    public string Category => "Harmony";

    /// <inheritdoc />
    /// <remarks>Always ready: the static state on
    /// <see cref="KeystoneModStarter"/> is populated by
    /// <c>IModStarter.StartMod</c>, which runs at mod-load time, long
    /// before any Bindito scope exists.</remarks>
    public bool IsReady => true;

    /// <inheritdoc />
    public IEnumerable<StartupFinding> Run() {
      if (KeystoneModStarter.PatchAllException != null) {
        var ex = KeystoneModStarter.PatchAllException;
        yield return new StartupFinding(
            StartupFindingSeverity.Error,
            "Keystone couldn't hook into the game properly. " +
            "Cross-faction content visibility and ambient-object " +
            "interaction may not work.",
            DetailedMessage:
                $"Harmony PatchAll threw {ex.GetType().Name}: {ex.Message}.");
        yield break;
      }

      var applied = KeystoneModStarter.AppliedPatchCount;
      var expected = KeystoneModStarter.ExpectedPatchedMethodCount;
      if (applied != expected) {
        yield return new StartupFinding(
            StartupFindingSeverity.Warning,
            "A recent Timberborn update may have moved something Keystone " +
            "expects. Some features may not work correctly.",
            DetailedMessage:
                $"Applied {applied} Harmony patch(es) but expected {expected}; " +
                "a target method may have been renamed or removed.");
      }
    }

  }

}
