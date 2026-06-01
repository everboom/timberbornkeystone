using System.Collections.Generic;
using Keystone.Mod.Diagnostics;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Once-per-session "first invocation" logging hook for Harmony
  /// patches. Each patch calls <see cref="Once"/> on first execution;
  /// subsequent calls are no-ops.
  ///
  /// <para><b>Why this exists.</b> The startup
  /// <c>ExpectedPatchedMethodCount</c> assertion in
  /// <see cref="KeystoneModStarter"/> catches "Harmony failed to
  /// resolve the target method" (count is wrong). It does NOT catch
  /// "Harmony resolved the target but the patch silently no-ops" —
  /// e.g. when a <c>ref</c>-enumerable parameter is renamed and
  /// Harmony's by-name parameter matching binds nothing into our
  /// patch's <c>ref</c> argument. In that case the patch is "applied"
  /// (count still matches) but the player-visible behavior never
  /// fires.</para>
  ///
  /// <para>A missing first-invocation log line during a known-good
  /// test scenario (bulk-demolish over a Class B mini, hover over a
  /// flourish, cross-faction template strip) is the regression
  /// signal. Players including the line in bug reports lets us
  /// distinguish "Keystone never loaded" from "Keystone loaded but
  /// patch X never fired."</para>
  ///
  /// <para>Thread-safety: Harmony patches run on the Unity main
  /// thread under normal use; this helper does not synchronise.
  /// If we ever need patches to run from background threads, swap
  /// the <see cref="HashSet{T}"/> for a <c>ConcurrentDictionary</c>.</para>
  /// </summary>
  internal static class PatchInvocationLog {

    private static readonly HashSet<string> Seen = new();

    /// <summary>
    /// Log a one-time first-invocation line for <paramref name="patchName"/>.
    /// Subsequent calls with the same name are silent.
    /// </summary>
    public static void Once(string patchName) {
      if (!Seen.Add(patchName)) return;
      KeystoneLog.Verbose($"[Keystone] Harmony patch first invocation: {patchName}");
    }

  }

}
