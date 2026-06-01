using System;
using System.Collections.Generic;
using Keystone.Core.Diagnostics;
using Keystone.Core.Time;

namespace Keystone.Core.Sweep {

  /// <summary>
  /// Reusable rolling-sweep amortisation algorithm. Builds a schedule
  /// of work units once per cycle, then drains it gradually across the
  /// cycle's full duration — per-tick budget proportional to elapsed
  /// game-time, so the schedule completes exactly when one
  /// <see cref="CycleDurationDays"/> has elapsed at any game speed.
  /// No frame-time bursts; no idle periods.
  ///
  /// <para><b>The pattern:</b>
  /// <list type="number">
  /// <item>At cycle start, <see cref="BuildSchedule"/> populates the
  ///       work list. Cycle dt (game-days since previous cycle's
  ///       start) is captured into <see cref="CurrentCycleDt"/> so
  ///       processing can use it as the time advance.</item>
  /// <item>The list is shuffled (Fisher-Yates, deterministic seed)
  ///       so per-tick processing touches spatially-uncorrelated
  ///       units rather than sweeping in index order. Avoids the
  ///       visible "wave of updates" pattern an ordered drain
  ///       produces.</item>
  /// <item>Each tick, the cursor advances by
  ///       <c>(elapsed/cycleDuration) * scheduleSize - cursor</c>
  ///       units; <see cref="ProcessUnit"/> is called for each.</item>
  /// <item>When the cursor reaches the end, <see cref="OnCycleComplete"/>
  ///       fires and the cycle is armed for restart.</item>
  /// </list></para>
  ///
  /// <para><b>First-cycle bootstrap.</b> The first tick after init
  /// just anchors the cycle-start; the second tick builds the first
  /// schedule immediately (no day-long wait) but with
  /// <see cref="CurrentCycleDt"/> = 0 (so subclasses don't see a
  /// spurious time advance on the first cycle of a freshly loaded
  /// game).</para>
  ///
  /// <para><b>Cadence units.</b> <see cref="CycleDurationDays"/> is
  /// in game-days, matching <see cref="IClock.TotalDaysElapsed"/>.
  /// Subclasses pick fractions (e.g. <c>1f/24f</c> for once per
  /// game-hour, <c>1f</c> for once per game-day).</para>
  ///
  /// <para><b>Mod-side adapter.</b> Subclasses can derive directly
  /// from this Core class for headless / test scenarios; the Mod
  /// project supplies a thin <c>RollingSweepTicker&lt;TUnit&gt;</c>
  /// that adds Timberborn's <c>ITickableSingleton</c> marker so the
  /// game's tick dispatch picks it up.</para>
  /// </summary>
  /// <typeparam name="TUnit">Type of one work unit. Typically a
  /// small struct — a chunk address, a surface coord, etc.</typeparam>
  public abstract class RollingSweep<TUnit> {

    /// <summary>Default seed for the shuffle RNG. Reproducible across
    /// runs; each cycle's shuffle is then determined by the RNG's
    /// state, so consecutive cycles get different orderings.</summary>
    private const int DefaultSeed = 0;

    private readonly IClock _clock;
    private readonly IPerfScope _perf;
    private readonly Func<float> _cycleDurationDaysProvider;
    private readonly Random _rng;

    private readonly List<TUnit> _schedule = new();
    private int _scheduleCursor;
    private float _cycleStartDay;
    private bool _initialised;
    private bool _hasFiredOnce;
    private float _currentCycleDt;

    /// <summary>
    /// Optional per-unit error callback. When set, a
    /// <see cref="ProcessUnit"/> throw is caught and routed here
    /// (the failing unit is skipped and the drain loop continues).
    /// When <c>null</c>, the exception propagates as before — which
    /// is the correct behaviour for unit tests where a
    /// <c>ProcessUnit</c> throw should fail the test.
    ///
    /// <para><b>Mod-layer wiring.</b>
    /// <see cref="Keystone.Core.Sweep.RollingSweep{TUnit}"/> is
    /// pure Core; the Mod-layer base
    /// (<c>RollingSweepTicker&lt;T&gt;</c>) sets this to a callback
    /// that logs the exception and records to the integration-health
    /// aggregator, so every Mod-side ticker gets catch-and-continue
    /// without per-subclass boilerplate. The callback fires at most
    /// once per distinct <typeparamref name="TUnit"/> hash to avoid
    /// log-spam when one unit fails persistently across ticks — the
    /// rate-limiting is the callback's responsibility, not the
    /// caller's.</para>
    /// </summary>
    protected Action<TUnit, Exception>? OnUnitError { get; set; }

    /// <summary>Constant-cadence constructor. Use this when the cycle
    /// duration is known statically at construction time (tests,
    /// fauna ticker, any sweep whose cadence isn't player-tunable).</summary>
    protected RollingSweep(
        IClock clock, IPerfScope perf, float cycleDurationDays, int seed = DefaultSeed) {
      if (cycleDurationDays <= 0f) {
        throw new ArgumentOutOfRangeException(
            nameof(cycleDurationDays),
            $"Cycle duration must be positive; got {cycleDurationDays}.");
      }
      _clock = clock;
      _perf = perf;
      _cycleDurationDaysProvider = () => cycleDurationDays;
      _rng = new Random(seed);
    }

    /// <summary>Dynamic-cadence constructor. The provider is consulted
    /// once per <see cref="Tick"/> (and once per <see cref="RunCycleNow"/>),
    /// so a value that wasn't yet hydrated at sweep-construction time
    /// (e.g. a <c>ModSetting</c> backed by a persisted store that loads
    /// after Bindito constructs dependent singletons) still drives the
    /// schedule once it does come online. The provider must return a
    /// strictly positive value when consulted; this is enforced lazily
    /// in <see cref="CycleDurationDays"/> rather than at construction
    /// so that pre-hydration construction doesn't trip.</summary>
    protected RollingSweep(
        IClock clock, IPerfScope perf, Func<float> cycleDurationDaysProvider,
        int seed = DefaultSeed) {
      _clock = clock;
      _perf = perf;
      _cycleDurationDaysProvider = cycleDurationDaysProvider
          ?? throw new ArgumentNullException(nameof(cycleDurationDaysProvider));
      _rng = new Random(seed);
    }

    /// <summary>Game-days the schedule is drained over. Each cycle
    /// rebuilds and drains a full schedule across this duration of
    /// game time, regardless of game speed. Public so diagnostics
    /// (the activity panel) can read what the sweep would use right
    /// now — the load-bearing detail when verifying that mod-settings
    /// throttles like <c>KeystonePerformanceSettings.MapUpdateHours</c>
    /// are actually driving the cadence.
    /// <para>For dynamic-cadence sweeps the underlying provider is
    /// consulted on every read; for constant-cadence sweeps the value
    /// is fixed at construction. Throws
    /// <see cref="InvalidOperationException"/> if the provider returns
    /// a non-positive value — the schedule math divides by this and
    /// would otherwise NaN-propagate silently.</para></summary>
    public float CycleDurationDays {
      get {
        var v = _cycleDurationDaysProvider();
        if (v <= 0f) {
          throw new InvalidOperationException(
              $"{GetType().Name}: cycle-duration provider returned {v}; "
              + "must be strictly positive.");
        }
        return v;
      }
    }

    /// <summary>Game-days that elapsed during the cycle currently
    /// being drained. Zero on the very first cycle (so subclasses
    /// don't see a spurious time advance at PostLoad). Stable for
    /// the whole cycle — read it during
    /// <see cref="ProcessUnit"/>.</summary>
    protected float CurrentCycleDt => _currentCycleDt;

    /// <summary>Total number of cycles completed since construction.
    /// Incremented inside <see cref="OnCycleComplete"/> — both empty
    /// cycles and ones with actual work count. Diagnostic readers
    /// (e.g. the activity panel) sample this against a previous
    /// snapshot to compute "cycles per game-day" — the cleanest
    /// in-game verification that <c>MapUpdateHours</c> scaling is
    /// actually driving the cadence.</summary>
    public long CyclesCompleted { get; private set; }

    /// <summary>Override to populate the next cycle's schedule.
    /// Called once at cycle start. The list is freshly cleared.
    /// After return, the list is shuffled before draining begins.</summary>
    protected abstract void BuildSchedule(List<TUnit> schedule);

    /// <summary>Override to process one work unit. Called once per
    /// unit per cycle, spread across the cycle's ticks.</summary>
    protected abstract void ProcessUnit(TUnit unit);

    /// <summary>Optional hook fired when a cycle's drain finishes.
    /// Useful for end-of-cycle bookkeeping (e.g. pruning stores of
    /// units that have left the world). Also fires for empty cycles
    /// (BuildSchedule produced no units).</summary>
    protected virtual void OnCycleComplete() {}

    /// <summary>Increment <see cref="CyclesCompleted"/> and dispatch
    /// to <see cref="OnCycleComplete"/>. Single fire point so the
    /// counter and the hook can't drift across the three sites in
    /// <see cref="Tick"/> / <see cref="RunCycleNow"/> that complete a
    /// cycle (empty schedule, drained schedule, synchronous drain).</summary>
    private void FireCycleComplete() {
      CyclesCompleted++;
      OnCycleComplete();
    }

    /// <summary>Hook fired at the very start of each <see cref="Tick"/>
    /// call, before schedule build and drain. Used by parallel sweep
    /// subclasses to sync completed worker results from the previous
    /// tick before the next drain fills a new batch.</summary>
    protected virtual void OnTickStart() {}

    /// <summary>Hook fired at the end of each <see cref="Tick"/> call,
    /// after all schedule / drain / complete work. Symmetric to
    /// <see cref="OnTickStart"/>. Fires on every passing-ShouldRun
    /// tick regardless of whether work was actually drained
    /// (including initial-anchor ticks and empty-cycle ticks).
    /// <para>Used by subclasses that aggregate per-tick totals across
    /// per-chunk <see cref="ProcessUnit"/> calls: clear accumulators
    /// in <see cref="OnTickStart"/>, accumulate during ProcessUnit,
    /// flush to the perf tracker here. Per-tick records are then
    /// directly comparable to the parent <c>.Tick</c> scope.</para></summary>
    protected virtual void OnTickEnd() {}

    /// <summary>Optional precondition. Return false to skip this
    /// tick entirely (no schedule build, no drain). The base class
    /// uses this for the initial-anchor logic; subclasses can layer
    /// their own gates by overriding and consulting <c>base.ShouldRun()</c>.</summary>
    protected virtual bool ShouldRun() => true;

    /// <summary>Drive the sweep one step. Called from the Mod-side
    /// <c>ITickableSingleton.Tick</c> at the host's tick cadence; tests
    /// can call it directly with a fake clock to simulate cycles.</summary>
    public void Tick() {
      if (!ShouldRun()) return;
      OnTickStart();
      try {
        TickCore();
      } finally {
        OnTickEnd();
      }
    }

    /// <summary>The original <see cref="Tick"/> body, extracted so the
    /// public method can wrap it in <c>try / finally</c> for the
    /// symmetric <see cref="OnTickEnd"/> hook without changing the
    /// existing early-return structure. Any subclass that needs to
    /// short-circuit must do so via <see cref="ShouldRun"/>.</summary>
    private void TickCore() {
      var now = _clock.TotalDaysElapsed;
      // Snapshot once so every comparison inside this Tick agrees,
      // even if a future setting becomes hot-reloadable and the
      // provider returns different values across reads.
      var cycleDuration = CycleDurationDays;

      if (!_initialised) {
        _initialised = true;
        // Anchor in the past so the next tick's "ready to build"
        // check fires immediately. Without this the first cycle
        // would wait a full CycleDurationDays before doing anything.
        _cycleStartDay = now - cycleDuration;
        return;
      }

      var name = GetType().Name;

      // Build a fresh schedule once a full cycle has elapsed since
      // the previous build. Captures dt for the new cycle.
      if (_schedule.Count == 0 && (now - _cycleStartDay) >= cycleDuration) {
        _currentCycleDt = _hasFiredOnce ? (now - _cycleStartDay) : 0f;
        _cycleStartDay = now;
        _hasFiredOnce = true;

        using (_perf.Track(name + ".Build")) {
          BuildSchedule(_schedule);
          Shuffle(_schedule);
        }
        _scheduleCursor = 0;

        if (_schedule.Count == 0) {
          // Empty cycle (no work this round): still fire the
          // complete hook so subclasses can do per-cycle bookkeeping.
          using (_perf.Track(name + ".Complete")) {
            FireCycleComplete();
          }
          return;
        }
      }

      if (_schedule.Count == 0) return;

      // Drain proportional to elapsed game-time within the cycle:
      // at fraction f of CycleDurationDays elapsed, fraction f of
      // the schedule has been processed. Self-corrects across game-
      // speed changes mid-cycle.
      var elapsed = now - _cycleStartDay;
      var fraction = elapsed >= cycleDuration ? 1f : elapsed / cycleDuration;
      var totalUnits = _schedule.Count;
      var desiredCursor = (int)(fraction * totalUnits);
      if (desiredCursor > totalUnits) desiredCursor = totalUnits;
      var drainedThisTick = 0;
      using (_perf.Track(name + ".Tick")) {
        while (_scheduleCursor < desiredCursor) {
          var unit = _schedule[_scheduleCursor];
          try {
            ProcessUnit(unit);
          } catch (Exception ex) when (OnUnitError != null) {
            // Skip the failing unit and continue draining. The
            // callback (set by the Mod-layer base class) logs the
            // exception and records to the integration-health
            // aggregator. Without OnUnitError set (test scenarios),
            // the `when` guard lets the exception propagate so test
            // assertions still see it.
            OnUnitError(unit, ex);
          }
          _scheduleCursor++;
          drainedThisTick++;
        }
      }
      // Per-tick drain count: lets us see if the rolling sweep is
      // actually slicing work or if individual ticks balloon. A count,
      // not a duration — RecordCount keeps it in the counter table.
      _perf.RecordCount(name + ".Units", drainedThisTick);

      if (_scheduleCursor >= totalUnits) {
        _schedule.Clear();
        _scheduleCursor = 0;
        using (_perf.Track(name + ".Complete")) {
          FireCycleComplete();
        }
      }
    }

    /// <summary>Drain one full cycle synchronously, bypassing the
    /// rolling-sweep amortisation. Used at game-start / post-load to
    /// bring the ticker's outputs to a consistent state without
    /// waiting for the natural cycle duration (which can be 1 game-
    /// hour or 1 game-day depending on the subclass).
    /// <para>After this returns: the schedule is empty, the cycle
    /// anchor is reset to "now", and <see cref="OnCycleComplete"/>
    /// has fired. The next regular <see cref="Tick"/> behaves as if
    /// it were the second tick of a freshly initialised ticker —
    /// it'll build the next cycle's schedule once
    /// <see cref="CycleDurationDays"/> has elapsed.</para>
    /// <para><paramref name="cycleDtDays"/> is the time advance
    /// attributed to this cycle (read via
    /// <see cref="CurrentCycleDt"/> by subclasses). Default 0 — no
    /// time-dependent state changes, just rebuild derived data from
    /// the current world state. Pass <see cref="CycleDurationDays"/>
    /// if a one-cycle drift advance is desired.</para>
    /// <para><see cref="ShouldRun"/> is consulted; if it returns
    /// false the call is a no-op (matching <see cref="Tick"/>'s
    /// gate).</para></summary>
    public void RunCycleNow(float cycleDtDays = 0f) {
      if (!ShouldRun()) return;
      // Deliberately not wrapped in a IPerfScope.Track: this is the
      // one-shot startup warmup path (multi-second by design on large
      // maps) and lumping it into the same rolling-sweep stats as the
      // per-tick steady-state samples skews the avg / P99 / max
      // columns in the perf panel. KeystoneStartupWarmup.PostLoad
      // logs its own elapsed-ms total to Player.log for the diagnostic
      // record.
      _schedule.Clear();
      _scheduleCursor = 0;
      _currentCycleDt = cycleDtDays;
      BuildSchedule(_schedule);
      Shuffle(_schedule);
      for (var i = 0; i < _schedule.Count; i++) {
        ProcessUnit(_schedule[i]);
      }
      _schedule.Clear();
      _scheduleCursor = 0;
      FireCycleComplete();
      _cycleStartDay = _clock.TotalDaysElapsed;
      _initialised = true;
      _hasFiredOnce = true;
    }

    /// <summary>Fisher-Yates in place. Randomises the order so
    /// per-tick processing touches spatially-uncorrelated units
    /// rather than scanning in index order; avoids the visible
    /// wave-of-updates pattern an ordered drain produces.</summary>
    private void Shuffle(List<TUnit> list) {
      for (var i = list.Count - 1; i > 0; i--) {
        var j = _rng.Next(i + 1);
        if (j != i) {
          var tmp = list[i];
          list[i] = list[j];
          list[j] = tmp;
        }
      }
    }

  }

}
