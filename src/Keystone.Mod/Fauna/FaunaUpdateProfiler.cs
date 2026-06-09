using System.Diagnostics;
using Keystone.Mod.Diagnostics;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Per-frame aggregator for the cost of every fauna agent's
  /// <see cref="BaseFaunaAgent.Update"/>. Each agent is an
  /// <c>IUpdatableComponent</c> ticked individually by the engine, so
  /// its per-frame cost is otherwise invisible to the Keystone perf
  /// window (it folds into Unity's update pass, attributed to nothing).
  /// This closes that blind spot the same way the rolling-sweep tickers
  /// and <c>WetlandMistDirector</c> are tracked.
  ///
  /// <para><b>Why aggregate rather than per-agent <c>Track</c>.</b>
  /// Wrapping each agent's update in its own <c>PerfTracker.Track</c>
  /// scope would emit one sample <i>per agent per frame</i> — the
  /// row's avg/P99 would then read as "cost of a single agent update"
  /// and the sample ring would churn N× per frame. Summing all agents'
  /// elapsed ticks and flushing <b>one</b> sample per frame instead
  /// makes the <c>Fauna.AgentUpdate</c> row's avg/P99/max read as the
  /// <i>total per-frame fauna update cost</i> — which is the number you
  /// want for "how much are the fauna ticks costing," and whose max
  /// column catches the spike frames directly.</para>
  ///
  /// <para><b>Threading / ordering.</b> Both
  /// <c>IUpdatableComponent.Update</c> and
  /// <see cref="IUpdatableSingleton.UpdateSingleton"/> run on the main
  /// thread in distinct per-frame passes (never interleaved), so the
  /// unsynchronised accumulate is safe and <see cref="UpdateSingleton"/>
  /// flushes a whole frame's accumulation at once — at most a one-frame
  /// phase offset between accumulation and flush, irrelevant to the
  /// rolling avg/P99/max.</para>
  /// </summary>
  public sealed class FaunaUpdateProfiler : IUpdatableSingleton {

    #region Constants

    /// <summary>Timer scope: total fauna-agent <c>Update</c> ms per
    /// frame.</summary>
    private const string UpdateScope = "Fauna.AgentUpdate";

    /// <summary>Counter scope: how many agents contributed to the
    /// frame's total (lets you read per-agent avg = total / agents).</summary>
    private const string AgentCountScope = UpdateScope + ".Agents";

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    #endregion

    #region Fields

    private readonly PerfTracker _perf;
    private long _accumTicks;
    private int _accumCount;

    #endregion

    #region Construction

    public FaunaUpdateProfiler(PerfTracker perf) {
      _perf = perf;
    }

    #endregion

    #region API

    /// <summary>Add one agent's measured <c>Update</c> duration (in
    /// <see cref="Stopwatch"/> ticks) to the current frame's running
    /// total. Called by <see cref="BaseFaunaAgent.Update"/> once per
    /// agent per frame.</summary>
    public void Record(long elapsedTicks) {
      _accumTicks += elapsedTicks;
      _accumCount++;
    }

    #endregion

    #region IUpdatableSingleton

    /// <summary>Flush the accumulated frame total as a single perf
    /// sample and reset. No-op on frames where no agent ran (keeps the
    /// row from filling with zero samples when there are no fauna).</summary>
    public void UpdateSingleton() {
      if (_accumCount > 0) {
        _perf.Record(UpdateScope, _accumTicks * TicksToMs);
        _perf.RecordCount(AgentCountScope, _accumCount);
      }
      _accumTicks = 0;
      _accumCount = 0;
    }

    #endregion

  }

}
