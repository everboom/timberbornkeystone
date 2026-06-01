using System;
using System.Collections.Generic;
using System.Diagnostics;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Settings;
using Keystone.Mod.Sweep;
using Timberborn.Multithreading;
using Timberborn.TickSystem;

namespace Keystone.Mod.Biomes {

  /// <summary>
  /// Per-chunk biome value ticker. Walks every region's valid chunks
  /// once per map-update cycle, captures inputs on the main thread,
  /// and advances each chunk's drift dynamics on background worker
  /// threads via <see cref="IParallelizer"/>.
  ///
  /// <para><b>Execution model.</b> Each tick the rolling-sweep drain
  /// loop calls <see cref="ProcessUnit"/> on the main thread, which
  /// builds <see cref="ChunkBiomeInputs"/> (game-state reads) and
  /// appends them to a filling batch. At
  /// <see cref="StartParallelTick"/>, the batch is swapped to the
  /// inflight buffer and scheduled on worker threads. Workers run
  /// <see cref="BiomeSuitabilityUpdater.Tick"/> and
  /// <see cref="BiomeMaturityUpdater.Tick"/> — pure math on per-chunk
  /// <see cref="ChunkData"/> arrays. At the next tick's
  /// <see cref="OnTickStart"/>, completed results are synced to
  /// <see cref="ChunkValueStore"/>. One-tick pipeline latency, same
  /// pattern as vanilla parallel systems.</para>
  ///
  /// <para><b>Thread safety.</b> Each chunk in the batch maps to a
  /// distinct <see cref="ChunkData"/> instance. No two workers share
  /// a <see cref="ChunkData"/>. Both updaters are stateless (zero
  /// instance fields). Game-state reads and
  /// <see cref="ChunkValueStore"/> sync happen only on the main
  /// thread.</para>
  /// </summary>
  public sealed class ChunkBiomeTicker
      : RollingSweepTicker<ChunkBiomeTicker.ChunkUnit>,
        IParallelTickableSingleton {

    #region Constants

    private const int InitialBatchCapacity = 64;
    private const int ParallelBatchSize = 4;
    internal const string ParallelScopeName = "ChunkBiomeTicker.Parallel";

    #endregion

    #region Fields

    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly ChunkValueStore _chunkValues;
    private readonly ChunkDataStore _chunkData;
    private readonly ChunkBiomeAdapter _adapter;
    private readonly BiomeSuitabilityUpdater _suitabilityUpdater;
    private readonly BiomeMaturityUpdater _maturityUpdater;
    private readonly IClock _clock;
    private readonly IParallelizer _parallelizer;
    private readonly PerfTracker _perf;

    private readonly List<ChunkUnit> _cachedSchedule = new();
    private int _cachedForTopologyVersion = -1;
    private int _cachedForFieldShapeVersion = -1;

    #endregion

    #region Double-buffered batch

    private ChunkBiomeInputs[] _fillingInputs = new ChunkBiomeInputs[InitialBatchCapacity];
    private ChunkData[] _fillingData = new ChunkData[InitialBatchCapacity];
    private ChunkUnit[] _fillingUnits = new ChunkUnit[InitialBatchCapacity];
    private int _fillingCount;

    private ChunkBiomeInputs[] _inflightInputs = new ChunkBiomeInputs[InitialBatchCapacity];
    private ChunkData[] _inflightData = new ChunkData[InitialBatchCapacity];
    private ChunkUnit[] _inflightUnits = new ChunkUnit[InitialBatchCapacity];
    private int _inflightCount;
    private float _inflightCycleDt;
    private float _inflightTimestamp;
    private long _dispatchTicks;

    #endregion

    #region Types

    /// <summary>One chunk's worth of work in the cycle schedule. Carries
    /// the owning region's <see cref="Region.Z"/> so the writer can stamp
    /// it onto <see cref="ChunkData.Z"/> without a per-chunk region lookup
    /// — the carried Z is what lets the chunk reconcile to a new owner if
    /// this region later dies (see <c>ChunkReconciler</c>).</summary>
    public readonly struct ChunkUnit {
      public readonly RegionId RegionId;
      public readonly int GlobalCx;
      public readonly int GlobalCy;
      public readonly int Z;
      public ChunkUnit(RegionId regionId, int globalCx, int globalCy, int z) {
        RegionId = regionId;
        GlobalCx = globalCx;
        GlobalCy = globalCy;
        Z = z;
      }
    }

    /// <summary>Worker-thread task: runs suitability + maturity
    /// updaters on a batch of pre-captured inputs.</summary>
    private readonly struct BiomeComputeTask : IParallelizerLoopTask {

      private readonly ChunkBiomeInputs[] _inputs;
      private readonly ChunkData[] _data;
      private readonly float _cycleDt;
      private readonly float _timestamp;
      private readonly BiomeSuitabilityUpdater _suitabilityUpdater;
      private readonly BiomeMaturityUpdater _maturityUpdater;

      public BiomeComputeTask(
          ChunkBiomeInputs[] inputs,
          ChunkData[] data,
          float cycleDt,
          float timestamp,
          BiomeSuitabilityUpdater suitabilityUpdater,
          BiomeMaturityUpdater maturityUpdater) {
        _inputs = inputs;
        _data = data;
        _cycleDt = cycleDt;
        _timestamp = timestamp;
        _suitabilityUpdater = suitabilityUpdater;
        _maturityUpdater = maturityUpdater;
      }

      public void Run(int iteration) {
        var data = _data[iteration];
        _suitabilityUpdater.Tick(data, _inputs[iteration]);
        _maturityUpdater.Tick(data, _cycleDt);
        data.LastUpdatedDay = _timestamp;
      }

    }

    #endregion

    #region Construction

    public ChunkBiomeTicker(
        RegionService regions,
        IEcologyFieldQuery fieldQuery,
        ChunkValueStore chunkValues,
        ChunkDataStore chunkData,
        ChunkBiomeAdapter adapter,
        BiomeSuitabilityUpdater suitabilityUpdater,
        BiomeMaturityUpdater maturityUpdater,
        IClock clock,
        IParallelizer parallelizer,
        PerfTracker perf,
        KeystonePerformanceSettings perfSettings)
        : base(clock, perf, () => perfSettings.MapUpdateCycleDays) {
      _regions = regions;
      _fieldQuery = fieldQuery;
      _chunkValues = chunkValues;
      _chunkData = chunkData;
      _adapter = adapter;
      _suitabilityUpdater = suitabilityUpdater;
      _maturityUpdater = maturityUpdater;
      _clock = clock;
      _parallelizer = parallelizer;
      _perf = perf;
    }

    #endregion

    #region Parallel tick lifecycle

    /// <summary>Sync completed inflight batch to
    /// <see cref="ChunkValueStore"/> before this tick's drain runs.
    /// Called after the engine's <c>FinishParallelTick</c> has joined
    /// all workers — the inflight <see cref="ChunkData"/> arrays are
    /// safe to read.</summary>
    protected override void OnTickStart() {
      if (_inflightCount == 0) return;
      if (_dispatchTicks > 0) {
        var elapsed = Stopwatch.GetTimestamp() - _dispatchTicks;
        var elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;
        _perf.Record(ParallelScopeName, elapsedMs);
        _dispatchTicks = 0;
      }
      for (var i = 0; i < _inflightCount; i++) {
        SyncToChunkValueStore(
            _inflightUnits[i].RegionId,
            _inflightUnits[i].GlobalCx,
            _inflightUnits[i].GlobalCy,
            _inflightData[i]);
      }
      _inflightCount = 0;
    }

    /// <summary>Schedule the filling batch on worker threads. Called
    /// by the engine after all <c>ITickableSingleton.Tick</c> calls
    /// have completed.</summary>
    public void StartParallelTick() {
      // Swap filling → inflight
      (_fillingInputs, _inflightInputs) = (_inflightInputs, _fillingInputs);
      (_fillingData, _inflightData) = (_inflightData, _fillingData);
      (_fillingUnits, _inflightUnits) = (_inflightUnits, _fillingUnits);
      _inflightCount = _fillingCount;
      _inflightCycleDt = CurrentCycleDt;
      _inflightTimestamp = _clock.TotalDaysElapsed;
      _fillingCount = 0;

      if (_inflightCount == 0) return;

      var task = new BiomeComputeTask(
          _inflightInputs, _inflightData,
          _inflightCycleDt, _inflightTimestamp,
          _suitabilityUpdater, _maturityUpdater);
      _dispatchTicks = Stopwatch.GetTimestamp();
      _parallelizer.Schedule(0, _inflightCount, ParallelBatchSize, ref task);
    }

    #endregion

    #region Rolling sweep overrides

    /// <inheritdoc />
    protected override void BuildSchedule(List<ChunkUnit> schedule) {
      var topologyVersion = _regions.TopologyVersion;
      var fieldShapeVersion = _fieldQuery.FieldShapeVersion;
      if (topologyVersion != _cachedForTopologyVersion
          || fieldShapeVersion != _cachedForFieldShapeVersion) {
        RebuildCachedSchedule();
        _cachedForTopologyVersion = topologyVersion;
        _cachedForFieldShapeVersion = fieldShapeVersion;
      }
      schedule.AddRange(_cachedSchedule);
    }

    private void RebuildCachedSchedule() {
      _cachedSchedule.Clear();
      foreach (var region in _regions.All) {
        var field = _fieldQuery.FieldFor(region.Id);
        if (field == null) continue;

        var chunkOriginX = field.OriginX / RegionEcologyField.ChunkSize;
        var chunkOriginY = field.OriginY / RegionEcologyField.ChunkSize;
        for (var cy = 0; cy < field.ChunksY; cy++) {
          for (var cx = 0; cx < field.ChunksX; cx++) {
            var globalCx = chunkOriginX + cx;
            var globalCy = chunkOriginY + cy;
            _cachedSchedule.Add(new ChunkUnit(region.Id, globalCx, globalCy, region.Z));
          }
        }
      }
    }

    /// <summary>Main-thread input capture. Builds
    /// <see cref="ChunkBiomeInputs"/> via the adapter and appends to
    /// the filling batch. The compute (suitability + maturity
    /// updaters) runs on worker threads in
    /// <see cref="StartParallelTick"/>.</summary>
    protected override void ProcessUnit(ChunkUnit unit) {
      var field = _fieldQuery.FieldFor(unit.RegionId);
      if (field == null) return;

      var chunkOriginX = field.OriginX / RegionEcologyField.ChunkSize;
      var chunkOriginY = field.OriginY / RegionEcologyField.ChunkSize;
      var localCx = unit.GlobalCx - chunkOriginX;
      var localCy = unit.GlobalCy - chunkOriginY;
      if (localCx < 0 || localCx >= field.ChunksX
          || localCy < 0 || localCy >= field.ChunksY) {
        return;
      }
      if (!field.ChunkValid(localCx, localCy)) {
        return;
      }

      var inputs = _adapter.Build(field, localCx, localCy);
      var data = _chunkData.GetOrCreate(unit.RegionId, unit.GlobalCx, unit.GlobalCy);
      data.Z = unit.Z;
      AppendToFillingBatch(unit, data, inputs);
    }

    /// <inheritdoc />
    protected override void OnCycleComplete() {
      // Stale-chunk cleanup is no longer done here. Dropping a region's
      // entries for chunks it no longer owns moved to ChunkReconciler,
      // which runs on every topology flush (RegionUpdater.Flush) scoped to
      // the regions that actually changed — better-timed than the old
      // per-cycle PruneToValid (it fires exactly when ownership shifts, not
      // a cycle later) and non-destructive (chunk data follows the land to
      // its new owner instead of being deleted). See
      // Keystone.Core.Persistence.ChunkReconciler.
      KeystoneLog.Verbose(
          $"[Keystone] ChunkBiomeTicker cycle complete: dt={CurrentCycleDt:F4}d, " +
          $"chunks={_cachedSchedule.Count}, regions={_regions.Count}. " +
          "Cluster rebuild is driven by ChunkClusterTicker.");
    }

    #endregion

    #region Warmup (synchronous, pre-first-tick)

    /// <summary>Game-start / post-load warmup. Runs synchronously on
    /// the main thread before the first tick — the one-time cost is
    /// acceptable and avoids the parallel pipeline's one-tick
    /// latency.</summary>
    public void RunWarmupNow(float maturitySeedDays = 0f) {
      var regionCount = 0;
      var chunkCount = 0;
      var nullFieldCount = 0;
      foreach (var region in _regions.All) {
        regionCount++;
        var field = _fieldQuery.FieldFor(region.Id);
        if (field == null) { nullFieldCount++; continue; }
        var chunkOriginX = field.OriginX / RegionEcologyField.ChunkSize;
        var chunkOriginY = field.OriginY / RegionEcologyField.ChunkSize;
        for (var cy = 0; cy < field.ChunksY; cy++) {
          for (var cx = 0; cx < field.ChunksX; cx++) {
            // Skip padding chunks: cells inside the region's rectangular
            // field bbox that hold none of its surfaces. ChunkValid is
            // reliable here — the field updater's WarmUpNow ran Phase 1
            // synchronously just before this and wrote per-chunk validity
            // from sample counts (same flag ProcessUnit gates on in steady
            // state). Creating ChunkData for padding cells (as this loop
            // used to, unconditionally) left empty entries: the retired
            // PruneToValid once swept them, and ChunkReconciler now drops
            // them en masse on the first flush with a misleading
            // "lost maturity" warning over chunks that never held any.
            // A saved chunk that reads invalid here has genuinely lost its
            // surfaces (terrain edited between save and load) — not seeding
            // it is correct; its data is homeless and would be dropped.
            if (!field.ChunkValid(cx, cy)) continue;
            var globalCx = chunkOriginX + cx;
            var globalCy = chunkOriginY + cy;
            var data = _chunkData.GetOrCreate(region.Id, globalCx, globalCy);
            data.Z = region.Z;
            // Seed ChunkData from saved values (ChunkValueStore was
            // populated from the snapshot at PostLoad).
            SeedFromChunkValueStore(region.Id, globalCx, globalCy, data);
            var inputs = _adapter.Build(field, cx, cy);
            _suitabilityUpdater.Tick(data, in inputs);
            if (maturitySeedDays > 0f) {
              _maturityUpdater.Tick(data, maturitySeedDays);
              // New games: sync the seeded maturity back so
              // ChunkValueStore reflects the seed. Loaded saves
              // skip this — ChunkValueStore already has the correct
              // values from RehydrateFrom.
              SyncToChunkValueStore(region.Id, globalCx, globalCy, data);
            }
            chunkCount++;
          }
        }
      }
      KeystoneLog.Verbose(
          $"[Keystone] RunWarmupNow: {regionCount} regions, {nullFieldCount} null fields, " +
          $"{chunkCount} chunks seeded, maturitySeedDays={maturitySeedDays:F1}");
    }

    #endregion

    #region Batch buffer management

    private void AppendToFillingBatch(
        in ChunkUnit unit, ChunkData data, in ChunkBiomeInputs inputs) {
      if (_fillingCount >= _fillingInputs.Length) {
        var newCapacity = _fillingInputs.Length * 2;
        Array.Resize(ref _fillingInputs, newCapacity);
        Array.Resize(ref _fillingData, newCapacity);
        Array.Resize(ref _fillingUnits, newCapacity);
      }
      _fillingInputs[_fillingCount] = inputs;
      _fillingData[_fillingCount] = data;
      _fillingUnits[_fillingCount] = unit;
      _fillingCount++;
    }

    #endregion

    #region Store sync helpers

    private bool _seedDiagLogged;

    private void SeedFromChunkValueStore(RegionId regionId, int chunkX, int chunkY, ChunkData data) {
      if (!BiomeValueKinds.IsInitialized) {
        if (!_seedDiagLogged) {
          KeystoneLog.Error("[Keystone] SeedFromChunkValueStore: BiomeValueKinds NOT initialized! Seeding skipped.");
          _seedDiagLogged = true;
        }
        return;
      }
      var values = data.Values;
      var logThis = !_seedDiagLogged && chunkX == 25 && chunkY == 25;
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        values[BiomeValueKinds.SuitabilityOrdinal(biome)] =
            _chunkValues.Get(regionId, chunkX, chunkY, BiomeValueKinds.ForSuitability(biome)) ?? 0f;
        var mat = _chunkValues.Get(regionId, chunkX, chunkY, BiomeValueKinds.ForMaturity(biome));
        values[BiomeValueKinds.MaturityOrdinal(biome)] = mat ?? 0f;
        if (logThis && (mat ?? 0f) > 0.01f) {
          KeystoneLog.Verbose(
              $"[Keystone] SEED chunk(25,25) region={regionId}: {biome} maturity={mat:F2} → ordinal={BiomeValueKinds.MaturityOrdinal(biome)}");
        }
      }
      // Refresh the top-3 cache from the just-loaded suitabilities.
      // Without this the chunk's TopBiomes stays at its initial
      // (-1, -1, -1) state until the next BiomeSuitabilityUpdater.Tick
      // runs (~1 game-hour at default cadence), during which time
      // ChunkBiomeSampler.SampleDominantBiome returns (null, 0) for
      // every tile in this chunk -- which makes Class A reconcile
      // think all content is invalid and rip it out.
      BiomeValueKinds.RecomputeTopBiomes(data);
      if (logThis) {
        KeystoneLog.Verbose($"[Keystone] SEED chunk(25,25) done — data hashcode={data.GetHashCode()}");
        _seedDiagLogged = true;
      }
    }

    private bool _syncDiagLogged;

    private void SyncToChunkValueStore(RegionId regionId, int chunkX, int chunkY, ChunkData data) {
      var values = data.Values;
      var logThis = !_syncDiagLogged && chunkX == 25 && chunkY == 25;
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        var suit = values[BiomeValueKinds.SuitabilityOrdinal(biome)];
        var mat = values[BiomeValueKinds.MaturityOrdinal(biome)];
        _chunkValues.Set(regionId, chunkX, chunkY, BiomeValueKinds.ForSuitability(biome), suit);
        _chunkValues.Set(regionId, chunkX, chunkY, BiomeValueKinds.ForMaturity(biome), mat);
        if (logThis && mat > 0.01f) {
          KeystoneLog.Verbose(
              $"[Keystone] SYNC chunk(25,25) region={regionId}: {biome} maturity={mat:F2}, suitability={suit:F2}");
        }
      }
      if (logThis) {
        KeystoneLog.Verbose($"[Keystone] SYNC chunk(25,25) done — data hashcode={data.GetHashCode()}");
        _syncDiagLogged = true;
      }
    }

    #endregion

  }

}
