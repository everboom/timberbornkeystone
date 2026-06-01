using System;
using System.Diagnostics;

namespace Keystone.Core.Diagnostics {

  /// <summary>
  /// What kind of value a <see cref="PerfStats"/> accumulates. The
  /// debug panel renders the two kinds in separate tables with
  /// different column units, and excludes counters from the
  /// millisecond cost aggregates (a count summed as "ms of work" is
  /// meaningless). Set once at construction — a given scope name is
  /// only ever a timer or only ever a counter.
  /// </summary>
  public enum PerfStatsKind {

    /// <summary>Samples are durations in milliseconds (the default —
    /// every <c>Track</c> scope and every <c>Record</c> call).</summary>
    Timer,

    /// <summary>Samples are unit counts per recorded tick, not times
    /// (e.g. "chunks drained this tick", "classify calls this flush").
    /// Recorded via <c>RecordCount</c>.</summary>
    Counter,

  }

  /// <summary>
  /// Rolling-window timing stats for a single named scope. Backing
  /// store is a fixed-capacity ring buffer of the most recent
  /// <see cref="Capacity"/> samples; computed stats (average, P99,
  /// max, frequency) walk the live portion of the ring on demand.
  ///
  /// <para><b>Hot/cold split.</b> <see cref="Add"/> is the hot path
  /// and is allocation-free: two index writes plus a counter bump.
  /// The stat properties are cold paths — they're read by the debug
  /// panel at human cadence (a few times per second at most), so a
  /// per-call sort or scan is fine.</para>
  ///
  /// <para><b>P99 index choice.</b> <c>P99 = sample[ceil(0.99 * count) - 1]</c>
  /// after sorting the live portion ascending. For count=100 this
  /// returns the 99th-largest sample, which matches the conventional
  /// "99% of samples are at or below this value" framing.</para>
  ///
  /// <para><b>Frequency.</b> Derived from the <see cref="Stopwatch.GetTimestamp"/>
  /// span between the oldest and newest live samples. Returns 0 Hz
  /// when there are fewer than 2 samples (span undefined).</para>
  ///
  /// <para>This class is pure data and lives in Core for testability.
  /// The Mod-side <c>PerfTracker</c> dispatcher allocates one of these
  /// per tracked name and calls <see cref="Add"/> from a struct-typed
  /// using-scope's <c>Dispose</c>.</para>
  /// </summary>
  public sealed class PerfStats {

    #region Constants

    /// <summary>
    /// Ring-buffer size. ~40 seconds at 5 Hz tick rate (Timberborn's 1×
    /// time speed); shorter at faster speeds. Tunable.
    /// </summary>
    public const int Capacity = 200;

    #endregion

    #region Fields

    private readonly double[] _samples = new double[Capacity];
    private readonly long[] _timestamps = new long[Capacity];

    /// <summary>Total samples ever added; <see cref="SampleCount"/> caps this at <see cref="Capacity"/>.</summary>
    private int _totalAdded;

    /// <summary>Next write index (mod <see cref="Capacity"/>).</summary>
    private int _next;

    #endregion

    #region Construction

    /// <summary>Create a stats accumulator of the given
    /// <paramref name="kind"/>. Defaults to <see cref="PerfStatsKind.Timer"/>
    /// so existing timer call sites need no change.</summary>
    public PerfStats(PerfStatsKind kind = PerfStatsKind.Timer) {
      Kind = kind;
    }

    #endregion

    #region Properties

    /// <summary>Whether this scope accumulates durations
    /// (<see cref="PerfStatsKind.Timer"/>) or unit counts
    /// (<see cref="PerfStatsKind.Counter"/>). Fixed at construction;
    /// drives how the debug panel labels and aggregates the row.</summary>
    public PerfStatsKind Kind { get; }

    /// <summary>Number of live samples currently in the ring (0..<see cref="Capacity"/>).</summary>
    public int SampleCount => _totalAdded < Capacity ? _totalAdded : Capacity;

    /// <summary>
    /// Arithmetic mean of live samples in milliseconds. Returns 0 when
    /// the buffer is empty.
    /// </summary>
    public double Average {
      get {
        var count = SampleCount;
        if (count == 0) return 0.0;
        var sum = 0.0;
        for (var i = 0; i < count; i++) sum += _samples[i];
        return sum / count;
      }
    }

    /// <summary>
    /// 99th-percentile sample (ms). Returns 0 when buffer is empty.
    /// Computed by snapshot-sort on demand.
    /// </summary>
    public double P99 => PercentileOrZero(0.99);

    /// <summary>Median sample (ms). Returns 0 when buffer is empty.</summary>
    public double Median => PercentileOrZero(0.5);

    /// <summary>Largest sample currently in the ring (ms). Returns 0 when empty.</summary>
    public double Max {
      get {
        var count = SampleCount;
        if (count == 0) return 0.0;
        var max = _samples[0];
        for (var i = 1; i < count; i++) {
          if (_samples[i] > max) max = _samples[i];
        }
        return max;
      }
    }

    /// <summary>
    /// Effective sample rate in Hz when the scope is actively firing.
    /// Computed from the <b>median</b> inter-sample timestamp gap,
    /// which makes the value robust to long real-time gaps from game
    /// pauses, lost focus / alt-tab, or system suspend: those become
    /// outlier gaps that the median ignores. Returns 0 when there are
    /// fewer than 2 samples or the median gap is degenerate.
    /// </summary>
    public double FrequencyHz {
      get {
        var count = SampleCount;
        if (count < 2) return 0.0;
        // Walk the live portion of the ring in chronological order and
        // collect inter-sample gaps. Then median-sort. Allocates a small
        // array per query; cold path (panel render) so acceptable.
        var oldestIdx = _totalAdded < Capacity ? 0 : _next;
        var gaps = new long[count - 1];
        for (var i = 0; i < count - 1; i++) {
          var curr = (oldestIdx + i) % Capacity;
          var next = (oldestIdx + i + 1) % Capacity;
          gaps[i] = _timestamps[next] - _timestamps[curr];
        }
        Array.Sort(gaps);
        var medianGap = gaps[gaps.Length / 2];
        if (medianGap <= 0) return 0.0;
        return (double)Stopwatch.Frequency / medianGap;
      }
    }

    /// <summary>
    /// Sum of sample values whose timestamps fall within the
    /// <paramref name="windowSeconds"/>-long window ending at
    /// <paramref name="endTicks"/> (a <see cref="Stopwatch.GetTimestamp"/>
    /// value). Walks the live portion of the ring once.
    ///
    /// <para>For timing scopes this returns "ms of work recorded in
    /// the last N seconds" -- the natural numerator for a rolling
    /// ms/sec headline. For counter-style scopes
    /// (<see cref="Kind"/> == <see cref="PerfStatsKind.Counter"/>) the
    /// sum has no meaningful units and callers should filter those rows
    /// out (by <see cref="Kind"/>) before aggregating.</para>
    /// </summary>
    public double SumInWindow(long endTicks, double windowSeconds) {
      var count = SampleCount;
      if (count == 0) return 0.0;
      var windowTicks = (long)(windowSeconds * Stopwatch.Frequency);
      var cutoff = endTicks - windowTicks;
      var sum = 0.0;
      for (var i = 0; i < count; i++) {
        if (_timestamps[i] >= cutoff) sum += _samples[i];
      }
      return sum;
    }

    #endregion

    #region Add

    /// <summary>
    /// Record one sample of <paramref name="elapsedMs"/> taken at
    /// timestamp <paramref name="timestampTicks"/> (a value from
    /// <see cref="Stopwatch.GetTimestamp"/>). No allocation; constant
    /// time.
    /// </summary>
    public void Add(double elapsedMs, long timestampTicks) {
      _samples[_next] = elapsedMs;
      _timestamps[_next] = timestampTicks;
      _next = (_next + 1) % Capacity;
      _totalAdded++;
    }

    #endregion

    #region Percentile

    private double PercentileOrZero(double fraction) {
      var count = SampleCount;
      if (count == 0) return 0.0;
      // Snapshot live samples and sort ascending. Allocates a small
      // double[] per call -- acceptable on the cold panel-render path.
      var sorted = new double[count];
      Array.Copy(_samples, sorted, count);
      Array.Sort(sorted);
      var idx = (int)Math.Ceiling(fraction * count) - 1;
      if (idx < 0) idx = 0;
      if (idx >= count) idx = count - 1;
      return sorted[idx];
    }

    #endregion

  }

}
