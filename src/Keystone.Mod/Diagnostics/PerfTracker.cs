using System;
using System.Collections.Generic;
using System.Diagnostics;
using Keystone.Core.Diagnostics;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Central dispatcher for per-scope timing measurements. Subsystems
  /// inject this and wrap work-doing methods with
  /// <see cref="Track"/> + <c>using var</c>; the returned
  /// <see cref="Scope"/> records elapsed time to the named
  /// <see cref="PerfStats"/> in this tracker on <c>Dispose</c>.
  ///
  /// <para><b>Allocation discipline.</b> <see cref="Track"/> allocates
  /// only on first encounter of a new name (lazy <see cref="PerfStats"/>
  /// creation). Steady-state per-scope cost is two <see cref="Stopwatch.GetTimestamp"/>
  /// calls plus a struct-typed value-return — no heap allocation, no
  /// boxing (the <c>using var</c> pattern with a <c>readonly struct</c>
  /// is non-virtual). Subsystems can wrap their hot paths without
  /// worrying about the tracker introducing the very perf hazard
  /// it's there to detect.</para>
  ///
  /// <para><b>Threading.</b> Single-threaded by design — Bindito
  /// singletons run on the Unity main thread (<c>Tick</c>,
  /// <c>UpdateSingleton</c>, <c>GetText</c> all dispatch there). No
  /// locking. Don't introduce concurrent <see cref="Track"/> callers
  /// without revisiting this assumption.</para>
  ///
  /// <para><b>Wrap after early-returns.</b> Convention: subsystems
  /// place the <c>using</c> line <i>after</i> their early-return guards
  /// (uninitialised, no work to do, dt &lt;= 0). The recorded average
  /// then reflects "when work happens, how long does it take" rather
  /// than "average across all gated entries including no-ops" — the
  /// former is what perf budgeting actually cares about.</para>
  /// </summary>
  public sealed class PerfTracker : IPerfScope {

    #region Fields

    private readonly Dictionary<string, PerfStats> _stats = new();
    private readonly List<KeyValuePair<string, double>> _oneShots = new();
    private readonly Dictionary<string, double> _latestValues = new();
    private readonly List<string> _latestValueOrder = new();

    #endregion

    #region Properties

    /// <summary>
    /// All stats indexed by scope name. Read-only contract for
    /// consumers (the panel) — the tracker mutates on <see cref="Track"/>.
    /// </summary>
    public IReadOnlyDictionary<string, PerfStats> Snapshot => _stats;

    /// <summary>
    /// One-shot timings recorded via <see cref="RecordOnce"/>, in
    /// insertion order. Kept separate from <see cref="Snapshot"/> so
    /// the panel can render them plainly (single ms value, no
    /// avg/P99/Hz columns -- those columns are meaningless for samples
    /// that fire exactly once per load).
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, double>> OneShots => _oneShots;

    /// <summary>
    /// Most-recent value for each label posted via
    /// <see cref="RecordLatest"/>, in first-seen order. Same idea as
    /// <see cref="OneShots"/> — a plain "label: N ms" row — but
    /// subsequent calls for the same label REPLACE the value rather
    /// than appending a new row. Use for periodic per-cycle totals
    /// where the most recent value is what matters (e.g. "fauna
    /// spawn cost for the most recent game-day"), not the full
    /// history.
    /// </summary>
    public IReadOnlyDictionary<string, double> LatestValues => _latestValues;

    /// <summary>Stable rendering order for <see cref="LatestValues"/>:
    /// first time a label is recorded, it's appended here; subsequent
    /// recordings update the dict without reordering. Lets the panel
    /// render a clean fixed-position row per label.</summary>
    public IReadOnlyList<string> LatestValueOrder => _latestValueOrder;

    #endregion

    #region Public API

    /// <summary>
    /// Begin tracking a named scope. Returns a struct-typed scope; on
    /// <c>Dispose</c> the elapsed time is recorded against
    /// <paramref name="name"/>'s stats. Lazily creates a
    /// <see cref="PerfStats"/> on first use.
    /// </summary>
    public Scope Track(string name) {
      var stats = GetOrCreate(name, PerfStatsKind.Timer);
      return new Scope(stats, Stopwatch.GetTimestamp());
    }

    /// <summary>Explicit <see cref="IPerfScope.Track"/> implementation.
    /// Boxes the value-typed <see cref="Scope"/> to <see cref="IDisposable"/>;
    /// reserved for Core-side consumers (e.g. <c>RollingSweep&lt;TUnit&gt;</c>)
    /// that need the abstraction. Mod-side hot paths should call the
    /// non-interface overload above to avoid the boxing.</summary>
    IDisposable IPerfScope.Track(string name) => Track(name);

    /// <summary>
    /// Record an already-measured elapsed time without using the
    /// scope-pattern. Provided for testability and edge cases where
    /// a <c>using</c> block doesn't fit cleanly.
    /// </summary>
    public void Record(string name, double elapsedMs) {
      var stats = GetOrCreate(name, PerfStatsKind.Timer);
      stats.Add(elapsedMs, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Record a unit count (not a duration) against
    /// <paramref name="name"/>. Tags the scope as a
    /// <see cref="PerfStatsKind.Counter"/> on first use so the panel
    /// renders it in the count table and keeps it out of the
    /// millisecond cost aggregates. See <see cref="IPerfScope.RecordCount"/>.
    /// </summary>
    public void RecordCount(string name, long count) {
      var stats = GetOrCreate(name, PerfStatsKind.Counter);
      stats.Add(count, Stopwatch.GetTimestamp());
    }

    /// <summary>Fetch the stats for <paramref name="name"/>, lazily
    /// creating it with <paramref name="kind"/> on first encounter.
    /// Throws if a name previously seen as one kind is now used as the
    /// other — that's a call-site bug (the same scope recorded as both
    /// a duration and a count), and silently tolerating it would put
    /// the row in the wrong table and corrupt the cost aggregates.</summary>
    private PerfStats GetOrCreate(string name, PerfStatsKind kind) {
      if (!_stats.TryGetValue(name, out var stats)) {
        stats = new PerfStats(kind);
        _stats[name] = stats;
        return stats;
      }
      if (stats.Kind != kind) {
        throw new System.InvalidOperationException(
            $"Perf scope '{name}' was first recorded as {stats.Kind} but is now "
            + $"being used as {kind}. A scope name must be consistently a timer "
            + "(Track/Record) or a counter (RecordCount), never both.");
      }
      return stats;
    }

    /// <summary>
    /// Record a one-shot timing (e.g. a load-time cost measured once
    /// per session). Stored separately from the per-cycle stats so
    /// the panel can render it as a plain "label: N ms" row without
    /// the avg/P99/Hz columns that have no meaning for a single sample.
    /// Multiple calls with the same label append additional rows --
    /// the tracker doesn't dedupe; callers control row order.
    /// </summary>
    public void RecordOnce(string label, double elapsedMs) {
      _oneShots.Add(new KeyValuePair<string, double>(label, elapsedMs));
    }

    /// <summary>
    /// Record (or replace) a labeled value that's updated periodically
    /// — e.g. "fauna spawn cost over the most recent game-day."
    /// Subsequent calls with the same label overwrite the previous
    /// value; the panel renders one row per unique label. Use this
    /// when the value has a natural rhythm (per game-day, per cycle)
    /// and the most recent observation is the one that matters.
    /// First-seen order is preserved for stable row positions.
    /// </summary>
    public void RecordLatest(string label, double valueMs) {
      if (!_latestValues.ContainsKey(label)) {
        _latestValueOrder.Add(label);
      }
      _latestValues[label] = valueMs;
    }

    /// <summary>
    /// Drop all rolling per-scope stats, counters, and latest-value
    /// rows, resetting them to empty. Used by the perf window's "Clear"
    /// button to re-baseline the tables after the cold load/prewarm
    /// window has passed, so steady-state avg/P99/max aren't skewed by
    /// the first cold samples for the rest of the session. Scope rows
    /// re-add lazily (with their <see cref="PerfStatsKind"/> re-derived)
    /// the next time each scope fires.
    ///
    /// <para><b>Deliberately preserves <see cref="OneShots"/>.</b> The
    /// load-time one-shot costs fire exactly once per session and can't
    /// be regenerated mid-game; they're already segregated from the
    /// rolling tables and don't skew them, so a re-baseline keeps them
    /// as a reference rather than throwing them away.</para>
    ///
    /// <para>Main-thread only, like all recording — no locking.</para>
    /// </summary>
    public void Clear() {
      _stats.Clear();
      _latestValues.Clear();
      _latestValueOrder.Clear();
    }

    #endregion

    #region Scope

    /// <summary>
    /// Disposable struct that records elapsed time against a captured
    /// <see cref="PerfStats"/>. Caches the stats reference at
    /// construction so <see cref="Dispose"/> doesn't re-look-it-up.
    /// </summary>
    public readonly struct Scope : IDisposable {

      private readonly PerfStats _stats;
      private readonly long _startTicks;

      internal Scope(PerfStats stats, long startTicks) {
        _stats = stats;
        _startTicks = startTicks;
      }

      /// <inheritdoc />
      public void Dispose() {
        var endTicks = Stopwatch.GetTimestamp();
        var elapsedMs = (endTicks - _startTicks) * 1000.0 / Stopwatch.Frequency;
        _stats.Add(elapsedMs, endTicks);
      }

    }

    #endregion

  }

}
