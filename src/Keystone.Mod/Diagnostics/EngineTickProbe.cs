using Timberborn.SingletonSystem;
using Timberborn.TickSystem;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Samples Timberborn's per-sim-tick simulation cost into
  /// <see cref="PerfTracker"/> as a scope named "Engine.TickWork".
  /// Vanilla's <see cref="Ticker"/> already measures the duration of
  /// each bucket-processing call with a <c>Stopwatch</c> and exposes
  /// it via <see cref="Ticker.LengthOfLastTickInSeconds"/>; this probe
  /// reads that value once per sim tick — via
  /// <see cref="ITickableSingleton"/>, not <see cref="IUpdatableSingleton"/>
  /// — and pushes it into the existing perf-tracking pipeline so it
  /// shows up in the perf window alongside Keystone's own scopes.
  ///
  /// <para><b>Why per sim tick, not per Unity frame.</b> The sim ticks
  /// at ~5Hz at 1x speed while Unity renders at ~60fps, and
  /// <see cref="Ticker.LengthOfLastTickInSeconds"/> only changes when a
  /// sim tick actually runs. An <see cref="IUpdatableSingleton"/> reading
  /// it every frame would record the same tick's cost ~12 times,
  /// inflating the scope's windowed sum — and the headline "Keystone is
  /// N% of Engine" denominator, the per-tick, and the per-hour Engine
  /// figures — by the frame-to-tick ratio (which itself varies with game
  /// speed). Sampling once per sim tick makes Engine.TickWork's sample
  /// cadence match Keystone's own per-tick scopes, so the share and
  /// per-tick numbers are apples-to-apples.</para>
  ///
  /// <para><b>Semantic.</b> "Engine.TickWork" measures all simulation
  /// work that happened in a sim tick — entity ticks plus singleton
  /// ticks plus everything else the bucket service drives. <b>Includes
  /// Keystone's tickables</b> because they run in the same singleton
  /// bucket pass. To get a vanilla-only number, subtract the sum of
  /// Keystone scopes from this row.</para>
  ///
  /// <para><b>What it doesn't include.</b> Unity rendering, UI,
  /// <c>Update</c> / <c>LateUpdate</c> work outside the tick driver,
  /// and idle/vsync time between frames. So this is the sim-tick
  /// budget, not the wall-clock frame budget.</para>
  ///
  /// <para><b>Skips zero-length ticks</b> (the degenerate first tick
  /// before the Ticker has timed anything). As an
  /// <see cref="ITickableSingleton"/> this no longer fires while the
  /// game is paused, so the guard now mainly covers that first-tick
  /// case; recording a zero would still distort the
  /// <see cref="Core.Diagnostics.PerfStats"/> average and pull P99
  /// toward zero.</para>
  /// </summary>
  public sealed class EngineTickProbe : ITickableSingleton {

    /// <summary>Scope name under which engine sim cost is recorded.
    /// Surfaced in the perf window's per-scope table.</summary>
    public const string ScopeName = "Engine.TickWork";

    private readonly PerfTracker _tracker;
    private readonly Ticker _ticker;

    public EngineTickProbe(PerfTracker tracker, Ticker ticker) {
      _tracker = tracker;
      _ticker = ticker;
    }

    /// <inheritdoc />
    public void Tick() {
      try {
        var seconds = _ticker.LengthOfLastTickInSeconds;
        if (seconds <= 0.0) return;
        _tracker.Record(ScopeName, seconds * 1000.0);
      } catch (System.Exception ex) {
        LifecycleGuard.HandleErrorOnce(
            "EngineTickProbe.Tick", "Subsystem failed", ex, ref _failureLogged);
      }
    }

    private bool _failureLogged;

  }

}
