using System;
using System.Collections.Generic;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Collects every bound <see cref="IKeystoneSelfTest"/> and runs them
  /// all, returning an aggregated <see cref="SelfTestReport"/>. Owned by
  /// <see cref="KeystonePerfWindow"/>'s Test tab; called when the
  /// developer clicks the Run button.
  ///
  /// <para><b>Failure isolation.</b> A test that throws is treated as a
  /// failed test, not a runner crash — the exception is captured into
  /// the result's <see cref="SelfTestResult.Detail"/> and the runner
  /// moves to the next test. One broken test must not prevent the rest
  /// of the battery from reporting.</para>
  /// </summary>
  public sealed class SelfTestRunner {

    private readonly IEnumerable<IKeystoneSelfTest> _tests;

    public SelfTestRunner(IEnumerable<IKeystoneSelfTest> tests) {
      _tests = tests;
    }

    /// <summary>Run every test in iteration order. Each test's outcome
    /// is appended to the report whether it passed, failed, was
    /// skipped, or threw.</summary>
    public SelfTestReport RunAll() {
      var rows = new List<(IKeystoneSelfTest, SelfTestResult)>();
      var passes = 0;
      var fails = 0;
      var warnings = 0;
      var skips = 0;

      foreach (var test in _tests) {
        SelfTestResult result;
        try {
          result = test.Run();
        }
        catch (Exception ex) {
          // A test throwing is a failure of the test (or of the
          // subject) — never a runner crash. Capture the exception
          // text into Detail; the developer can see the type/message
          // in the panel without grepping Player.log.
          result = SelfTestResult.Fail(
              "Threw " + ex.GetType().Name + ": " + ex.Message,
              ex.ToString());
        }

        switch (result.Status) {
          case SelfTestStatus.Pass: passes++; break;
          case SelfTestStatus.Fail: fails++; break;
          case SelfTestStatus.Warning: warnings++; break;
          case SelfTestStatus.Skipped: skips++; break;
        }
        rows.Add((test, result));
      }

      return new SelfTestReport(rows, passes, fails, warnings, skips);
    }

  }

}
