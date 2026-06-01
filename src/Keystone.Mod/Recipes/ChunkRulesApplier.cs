using System.Collections.Generic;
using System.Diagnostics;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Settings;
using Keystone.Mod.Surface;
using Keystone.Mod.Sweep;
using UnityEngine;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Per-cycle scheduler that visits every <c>(region, chunk)</c> pair
  /// in randomised order and applies the active level's rules to it
  /// via the registered <see cref="IRuleHandler"/> handlers.
  ///
  /// <para><b>One scheduler for all rule application.</b> Replaces
  /// the four prior per-class <c>RollingSweepTicker</c>-based
  /// reconcilers, each of which independently walked every surveyed
  /// surface and re-derived the chunk's biome+level at each tile.
  /// This applier shares the surveyed-surface walk across all four
  /// classes via per-class <see cref="IRuleHandler"/> plug-ins; the
  /// biome+level lookup itself still happens per surface (see
  /// <see cref="ProcessUnit"/>) so the bilinear smoothing in
  /// <c>ChunkBiomeSampler</c> is not defeated.</para>
  ///
  /// <para><b>Companion scheduler (separate concern):</b>
  /// <c>ChunkBiomeTicker</c> updates per-chunk Suitability and
  /// Maturity at a higher cadence than rule application. The
  /// two-scheduler split: value-updating frequent (don't miss
  /// ecological events), rules-applying less frequent (entity
  /// births/deaths shouldn't flicker).</para>
  ///
  /// <para><b>Pass flow per cycle:</b>
  /// <list type="number">
  ///   <item><see cref="BuildSchedule"/> enumerates every non-settled
  ///         region with an ecology field and adds one
  ///         <see cref="ChunkCoord"/> per chunk in the field.
  ///         Handlers' <see cref="IRuleHandler.OnCycleStart"/> hooks
  ///         fire before enumeration.</item>
  ///   <item>The base class shuffles the schedule (Fisher-Yates,
  ///         deterministic per-cycle) so visits are randomised
  ///         within the cycle.</item>
  ///   <item>For each <see cref="ChunkCoord"/>, <see cref="ProcessUnit"/>
  ///         walks every surface in the chunk that belongs to this
  ///         region (and isn't player-marked), resolves that surface's
  ///         dominant biome + maturity per tile, iterates the
  ///         winner's active levels, and invokes every handler's
  ///         <see cref="IRuleHandler.OnUnit"/> for each
  ///         <c>(surface, level)</c>.</item>
  ///   <item>When the schedule drains,
  ///         <see cref="IRuleHandler.OnCycleComplete"/> fires on each
  ///         handler.</item>
  /// </list></para>
  ///
  /// <para><b>Chunk ownership.</b> A single x/y chunk can host
  /// surfaces in multiple regions when terrain has vertical
  /// separations. Each region's field counts that chunk, so the
  /// applier processes it once per <c>(region, chunk)</c> pair and
  /// only touches surfaces belonging to that region.</para>
  /// </summary>
  public sealed class ChunkRulesApplier : RollingSweepTicker<ChunkCoord> {

    #region Constants

    // Cycle duration is player-tunable via
    // KeystonePerformanceSettings.RulesUpdateDays (1–4 game-days).
    // Consulted lazily through the Func<float> overload of
    // RollingSweep's base constructor so changes land at the next
    // cycle boundary without a reload.

    #endregion

    #region Fields

    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly TerrainSurveyor _surveyor;
    private readonly BiomeLevelTable _levels;
    private readonly IPlantingMarkQuery _marks;
    private readonly List<IRuleHandler> _handlers;
    private readonly RiparianTileQuery _riparian;

    /// <summary>Local reference to <see cref="PerfTracker"/> for the
    /// per-chunk sub-scopes added inside <see cref="ProcessUnit"/>.
    /// The base <see cref="RollingSweep{TUnit}"/> already holds the
    /// same instance as <c>IPerfScope</c> but doesn't expose it; the
    /// duplicate field avoids a virtual-call detour through the
    /// interface on the hot path.</summary>
    private readonly PerfTracker _perfTracker;

    // Pre-baked sub-scope names so the per-chunk Record calls don't
    // re-concat strings inside the hot path. Match the parent
    // ".Build" / ".Tick" / ".Complete" naming convention from
    // RollingSweep so all ChunkRulesApplier scopes share a prefix
    // in the perf window.
    // Sub-scope names nest under .Tick so the perf window can render
    // them as an indented tree (Tick → sub-stages → per-handler
    // breakdown). The dot-separator path is what the renderer uses
    // to compute display indent + last-segment column text.
    private const string TickScope = nameof(ChunkRulesApplier) + ".Tick";
    private const string CollectScope = TickScope + ".Collect";
    private const string SampleScope = TickScope + ".Sample";
    private const string DispatchScope = TickScope + ".HandlerDispatch";
    private const string MarksScope = TickScope + ".Marks";
    private const string LevelFilterScope = TickScope + ".LevelFilter";

    /// <summary>Counter scope for the total per-tick <c>OnUnit</c>
    /// dispatch count. Recorded via <c>RecordCount</c> so the perf
    /// window renders its columns as unit counts rather than ms (the
    /// <c>.Units</c> suffix is retained only so the panel's
    /// <c>DisplayName</c> indents it under the HandlerDispatch timer).
    /// Lets us see whether the interest-map filtering is actually
    /// reducing handler invocation count — a drop here means the map
    /// shrunk dispatch; a flat number means the bottleneck is in the
    /// handlers that still get called, not in the dispatch count
    /// itself.</summary>
    private const string DispatchCountScope = DispatchScope + ".Units";

    /// <summary>Scratch for the per-chunk surface collection. Reused
    /// across <see cref="ProcessUnit"/> calls.</summary>
    private readonly List<SurfaceCoord> _surfaceScratch = new();

    /// <summary>
    /// Precomputed inverse map of <c>(biome, levelId) → handler
    /// indices</c>, built lazily on first use from each handler's
    /// <see cref="IRuleHandler.ActiveBuckets"/>. Lets the
    /// per-surface dispatch loop skip handlers that would no-op on
    /// the current bucket — saving roughly N×150 ns per surface
    /// where N is the number of handlers that don't care about this
    /// (biome, level). On boring biomes (Badwater, Lake, Cave) all
    /// 5 handlers would no-op and the whole inner loop is skipped.
    ///
    /// <para>Empty array means "no handler has work for this
    /// bucket" (rare but legal — a biome / level combo with no
    /// authored content). Missing key means the same; we use
    /// TryGetValue and treat both cases identically.</para>
    ///
    /// <para>Built once, never rebuilt: recipes load once at
    /// <see cref="IPostLoadableSingleton.PostLoad"/> time and don't
    /// change afterwards. Build happens lazily in
    /// <see cref="BuildSchedule"/> so we're guaranteed to be past
    /// PostLoad before reading.</para>
    /// </summary>
    private Dictionary<(BiomeKind Biome, string LevelId), int[]>? _interestMap;

    /// <summary>Cached schedule + the <see cref="RegionService.TopologyVersion"/>
    /// it was built against. The (region, chunk) tuple list only
    /// changes when regions split / merge or terrain edits move
    /// region bboxes; otherwise the same chunks get visited every
    /// cycle. Per-cycle work that's actually per-cycle (handler
    /// <see cref="IRuleHandler.OnCycleStart"/> hooks, the shuffle in
    /// the base) stays unconditional.</summary>
    private readonly List<ChunkCoord> _cachedSchedule = new();
    private int _cachedForVersion = -1;

    /// <summary>When <c>true</c>, <see cref="ProcessUnit"/> also fires
    /// handlers for levels declared with <c>RunAtStartup</c>. The
    /// rolling daily cycle keeps this <c>false</c>, so startup-only
    /// levels are silent during regular play. The new-game warmup
    /// flips it via <see cref="RunCycleIncludingStartupNow"/> so the
    /// one-shot geological / worldgen content fires exactly once on
    /// a fresh map.</summary>
    private bool _includeStartupOnly;

    // Per-tick aggregators for the sub-scopes recorded in OnTickEnd.
    // Cleared in OnTickStart, accumulated across every ProcessUnit
    // call drained in the current tick, flushed once at tick end.
    // Per-tick records align directly with the parent .Tick scope's
    // avg/P99/max columns (chunks-per-tick varies) instead of the
    // per-chunk records this used to produce, which required mental
    // multiplication to compare against .Tick.
    private long _tickCollectTicks;
    private long _tickMarksTicks;
    private long _tickSampleTicks;
    private long _tickLevelFilterTicks;
    private long _tickDispatchTicks;
    private long _tickDispatchCalls;

    /// <summary>Per-handler aggregated tick totals + per-handler
    /// scope-name strings. Index aligned with <see cref="_handlers"/>;
    /// allocated in the constructor once the handler list is known.
    /// Lets the dispatch loop break out which of the five handlers is
    /// eating time — after the interest-map shrink reduced raw call
    /// count, what's left is real per-handler work, and per-handler
    /// timing tells us which one to focus on.</summary>
    private long[] _perHandlerTicks = System.Array.Empty<long>();
    private string[] _perHandlerScopeNames = System.Array.Empty<string>();

    #endregion

    #region Construction

    public ChunkRulesApplier(
        RegionService regions,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        TerrainSurveyor surveyor,
        BiomeLevelTable levels,
        IPlantingMarkQuery marks,
        IEnumerable<IRuleHandler> handlers,
        RiparianTileQuery riparian,
        IClock clock,
        PerfTracker perf,
        KeystonePerformanceSettings perfSettings)
        : base(clock, perf, () => perfSettings.RulesUpdateCycleDays) {
      _regions = regions;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _surveyor = surveyor;
      _levels = levels;
      _marks = marks;
      _handlers = new List<IRuleHandler>(handlers);
      _riparian = riparian;
      _perfTracker = perf;

      // Pre-bake the per-handler accumulator array and scope-name
      // strings so the inner dispatch loop does no string work.
      _perHandlerTicks = new long[_handlers.Count];
      _perHandlerScopeNames = new string[_handlers.Count];
      for (var i = 0; i < _handlers.Count; i++) {
        _perHandlerScopeNames[i] =
            DispatchScope + "." + _handlers[i].GetType().Name;
      }
    }

    #endregion

    #region RollingSweepTicker hooks

    /// <inheritdoc />
    protected override bool ShouldRun() {
      for (var i = 0; i < _handlers.Count; i++) {
        if (_handlers[i].ShouldRun()) return true;
      }
      return false;
    }

    /// <inheritdoc />
    protected override void BuildSchedule(List<ChunkCoord> schedule) {
      // Cycle-start hook fires on every handler regardless of whether
      // it'll see work this cycle -- handlers count on it for per-cycle
      // scratch reset (Class A's "seen" set in particular). This is a
      // per-CYCLE concern, not a per-topology one, so it stays
      // unconditional even when the schedule itself is cached.
      for (var i = 0; i < _handlers.Count; i++) {
        _handlers[i].OnCycleStart();
      }

      // Lazy first-use build of the (biome, level) → handler-indices
      // interest map. Cycle-start fires after all PostLoad work, so
      // every handler's catalog is populated by now. Built once,
      // referenced for the lifetime of the applier.
      if (_interestMap == null) BuildInterestMap();

      if (_regions.TopologyVersion != _cachedForVersion) {
        RebuildCachedSchedule();
        _cachedForVersion = _regions.TopologyVersion;
      }
      schedule.AddRange(_cachedSchedule);
    }

    /// <summary>Build the inverse <c>bucket → handler-indices</c>
    /// map from each handler's
    /// <see cref="IRuleHandler.ActiveBuckets"/>. Called once at first
    /// cycle start.</summary>
    private void BuildInterestMap() {
      var temp = new Dictionary<(BiomeKind, string), List<int>>();
      for (var i = 0; i < _handlers.Count; i++) {
        foreach (var bucket in _handlers[i].ActiveBuckets) {
          if (!temp.TryGetValue(bucket, out var list)) {
            list = new List<int>();
            temp[bucket] = list;
          }
          // De-dupe: a handler with multiple recipes in the same
          // bucket still only appears once in its interested list.
          if (list.Count == 0 || list[list.Count - 1] != i) {
            // ActiveBuckets is allowed to emit duplicates; collapsing
            // them here means the dispatch loop never invokes the
            // same handler twice for one (surface, level).
            if (!list.Contains(i)) list.Add(i);
          }
        }
      }
      _interestMap = new Dictionary<(BiomeKind, string), int[]>(temp.Count);
      foreach (var kv in temp) {
        _interestMap[kv.Key] = kv.Value.ToArray();
      }
    }

    private void RebuildCachedSchedule() {
      _cachedSchedule.Clear();
      const int chunkSize = RegionEcologyField.ChunkSize;
      foreach (var region in _regions.All) {
        if (region.IsSettled) continue;
        var field = _fieldQuery.FieldFor(region.Id);
        if (field == null) continue;
        var originChunkX = field.OriginX / chunkSize;
        var originChunkY = field.OriginY / chunkSize;
        for (var cy = 0; cy < field.ChunksY; cy++) {
          for (var cx = 0; cx < field.ChunksX; cx++) {
            _cachedSchedule.Add(new ChunkCoord(region.Id, originChunkX + cx, originChunkY + cy));
          }
        }
      }
    }

    /// <inheritdoc />
    protected override void ProcessUnit(ChunkCoord chunk) {
      var region = _regions.Get(chunk.RegionId);
      if (region == null || region.IsSettled) return;
      var field = _fieldQuery.FieldFor(chunk.RegionId);
      if (field == null) return;

      var tCollect = Stopwatch.GetTimestamp();
      CollectSurfacesInChunk(chunk, _surfaceScratch);
      _tickCollectTicks += Stopwatch.GetTimestamp() - tCollect;
      if (_surfaceScratch.Count == 0) return;

      // Per-chunk locals accumulated into the per-tick fields at the
      // end of ProcessUnit. Cheaper than touching the field on every
      // surface — locals stay in registers, fields don't.
      long sampleTicks = 0;
      long dispatchTicks = 0;
      long marksTicks = 0;
      long levelFilterTicks = 0;

      // Biome dominance is resolved per surface, not per chunk. The
      // ChunkBiomeSampler reads are bilinearly interpolated; calling
      // them once at the chunk centre and reusing the result for all
      // 16 surfaces throws away that smoothing and produces 4-tile
      // hard edges along the chunk grid. The rolling-sweep ticker
      // amortises the extra reads across game-ticks, so per-surface
      // resolution is cheap in absolute terms.
      for (var si = 0; si < _surfaceScratch.Count; si++) {
        var surface = _surfaceScratch[si];

        var tMarks = Stopwatch.GetTimestamp();
        var isMarked = _marks.IsMarked(surface.X, surface.Y, surface.Z);
        marksTicks += Stopwatch.GetTimestamp() - tMarks;
        if (isMarked) continue;

        var tSample = Stopwatch.GetTimestamp();
        // Fold per-tile riparian into dominance: on a clean-near-water
        // tile riparian out-ranks grassland/forest/dry and takes the
        // tile, carrying its per-tile R as the maturity that gates its
        // levels. Safe now that Riparian has its own BiomeLevels +
        // recipe book (KeystoneRiparian blueprint).
        var (ripSuit, ripMat) = _riparian.Sample(chunk.RegionId, surface);
        var (dominant, maturity) = ChunkBiomeSampler.SampleDominantBiome(
            _biomeValues, chunk.RegionId,
            field.OriginX, field.OriginY,
            field.ChunksX, field.ChunksY,
            surface.X, surface.Y,
            ripSuit, ripMat);
        sampleTicks += Stopwatch.GetTimestamp() - tSample;

        if (dominant == null) continue;
        var biome = dominant.Value;

        // .LevelFilter covers the LevelsFor lookup, the level
        // iteration / filtering / progress math, and the interest-
        // map lookup that gates handler dispatch. If it grows with
        // playtime, biomes maturing into more active levels is the
        // cause; the per-level loop body runs more even when its
        // handler dispatch is cheap.
        var tLevelFilter = Stopwatch.GetTimestamp();
        var levels = _levels.LevelsFor(biome);
        if (levels.Count == 0) {
          levelFilterTicks += Stopwatch.GetTimestamp() - tLevelFilter;
          continue;
        }

        for (var li = 0; li < levels.Count; li++) {
          var level = levels[li];
          if (level.RunAtStartup && !_includeStartupOnly) continue;
          if (maturity < level.LowerMaturity) continue;

          // Interest-map gate: skip the whole inner work (progress
          // compute + handler dispatch) when no handler has any
          // recipe for this (biome, levelId) bucket. Saves the
          // virtual-call + dict-lookup overhead of N handlers each
          // independently returning "nothing to do for me here."
          if (!_interestMap!.TryGetValue((biome, level.LevelId), out var interestedHandlers)
              || interestedHandlers.Length == 0) {
            continue;
          }

          // Saturation fraction across the level's maturity range,
          // clamped to [0, 1]. Spawn handlers multiply Density by this
          // so activation ramps in linearly rather than snapping to
          // full strength the moment maturity crosses LowerMaturity.
          // The denominator is defended against zero/negative even
          // though BiomeLevel's contract is Upper > Lower; treating
          // a degenerate range as "always saturated" is the least
          // surprising fallback.
          //
          // RunAtStartup levels (worldgen rock clusters etc.) are
          // one-shot snapshots at map gen, not maturity-progressive
          // — the ramp has no meaning for them since they fire exactly
          // once and there's no "progression" to ramp through.
          // Force progress=1 so they fire at full Density regardless
          // of whatever Maturity happens to be when the startup pass
          // runs.
          float progress;
          if (level.RunAtStartup) {
            progress = 1f;
          } else {
            var range = level.UpperMaturity - level.LowerMaturity;
            progress = range > 0f
                ? Mathf.Clamp01((maturity - level.LowerMaturity) / range)
                : 1f;
          }
          // Pause the LevelFilter clock for the duration of the
          // dispatch so the two scopes don't double-count the same
          // wall-clock interval.
          levelFilterTicks += Stopwatch.GetTimestamp() - tLevelFilter;

          var tDispatch = Stopwatch.GetTimestamp();
          for (var hi = 0; hi < interestedHandlers.Length; hi++) {
            var idx = interestedHandlers[hi];
            var tHandler = Stopwatch.GetTimestamp();
            _handlers[idx].OnUnit(surface, biome, level, progress);
            _perHandlerTicks[idx] += Stopwatch.GetTimestamp() - tHandler;
          }
          dispatchTicks += Stopwatch.GetTimestamp() - tDispatch;
          _tickDispatchCalls += interestedHandlers.Length;

          tLevelFilter = Stopwatch.GetTimestamp();
        }
        levelFilterTicks += Stopwatch.GetTimestamp() - tLevelFilter;
      }

      _tickSampleTicks += sampleTicks;
      _tickDispatchTicks += dispatchTicks;
      _tickMarksTicks += marksTicks;
      _tickLevelFilterTicks += levelFilterTicks;
    }

    /// <inheritdoc />
    protected override void OnTickStart() {
      _tickCollectTicks = 0;
      _tickSampleTicks = 0;
      _tickMarksTicks = 0;
      _tickLevelFilterTicks = 0;
      _tickDispatchTicks = 0;
      _tickDispatchCalls = 0;
      for (var i = 0; i < _perHandlerTicks.Length; i++) _perHandlerTicks[i] = 0;
    }

    /// <inheritdoc />
    protected override void OnTickEnd() {
      _perfTracker.Record(CollectScope, TicksToMs(_tickCollectTicks));
      _perfTracker.Record(SampleScope, TicksToMs(_tickSampleTicks));
      _perfTracker.Record(MarksScope, TicksToMs(_tickMarksTicks));
      _perfTracker.Record(LevelFilterScope, TicksToMs(_tickLevelFilterTicks));
      _perfTracker.Record(DispatchScope, TicksToMs(_tickDispatchTicks));
      _perfTracker.RecordCount(DispatchCountScope, _tickDispatchCalls);
      for (var i = 0; i < _perHandlerTicks.Length; i++) {
        _perfTracker.Record(_perHandlerScopeNames[i], TicksToMs(_perHandlerTicks[i]));
        _handlers[i].OnTickEnd();
      }
    }

    /// <summary>Convert raw <see cref="Stopwatch"/> tick delta to
    /// milliseconds. Same arithmetic as <see cref="Stopwatch"/>'s
    /// own conversion, exposed here so the per-chunk aggregator
    /// produces stats in the units <see cref="PerfTracker.Record"/>
    /// expects.</summary>
    private static double TicksToMs(long ticks) =>
        (double)ticks * 1000.0 / Stopwatch.Frequency;

    /// <inheritdoc />
    protected override void OnCycleComplete() {
      for (var i = 0; i < _handlers.Count; i++) {
        _handlers[i].OnCycleComplete();
      }
    }

    #endregion

    #region Startup pass

    /// <summary>Drain a synchronous cycle that *includes* levels marked
    /// <c>RunAtStartup</c>, then restore the regular filter. Intended
    /// to be called exactly once on a fresh map from
    /// <c>KeystoneStartupWarmup</c>; the normal rolling-sweep cadence
    /// continues to skip startup-only levels.
    /// <para>Behaves like the base <c>RunCycleNow</c> in every other
    /// respect (handlers' OnCycleStart / OnCycleComplete hooks fire,
    /// schedule is built + shuffled + drained synchronously).</para></summary>
    public void RunCycleIncludingStartupNow() {
      _includeStartupOnly = true;
      try {
        RunCycleNow();
      } finally {
        _includeStartupOnly = false;
      }
    }

    #endregion

    #region Helpers

    /// <summary>Walk every tile in the 4×4 chunk, look up its surveyed
    /// surface heights, and add each <see cref="SurfaceCoord"/> whose
    /// region matches <paramref name="chunk"/>'s region. Multi-region
    /// columns are common at terrain-step boundaries; without this
    /// filter we'd process the same chunk's surfaces multiple times
    /// (once per region in the column).</summary>
    private void CollectSurfacesInChunk(ChunkCoord chunk, List<SurfaceCoord> scratch) {
      scratch.Clear();
      const int chunkSize = RegionEcologyField.ChunkSize;
      var startX = chunk.GlobalChunkX * chunkSize;
      var startY = chunk.GlobalChunkY * chunkSize;
      for (var dy = 0; dy < chunkSize; dy++) {
        for (var dx = 0; dx < chunkSize; dx++) {
          var column = new TileCoord(startX + dx, startY + dy);
          var heights = _surveyor.ColumnSurfaceHeights(column);
          for (var i = 0; i < heights.Count; i++) {
            var surface = new SurfaceCoord(column.X, column.Y, heights[i]);
            var surfaceRegion = _regions.Containing(surface);
            if (surfaceRegion != null && surfaceRegion.Id == chunk.RegionId) {
              scratch.Add(surface);
            }
          }
        }
      }
    }

    #endregion

  }

}
