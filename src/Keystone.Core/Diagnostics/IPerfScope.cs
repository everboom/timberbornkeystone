using System;

namespace Keystone.Core.Diagnostics {

  /// <summary>
  /// Minimal perf-tracking surface that Core-side amortising drivers
  /// (e.g. <c>Keystone.Core.Sweep.RollingSweep&lt;TUnit&gt;</c>) need.
  /// Mod-side <c>PerfTracker</c> implements this; tests can substitute
  /// a no-op or recording fake without bringing the full
  /// <c>PerfTracker</c> in.
  ///
  /// <para><b>Boxing note.</b> <see cref="Track"/> returns
  /// <see cref="IDisposable"/>, so any value-typed scope implementation
  /// is boxed at the interface boundary. Affordable for the rolling-
  /// sweep call sites (4 scopes per cycle, ≤24 cycles per game-day);
  /// hot per-tile paths should call <c>PerfTracker.Track</c> directly
  /// for the value-typed scope.</para>
  /// </summary>
  public interface IPerfScope {

    /// <summary>Open a named timing scope. Dispose the returned handle
    /// when the work being measured finishes.</summary>
    IDisposable Track(string name);

    /// <summary>Record an already-measured <b>duration</b> (ms) against
    /// <paramref name="name"/>'s stats. Used when the timed work doesn't
    /// fit a <c>using</c> block. For non-time samples (unit counts) use
    /// <see cref="RecordCount"/> instead, so the panel renders them in
    /// the counter table rather than mislabelling them as milliseconds.</summary>
    void Record(string name, double valueMs);

    /// <summary>Record a <b>unit count</b> (not a duration) against
    /// <paramref name="name"/>'s stats — e.g. "chunks drained this tick",
    /// "classify calls this flush". Tags the scope as a counter so the
    /// debug panel shows it in the count table (avg/P99/max as counts,
    /// no ms columns) and excludes it from the millisecond cost
    /// aggregates. A given name must be used with either
    /// <see cref="Record"/>/<see cref="Track"/> or this — never both.</summary>
    void RecordCount(string name, long count);

  }

}
