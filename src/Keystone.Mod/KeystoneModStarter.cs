using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Keystone.Mod.Diagnostics;
using Timberborn.ModManagerScene;

namespace Keystone.Mod {

  /// <summary>
  /// Earliest entry-point Timberborn offers a mod. Runs once at game
  /// startup, before any DI scope spins up — exactly the right place to
  /// apply Harmony patches so they're live when downstream
  /// <c>ILoadableSingleton.Load()</c> calls fire.
  ///
  /// <para>We use Harmony narrowly: cross-faction template mutation
  /// (<c>TemplateCollectionServicePatch</c>) plus three patches that
  /// make Keystone-ambient entities invisible to player interaction
  /// (selection retriever, selection service, demolish tool). Anything
  /// more invasive goes through the normal Bindito → adapter → port
  /// chain instead.</para>
  ///
  /// <para>After <c>PatchAll</c>, we log the applied patch count and
  /// the targeted methods. If a future Timberborn update breaks any of
  /// our <c>[HarmonyPatch]</c> resolutions (renamed methods, removed
  /// types, signature changes), the mismatch shows up loudly in the log
  /// rather than as silent breakage at runtime.</para>
  /// </summary>
  public sealed class KeystoneModStarter : IModStarter {

    /// <summary>Harmony id for our patch set. Distinct per assembly so multiple Keystone-style mods can coexist.</summary>
    public const string HarmonyId = "SylvanGames.Keystone";

    /// <summary>How many methods we expect <c>PatchAll</c> to touch.
    /// Update when adding/removing patches. A mismatch logs a warning
    /// at startup -- catches "patch silently no-op'd because Timberborn
    /// renamed the target" before it bites in-game.
    ///
    /// <para>Currently 8: template strip, NaturalResourceModel
    /// ShowCurrentModel, SelectableObjectRetriever, EntitySelection
    /// Service, resource-demolish ActionCallback + PreviewCallback,
    /// building-demolish PreviewCallback + ActionCallback.</para></summary>
    public const int ExpectedPatchedMethodCount = 8;

    #region Static patch report

    // IModStarter runs before any Bindito scope spins up, so we can't
    // hand the patch-application results to a singleton at PatchAll
    // time. Stash them in static fields instead; the
    // HarmonyStartupCheck reads these at PostLoad time when DI is
    // live. Marked nullable for clarity: null means StartMod hasn't
    // run yet (only happens in unit tests; in-game it always has).

    /// <summary>Number of methods Harmony actually patched on this
    /// session's <c>PatchAll</c>. <c>HarmonyStartupCheck</c>
    /// compares this to <see cref="ExpectedPatchedMethodCount"/>.</summary>
    public static int AppliedPatchCount { get; private set; }

    /// <summary>Fully-qualified names of the methods Harmony patched
    /// (one per <c>DeclaringType.FullName.MethodName</c>). Order
    /// follows Harmony's enumeration -- typically declaration order
    /// within the assembly, not semantically meaningful.</summary>
    public static IReadOnlyList<string> AppliedPatchTargets { get; private set; } = System.Array.Empty<string>();

    /// <summary>Exception thrown by <c>PatchAll</c>, or null if it
    /// succeeded. A non-null value means none of our patches landed.</summary>
    public static Exception? PatchAllException { get; private set; }

    #endregion

    /// <inheritdoc />
    public void StartMod(IModEnvironment modEnvironment) {
      // Initialise verbose logging from the dev-mode sentinel before
      // any other startup work, so HarmonyPatches and downstream load
      // sites all see the same gate. KeystoneDevMode.IsEnabled is
      // false on a clean release deploy; on the local dev machine the
      // build target drops the sentinel and this flips to true.
      // Runtime overrides (e.g. future mod-settings UI) can still
      // toggle IsVerbose independently after startup.
      KeystoneLog.IsVerbose = KeystoneDevMode.IsEnabled;

      var harmony = new Harmony(HarmonyId);
      try {
        harmony.PatchAll(typeof(KeystoneModStarter).Assembly);
      } catch (Exception ex) {
        PatchAllException = ex;
        KeystoneLog.Error($"[Keystone] Harmony PatchAll threw: {ex}");
        return;
      }
      VerifyPatchedMethods(harmony);
    }

    private static void VerifyPatchedMethods(Harmony harmony) {
      var ours = harmony.GetPatchedMethods()
          .Where(m => m != null
                      && Harmony.GetPatchInfo(m)?.Owners?.Contains(HarmonyId) == true)
          .ToList();

      var targets = new List<string>(ours.Count);
      var sb = new StringBuilder();
      sb.Append("[Keystone] Harmony applied ").Append(ours.Count)
        .Append(" patch(es) (expected ").Append(ExpectedPatchedMethodCount).AppendLine("):");
      foreach (var m in ours) {
        var full = $"{m.DeclaringType?.FullName}.{m.Name}";
        targets.Add(full);
        sb.Append("  ").AppendLine(full);
      }
      KeystoneLog.Verbose(sb.ToString());

      AppliedPatchCount = ours.Count;
      AppliedPatchTargets = targets;

      if (ours.Count != ExpectedPatchedMethodCount) {
        KeystoneLog.Warn(
            $"[Keystone] Harmony patch count mismatch: applied {ours.Count}, " +
            $"expected {ExpectedPatchedMethodCount}. " +
            "A target method may have been renamed/removed in a Timberborn update.");
      }
    }

  }

}
