using System.Reflection;
using Keystone.Mod.Diagnostics;
using Timberborn.Growing;
using Timberborn.TimeSystem;

namespace Keystone.Mod.Flora {

  /// <summary>
  /// Reflection helper for fast-forwarding a <see cref="Growable"/>'s
  /// private <c>_timeTrigger</c> to maturity. Used by the three dev
  /// placement tools so a click-spawn produces visible adult flora
  /// rather than a tiny seedling.
  ///
  /// <para><b>Why a shared helper.</b> Three call sites
  /// (<see cref="Keystone.Mod.Flourish.FlourishPlacementTool"/>,
  /// <see cref="VanillaFloraPlacementTool"/>,
  /// <see cref="CrossFactionFloraPlacementTool"/>) were doing the
  /// same reflection inline. Pulling it here also gives us one place
  /// to emit a one-time warning if the field disappears in a future
  /// Timberborn update — the inline copies silently no-op'd, which
  /// would leave the dev-tool maturity fast-forward quietly broken
  /// across a game version.</para>
  ///
  /// <para>Documented in <c>docs/timberborn-api.md</c> § "Adult-spawn
  /// via Growable._timeTrigger".</para>
  /// </summary>
  internal static class GrowableTimeTriggerAccessor {

    #region Fields

    private static readonly FieldInfo? Field =
        typeof(Growable).GetField(
            "_timeTrigger", BindingFlags.Instance | BindingFlags.NonPublic);

    private static bool _warnedMissing;

    #endregion

    #region Public API

    /// <summary>
    /// Fast-forward <paramref name="growable"/> to maturity if the
    /// private <c>_timeTrigger</c> field is still reachable. Logs a
    /// single warning on the first invocation where the field can't
    /// be located (Timberborn API change), then silently no-ops on
    /// subsequent calls. No-op if <paramref name="growable"/> is null.
    /// </summary>
    public static void FastForwardToMature(Growable? growable) {
      if (growable == null) return;
      if (Field == null) {
        WarnMissingFieldOnce();
        return;
      }
      if (Field.GetValue(growable) is ITimeTrigger trigger) {
        trigger.FastForwardProgress(trigger.DaysLeft);
      }
    }

    /// <summary>
    /// True if <paramref name="growable"/> is still maturing — its
    /// private <c>_timeTrigger</c> is reachable and reports
    /// <see cref="ITimeTrigger.InProgress"/>. Returns <c>false</c>
    /// for a null growable, a growable with an unreachable time
    /// trigger (logs the same one-time warning as
    /// <see cref="FastForwardToMature"/>), or a growable whose timer
    /// has finished. Used by <c>ClassDSpawnHandler</c>'s same-chunk
    /// seedling gate.
    /// </summary>
    public static bool IsImmature(Growable? growable) {
      if (growable == null) return false;
      if (Field == null) {
        WarnMissingFieldOnce();
        return false;
      }
      return Field.GetValue(growable) is ITimeTrigger trigger && trigger.InProgress;
    }

    #endregion

    #region Helpers

    private static void WarnMissingFieldOnce() {
      if (_warnedMissing) return;
      _warnedMissing = true;
      KeystoneLog.Warn(
          "[Keystone] Growable._timeTrigger field not found via reflection -- " +
          "dev-tool seedlings will not be fast-forwarded to maturity. " +
          "Timberborn API change?");
    }

    #endregion

  }

}
