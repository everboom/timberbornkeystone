using System.Collections.Generic;

namespace Keystone.Mod.Diagnostics.StartupChecks {

  /// <summary>
  /// Severity of a single self-check finding. Three levels:
  /// <list type="bullet">
  ///   <item><see cref="Note"/> — informational: something happened
  ///         worth surfacing but it doesn't suggest a problem. Used
  ///         for "we hit a fallback path that did its job."</item>
  ///   <item><see cref="Warning"/> — looks off but not necessarily
  ///         broken. The mod will keep running; the player should
  ///         know but doesn't need to act.</item>
  ///   <item><see cref="Error"/> — a hard expectation failed. The
  ///         mod is likely degraded; investigation needed.</item>
  /// </list>
  /// </summary>
  public enum StartupFindingSeverity {
    Note,
    Warning,
    Error,
  }

  /// <summary>One observation about Keystone's load-time state worth
  /// surfacing to the player.
  ///
  /// <para><see cref="Message"/> is the user-facing line: plain
  /// English, no jargon, brief enough to read in a popup. Phrase it
  /// so a player who has never read the source can act on it (or at
  /// least file a useful bug).</para>
  ///
  /// <para><see cref="DetailedMessage"/> (optional) is the developer-
  /// facing follow-up: counts, internal terms, root-cause hints. It
  /// is appended to <see cref="Message"/> only in dev mode (where
  /// <see cref="KeystoneDevMode.IsEnabled"/> is true). In release
  /// mode, the dialog shows only <see cref="Message"/> -- but
  /// Player.log always carries both, so a bug report retains the
  /// full diagnostic context.</para></summary>
  public readonly record struct StartupFinding(
      StartupFindingSeverity Severity,
      string Message,
      string? DetailedMessage = null);

  /// <summary>
  /// Per-subsystem self-check, executed once after PostLoad. Each
  /// check yields findings only when something looks wrong; "nothing
  /// to report" is the silent default. Aggregated by
  /// <see cref="StartupReporter"/>.
  ///
  /// <para><b>Goal.</b> Catch the class of failure mode where a
  /// subsystem silently misses its target -- a JSON-property rename
  /// that produces an empty catalog, a Harmony patch whose target
  /// vanished in a Timberborn update, a save-file decode that came
  /// back empty -- so the player isn't left wondering why nothing
  /// is happening in their game.</para>
  ///
  /// <para><b>Don't report success.</b> A check that always emits a
  /// "0 problems" finding would defeat the "only show the dialog
  /// when something's off" guarantee that's coming. Stay silent on
  /// the happy path.</para>
  /// </summary>
  public interface IStartupCheck {

    /// <summary>Short label for grouping findings in the dialog
    /// (e.g. <c>"Harmony"</c>, <c>"Catalogs"</c>, <c>"Save state"</c>).</summary>
    string Category { get; }

    /// <summary>True iff every dependency the check reads has finished
    /// its own load/post-load. While false on any single check,
    /// <see cref="StartupReporter"/> defers running every check to the
    /// next tick. Each implementation is responsible for declaring
    /// what it needs -- typically a conjunction of <c>IsLoaded</c>
    /// flags on the catalogs/loaders it reads.</summary>
    bool IsReady { get; }

    /// <summary>Run the check. Only called when <see cref="IsReady"/>
    /// returned true. Return zero findings if everything looks fine.</summary>
    IEnumerable<StartupFinding> Run();

  }

}
