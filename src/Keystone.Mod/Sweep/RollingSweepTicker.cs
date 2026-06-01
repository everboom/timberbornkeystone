using System;
using Keystone.Core.Sweep;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Timberborn.TickSystem;

namespace Keystone.Mod.Sweep {

  /// <summary>
  /// Mod-side base for rolling-sweep tickers. Adds Timberborn's
  /// <see cref="ITickableSingleton"/> marker so the game's tick
  /// dispatch picks the subclass up; the actual amortisation
  /// algorithm lives in <see cref="RollingSweep{TUnit}"/> in
  /// <c>Keystone.Core.Sweep</c> so the cycle-anchoring + drain
  /// behaviour is unit-testable without a Timberborn host.
  ///
  /// <para>Subclasses derive from this type (not from
  /// <see cref="RollingSweep{TUnit}"/> directly) so the
  /// <c>ITickableSingleton.Tick</c> dispatch resolves through the
  /// inherited <see cref="RollingSweep{TUnit}.Tick"/> method.</para>
  /// </summary>
  public abstract class RollingSweepTicker<TUnit>
      : RollingSweep<TUnit>, ITickableSingleton {

    /// <summary>Constant-cadence overload. Use for tickers whose
    /// cadence is fixed at construction (e.g. <c>FaunaCycleTicker</c>
    /// with its compile-time <c>CycleDays</c>).</summary>
    protected RollingSweepTicker(
        IClock clock, PerfTracker perf, float cycleDurationDays, int seed = 0)
        : base(clock, perf, cycleDurationDays, seed) {
      WireUnitErrorHandler();
    }

    /// <summary>Dynamic-cadence overload. Use for tickers whose
    /// cadence is player-tunable through a <c>ModSetting</c> whose
    /// persisted value may not be hydrated yet at the moment Bindito
    /// constructs the ticker — see the
    /// <see cref="RollingSweep{TUnit}"/> dynamic-cadence constructor
    /// for the lazy-read semantics.</summary>
    protected RollingSweepTicker(
        IClock clock, PerfTracker perf, Func<float> cycleDurationDaysProvider,
        int seed = 0)
        : base(clock, perf, cycleDurationDaysProvider, seed) {
      WireUnitErrorHandler();
    }

    /// <summary>One-shot set-up: wires <see cref="RollingSweep{TUnit}.OnUnitError"/>
    /// to a callback that logs the exception and records to
    /// <see cref="KeystoneIntegrationHealth"/> under a Note-severity
    /// category (one failing unit is localised, not "Keystone is
    /// broken"). Log lines are rate-limited by exception type per
    /// ticker instance to avoid per-tick spam when one unit fails
    /// persistently.</summary>
    private void WireUnitErrorHandler() {
      var loggedExceptions = new System.Collections.Generic.HashSet<string>(
          System.StringComparer.Ordinal);
      var typeName = GetType().Name;
      OnUnitError = (_, ex) => {
        LifecycleGuard.HandleErrorByType(
            typeName + ".ProcessUnit", "Per-tick errors", ex, loggedExceptions);
      };
    }

  }

}
