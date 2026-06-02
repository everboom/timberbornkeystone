using System.Collections.Generic;
using System.Text;
using Timberborn.CoreUI;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.Diagnostics.StartupChecks {

  /// <summary>
  /// Runs every bound <see cref="IStartupCheck"/> once, on the first
  /// game <see cref="Tick"/> after load, collates the findings, and
  /// shows a modal dialog summarising anything that looks wrong.
  ///
  /// <para><b>Dev vs. user mode.</b> "Always show" is wired to
  /// <see cref="KeystoneDevMode.IsEnabled"/>: on a dev machine (where
  /// the deploy target dropped the <c>keystone-dev.flag</c> sentinel)
  /// the dialog appears after every load so we can iterate on the
  /// checks themselves; in a release deploy the dialog only appears
  /// when at least one finding was produced.</para>
  ///
  /// <para><b>Why <c>IUpdatableSingleton</c> and not
  /// <c>IPostLoadableSingleton</c> or <c>ITickableSingleton</c>.</b>
  /// Bindito's PostLoad order isn't deterministic, so a PostLoad-time
  /// check could race the loaders it inspects and falsely report
  /// empty catalogs. Game ticks (<c>ITickableSingleton.Tick</c>) only
  /// fire while the simulation is running, but a freshly loaded save
  /// often starts paused -- so a tick-driven reporter never fires
  /// until the player presses play. <c>IUpdatableSingleton.UpdateSingleton</c>
  /// runs every Unity frame unconditionally, after PostLoad and
  /// independent of pause state. We pair it with a readiness poll on
  /// each <see cref="IStartupCheck"/> so the dialog reflects the
  /// post-PostLoad state of every catalog regardless of when the
  /// player un-pauses.</para>
  ///
  /// <para><b>One-shot.</b> Bindito doesn't let an
  /// <c>IUpdatableSingleton</c> unregister itself from the update
  /// list, so we self-gate with a <see cref="_hasRun"/> flag. The
  /// cost is one bool check per frame forever -- negligible.</para>
  /// </summary>
  public sealed class StartupReporter : IUpdatableSingleton {

    /// <summary>While <c>true</c>, the dialog appears on every load
    /// even when no findings were produced (it shows
    /// "No issues detected." instead). Useful during development to
    /// confirm the checks are actually running. When <c>false</c> we
    /// run in "warn only" mode -- silent on the happy path, dialog
    /// only when something is off. Player.log always carries the
    /// structured summary either way. Wired to
    /// <see cref="KeystoneDevMode.IsEnabled"/> so it's on for the
    /// local dev deploy and off for release.</summary>
    private static bool AlwaysShow => KeystoneDevMode.IsEnabled;

    private readonly IEnumerable<IStartupCheck> _checks;
    private readonly DialogBoxShower _dialogs;
    private bool _hasRun;

    public StartupReporter(IEnumerable<IStartupCheck> checks, DialogBoxShower dialogs) {
      _checks = checks;
      _dialogs = dialogs;
    }

    /// <inheritdoc />
    public void UpdateSingleton() {
      if (_hasRun) return;
      // Outermost try/catch: StartupReporter IS the safety surface
      // that tells users about every other failure, so if THIS method
      // throws we lose the bridge between problems and user awareness.
      // A failure here goes only to Player.log -- there's no dialog
      // path left to surface it through. _hasRun is set inside the
      // try so a throw doesn't permanently jam the reporter (it'll
      // retry next frame, which is the right behaviour if a
      // dependency's IsReady transiently returned a bad answer).
      try {
        // Defer until every check declares itself ready. Each check
        // tracks its own dependencies' IsLoaded flags; in practice this
        // resolves on the very first UpdateSingleton call, since by then
        // PostLoad has completed for all singletons. The polling here is
        // defensive against any constituent whose load lands later than
        // expected -- in that case we log which check is holding things
        // up (at most once per check) so a stuck flag is debuggable.
        _framesDeferred++;
        foreach (var check in _checks) {
          if (!check.IsReady) {
            if (_notReadyLogged.Add(check.GetType().Name)) {
              KeystoneLog.Verbose(
                  $"[Keystone] StartupReporter deferred: " +
                  $"{check.GetType().Name}.IsReady is false (will retry).");
            }
            // Stuck-check escalation: a check that hasn't gone ready
            // after StuckCheckFrameThreshold frames is almost certainly
            // never going to. Promote the per-check Verbose to a Warn
            // (once per stuck check) so a release player with a stuck
            // load has evidence in Player.log of WHICH check is
            // blocking the dialog -- otherwise we just defer silently
            // forever.
            if (_framesDeferred >= StuckCheckFrameThreshold
                && _stuckCheckWarned.Add(check.GetType().Name)) {
              KeystoneLog.Warn(
                  $"[Keystone] StartupReporter is stuck on " +
                  $"{check.GetType().Name}.IsReady (false after {_framesDeferred} frames). " +
                  "The startup-report dialog can't fire until this resolves. " +
                  "Likely cause: the check's dependency (a loader / catalog / patch) failed " +
                  "to complete and didn't surface the failure via KeystoneIntegrationHealth.");
            }
            return;
          }
        }
        RunAndReport();
        // _hasRun set AFTER RunAndReport returns so a throw inside
        // RunAndReport leaves _hasRun false and the reporter retries
        // on the next frame. The catch below logs once per session
        // (rate-limited) so the retry doesn't spam Player.log.
        _hasRun = true;
      } catch (System.Exception ex) {
        if (!_outerFailureLogged) {
          _outerFailureLogged = true;
          KeystoneLog.Error(
              "[Keystone] StartupReporter.UpdateSingleton threw at the safety-surface level: "
              + ex
              + ". The startup-report dialog won't fire; check this stack trace -- if "
              + "the safety surface is broken, every other diagnostic is invisible.");
        }
      }
    }

    /// <summary>One-shot rate-limit so a persistent failure inside
    /// the safety surface doesn't spam Player.log frame after frame.
    /// One log line per session is enough to tell the developer the
    /// reporter itself is broken.</summary>
    private bool _outerFailureLogged;

    private readonly HashSet<string> _notReadyLogged = new();
    private readonly HashSet<string> _stuckCheckWarned = new();
    private int _framesDeferred;

    /// <summary>UpdateSingleton fires per Unity frame (~60/s). 300
    /// frames is ~5 seconds of real time -- past that, a check
    /// that's still not ready almost certainly never will be, and
    /// the developer / player needs to see WHICH one is stuck.</summary>
    private const int StuckCheckFrameThreshold = 300;

    private void RunAndReport() {
      var findingsByCategory = new List<(string Category, List<StartupFinding> Findings)>();
      var totalFindings = 0;
      foreach (var check in _checks) {
        var bucket = new List<StartupFinding>();
        foreach (var finding in check.Run()) {
          bucket.Add(finding);
        }
        if (bucket.Count > 0) {
          findingsByCategory.Add((check.Category, bucket));
          totalFindings += bucket.Count;
        }
      }

      // Count actionable severities separately so the dialog can stay
      // quiet when only Notes are present. Notes represent informational
      // outcomes the player doesn't need to act on (e.g. recovery via
      // representative-surface fallback succeeded -- nothing's wrong,
      // the load just took a non-default code path).
      var actionableFindings = 0;
      foreach (var (_, findings) in findingsByCategory) {
        for (var i = 0; i < findings.Count; i++) {
          if (findings[i].Severity != StartupFindingSeverity.Note) {
            actionableFindings++;
          }
        }
      }

      if (totalFindings == 0) {
        // Happy path: one Info line confirming the mod is healthy.
        // This is the single non-verbose log line Keystone produces
        // per load; everything else is gated behind KeystoneLog.IsVerbose.
        KeystoneLog.Info("[Keystone] Loaded successfully.");
      } else {
        // Each finding fires its own Info / Warn / Error log so it
        // survives verbose-mode-off and remains visible to anyone
        // reading the log to diagnose a player report. The dialog adds
        // the user-facing surface for the same data.
        foreach (var (category, findings) in findingsByCategory) {
          foreach (var f in findings) {
            // Log always carries the full diagnostic context (Message
            // + DetailedMessage) regardless of dev mode -- a bug
            // report should never need a dev-mode-on rerun.
            var line = $"[Keystone] [{category}] {f.Message}";
            if (!string.IsNullOrEmpty(f.DetailedMessage)) {
              line += " " + f.DetailedMessage;
            }
            switch (f.Severity) {
              case StartupFindingSeverity.Error:
                KeystoneLog.Error(line);
                break;
              case StartupFindingSeverity.Warning:
                KeystoneLog.Warn(line);
                break;
              default:
                KeystoneLog.Info(line);
                break;
            }
          }
        }
      }

      // Dialog gate: open whenever (a) there's something the player
      // needs to act on (Warning or Error finding present), (b)
      // AlwaysShow is enabled for dev iteration, or (c) verbose mode is
      // on so the developer wants to see Notes surfaced too. Pure-Note
      // findings in default mode log to player.log but stay out of the
      // player's face -- nothing to act on, no need for a popup that
      // reads as "something's wrong" when nothing actually is.
      var shouldShow =
          actionableFindings > 0
          || AlwaysShow
          || (totalFindings > 0 && KeystoneLog.IsVerbose);
      if (!shouldShow) return;

      _dialogs.Create()
              .SetMessage(BuildDialogMessage(findingsByCategory, totalFindings))
              .Show();
    }

    private static string BuildDialogMessage(
        List<(string Category, List<StartupFinding> Findings)> findingsByCategory,
        int totalFindings) {
      var sb = new StringBuilder();
      // Lead with an unmistakable identifier so the player can tell at
      // a glance which mod is talking to them. DialogBoxShower doesn't
      // expose a separate title field, so the heading lives inside the
      // message body.
      sb.AppendLine("=== Keystone Startup Report ===");
      sb.AppendLine();
      if (totalFindings == 0) {
        sb.Append("No issues detected.");
        return sb.ToString();
      }
      sb.Append("Found ").Append(totalFindings).AppendLine(" potential issue(s):");
      foreach (var (category, findings) in findingsByCategory) {
        sb.AppendLine();
        sb.Append("[").Append(category).AppendLine("]");
        foreach (var f in findings) {
          sb.Append("  ").Append(SeverityTag(f.Severity)).Append(' ').AppendLine(f.Message);
          // DetailedMessage is the developer-facing follow-up: it names
          // internal types, counts, and root-cause hints (ITerrainQuery,
          // BiomeLevelTable, "footprint+Z", exception types) -- jargon a
          // player can't act on and that reads like a crash dump in the
          // dialog. Show it ONLY in dev mode, matching the IStartupCheck
          // contract. The bug-report fingerprint isn't lost: RunAndReport
          // logs Message + DetailedMessage to Player.log unconditionally
          // (see above), so a report that includes the log still carries
          // the specifics. The release dialog stays plain-English.
          if (KeystoneDevMode.IsEnabled && !string.IsNullOrEmpty(f.DetailedMessage)) {
            sb.Append("    ").AppendLine(f.DetailedMessage);
          }
        }
      }
      return sb.ToString().TrimEnd();
    }

    private static string SeverityTag(StartupFindingSeverity s) => s switch {
      StartupFindingSeverity.Error => "ERROR:",
      StartupFindingSeverity.Warning => "Warning:",
      _ => "Note:",
    };

  }

}
