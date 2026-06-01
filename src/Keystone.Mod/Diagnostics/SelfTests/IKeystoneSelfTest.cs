using System.Collections.Generic;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Outcome of a single self-test invocation. Four states:
  /// <list type="bullet">
  ///   <item><see cref="Pass"/> — the test ran and every assertion held.</item>
  ///   <item><see cref="Fail"/> — the test ran and at least one assertion
  ///         did not hold. <see cref="SelfTestResult.Message"/> describes
  ///         what was off; <see cref="SelfTestResult.Detail"/> carries
  ///         the technical specifics.</item>
  ///   <item><see cref="Warning"/> — the test ran and surfaced something
  ///         worth the developer's attention, but the condition has
  ///         legitimate explanations (e.g. a list of building names
  ///         where some don't resolve because they belong to other
  ///         factions). Not a failure — distinguished from
  ///         <see cref="Pass"/> so the panel can flag the situation
  ///         without sounding the alarm.</item>
  ///   <item><see cref="Skipped"/> — the test couldn't run in the current
  ///         game state (e.g. "no Keystone entities currently spawned, so
  ///         the describer invocation pass has nothing to verify").
  ///         <see cref="SelfTestResult.Message"/> tells the developer what
  ///         to do to make the test runnable. Distinguished from
  ///         <see cref="Pass"/> so a clean run doesn't masquerade as
  ///         coverage when nothing was actually exercised.</item>
  /// </list>
  /// </summary>
  public enum SelfTestStatus {
    Pass,
    Fail,
    Warning,
    Skipped,
  }

  /// <summary>
  /// Outcome of one <see cref="IKeystoneSelfTest"/> invocation.
  ///
  /// <para><b>Message vs. Detail.</b> <see cref="Message"/> is the one-line
  /// summary rendered next to the test name in the panel
  /// (e.g. "3 of 14 specs missing required Sources entry"). <see cref="Detail"/>
  /// is the optional multi-line technical follow-up rendered indented
  /// underneath when the test failed (the specific spec ids, the
  /// adapter return values, the deserializer stack trace). Pass with
  /// no surprises = both empty.</para>
  /// </summary>
  public readonly record struct SelfTestResult(
      SelfTestStatus Status,
      string Message = "",
      string Detail = "") {

    /// <summary>Convenience constructor for the common "passed cleanly"
    /// case where there's nothing extra to say.</summary>
    public static SelfTestResult Pass(string message = "OK") =>
        new(SelfTestStatus.Pass, message);

    /// <summary>Convenience constructor for the common "failed with a
    /// reason and optional details" case.</summary>
    public static SelfTestResult Fail(string message, string detail = "") =>
        new(SelfTestStatus.Fail, message, detail);

    /// <summary>Convenience constructor for the "ran cleanly but
    /// flagged something worth a look" case. Use when the condition
    /// has legitimate explanations (e.g. cross-faction absences) but
    /// the developer should still see the list.</summary>
    public static SelfTestResult Warn(string message, string detail = "") =>
        new(SelfTestStatus.Warning, message, detail);

    /// <summary>Convenience constructor for the "couldn't run in this
    /// game state" case. Skipped is not a failure; surface what would
    /// make the test runnable.</summary>
    public static SelfTestResult Skipped(string message) =>
        new(SelfTestStatus.Skipped, message);
  }

  /// <summary>
  /// Developer-facing integration regression test. Runs only when the
  /// developer clicks the Run button in the Test tab of the Keystone
  /// window (<see cref="KeystonePerfWindow"/>). Each implementation
  /// verifies one slice of Keystone's integration with Timberborn that
  /// can't be covered by a <c>Keystone.Core.Tests</c> unit test —
  /// typically because it would require mocking large swathes of the
  /// game engine.
  ///
  /// <para><b>Audience.</b> The developer. False positives are tolerable;
  /// the developer reads the report and decides. Phrase
  /// <see cref="SelfTestResult.Message"/> and <see cref="SelfTestResult.Detail"/>
  /// for someone who knows the codebase, not for a player.</para>
  ///
  /// <para><b>Distinct from
  /// <see cref="Keystone.Mod.Diagnostics.StartupChecks.IStartupCheck"/>.</b>
  /// StartupChecks verify the player's *environment* (dependencies,
  /// Harmony patch targets, save-state shape) and auto-run on load with
  /// a brief popup. Self-tests verify *our own* integration code
  /// (deserialization round-trips, modifier matching, describer
  /// invocations, adapter sanity) and run only on manual trigger with a
  /// dense report. Different audience, different false-positive
  /// tolerance, different output format. See the memory note
  /// <c>project-two-test-systems</c> for the full rationale.</para>
  /// </summary>
  public interface IKeystoneSelfTest {

    /// <summary>Short, human-readable name for the test row in the
    /// panel (e.g. "Spec round-trip", "Modifier suffix match").
    /// Keep terse — the report has many rows.</summary>
    string Name { get; }

    /// <summary>Category label used to group tests in the rendered
    /// report (e.g. "Specs", "Wiring", "Adapters", "Describers"). Use a
    /// stable short string; tests sharing a category are rendered
    /// under one heading.</summary>
    string Category { get; }

    /// <summary>Run the test. Should be safe to invoke at any point
    /// while the game is loaded — no entity placement, no save/load
    /// mutation, no destructive side effects. Returns one result. If
    /// the test inherently exercises multiple sub-cases, fold them
    /// into one result whose <see cref="SelfTestResult.Detail"/>
    /// lists the per-case outcomes.</summary>
    SelfTestResult Run();

  }

  /// <summary>Test result aggregated across all bound
  /// <see cref="IKeystoneSelfTest"/> instances, in the order a runner
  /// iterated them. Returned by <see cref="SelfTestRunner.RunAll"/>.</summary>
  public readonly record struct SelfTestReport(
      IReadOnlyList<(IKeystoneSelfTest Test, SelfTestResult Result)> Rows,
      int PassCount,
      int FailCount,
      int WarningCount,
      int SkippedCount);

}
