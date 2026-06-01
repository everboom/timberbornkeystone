using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Keystone.Mod.Diagnostics.StartupChecks {

  /// <summary>
  /// Bridge between the mod-wide
  /// <see cref="KeystoneIntegrationHealth"/> aggregator and the
  /// startup-report dialog. Reads the recorded category buckets and
  /// emits one <see cref="StartupFinding"/> per category whose
  /// subsystem caught something during load / runtime.
  ///
  /// <para><b>Why this is one check rather than many.</b> Every
  /// subsystem that already logs a <c>KeystoneLog.Warn</c> for a
  /// log-and-continue path also calls
  /// <see cref="KeystoneIntegrationHealth.Record"/> with a category
  /// label. This check picks the records up automatically; we don't
  /// have to author a new <see cref="IStartupCheck"/> implementation
  /// per subsystem. Future log-and-continue sites get surfaced for
  /// free by adding the one-line Record call.</para>
  ///
  /// <para><b>Severity assignment.</b> Per-category: a hardcoded
  /// allow-list (<see cref="DialogWorthyCategories"/>) names the
  /// categories that mean "Keystone is in a broken state the user
  /// should know about." Those emit
  /// <see cref="StartupFindingSeverity.Warning"/>, which makes
  /// <see cref="StartupReporter"/> open the dialog. Everything else
  /// emits <see cref="StartupFindingSeverity.Note"/> -- the
  /// reporter logs an aggregate summary line for the player.log
  /// reader but does NOT open the dialog. The original constraint
  /// the startup-report system was built around is "popup only when
  /// the mod is broken." Coping with a third-party mod's malformed
  /// data is the OPPOSITE of broken -- it's exactly what the
  /// per-iteration isolation we added is supposed to do -- so those
  /// events go to log only.</para>
  ///
  /// <para><b>Message format.</b> User-facing line names the category
  /// and the counts ("Compatibility patch failed: 1 issue -- see
  /// Player.log for details."). Detailed message (logged always,
  /// shown in the dialog only in dev mode) lists the specific
  /// subjects with their occurrence counts so a bug report can name
  /// names.</para>
  /// </summary>
  internal sealed class IntegrationHealthCheck : IStartupCheck {

    /// <summary>Cap on subjects rendered inline in the user-facing
    /// detailed message body. Beyond this we say "...and N more --
    /// see Player.log" rather than overflowing the dialog.</summary>
    private const int MaxInlineSubjects = 10;

    /// <summary>Categories that mean "Keystone is in a broken state
    /// the user should know about" and therefore trigger the
    /// startup-report dialog. Everything OUTSIDE this set is treated
    /// as a coped-with-mess event: logged as an aggregate Note (so
    /// the per-category summary still lands in Player.log) but
    /// silent on the dialog. Keep the list short and load-bearing
    /// -- adding a category here makes a dialog popup happen on a
    /// nontrivial fraction of players' machines, so a category
    /// earns its place only if its presence means the player should
    /// take action (disable a mod, file a bug, investigate
    /// something).
    ///
    /// <para>Today the only dialog-worthy category is
    /// <c>"Compatibility patch failed"</c> -- our cross-faction
    /// patch's load-time work threw out, which means cross-faction
    /// flora won't render and any other cross-faction integration
    /// the patch normally handles is disabled. The user sees the
    /// degradation in-game and the dialog explains what happened.</para>
    ///
    /// <para>Skipped blueprints, malformed third-party specs,
    /// per-entity runtime catches, per-tile classification skips --
    /// all of these are "Keystone coped" events. They go to
    /// Player.log via <see cref="KeystoneLog.Warn"/> at the
    /// recording site AND are aggregated here as Notes, but they
    /// don't open the dialog.</para></summary>
    private static readonly HashSet<string> DialogWorthyCategories =
        new(System.StringComparer.Ordinal) {
            "Compatibility patch failed",
            // "Subsystem failed" is the catch-all category recorded by
            // the outermost try/catch on every Keystone-owned method
            // the host invokes (PostLoad, Load, UpdateSingleton, Tick,
            // InitializeEntity on SINGLETONS only -- per-entity
            // lifecycle catches use the "Per-entity *" categories,
            // which are log-only since one bad entity isn't a Keystone-
            // wide break). Any entry here means an entire subsystem
            // didn't run at all.
            "Subsystem failed",
        };

    /// <summary>Player-facing display names for dialog-worthy
    /// categories. The category strings in
    /// <see cref="DialogWorthyCategories"/> are developer-grep terms
    /// (short, machine-friendly, consistent across recording sites);
    /// the dialog needs phrasing a non-technical player can act on.
    /// Unmapped categories fall back to their raw name -- that means
    /// log-only Note categories never need translation (they don't
    /// appear in the dialog) and adding a new dialog-worthy category
    /// without a translation just degrades to "raw category: N issues"
    /// rather than silently breaking.</summary>
    private static readonly Dictionary<string, string> PlayerFriendlyCategoryNames =
        new(System.StringComparer.Ordinal) {
            ["Compatibility patch failed"] =
                "Cross-mod compatibility logic failed",
            ["Subsystem failed"] =
                "A Keystone subsystem failed to initialise",
        };

    private readonly KeystoneIntegrationHealth _health;

    public IntegrationHealthCheck(KeystoneIntegrationHealth health) {
      _health = health;
    }

    /// <inheritdoc />
    public string Category => "Integration health";

    /// <inheritdoc />
    /// <remarks>The aggregator is alive immediately after
    /// construction. Its records accumulate over the whole load
    /// pass; by the time every other check declares itself ready,
    /// every load-time recording has happened. We don't depend on
    /// any specific subsystem's <c>IsLoaded</c> flag because the
    /// aggregator's contents are inherently "whatever has happened
    /// so far" -- a deferred record won't be missed because the
    /// reporter only runs once and reads the live aggregator at that
    /// instant.</remarks>
    public bool IsReady => true;

    /// <inheritdoc />
    public IEnumerable<StartupFinding> Run() {
      if (!_health.HasIssues) yield break;

      foreach (var bucket in _health.Categories) {
        var severity = DialogWorthyCategories.Contains(bucket.Category)
            ? StartupFindingSeverity.Warning
            : StartupFindingSeverity.Note;
        yield return new StartupFinding(
            Severity: severity,
            Message: BuildMessage(bucket),
            DetailedMessage: BuildDetailedMessage(bucket));
      }
    }

    private static string BuildMessage(KeystoneIntegrationHealth.CategoryBucket bucket) {
      // Count phrasing: "3 unique issue(s), 600 total occurrence(s)"
      // when the two diverge, or just "3 issue(s)" when they match.
      // A divergence means a single subject failed many times (per-
      // tick on one entity) and the player should know the count is
      // not the same as the unique-thing count.
      var distinct = bucket.RetainedSubjectCount + bucket.OverflowSubjectCount;
      var occurrences = bucket.TotalOccurrences;
      var distinctNoun = distinct == 1 ? "issue" : "issues";
      string countPhrase;
      if (occurrences == distinct) {
        countPhrase = $"{distinct} {distinctNoun}";
      } else {
        countPhrase = $"{distinct} {distinctNoun}, {occurrences} total occurrence(s)";
      }
      // Translate the developer-grep category name into something a
      // non-technical player can parse. Unknown categories fall back
      // to the raw name (acceptable degradation for log-only Notes;
      // any dialog-worthy category should always have a translation).
      var displayCategory = PlayerFriendlyCategoryNames.TryGetValue(bucket.Category, out var friendly)
          ? friendly
          : bucket.Category;
      return $"{displayCategory}: {countPhrase}.";
    }

    private static string BuildDetailedMessage(KeystoneIntegrationHealth.CategoryBucket bucket) {
      var sb = new StringBuilder();
      var i = 0;
      foreach (var kv in bucket.SubjectCounts.OrderByDescending(kv => kv.Value)) {
        if (i >= MaxInlineSubjects) break;
        if (i > 0) sb.Append("; ");
        sb.Append(kv.Key);
        if (kv.Value > 1) sb.Append(" (x").Append(kv.Value).Append(')');
        i++;
      }
      var remaining = bucket.RetainedSubjectCount - i + bucket.OverflowSubjectCount;
      if (remaining > 0) {
        if (i > 0) sb.Append("; ");
        sb.Append("...and ").Append(remaining).Append(" more");
      }
      return sb.ToString();
    }

  }

}
