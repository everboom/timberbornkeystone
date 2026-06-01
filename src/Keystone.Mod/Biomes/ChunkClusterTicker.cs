using System.Collections.Generic;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Regions;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Settings;
using Keystone.Mod.Sweep;

namespace Keystone.Mod.Biomes {

  /// <summary>
  /// Drives <see cref="ChunkClusterIndex"/>'s rebuild on an amortised
  /// rolling-sweep cadence. Same cycle duration as
  /// <see cref="ChunkBiomeTicker"/> (1–4 game-hours, configurable via
  /// <see cref="Keystone.Mod.Settings.KeystonePerformanceSettings.MapUpdateHours"/>;
  /// default 1); each cycle visits every live region once, folding it
  /// into a shadow index. At cycle end the shadow is swapped in as the
  /// new live snapshot.
  ///
  /// <para><b>Why a separate ticker.</b> Cluster rebuild used to fire
  /// atomically at the end of <see cref="ChunkBiomeTicker"/>'s cycle
  /// (the old <c>RebuildClusterIndexNow</c>) and was the dominant cost
  /// in <c>ChunkBiomeTicker.Complete</c> on large maps — a single-
  /// frame spike that surfaced as visible stutter at game-hour
  /// boundaries. Lifting the rebuild into its own rolling-sweep
  /// ticker spreads the per-region fold across the cycle's many
  /// frames; the per-frame budget no longer sees the spike. Total
  /// CPU work is unchanged (modulo the filters and quadratic-loop
  /// fixes shipped separately).</para>
  ///
  /// <para><b>Snapshot consistency.</b> The shadow buffers live inside
  /// <see cref="ChunkClusterIndex"/>; queries (<c>ClusterFor</c>,
  /// <c>BiomeFor</c>, etc.) always read the live snapshot from the
  /// last <c>CommitRebuild</c>. Mid-cycle reads see the previous
  /// cycle's clusters consistently — never a partially-built
  /// shadow.</para>
  ///
  /// <para><b>Coupling to <see cref="ChunkBiomeTicker"/>.</b> Both
  /// tickers run on the same map-update cadence (so they stay in sync
  /// when the player widens the cycle to 4 game-hours) and visit chunks
  /// / regions in randomised order. During a cycle, the biome ticker is
  /// still writing Suitability + Maturity values to
  /// <c>ChunkValueStore</c> while this ticker reads them. The result
  /// is a fold over a continuously-updating snapshot rather than a
  /// strict end-of-cycle snapshot — acceptable since consumers (fauna
  /// agents, Nature need) already tolerate cycle-grain staleness, and
  /// no consumer requires cross-chunk consistency at finer than
  /// cluster-level granularity.</para>
  ///
  /// <para><b>Synchronous warmup.</b>
  /// <see cref="RollingSweepTicker{TUnit}.RunCycleNow"/> drives the
  /// same Begin → Include* → Commit code path as the steady-state
  /// rolling sweep, just drained in one call. Used by
  /// <c>KeystoneStartupWarmup</c> so the cluster index is populated
  /// before the first gameplay frame.</para>
  /// </summary>
  public sealed class ChunkClusterTicker : RollingSweepTicker<RegionId> {

    #region Constants

    /// <summary>Minimum Maturity (game-days) the chunk's dominant
    /// biome must have accrued for the chunk to join a cluster.
    /// Forwarded to <see cref="ChunkClusterIndex.IncludeRegionInRebuild"/>
    /// on every region. v1 global constant; promote to per-biome or
    /// per-consumer when a reason emerges.
    /// <para>Previously lived on <see cref="ChunkBiomeTicker"/> back
    /// when that ticker drove the cluster rebuild directly; moved here
    /// alongside the driver.</para></summary>
    private const float ClusterMaturityThreshold = 1.0f;

    #endregion

    #region Fields

    private readonly RegionService _regions;
    private readonly ChunkClusterIndex _clusterIndex;

    /// <summary>Cached schedule + the <see cref="RegionService.TopologyVersion"/>
    /// it was built against. The region set only changes when regions
    /// split / merge or terrain edits move region bboxes; otherwise
    /// the same region ids get visited every cycle. Matches
    /// <see cref="ChunkBiomeTicker"/>'s caching shape.</summary>
    private readonly List<RegionId> _cachedSchedule = new();
    private int _cachedForTopologyVersion = -1;

    #endregion

    #region Construction

    public ChunkClusterTicker(
        RegionService regions,
        ChunkClusterIndex clusterIndex,
        IClock clock,
        PerfTracker perf,
        KeystonePerformanceSettings perfSettings)
        : base(clock, perf, () => perfSettings.MapUpdateCycleDays) {
      _regions = regions;
      _clusterIndex = clusterIndex;
    }

    #endregion

    #region RollingSweepTicker overrides

    /// <inheritdoc />
    /// <remarks>Also opens the rebuild here: <see cref="BuildSchedule"/>
    /// fires once at the start of each cycle, before any
    /// <see cref="ProcessUnit"/> call, which is exactly when the shadow
    /// needs to be allocated. The pairing with
    /// <see cref="OnCycleComplete"/>'s <c>CommitRebuild</c> matches the
    /// rolling sweep base's natural cycle boundaries.</remarks>
    protected override void BuildSchedule(List<RegionId> schedule) {
      var topologyVersion = _regions.TopologyVersion;
      if (topologyVersion != _cachedForTopologyVersion) {
        _cachedSchedule.Clear();
        foreach (var region in _regions.All) _cachedSchedule.Add(region.Id);
        _cachedForTopologyVersion = topologyVersion;
      }
      schedule.AddRange(_cachedSchedule);
      _clusterIndex.BeginRebuild();
    }

    /// <inheritdoc />
    protected override void ProcessUnit(RegionId unit) {
      // Per-region failures are surfaced as Player.log errors but must
      // not abort the cycle — the shadow is partially populated and
      // CommitRebuild still has to fire to swap a consistent snapshot
      // in (even if it's missing one region). Mirrors the defensive
      // wrap on the old RebuildClusterIndexNow path.
      try {
        _clusterIndex.IncludeRegionInRebuild(unit, ClusterMaturityThreshold);
      } catch (System.Exception ex) {
        KeystoneLog.Error(
            $"[Keystone] ChunkClusterTicker: IncludeRegionInRebuild threw for region " +
            $"{unit}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
      }
    }

    /// <inheritdoc />
    protected override void OnCycleComplete() {
      try {
        _clusterIndex.CommitRebuild();
      } catch (System.Exception ex) {
        KeystoneLog.Error(
            $"[Keystone] ChunkClusterTicker: CommitRebuild threw " +
            $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
      }

      // Diagnostic: confirms the cluster ticker is actually completing
      // cycles. Useful when investigating "no clusters" reports — if
      // this line doesn't appear after game-time elapses, the rolling-
      // sweep base isn't firing.
      KeystoneLog.Verbose(
          $"[Keystone] ChunkClusterTicker cycle complete: " +
          $"regions={_cachedSchedule.Count}, clusters={_clusterIndex.ClusterCount}.");
    }

    #endregion

  }

}
