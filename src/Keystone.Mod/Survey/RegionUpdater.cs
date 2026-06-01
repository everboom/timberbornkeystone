using System;
using System.Collections.Generic;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Core.Tiles;
using Keystone.Mod.Adapters;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.SingletonSystem;
using Timberborn.TerrainSystem;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Survey {

  /// <summary>
  /// Keeps the region graph in sync with the live terrain and block-object
  /// state. Subscribes to <see cref="ITerrainService.TerrainHeightChanged"/>
  /// (terrain edits) plus <see cref="BlockObjectSetEvent"/> /
  /// <see cref="BlockObjectUnsetEvent"/> via the <see cref="EventBus"/>
  /// (building placement / demolition). Accumulates dirty columns in a
  /// HashSet and flushes on a debounced cadence:
  ///
  /// <list type="bullet">
  ///   <item>Quiet-period: flush when no event has arrived for
  ///         <see cref="QuietPeriodTicks"/>. Optimises for the common case
  ///         of a chain of edits (e.g., dynamite propagation) where we'd
  ///         rather wait until things settle than process intermediate
  ///         topologies.</item>
  ///   <item>Max-latency: flush at least every
  ///         <see cref="MaxLatencyTicks"/> regardless, so a continuous
  ///         trickle of events doesn't starve the flush forever.</item>
  /// </list>
  ///
  /// Per-event work is just a HashSet add. Per-flush work is bounded by
  /// the affected column count (resurvey) plus the cost of incremental
  /// region updates (typically O(1) per affected surface, with rare
  /// O(region) split-checks).
  /// </summary>
  public sealed class RegionUpdater : ILoadableSingleton, IUnloadableSingleton, ITickableSingleton {

    #region Constants

    /// <summary>Flush after this many ticks of silence following the last event. ~2s at 1x speed (5 ticks/sec).</summary>
    public const int QuietPeriodTicks = 10;

    /// <summary>Flush at least this often even if events keep arriving. ~5s at 1x speed -- bounds player-perceived staleness.</summary>
    public const int MaxLatencyTicks = 25;

    #endregion

    #region Fields

    private readonly ITerrainService _terrain;
    private readonly EventBus _eventBus;
    private readonly TerrainSurveyor _surveyor;
    private readonly RegionService _regions;
    private readonly ChunkReconciler _reconciler;
    private readonly PerfTracker _perf;

    private readonly HashSet<TileCoord> _dirtyColumns = new();
    private int _ticksSinceLastEvent;
    private int _ticksSinceLastFlush;
    private int _eventsSinceLastFlush;
    // Source attribution for the dirty set, so a large flush can be traced to
    // its cause: terrain-height edits (dynamite, leveling, platforms — one
    // event per tile) versus block-object set/unset (buildings/paths — one per
    // BO, each marking footprint + 8-neighbour halo). Reset each flush.
    private int _terrainEventsSinceLastFlush;
    private int _blockEventsSinceLastFlush;

    #endregion

    #region Construction

    public RegionUpdater(
        ITerrainService terrain,
        EventBus eventBus,
        TerrainSurveyor surveyor,
        RegionService regions,
        ChunkReconciler reconciler,
        PerfTracker perf) {
      _terrain = terrain;
      _eventBus = eventBus;
      _surveyor = surveyor;
      _regions = regions;
      _reconciler = reconciler;
      _perf = perf;
    }

    #endregion

    #region ILoadableSingleton / IUnloadableSingleton

    /// <inheritdoc />
    public void Load() {
      _terrain.TerrainHeightChanged += OnTerrainHeightChanged;
      _eventBus.Register(this);
    }

    /// <inheritdoc />
    public void Unload() {
      _terrain.TerrainHeightChanged -= OnTerrainHeightChanged;
      _eventBus.Unregister(this);
    }

    #endregion

    #region ITickableSingleton

    /// <inheritdoc />
    public void Tick() {
      // Outermost try/catch: a Flush failure (per-column resurvey or
      // region service mutation throwing) would otherwise let Bindito
      // drop us from the tick list. Region topology then never updates
      // for the rest of the session and downstream readers (cluster
      // index, biome scoring) drift further from reality each tick.
      // Rate-limited so a persistent failure doesn't spam every tick.
      try {
        _ticksSinceLastEvent++;
        _ticksSinceLastFlush++;

        if (_dirtyColumns.Count == 0) {
          return;
        }

        var quietPeriodElapsed = _ticksSinceLastEvent >= QuietPeriodTicks;
        var maxLatencyExceeded = _ticksSinceLastFlush >= MaxLatencyTicks;
        if (!quietPeriodElapsed && !maxLatencyExceeded) {
          return;
        }

        // Record only when an actual flush is going to fire; gated entries
        // would dilute the average toward zero.
        using var _ = _perf.Track(nameof(RegionUpdater) + ".Tick");
        var trigger = quietPeriodElapsed ? "quiet" : "max-latency";
        Flush(trigger);
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorOnce(
            "RegionUpdater.Tick", "Subsystem failed", ex, ref _tickFailureLogged);
      }
    }

    private bool _tickFailureLogged;

    #endregion

    #region Event handler

    private void OnTerrainHeightChanged(object sender, TerrainHeightChangeEventArgs args) {
      var c = args.Change.Coordinates;
      _dirtyColumns.Add(new TileCoord(c.x, c.y));
      _ticksSinceLastEvent = 0;
      _eventsSinceLastFlush++;
      _terrainEventsSinceLastFlush++;
    }

    /// <summary>
    /// Building / path placed. Mark every column the block object
    /// covers as dirty (footprint can span multiple columns for
    /// multi-tile buildings).
    /// </summary>
    [OnEvent]
    public void OnBlockObjectSet(BlockObjectSetEvent e) {
      MarkBlockObjectColumnsDirty(e.BlockObject, "Set");
    }

    /// <summary>
    /// Building / path demolished. Same column-marking logic as set.
    /// </summary>
    [OnEvent]
    public void OnBlockObjectUnset(BlockObjectUnsetEvent e) {
      MarkBlockObjectColumnsDirty(e.BlockObject, "Unset");
    }

    /// <summary>
    /// Pause was just applied (CurrentSpeed transitioned to 0). Force any
    /// pending dirty columns through immediately so anything the player
    /// inspects while paused -- debug overlay, hover panel, future UI --
    /// reflects current terrain instead of a debounce-window snapshot.
    /// Idempotent; cheap when nothing is dirty.
    /// </summary>
    [OnEvent]
    public void OnCurrentSpeedChanged(CurrentSpeedChangedEvent e) {
      if (e.CurrentSpeed == 0f) {
        FlushPending("pause");
      }
    }

    private void MarkBlockObjectColumnsDirty(BlockObject blockObject, string what) {
      // Naturals (trees, crops, gatherables, etc.) don't affect region
      // structure -- they're not Buildings, not Paths, not caves, not
      // height-changing. Skipping them avoids both pointless flushes
      // and event-spam during world load (every tree fires a Set event).
      // Exception: the curated set of natural impassables (blockages,
      // dams, etc.) DO affect region structure -- their tile leaves /
      // joins the region graph when they appear / disappear. Run those
      // through the dirty path like buildings.
      if (BlockObjectClassification.IsSkippableForRegions(blockObject)) {
        return;
      }

      var cols = new HashSet<TileCoord>();
      foreach (var coord in blockObject.PositionedBlocks.GetAllCoordinates()) {
        cols.Add(new TileCoord(coord.x, coord.y));
        // Halo rule: changing a building flips IsSettled on its 8-neighbour
        // columns too (they were either inside the halo or not). Dirty
        // those columns so they re-classify on the next flush; otherwise
        // the region graph drifts -- neighbours stay in the wrong Settled
        // bucket until something else happens to dirty them. We expand
        // the halo for every non-natural BO (paths included). Path
        // placement doesn't actually change any halo column's Settled
        // state under the current rule, so this is mildly wasteful for
        // paths -- but the dirty-column refresh is cheap, and keeping a
        // single rule is simpler than branching on Building vs Path here.
        for (var dx = -1; dx <= 1; dx++) {
          for (var dy = -1; dy <= 1; dy++) {
            if (dx == 0 && dy == 0) continue;
            cols.Add(new TileCoord(coord.x + dx, coord.y + dy));
          }
        }
      }
      foreach (var col in cols) {
        _dirtyColumns.Add(col);
      }
      _ticksSinceLastEvent = 0;
      _eventsSinceLastFlush++;
      _blockEventsSinceLastFlush++;
    }

    #endregion

    #region Flush

    /// <summary>
    /// Force any pending dirty columns through the surveyor + indexer
    /// immediately, instead of waiting for the debounced cadence.
    /// Idempotent: a no-op when there are no dirty columns. Used by
    /// <c>KeystonePersistence.Save</c> so the snapshot reflects current
    /// terrain even if the player saves seconds after a terrain edit.
    /// </summary>
    public void FlushPending(string trigger) {
      if (_dirtyColumns.Count == 0) return;
      Flush(trigger);
    }

    private void Flush(string trigger) {
      using var _ = _perf.Track(nameof(RegionUpdater) + ".Flush");
      var sw = System.Diagnostics.Stopwatch.StartNew();
      var allDetached = new List<SurfaceCoord>();
      var allAttached = new List<SurfaceCoord>();

      var classifyCallsBefore = _surveyor.SettledClassifyCalls;
      using (_perf.Track(nameof(RegionUpdater) + ".Flush.Resurvey")) {
        foreach (var col in _dirtyColumns) {
          var diff = _surveyor.ResurveyColumn(col);
          if (diff.IsEmpty) continue;
          if (diff.Detached.Count > 0) allDetached.AddRange(diff.Detached);
          if (diff.Attached.Count > 0) allAttached.AddRange(diff.Attached);
        }
      }
      // How many building-port classifications this resurvey issued. Pairs
      // with Flush.Resurvey timing and DirtyColumns: ~9x the column count
      // means the settled-halo ClassifyAt calls dominate (mostly open terrain,
      // no early exits), which a flush-scoped memo cache would collapse ~9x.
      _perf.RecordCount(nameof(RegionUpdater) + ".Flush.ClassifyCalls",
          _surveyor.SettledClassifyCalls - classifyCallsBefore);

      var dirtyCount = _dirtyColumns.Count;
      var eventCount = _eventsSinceLastFlush;
      var ticksElapsed = _ticksSinceLastFlush;
      // Diagnostic: how many columns this flush resurveyed, and what caused
      // them. Pairs with the Flush.Resurvey timing to tell "many columns" from
      // "expensive per column"; the terrain/block split attributes a large
      // dirty set to its source (mass terrain edit vs many building events).
      _perf.RecordCount(nameof(RegionUpdater) + ".DirtyColumns", dirtyCount);
      _perf.RecordCount(nameof(RegionUpdater) + ".TerrainEvents", _terrainEventsSinceLastFlush);
      _perf.RecordCount(nameof(RegionUpdater) + ".BlockEvents", _blockEventsSinceLastFlush);
      _dirtyColumns.Clear();
      _ticksSinceLastFlush = 0;
      _eventsSinceLastFlush = 0;
      _terrainEventsSinceLastFlush = 0;
      _blockEventsSinceLastFlush = 0;

      if (allDetached.Count == 0 && allAttached.Count == 0) {
        // Re-poll only -- pollable fields refreshed in place by ResurveyColumn,
        // no region structure change. Still log so we can see flush cadence.
        sw.Stop();
        KeystoneLog.Verbose($"[Keystone] Flush ({trigger}, {ticksElapsed} ticks, {eventCount} events, {sw.ElapsedMilliseconds} ms): {dirtyCount} dirty col(s), no structural change.");
        return;
      }

      IReadOnlyCollection<RegionId> touched;
      using (_perf.Track(nameof(RegionUpdater) + ".Flush.Process")) {
        touched = _regions.ProcessChanges(allDetached, allAttached, _perf);
      }

      // Re-bind any chunk data whose region ownership just shifted to the
      // region that now physically owns each chunk's (X, Y, Z) footprint,
      // scoped to the regions this flush touched. This is what keeps
      // accumulated biome Maturity attached to the land across splits,
      // merges, and region deaths instead of being stranded under a stale
      // id or wiped — replacing the old per-cycle PruneToValid deletion and
      // the chunk-store split/merge/remove lifecycle handlers.
      ChunkReconcileResult reconcile;
      using (_perf.Track(nameof(RegionUpdater) + ".Flush.Reconcile")) {
        reconcile = _reconciler.ReconcileFromDataStore(
            new HashSet<RegionId>(touched), perf: _perf);
      }
      sw.Stop();

      if (reconcile.Outcome == Keystone.Core.Persistence.ChunkReconcileOutcome.MaturityLost) {
        // The only path that loses real, unrecoverable ecology history.
        // Expected where matured terrain was genuinely removed (no live
        // region at the chunk's footprint/Z any more); loud because a large
        // count on an edit that shouldn't have destroyed matured land is
        // the regression signal. Empty drops are reported quietly below.
        KeystoneLog.Warn(
            $"[Keystone] Flush ({trigger}): chunk reconciliation dropped " +
            $"{reconcile.HomelessDroppedWithMaturity} chunk(s) holding accumulated biome " +
            $"maturity, with no live region at their footprint/Z " +
            $"({reconcile.HomelessDroppedEmpty} more empty chunk(s) dropped, " +
            $"{reconcile.Rehomed} re-homed, {reconcile.CollisionsResolved} collisions). " +
            "Real ecology history lost — expected only where matured terrain was removed; " +
            "a large count on a small edit points at a bug.");
      } else if (reconcile.AnyChange) {
        KeystoneLog.Verbose(
            $"[Keystone] Flush ({trigger}): re-homed {reconcile.Rehomed} chunk(s), dropped " +
            $"{reconcile.HomelessDroppedEmpty} empty chunk(s), {reconcile.CollisionsResolved} " +
            "collisions (no maturity lost).");
      }

      KeystoneLog.Verbose($"[Keystone] Flush ({trigger}, {ticksElapsed} ticks, {eventCount} events, {sw.ElapsedMilliseconds} ms): {dirtyCount} dirty col(s), {allDetached.Count} detached, {allAttached.Count} attached -> {_regions.Count} regions.");
    }

    #endregion

  }

}
