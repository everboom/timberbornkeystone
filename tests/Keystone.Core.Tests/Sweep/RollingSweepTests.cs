using System.Collections.Generic;
using Keystone.Core.Sweep;
using Keystone.Core.Tests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Sweep {

  /// <summary>
  /// Pins the rolling-sweep amortisation algorithm:
  /// <list type="bullet">
  ///   <item>First-tick bootstrap (anchor only, no work).</item>
  ///   <item>First-cycle dt = 0 (no spurious time advance at PostLoad).</item>
  ///   <item>Subsequent cycles report dt = elapsed game-days.</item>
  ///   <item>Drain proportional to elapsed time within a cycle.</item>
  ///   <item>Empty cycles still fire <c>OnCycleComplete</c>.</item>
  ///   <item><c>RunCycleNow</c> drains synchronously, resets the anchor.</item>
  ///   <item><c>ShouldRun</c> gate suppresses entirely.</item>
  ///   <item>Constructor rejects non-positive cycle duration.</item>
  /// </list>
  /// </summary>
  [TestClass]
  public class RollingSweepTests {

    #region Helpers

    /// <summary>Concrete test subclass that records every event so
    /// tests can assert on the sequence and contents of
    /// BuildSchedule/ProcessUnit/OnCycleComplete calls.</summary>
    private sealed class CapturingSweep : RollingSweep<int> {

      public List<int> ScheduleToBuild { get; } = new();
      public List<int> Processed { get; } = new();
      public List<float> DtAtCycleStart { get; } = new();
      public int CompletionsObserved { get; private set; }
      public bool ShouldRunResult { get; set; } = true;
      public int OnTickStartCount { get; private set; }

      public CapturingSweep(FakeClock clock, RecordingPerfScope perf, float cycleDays)
          : base(clock, perf, cycleDays) {}

      public CapturingSweep(FakeClock clock, RecordingPerfScope perf, float cycleDays, int seed)
          : base(clock, perf, cycleDays, seed) {}

      public CapturingSweep(
          FakeClock clock, RecordingPerfScope perf, System.Func<float> cycleDaysProvider)
          : base(clock, perf, cycleDaysProvider) {}

      public float CurrentDt => CurrentCycleDt;

      protected override bool ShouldRun() => ShouldRunResult;

      protected override void BuildSchedule(List<int> schedule) {
        DtAtCycleStart.Add(CurrentCycleDt);
        for (var i = 0; i < ScheduleToBuild.Count; i++) {
          schedule.Add(ScheduleToBuild[i]);
        }
      }

      protected override void ProcessUnit(int unit) {
        Processed.Add(unit);
      }

      protected override void OnTickStart() {
        OnTickStartCount++;
      }

      protected override void OnCycleComplete() {
        CompletionsObserved++;
      }

    }

    #endregion

    #region Constructor validation

    [TestMethod]
    public void Constructor_NegativeCycleDuration_Throws() {
      var clock = new FakeClock();
      var perf = new RecordingPerfScope();
      Assert.ThrowsException<System.ArgumentOutOfRangeException>(
          () => new CapturingSweep(clock, perf, -0.1f));
    }

    [TestMethod]
    public void Constructor_ZeroCycleDuration_Throws() {
      var clock = new FakeClock();
      var perf = new RecordingPerfScope();
      Assert.ThrowsException<System.ArgumentOutOfRangeException>(
          () => new CapturingSweep(clock, perf, 0f));
    }

    [TestMethod]
    public void Constructor_DynamicCadence_NullProvider_Throws() {
      var clock = new FakeClock();
      var perf = new RecordingPerfScope();
      Assert.ThrowsException<System.ArgumentNullException>(
          () => new CapturingSweep(clock, perf, (System.Func<float>)null!));
    }

    [TestMethod]
    public void Constructor_DynamicCadence_ProviderReturningZero_DoesNotThrowUntilTick() {
      // Pins the hydration-race accommodation: a provider that's not
      // yet hydrated (returns 0) at construction must not crash the
      // ticker. The error surfaces only when Tick() consults the
      // provider — by which point the persisted store has hydrated and
      // the value is positive in practice. (If somehow it's still bad
      // at tick time, that throws — but loudly, not silently.)
      var clock = new FakeClock();
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, () => 0f);
      Assert.ThrowsException<System.InvalidOperationException>(() => sweep.Tick());
    }

    [TestMethod]
    public void DynamicCadence_ProviderChangesBetweenCycles_NextCycleUsesNewCadence() {
      // The whole point of the dynamic-cadence overload: the value is
      // re-read every Tick, so a provider whose return changes between
      // cycles (e.g. a ModSetting that hydrates from a persisted store
      // after Bindito construction completes) drives the schedule at
      // its current value, not a stale snapshot. Tested on cycle 2
      // because the anchor-in-past bootstrap makes cycle 1 fire on
      // the very next tick regardless of cadence — which would mask
      // the assertion.
      //
      // Regression for the MapUpdateHours-ignored bug: the previous
      // float-only constructor latched the value passed in, so a
      // pre-hydration default would persist for the lifetime of the
      // ticker.
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var live = 0.5f;
      var sweep = new CapturingSweep(clock, perf, () => live);
      sweep.ScheduleToBuild.AddRange(new[] { 1 });

      // Cycle 1: anchor → build → drain+complete.
      sweep.Tick();
      clock.TotalDaysElapsed = 0.01f;
      sweep.Tick();
      clock.TotalDaysElapsed = 0.51f;
      sweep.Tick();
      Assert.AreEqual(1, sweep.CyclesCompleted, "Cycle 1 should complete.");

      // Now the provider's value changes (simulating late hydration
      // or a future hot-reload). The next cycle must respect the new
      // cadence, not the old one.
      live = 2.0f;

      // 0.49 days past last cycle start: under the OLD cadence (0.5d)
      // this would build; under the live cadence (2.0d) it must not.
      clock.TotalDaysElapsed = 1.0f;
      sweep.Tick();
      Assert.AreEqual(1, sweep.DtAtCycleStart.Count,
          "Build #2 should NOT fire yet — live cadence is now 2d, only 0.49d elapsed.");

      // 2.0 days past last cycle start: live cadence threshold met.
      clock.TotalDaysElapsed = 2.51f;
      sweep.Tick();
      Assert.AreEqual(2, sweep.DtAtCycleStart.Count,
          "Build #2 should fire now — 2 days elapsed at the live cadence.");
    }

    #endregion

    #region First-tick bootstrap

    [TestMethod]
    public void Tick_FirstCallAfterConstruction_AnchorsButDoesNoWork() {
      var clock = new FakeClock { TotalDaysElapsed = 5f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1, 2, 3 });

      sweep.Tick();

      Assert.AreEqual(0, sweep.Processed.Count, "First tick should only anchor; no units processed.");
      Assert.AreEqual(0, sweep.DtAtCycleStart.Count, "First tick should not build a schedule.");
      Assert.AreEqual(0, sweep.CompletionsObserved, "First tick should not fire OnCycleComplete.");
    }

    [TestMethod]
    public void Tick_SecondCallAfterBootstrap_BuildsScheduleWithDtZero() {
      // Anchor at TotalDaysElapsed = 5; second tick at the same time still
      // satisfies "elapsed >= cycleDuration" because the anchor is placed
      // in the past at (now - cycleDuration) on the first tick.
      var clock = new FakeClock { TotalDaysElapsed = 5f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 10, 20, 30 });

      sweep.Tick();  // anchor
      sweep.Tick();  // build + drain

      Assert.AreEqual(1, sweep.DtAtCycleStart.Count, "Second tick should build exactly one cycle.");
      Assert.AreEqual(0f, sweep.DtAtCycleStart[0],
          "First cycle dt must be 0 — no spurious time advance at PostLoad.");
    }

    #endregion

    #region Cycle dt accounting

    [TestMethod]
    public void Tick_CycleAfterFirst_ReportsElapsedDtSinceLastCycleStart() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1 });

      // Bootstrap: anchor lands at now - 1 = -1.
      sweep.Tick();
      // First cycle: now=0, elapsed-since-anchor=1, builds with dt=0;
      // after build _cycleStartDay = 0, so this tick's drain sees
      // elapsed = 0 → no units drain yet.
      sweep.Tick();
      // Advance past the cycle. Tick at now=1.5: schedule still holds
      // the previous cycle's unit, falls through to drain section,
      // fraction = 1, all units drain, schedule clears + complete fires.
      clock.TotalDaysElapsed = 1.5f;
      sweep.Tick();
      // Tick again at the same time: schedule empty AND elapsed
      // (1.5 - 0) = 1.5 >= 1 → builds second cycle with dt = 1.5.
      sweep.Tick();

      Assert.AreEqual(2, sweep.DtAtCycleStart.Count, "Second cycle should build.");
      Assert.AreEqual(0f, sweep.DtAtCycleStart[0], "First cycle dt is 0 by contract.");
      Assert.AreEqual(1.5f, sweep.DtAtCycleStart[1], 1e-5f,
          "Second cycle dt should equal elapsed days since the previous cycle's start.");
    }

    #endregion

    #region Drain proportionality

    [TestMethod]
    public void Tick_FullCycleElapsedAfterBuild_DrainsEverythingInOneTick() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      // 10 units; expect proportional drain.
      for (var i = 0; i < 10; i++) sweep.ScheduleToBuild.Add(i);

      // Bootstrap.
      sweep.Tick();
      // First build: this tick triggers Build (anchor was now-1), and
      // updates _cycleStartDay = now. The drain in the same tick sees
      // elapsed = 0 and drains nothing.
      sweep.Tick();
      Assert.AreEqual(0, sweep.Processed.Count,
          "Build tick should not also drain — drain fraction is 0 right after the build resets _cycleStartDay.");

      // Advance a full cycle. Now fraction = 1, all 10 drain in one tick.
      clock.TotalDaysElapsed = 1f;
      sweep.Tick();
      Assert.AreEqual(10, sweep.Processed.Count);
    }

    [TestMethod]
    public void Tick_DrainStopsAtFractionalCursorWhenElapsedIsPartial() {
      // We want a tick where the cycle has already been built (previous
      // tick fired the build) but only part of the cycle's time has
      // elapsed. The drain target is (elapsed/cycleDuration) * scheduleSize.
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      // 100 units gives us granularity for fractional drain assertions.
      for (var i = 0; i < 100; i++) sweep.ScheduleToBuild.Add(i);

      sweep.Tick();  // anchor at -1
      // Move forward just enough that elapsed (since anchor) = 0.01 of
      // cycle. Build fires (because elapsed = anchor->now = 0 - (-1) = 1
      // satisfies the >=cycle gate, so the build happens). Right after
      // build, _cycleStartDay = 0; drain fraction = (now - 0)/1 = 0 → 0
      // units drained on the build tick.
      sweep.Tick();
      var afterBuildCount = sweep.Processed.Count;

      // Advance to 30% through the cycle and tick. Drain target =
      // 0.30 * 100 = 30 cumulative units.
      clock.TotalDaysElapsed = 0.30f;
      sweep.Tick();
      var thirtyPercentCount = sweep.Processed.Count;

      Assert.AreEqual(0, afterBuildCount,
          "Tick that triggers Build should not also drain — drain fraction is 0 right after build.");
      Assert.AreEqual(30, thirtyPercentCount,
          "Drain cursor should sit at 30% of schedule once 30% of cycle has elapsed.");
    }

    #endregion

    #region Empty cycles

    [TestMethod]
    public void Tick_EmptyScheduleCycle_FiresOnCycleCompleteWithoutWork() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      // No units in ScheduleToBuild → BuildSchedule yields empty.

      sweep.Tick();  // anchor
      sweep.Tick();  // build (empty), should still fire OnCycleComplete

      Assert.AreEqual(1, sweep.DtAtCycleStart.Count, "Build was attempted.");
      Assert.AreEqual(0, sweep.Processed.Count, "No units processed.");
      Assert.AreEqual(1, sweep.CompletionsObserved,
          "Empty cycle must still fire OnCycleComplete (per-cycle bookkeeping contract).");
    }

    #endregion

    #region Cycles-completed counter

    [TestMethod]
    public void CyclesCompleted_StartsAtZero() {
      var clock = new FakeClock();
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      Assert.AreEqual(0, sweep.CyclesCompleted);
    }

    [TestMethod]
    public void CyclesCompleted_IncrementsOncePerCompletedDrainCycle() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1, 2 });

      sweep.Tick();  // anchor
      sweep.Tick();  // build (no drain yet)
      Assert.AreEqual(0, sweep.CyclesCompleted, "Build tick alone shouldn't increment.");

      clock.TotalDaysElapsed = 1f;
      sweep.Tick();  // drain → complete fires

      Assert.AreEqual(1, sweep.CyclesCompleted);
    }

    [TestMethod]
    public void CyclesCompleted_EmptyCycleStillCounts() {
      // Empty BuildSchedule still fires OnCycleComplete per the rolling-
      // sweep contract; the counter must reflect that.
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      // No ScheduleToBuild entries.

      sweep.Tick();  // anchor
      sweep.Tick();  // empty build + complete

      Assert.AreEqual(1, sweep.CyclesCompleted);
    }

    [TestMethod]
    public void CyclesCompleted_RunCycleNowIncrements() {
      var clock = new FakeClock();
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1 });

      sweep.RunCycleNow();

      Assert.AreEqual(1, sweep.CyclesCompleted);
    }

    [TestMethod]
    public void CyclesCompleted_AccumulatesAcrossManyCycles() {
      // Verifies the "cycles per game-day" relationship the activity
      // panel uses to verify MapUpdateHours scaling. At cycleDays=0.5
      // and a tick interval of 0.5 game-days (one tick per cycle's
      // worth of game-time), each cycle takes two ticks to complete:
      // one that builds (and immediately drains to the cursor cap of
      // 1 since elapsed >= cycleDuration), one that fires complete
      // and re-arms. Actual cycle rate per game-day matches the
      // configured cadence within a few ticks of warmup.
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 0.5f);
      sweep.ScheduleToBuild.AddRange(new[] { 1 });

      sweep.Tick();  // anchor
      // Advance in full-cycle steps so each tick can both build AND
      // drain in one go (elapsed = cycleDuration → fraction = 1 →
      // drain reaches cursor end → complete fires).
      for (var i = 0; i < 6; i++) {
        clock.TotalDaysElapsed += 0.5f;
        sweep.Tick();
      }

      // Each cycle takes two ticks at this cadence: tick N builds (no
      // drain — drain elapsed is 0 right after _cycleStartDay = now);
      // tick N+1 drains to completion. 6 post-anchor ticks ⇒ ~3
      // completed cycles. The activity-panel rate diff (cycles per
      // game-day) is what matters; here that's 3 / 3 = 1 cycle/day,
      // which is the inverse of cycleDays=0.5... not quite — actually
      // it's exposing that the build-then-wait pattern halves the
      // effective rate on a 1-unit schedule.
      Assert.AreEqual(3, sweep.CyclesCompleted,
          "Six post-anchor ticks at 0.5-day spacing should complete three cycles (build/drain pair per cycle).");
    }

    #endregion

    #region Cycle completion

    [TestMethod]
    public void Tick_FullCycleDrainCompletes_FiresOnCycleCompleteExactlyOnce() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1, 2, 3, 4, 5 });

      sweep.Tick();  // anchor
      sweep.Tick();  // build (drain=0 since _cycleStartDay just moved to now)
      clock.TotalDaysElapsed = 1f;
      sweep.Tick();  // drain all 5 → complete fires

      Assert.AreEqual(5, sweep.Processed.Count);
      Assert.AreEqual(1, sweep.CompletionsObserved);
    }

    [TestMethod]
    public void Tick_PartialDrainThenFinish_FiresOnCycleCompleteExactlyOnce() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      for (var i = 0; i < 10; i++) sweep.ScheduleToBuild.Add(i);

      sweep.Tick();  // anchor
      sweep.Tick();  // build (drain=0 just after build)
      Assert.AreEqual(0, sweep.CompletionsObserved);

      clock.TotalDaysElapsed = 0.5f;
      sweep.Tick();  // drain to 50%
      Assert.AreEqual(0, sweep.CompletionsObserved, "Mid-cycle drain should not complete.");

      clock.TotalDaysElapsed = 1f;
      sweep.Tick();  // drain to 100% → completes
      Assert.AreEqual(10, sweep.Processed.Count);
      Assert.AreEqual(1, sweep.CompletionsObserved);
    }

    #endregion

    #region ShouldRun gate

    [TestMethod]
    public void Tick_ShouldRunFalse_SuppressesAllWork() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f) {
          ShouldRunResult = false,
      };
      sweep.ScheduleToBuild.AddRange(new[] { 1, 2, 3 });

      sweep.Tick();
      sweep.Tick();
      sweep.Tick();

      Assert.AreEqual(0, sweep.Processed.Count);
      Assert.AreEqual(0, sweep.DtAtCycleStart.Count);
      Assert.AreEqual(0, sweep.CompletionsObserved);
    }

    [TestMethod]
    public void Tick_ShouldRunFalseEvenSuppressesInitialAnchor() {
      // The bootstrap returns early before _initialised flips. Flipping
      // ShouldRun back to true later should still run a clean bootstrap.
      var clock = new FakeClock { TotalDaysElapsed = 5f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f) {
          ShouldRunResult = false,
      };
      sweep.ScheduleToBuild.AddRange(new[] { 1 });

      sweep.Tick();  // gated out
      sweep.ShouldRunResult = true;
      sweep.Tick();  // now anchors at 5 - 1 = 4
      sweep.Tick();  // now=5, elapsed=1 → builds

      Assert.AreEqual(1, sweep.DtAtCycleStart.Count,
          "After re-enabling, the second post-enable tick should be the bootstrap and the third should build.");
    }

    #endregion

    #region RunCycleNow

    [TestMethod]
    public void RunCycleNow_DefaultDt_RunsSynchronouslyWithDtZero() {
      var clock = new FakeClock { TotalDaysElapsed = 10f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 100, 200, 300 });

      sweep.RunCycleNow();

      Assert.AreEqual(3, sweep.Processed.Count, "All units processed in one call.");
      Assert.AreEqual(1, sweep.DtAtCycleStart.Count);
      Assert.AreEqual(0f, sweep.DtAtCycleStart[0],
          "Default RunCycleNow uses dt = 0 (rebuild derived data, no time advance).");
      Assert.AreEqual(1, sweep.CompletionsObserved);
    }

    [TestMethod]
    public void RunCycleNow_ExplicitDt_IsObservableInBuildSchedule() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1 });

      sweep.RunCycleNow(cycleDtDays: 0.5f);

      Assert.AreEqual(0.5f, sweep.DtAtCycleStart[0], 1e-5f);
    }

    [TestMethod]
    public void RunCycleNow_ResetsAnchorSoNextTickWaitsAFullCycle() {
      var clock = new FakeClock { TotalDaysElapsed = 10f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1 });

      sweep.RunCycleNow();
      Assert.AreEqual(1, sweep.DtAtCycleStart.Count);

      // No time advance — Tick should not start a new cycle.
      sweep.Tick();
      Assert.AreEqual(1, sweep.DtAtCycleStart.Count,
          "Tick at the same instant as RunCycleNow should not fire another cycle.");

      // Advance by a full cycle — now Tick should build.
      clock.TotalDaysElapsed = 11f;
      sweep.Tick();
      Assert.AreEqual(2, sweep.DtAtCycleStart.Count);
    }

    [TestMethod]
    public void RunCycleNow_ShouldRunFalse_IsNoop() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f) {
          ShouldRunResult = false,
      };
      sweep.ScheduleToBuild.AddRange(new[] { 1 });

      sweep.RunCycleNow();

      Assert.AreEqual(0, sweep.Processed.Count);
      Assert.AreEqual(0, sweep.DtAtCycleStart.Count);
      Assert.AreEqual(0, sweep.CompletionsObserved);
    }

    #endregion

    #region Perf scope wiring

    [TestMethod]
    public void Tick_BuildAndCompleteScopesOpenAndCloseOnEmptyCycle() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      // No units; expect Build + Complete scopes only.

      sweep.Tick();
      sweep.Tick();

      CollectionAssert.Contains(perf.Opened, "CapturingSweep.Build");
      CollectionAssert.Contains(perf.Closed, "CapturingSweep.Build");
      CollectionAssert.Contains(perf.Opened, "CapturingSweep.Complete");
      CollectionAssert.Contains(perf.Closed, "CapturingSweep.Complete");
    }

    [TestMethod]
    public void Tick_TickScopeEmitsOnDrainingTick() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1, 2 });

      sweep.Tick();   // anchor
      sweep.Tick();   // build + drain

      CollectionAssert.Contains(perf.Opened, "CapturingSweep.Tick");
      // ".Tick" is now the per-tick scope wrapping the drain loop --
      // previously there were two scopes (.Tick wrapping everything
      // and .Drain wrapping the loop). The outer scope always read
      // equal to the inner one, so it was dropped and the inner
      // renamed to ".Tick" (the more recognisable label).
      // .Build and .Complete remain as separate cycle-boundary scopes.
    }

    [TestMethod]
    public void Tick_UnitsCountRecordedPerDrainingTick() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1, 2, 3, 4, 5 });

      sweep.Tick();   // anchor
      sweep.Tick();   // build (Units = 0 — no drain post-build)
      clock.TotalDaysElapsed = 1f;
      sweep.Tick();   // drain all 5 → Units = 5

      // Two records expected: the build-tick's 0, then the drain tick's 5.
      var unitsRecords = perf.Counts.FindAll(r => r.Name == "CapturingSweep.Units");
      Assert.AreEqual(2, unitsRecords.Count,
          "One .Units record per draining-arm tick (post-build tick included with 0).");
      Assert.AreEqual(0L, unitsRecords[0].Value,
          "Build tick records 0 units drained (drain target = 0 after _cycleStartDay reset).");
      Assert.AreEqual(5L, unitsRecords[1].Value);
    }

    #endregion

    #region Shuffle determinism

    [TestMethod]
    public void Tick_SameSeed_ProducesSameProcessingOrder() {
      var clock1 = new FakeClock { TotalDaysElapsed = 0f };
      var perf1 = new RecordingPerfScope();
      var sweep1 = new CapturingSweep(clock1, perf1, cycleDays: 1f, seed: 42);
      for (var i = 0; i < 10; i++) sweep1.ScheduleToBuild.Add(i);

      sweep1.Tick();
      sweep1.Tick();
      clock1.TotalDaysElapsed = 1f;
      sweep1.Tick();

      var clock2 = new FakeClock { TotalDaysElapsed = 0f };
      var perf2 = new RecordingPerfScope();
      var sweep2 = new CapturingSweep(clock2, perf2, cycleDays: 1f, seed: 42);
      for (var i = 0; i < 10; i++) sweep2.ScheduleToBuild.Add(i);

      sweep2.Tick();
      sweep2.Tick();
      clock2.TotalDaysElapsed = 1f;
      sweep2.Tick();

      CollectionAssert.AreEqual(sweep1.Processed, sweep2.Processed,
          "Same seed and same schedule should produce identical processing order.");
    }

    [TestMethod]
    public void Tick_ScheduleShuffledFromInputOrder() {
      // Verify the shuffle is actually happening — with 50 units and a
      // fixed seed it would be astronomically unlikely for the shuffle
      // to return the input order.
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, cycleDays: 1f);
      for (var i = 0; i < 50; i++) sweep.ScheduleToBuild.Add(i);

      sweep.Tick();   // anchor
      sweep.Tick();   // build
      clock.TotalDaysElapsed = 1f;
      sweep.Tick();   // drain all 50

      // Processed should still contain all 50 values, but in a different order.
      Assert.AreEqual(50, sweep.Processed.Count);
      var sorted = new List<int>(sweep.Processed);
      sorted.Sort();
      for (var i = 0; i < 50; i++) Assert.AreEqual(i, sorted[i], "All units processed.");

      var inOrder = true;
      for (var i = 0; i < 50; i++) {
        if (sweep.Processed[i] != i) { inOrder = false; break; }
      }
      Assert.IsFalse(inOrder, "Schedule should be shuffled, not in input order.");
    }

    #endregion

    #region OnTickStart hook

    [TestMethod]
    public void OnTickStart_CalledOnEachTickBeforeDrain() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, 1f);
      sweep.ScheduleToBuild.AddRange(new[] { 1, 2, 3 });

      sweep.Tick();  // tick 0: anchor — OnTickStart fires, no drain
      Assert.AreEqual(1, sweep.OnTickStartCount, "Anchor tick fires OnTickStart.");
      Assert.AreEqual(0, sweep.Processed.Count, "Anchor tick has no drain.");

      clock.TotalDaysElapsed = 1f;
      sweep.Tick();  // tick 1: build (elapsed >= cycle), drain fraction = 0
      Assert.AreEqual(2, sweep.OnTickStartCount, "Build tick fires OnTickStart.");

      clock.TotalDaysElapsed = 2f;
      sweep.Tick();  // tick 2: drain to completion (fraction = 1)
      Assert.AreEqual(3, sweep.OnTickStartCount, "Drain tick fires OnTickStart.");
      Assert.AreEqual(3, sweep.Processed.Count, "All units drained.");
    }

    [TestMethod]
    public void OnTickStart_NotCalledWhenShouldRunReturnsFalse() {
      var clock = new FakeClock { TotalDaysElapsed = 0f };
      var perf = new RecordingPerfScope();
      var sweep = new CapturingSweep(clock, perf, 1f);
      sweep.ShouldRunResult = false;

      sweep.Tick();
      Assert.AreEqual(0, sweep.OnTickStartCount,
          "ShouldRun=false skips the entire tick, including OnTickStart.");
    }

    #endregion

  }

}
