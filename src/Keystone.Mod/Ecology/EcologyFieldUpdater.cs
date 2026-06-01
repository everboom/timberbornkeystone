using System;
using System.Collections.Generic;
using System.Diagnostics;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Flora;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Spatial;
using Keystone.Core.Survey;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Settings;
using Keystone.Mod.Surface;
using Keystone.Mod.Sweep;
using Timberborn.Multithreading;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Ecology {

  /// <summary>
  /// Polling driver that fills <see cref="RegionEcologyField"/>s for
  /// every region in the world. Two-mode operation:
  ///
  /// <para><b>Scalar channels</b> (water depth, moisture,
  /// contamination) run every cycle on worker threads via
  /// <see cref="IParallelizer"/>. The thread-safe Timberborn snapshot
  /// arrays back these reads, so no main-thread input capture is
  /// needed beyond pre-validating which surfaces exist.</para>
  ///
  /// <para><b>Entity channels</b> (natural resource counts per
  /// blueprint) run every <see cref="EntityCycleInterval"/> cycles on
  /// the main thread. Entity data changes rarely (plant/harvest
  /// events); the thread-unsafe <c>IBlockService</c> probes that feed
  /// it cannot move off the main thread.</para>
  ///
  /// <para><b>Cycle cadence:</b> 1 game-hour (configurable 1–4 via
  /// <see cref="KeystonePerformanceSettings.MapUpdateHours"/>). With
  /// <see cref="EntityCycleInterval"/> = 4, entity data refreshes
  /// every 4 game-hours.</para>
  /// </summary>
  public sealed class EcologyFieldUpdater
      : RollingSweepTicker<EcologyFieldUpdater.ChunkUnit>,
        IPostLoadableSingleton, IEcologyFieldQuery,
        IParallelTickableSingleton {

    #region Constants

    /// <inheritdoc cref="WaterContamination.Threshold"/>
    public const float WaterContaminationThreshold = WaterContamination.Threshold;

    private const int VerticalProbeRange = 8;

    public const string DeadEntityChannelName = "(dead)";

    private const int EntityCycleInterval = 4;

    private const int InitialBatchCapacity = 64;
    private const int ParallelBatchSize = 4;

    internal const string ParallelScopeName = "EcologyField.Parallel";

    #endregion

    #region Fields

    private readonly TerrainSurveyor _surveyor;
    private readonly RegionService _regions;
    private readonly FloraCatalog _flora;
    private readonly INaturalResourceEnumerator _enumerator;
    private readonly IWaterQuery _water;
    private readonly IMoistureQuery _moisture;
    private readonly IContaminationQuery _contamination;
    private readonly ITerrainQuery _terrain;
    private readonly PerfTracker _perf;
    private readonly IParallelizer _parallelizer;
    private readonly TileSlotRegistry _tileSlotRegistry;
    private readonly SurfaceFieldStore _surfaceFields;

    private readonly Dictionary<RegionId, RegionEcologyField> _published = new();
    private readonly Dictionary<RegionId, RegionTileData> _publishedTileData = new();
    private int _waterDistanceSlot = -1;

    private int _fieldShapeVersion;

    private readonly Dictionary<string, int> _entityIndices = new();
    private readonly List<string> _entityIndexToName = new();

    private int _deadEntityIndex;

    private float[] _scratchEntities = Array.Empty<float>();
    private readonly HashSet<object> _scratchSeen = new();

    private bool _entityPassThisCycle;

    private long _tickEntityTicks;

    private Stopwatch? _cycleStopwatch;
    private int _cycleChunksScheduled;
    private int _cycleRegionsScheduled;
    private int _cycleSurfacesSeen;
    private int _cycleSurfacesSkippedSettled;
    private bool _cycleUsedCache;

    private readonly List<ChunkUnit> _cachedSchedule = new();
    private int _cachedForVersion = -1;

    #endregion

    #region Double-buffered batch (scalar parallelization)

    private RegionEcologyField[] _fillingFields = new RegionEcologyField[InitialBatchCapacity];
    private RegionTileData[] _fillingTileData = new RegionTileData[InitialBatchCapacity];
    private int[] _fillingCx = new int[InitialBatchCapacity];
    private int[] _fillingCy = new int[InitialBatchCapacity];
    private int[] _fillingSampleCounts = new int[InitialBatchCapacity];
    private int _fillingCount;

    private RegionEcologyField[] _inflightFields = new RegionEcologyField[InitialBatchCapacity];
    private RegionTileData[] _inflightTileData = new RegionTileData[InitialBatchCapacity];
    private int[] _inflightCx = new int[InitialBatchCapacity];
    private int[] _inflightCy = new int[InitialBatchCapacity];
    private int[] _inflightSampleCounts = new int[InitialBatchCapacity];
    private int _inflightCount;
    private long _dispatchTicks;

    /// <summary>Per-batch-entry surface lists for workers. Each entry
    /// is a slice: start index + count into <see cref="_surfacePool"/>.
    /// Avoids per-entry List allocation.</summary>
    private int[] _fillingSurfaceStart = new int[InitialBatchCapacity];
    private int[] _fillingSurfaceCount = new int[InitialBatchCapacity];
    private SurfaceCoord[] _fillingSurfacePool = new SurfaceCoord[256];
    private int _fillingSurfacePoolUsed;

    private int[] _inflightSurfaceStart = new int[InitialBatchCapacity];
    private int[] _inflightSurfaceCount = new int[InitialBatchCapacity];
    private SurfaceCoord[] _inflightSurfacePool = new SurfaceCoord[256];

    /// <summary>Per-batch-entry scalar scratch buffers for workers.
    /// Each entry gets <see cref="RegionEcologyField.FixedChannelCount"/>
    /// floats. Pre-allocated and reused.</summary>
    private float[] _scalarPool = new float[InitialBatchCapacity * RegionEcologyField.FixedChannelCount];
    private float[] _inflightScalarPool = new float[InitialBatchCapacity * RegionEcologyField.FixedChannelCount];

    #endregion

    #region Types

    /// <summary>One chunk's worth of work in the cycle schedule.</summary>
    public readonly struct ChunkUnit {
      public readonly RegionId RegionId;
      public readonly int Cx;
      public readonly int Cy;
      public readonly List<SurfaceCoord> Surfaces;
      public ChunkUnit(RegionId regionId, int cx, int cy, List<SurfaceCoord> surfaces) {
        RegionId = regionId;
        Cx = cx;
        Cy = cy;
        Surfaces = surfaces;
      }
    }

    /// <summary>Worker-thread task: runs scalar channel accumulation
    /// and water distance computation for a batch of chunks.</summary>
    private readonly struct ScalarComputeTask : IParallelizerLoopTask {

      private readonly SurfaceCoord[] _surfaces;
      private readonly int[] _surfaceStart;
      private readonly int[] _surfaceCount;
      private readonly int[] _sampleCounts;
      private readonly float[] _scalarPool;
      private readonly int _channelCount;
      private readonly RegionTileData[] _tileData;
      private readonly int[] _cx;
      private readonly int[] _cy;
      private readonly int _waterDistanceSlot;
      private readonly IMoistureQuery _moisture;
      private readonly IContaminationQuery _contamination;
      private readonly IWaterQuery _water;
      private readonly ITerrainQuery _terrain;

      public ScalarComputeTask(
          SurfaceCoord[] surfaces,
          int[] surfaceStart, int[] surfaceCount,
          int[] sampleCounts,
          float[] scalarPool, int channelCount,
          RegionTileData[] tileData,
          int[] cx, int[] cy,
          int waterDistanceSlot,
          IMoistureQuery moisture,
          IContaminationQuery contamination,
          IWaterQuery water,
          ITerrainQuery terrain) {
        _surfaces = surfaces;
        _surfaceStart = surfaceStart;
        _surfaceCount = surfaceCount;
        _sampleCounts = sampleCounts;
        _scalarPool = scalarPool;
        _channelCount = channelCount;
        _tileData = tileData;
        _cx = cx;
        _cy = cy;
        _waterDistanceSlot = waterDistanceSlot;
        _moisture = moisture;
        _contamination = contamination;
        _water = water;
        _terrain = terrain;
      }

      public void Run(int iteration) {
        var start = _surfaceStart[iteration];
        var count = _surfaceCount[iteration];
        var bufferOffset = iteration * _channelCount;

        Array.Clear(_scalarPool, bufferOffset, _channelCount);

        for (var i = 0; i < count; i++) {
          var s = _surfaces[start + i];
          ScalarChannelAggregator.AccumulateSurfaceInto(
              s, _moisture, _contamination, _water,
              _scalarPool, bufferOffset);
        }

        var sampleCount = _sampleCounts[iteration];
        if (sampleCount > 0) {
          var inv = 1f / sampleCount;
          for (var ch = 0; ch < _channelCount; ch++) {
            _scalarPool[bufferOffset + ch] *= inv;
          }
        }

        // Water distance: compute per tile in this chunk. Shared with
        // the synchronous startup warmup so the two can't drift.
        ComputeChunkWaterDistances(
            _tileData[iteration], _cx[iteration], _cy[iteration],
            _waterDistanceSlot, _water, _terrain);
      }

    }

    /// <summary>Worker-thread task for the synchronous startup warmup:
    /// computes water distance for one chunk and, when seeding, accrues
    /// the riparian maturity seed on its near-water, non-toxic surfaces.
    /// Indexes the cached schedule directly (rather than the live cycle's
    /// inflight pool arrays) because the warmup runs outside the tick
    /// pipeline. The dominant warmup cost -- a 5x5 height-aware
    /// neighborhood scan per surface -- so it's worth fanning across the
    /// worker pool instead of walking every surface on the main thread.
    /// <para>Thread safety: each chunk owns disjoint tile cells
    /// (<see cref="RegionTileData"/> writes) and disjoint surface indices
    /// (<see cref="SurfaceFieldStore"/> writes), so no two workers touch
    /// the same slot. The water/terrain/contamination queries are read
    /// concurrently here exactly as the live <see cref="ScalarComputeTask"/>
    /// reads them, and the schedule list is read-only for the pass.</para></summary>
    private readonly struct WaterDistanceWarmupTask : IParallelizerLoopTask {

      private readonly List<ChunkUnit> _schedule;
      private readonly Dictionary<RegionId, RegionTileData> _tileDataByRegion;
      private readonly int _waterDistanceSlot;
      private readonly IWaterQuery _water;
      private readonly ITerrainQuery _terrain;

      public WaterDistanceWarmupTask(
          List<ChunkUnit> schedule,
          Dictionary<RegionId, RegionTileData> tileDataByRegion,
          int waterDistanceSlot,
          IWaterQuery water,
          ITerrainQuery terrain) {
        _schedule = schedule;
        _tileDataByRegion = tileDataByRegion;
        _waterDistanceSlot = waterDistanceSlot;
        _water = water;
        _terrain = terrain;
      }

      public void Run(int iteration) {
        var unit = _schedule[iteration];
        if (!_tileDataByRegion.TryGetValue(unit.RegionId, out var tileData)
            || tileData == null) {
          return;
        }
        // Parallel-safe: writes only into this region's RegionTileData at
        // this chunk's disjoint tiles. The riparian seed write goes into
        // the main-thread-only SurfaceFieldStore and so is deferred to
        // WarmUpNow's post-join main-thread pass -- NOT done here on a
        // worker thread.
        ComputeChunkWaterDistances(
            tileData, unit.Cx, unit.Cy, _waterDistanceSlot, _water, _terrain);
      }

    }

    #endregion

    #region Construction

    public EcologyFieldUpdater(
        TerrainSurveyor surveyor,
        RegionService regions,
        FloraCatalog flora,
        INaturalResourceEnumerator naturalResourceEnumerator,
        IWaterQuery water,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        ITerrainQuery terrain,
        IClock clock,
        IParallelizer parallelizer,
        PerfTracker perf,
        TileSlotRegistry tileSlotRegistry,
        SurfaceFieldStore surfaceFields,
        KeystonePerformanceSettings perfSettings)
        : base(clock, perf, () => perfSettings.MapUpdateCycleDays) {
      _surveyor = surveyor;
      _regions = regions;
      _flora = flora;
      _enumerator = naturalResourceEnumerator;
      _water = water;
      _moisture = moisture;
      _contamination = contamination;
      _terrain = terrain;
      _perf = perf;
      _parallelizer = parallelizer;
      _tileSlotRegistry = tileSlotRegistry;
      _surfaceFields = surfaceFields;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      try {
        _waterDistanceSlot = _tileSlotRegistry.Register("keystone.tile.waterDistance");
        InitializeEntityIndexMap();
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleError(
            "EcologyFieldUpdater.PostLoad", "Subsystem failed", ex);
      }
    }

    private void RebuildIndexMapIfStale() {
      if (_entityIndices.Count > 0) return;
      if (_flora.Count == 0) return;
      InitializeEntityIndexMap();
    }

    private void InitializeEntityIndexMap() {
      _entityIndices.Clear();
      _entityIndexToName.Clear();
      foreach (var entry in _flora.Entries) {
        _entityIndices[entry.BlueprintName] = _entityIndexToName.Count;
        _entityIndexToName.Add(entry.BlueprintName);
      }
      _deadEntityIndex = _entityIndexToName.Count;
      _entityIndexToName.Add(DeadEntityChannelName);
      _scratchEntities = new float[_entityIndexToName.Count];
      KeystoneLog.Verbose($"[Keystone] EcologyFieldUpdater: registered {_entityIndexToName.Count} entity channels (incl. {DeadEntityChannelName}).");
    }

    #endregion

    #region IEcologyFieldQuery

    /// <inheritdoc />
    public RegionEcologyField? FieldFor(RegionId region) =>
        _published.TryGetValue(region, out var f) ? f : null;

    /// <inheritdoc />
    public int? EntityIndex(string blueprintName) =>
        _entityIndices.TryGetValue(blueprintName, out var idx) ? idx : (int?)null;

    /// <inheritdoc />
    public IReadOnlyList<string> KnownEntityBlueprints => _entityIndexToName;

    /// <inheritdoc />
    public int FieldShapeVersion => _fieldShapeVersion;

    /// <inheritdoc />
    public RegionTileData? TileDataFor(RegionId region) =>
        _publishedTileData.TryGetValue(region, out var td) ? td : null;

    #endregion

    #region Parallel tick lifecycle

    /// <summary>Sync completed scalar results to fields and record
    /// timing. Called after the engine's <c>FinishParallelTick</c>
    /// has joined workers.</summary>
    protected override void OnTickStart() {
      // Sync completed inflight scalar batch
      if (_inflightCount > 0) {
        if (_dispatchTicks > 0) {
          var elapsed = Stopwatch.GetTimestamp() - _dispatchTicks;
          _perf.Record(ParallelScopeName, elapsed * 1000.0 / Stopwatch.Frequency);
          _dispatchTicks = 0;
        }
        var channelCount = RegionEcologyField.FixedChannelCount;
        for (var i = 0; i < _inflightCount; i++) {
          var field = _inflightFields[i];
          var bufferOffset = i * channelCount;
          field.WriteScalars(
              _inflightCx[i], _inflightCy[i],
              _inflightSampleCounts[i] > 0,
              _inflightSampleCounts[i],
              new ReadOnlySpan<float>(_inflightScalarPool, bufferOffset, channelCount));
        }
        _inflightCount = 0;
      }

      // Record entity timing from previous tick
      if (_tickEntityTicks > 0) {
        _perf.Record("EcologyField.Entities", _tickEntityTicks * 1000.0 / Stopwatch.Frequency);
        _tickEntityTicks = 0;
      }
    }

    /// <summary>Schedule the filling batch on worker threads.</summary>
    public void StartParallelTick() {
      // Swap filling → inflight
      (_fillingFields, _inflightFields) = (_inflightFields, _fillingFields);
      (_fillingTileData, _inflightTileData) = (_inflightTileData, _fillingTileData);
      (_fillingCx, _inflightCx) = (_inflightCx, _fillingCx);
      (_fillingCy, _inflightCy) = (_inflightCy, _fillingCy);
      (_fillingSampleCounts, _inflightSampleCounts) = (_inflightSampleCounts, _fillingSampleCounts);
      (_fillingSurfaceStart, _inflightSurfaceStart) = (_inflightSurfaceStart, _fillingSurfaceStart);
      (_fillingSurfaceCount, _inflightSurfaceCount) = (_inflightSurfaceCount, _fillingSurfaceCount);
      (_fillingSurfacePool, _inflightSurfacePool) = (_inflightSurfacePool, _fillingSurfacePool);
      (_scalarPool, _inflightScalarPool) = (_inflightScalarPool, _scalarPool);
      _inflightCount = _fillingCount;
      _fillingCount = 0;
      _fillingSurfacePoolUsed = 0;

      if (_inflightCount == 0) return;

      var task = new ScalarComputeTask(
          _inflightSurfacePool,
          _inflightSurfaceStart, _inflightSurfaceCount,
          _inflightSampleCounts,
          _inflightScalarPool, RegionEcologyField.FixedChannelCount,
          _inflightTileData, _inflightCx, _inflightCy,
          _waterDistanceSlot,
          _moisture, _contamination, _water, _terrain);
      _dispatchTicks = Stopwatch.GetTimestamp();
      _parallelizer.Schedule(0, _inflightCount, ParallelBatchSize, ref task);
    }

    #endregion

    #region RollingSweepTicker

    protected override bool ShouldRun() {
      return _entityIndexToName.Count > 0
          && _surveyor.Surfaces.Count > 0
          && _regions.Count > 0;
    }

    /// <inheritdoc />
    protected override void BuildSchedule(List<ChunkUnit> schedule) {
      RebuildIndexMapIfStale();
      _cycleStopwatch = Stopwatch.StartNew();
      _cycleSurfacesSeen = 0;
      _cycleSurfacesSkippedSettled = 0;

      _entityPassThisCycle = CyclesCompleted % EntityCycleInterval == 0;

      if (_regions.TopologyVersion != _cachedForVersion) {
        RebuildCachedSchedule();
        _cachedForVersion = _regions.TopologyVersion;
        _cycleUsedCache = false;
      } else {
        _cycleUsedCache = true;
      }

      schedule.AddRange(_cachedSchedule);
      _cycleRegionsScheduled = CountDistinctRegions(_cachedSchedule);
      _cycleChunksScheduled = _cachedSchedule.Count;
    }

    private void RebuildCachedSchedule() {
      _cachedSchedule.Clear();

      var perRegion = new Dictionary<RegionId, RegionScratch>(_regions.Count);
      foreach (var entry in _surveyor.Surfaces.Entries) {
        var s = entry.Key;
        _cycleSurfacesSeen++;
        var region = _regions.Containing(s);
        if (region == null) continue;
        if (region.IsSettled) {
          _cycleSurfacesSkippedSettled++;
          continue;
        }
        if (!perRegion.TryGetValue(region.Id, out var scratch)) {
          scratch = new RegionScratch();
          perRegion[region.Id] = scratch;
        }
        scratch.Add(s);
      }

      var entityCount = _entityIndexToName.Count;
      var shapeChanged = false;
      foreach (var (regionId, scratch) in perRegion) {
        var (originX, originY, chunksX, chunksY) = scratch.ChunkAlignedBbox();
        if (!_published.TryGetValue(regionId, out var field)
            || field.OriginX != originX
            || field.OriginY != originY
            || field.ChunksX != chunksX
            || field.ChunksY != chunksY
            || field.EntityChannelCount != entityCount) {
          field = new RegionEcologyField(originX, originY, chunksX, chunksY, entityCount);
          _published[regionId] = field;
          var tileW = chunksX * RegionEcologyField.ChunkSize;
          var tileH = chunksY * RegionEcologyField.ChunkSize;
          var validTiles = scratch.UniqueTileCoords();
          _publishedTileData[regionId] = new RegionTileData(
              originX, originY, tileW, tileH, _tileSlotRegistry.SlotCount, validTiles);
          shapeChanged = true;
        }

        var chunks = new Dictionary<long, List<SurfaceCoord>>();
        foreach (var s in scratch.Surfaces) {
          var cx = (s.X - originX) / RegionEcologyField.ChunkSize;
          var cy = (s.Y - originY) / RegionEcologyField.ChunkSize;
          var key = ((long)cx << 32) | (uint)cy;
          if (!chunks.TryGetValue(key, out var list)) {
            list = new List<SurfaceCoord>();
            chunks[key] = list;
          }
          list.Add(s);
        }
        foreach (var (key, list) in chunks) {
          var cx = (int)(key >> 32);
          var cy = (int)(key & 0xFFFFFFFFL);
          _cachedSchedule.Add(new ChunkUnit(regionId, cx, cy, list));
        }
      }

      List<RegionId>? stale = null;
      foreach (var id in _published.Keys) {
        if (!perRegion.ContainsKey(id)) (stale ??= new List<RegionId>()).Add(id);
      }
      if (stale != null) {
        foreach (var id in stale) {
          _published.Remove(id);
          _publishedTileData.Remove(id);
        }
        shapeChanged = true;
      }

      if (shapeChanged) {
        _fieldShapeVersion++;
      }
    }

    private static int CountDistinctRegions(List<ChunkUnit> schedule) {
      if (schedule.Count == 0) return 0;
      var seen = new HashSet<RegionId>();
      for (var i = 0; i < schedule.Count; i++) seen.Add(schedule[i].RegionId);
      return seen.Count;
    }

    /// <summary>Main-thread per-chunk work. Validates surfaces, runs
    /// entity enumeration on entity cycles, and appends to the scalar
    /// batch for worker-thread processing.</summary>
    protected override void ProcessUnit(ChunkUnit unit) {
      if (!_published.TryGetValue(unit.RegionId, out var field)) {
        return;
      }

      var surfaces = unit.Surfaces;
      var sampleCount = 0;

      // Entity pass (main thread, every EntityCycleInterval cycles)
      if (_entityPassThisCycle) {
        Array.Clear(_scratchEntities, 0, _scratchEntities.Length);
        _scratchSeen.Clear();

        var t0 = Stopwatch.GetTimestamp();
        for (var i = 0; i < surfaces.Count; i++) {
          var s = surfaces[i];
          if (!_surveyor.Surfaces.TryGet(s, out _)) continue;
          sampleCount++;
          AccumulateEntities(s);
        }
        var t1 = Stopwatch.GetTimestamp();
        _tickEntityTicks += t1 - t0;

        field.WriteEntities(unit.Cx, unit.Cy, _scratchEntities);
      } else {
        // Scalar-only cycle: still need sampleCount from validated surfaces
        for (var i = 0; i < surfaces.Count; i++) {
          if (_surveyor.Surfaces.TryGet(surfaces[i], out _)) sampleCount++;
        }
      }

      // Append to scalar batch for worker-thread processing
      _publishedTileData.TryGetValue(unit.RegionId, out var tileData);
      AppendToFillingBatch(unit, field, tileData, sampleCount, surfaces);

      // Integrate per-tile riparian maturity for this chunk's surfaces.
      AccrueRiparian(unit);
    }

    /// <summary>
    /// Integrate per-tile riparian maturity for this chunk's surfaces.
    /// Runs every cycle on the main thread, riding the surface walk that
    /// already happens in <see cref="ProcessUnit"/>. Reads the previous
    /// cycle's water distance from <see cref="RegionTileData"/> (a
    /// one-cycle lag, negligible against day-scale accrual) and steps
    /// each surface in <see cref="SurfaceFieldStore"/>: accrue while near
    /// water, dissipate otherwise. This is the sustained-water signal the
    /// old Riparian biome carried implicitly; restoring it as a per-tile
    /// maturity is what stops a transient flood from firing the
    /// (semi-permanent) riparian flourishes after the fold into Grassland
    /// made the water check instantaneous.
    /// </summary>
    private void AccrueRiparian(ChunkUnit unit) {
      if (_waterDistanceSlot < 0) return;
      if (!_publishedTileData.TryGetValue(unit.RegionId, out var tileData) || tileData == null) {
        return;
      }

      // CurrentCycleDt is the rolling sweep's per-cycle time advance --
      // 0 on the first cycle and during the synchronous startup warmup
      // (RunCycleNow with dt=0), so a loaded save's persisted maturity
      // isn't perturbed before WarmUpNow seeds/leaves it.
      var deltaDays = CurrentCycleDt;
      var surfaces = unit.Surfaces;
      for (var i = 0; i < surfaces.Count; i++) {
        var s = surfaces[i];
        if (!_surfaceFields.TryResolveSurfaceIndex(s.X, s.Y, s.Z, out var index3D)) {
          continue;
        }
        var waterDistance = tileData.Get(s.X, s.Y, _waterDistanceSlot);
        var nearWater = RiparianMaturityParameters.IsNearWater(waterDistance);
        var current = _surfaceFields.GetAt(SurfaceField.RiparianMaturity, index3D);
        // Cheap early-out for the common dry-inland tile: nothing to
        // accrue and decaying 0 stays 0, so skip the per-surface toxic
        // probes entirely.
        if (current == 0f && !nearWater) continue;
        var toxic = IsToxic(s);
        var next = RiparianMaturityUpdater.Step(current, nearWater, toxic, deltaDays);
        if (next != current) {
          _surfaceFields.SetAt(SurfaceField.RiparianMaturity, index3D, next);
        }
      }
    }

    /// <summary>True if a destructive factor is present at the surface --
    /// soil contamination or badwater in the column. Riparian maturity is
    /// destroyed (fast-decayed) while toxic and never accrues, so it
    /// builds only in clean sustained near-water.</summary>
    private bool IsToxic(SurfaceCoord surface) =>
        _contamination.IsContaminatedAt(surface)
        || _water.WaterContaminationAt(surface) >= WaterContaminationThreshold;

    /// <summary>
    /// Compute and store signed water distance for every in-bbox tile of
    /// one chunk. Shared by the parallel <see cref="ScalarComputeTask"/>
    /// and the synchronous <see cref="WarmUpNow"/> so the two can't
    /// drift. Touches only the passed <paramref name="tileData"/>
    /// (disjoint per chunk) plus the thread-safe terrain/water ports, so
    /// it is safe to call from a worker thread.
    /// </summary>
    private static void ComputeChunkWaterDistances(
        RegionTileData? tileData, int cx, int cy, int waterDistanceSlot,
        IWaterQuery water, ITerrainQuery terrain) {
      if (tileData == null || waterDistanceSlot < 0) return;
      var chunkSize = RegionEcologyField.ChunkSize;
      var tileOriginX = tileData.OriginX + cx * chunkSize;
      var tileOriginY = tileData.OriginY + cy * chunkSize;

      // Dead-region early-out. Distance is capped at MaxSearchDistance
      // tiles, so if the chunk plus a halo of that radius holds no water,
      // every column is "land far" (OutOfRange); if it holds no land,
      // every column is "deep water". Filling the uniform sentinel skips
      // the per-column path search entirely for interior chunks (big dry
      // plateaus, open-water bodies) — exactly where the old per-tile box
      // scan burned its time. The probe is purely horizontal (ignores
      // height), which makes it a sound conservative gate: nothing within
      // the halo horizontally means nothing within range at any height.
      if (TryUniformWaterDistance(
              tileOriginX, tileOriginY, chunkSize, water, terrain, out var sentinel)) {
        for (var ty = 0; ty < chunkSize; ty++) {
          for (var tx = 0; tx < chunkSize; tx++) {
            var tileX = tileOriginX + tx;
            var tileY = tileOriginY + ty;
            if (!tileData.Contains(tileX, tileY)) continue;
            tileData.Set(tileX, tileY, waterDistanceSlot, sentinel);
          }
        }
        return;
      }

      // Boundary chunk: water and land both within range, so resolve each
      // column with the path-connected distance search. The reference Z is
      // the column's top surface, found by a non-allocating downward scan
      // (TryGetTopSurfaceZ) rather than enumerating every surface in the
      // column — that enumeration was the warmup's allocation hot spot.
      for (var ty = 0; ty < chunkSize; ty++) {
        for (var tx = 0; tx < chunkSize; tx++) {
          var tileX = tileOriginX + tx;
          var tileY = tileOriginY + ty;
          if (!tileData.Contains(tileX, tileY)) continue;
          WaterDistanceCalculator.TryGetTopSurfaceZ(terrain, tileX, tileY, out var z);
          var dist = WaterDistanceCalculator.Compute(tileX, tileY, z, water, terrain);
          tileData.Set(tileX, tileY, waterDistanceSlot, dist);
        }
      }
    }

    /// <summary>Probe the chunk plus a <see cref="WaterDistanceCalculator.MaxSearchDistance"/>
    /// halo: if it contains only water or only land, every column in the
    /// chunk takes the matching sentinel (no per-column search needed).
    /// Returns false the moment both water and land are seen — the chunk
    /// straddles a boundary and must be resolved per column.</summary>
    private static bool TryUniformWaterDistance(
        int originX, int originY, int chunkSize,
        IWaterQuery water, ITerrainQuery terrain, out int sentinel) {
      const int halo = WaterDistanceCalculator.MaxSearchDistance;
      var anyWater = false;
      var anyLand = false;
      for (var ty = -halo; ty < chunkSize + halo; ty++) {
        for (var tx = -halo; tx < chunkSize + halo; tx++) {
          var col = new TileCoord(originX + tx, originY + ty);
          if (!terrain.Contains(col)) continue;
          if (water.HasWaterAtColumn(col)) anyWater = true;
          else anyLand = true;
          if (anyWater && anyLand) {
            sentinel = 0;
            return false;
          }
        }
      }
      // Uniform (or fully out of bounds): all-water → deep water, otherwise
      // (all-land, or no in-bounds columns) → land far.
      sentinel = anyWater
          ? WaterDistanceCalculator.DeepWater
          : WaterDistanceCalculator.OutOfRange;
      return true;
    }

    /// <summary>
    /// Synchronous startup warmup of the field + per-tile layers, called
    /// from <see cref="Startup.KeystoneStartupWarmup"/> in place of the
    /// base <see cref="Keystone.Core.Sweep.RollingSweep{TUnit}.RunCycleNow"/>.
    /// Runs the normal cycle (builds the schedule, allocates per-region
    /// tile data, counts entities), then -- because the scalar channels
    /// and water distance otherwise land only on the deferred parallel
    /// pass -- synchronously:
    /// <list type="bullet">
    ///   <item>computes the field's fixed scalar channels per chunk and
    ///   marks the chunk valid (<see cref="RegionEcologyField.WriteScalars"/>).
    ///   Without this the field reads as invalid at warmup, so the biome
    ///   ticker can't compute suitability, and on a new game (no snapshot
    ///   to restore from) the seeded chunk Maturity stays at 0.</item>
    ///   <item>computes water distance for every tile, and on a new game
    ///   (or pre-store-save migration) seeds riparian maturity for
    ///   near-water surfaces.</item>
    /// </list>
    /// Must run before the biome ticker and the Class B rule pass: both
    /// read this data, so it has to be there when they do.
    /// </summary>
    /// <param name="riparianSeedValue">Riparian maturity (R) to seed
    /// near-water surfaces with. The caller decides it per load kind
    /// (new game vs capped migration vs 0 for a post-store load). Pass 0
    /// to seed nothing -- a post-store save's R returns through
    /// <see cref="SurfaceFieldStore"/>'s own persistence and must not be
    /// overwritten.</param>
    /// <returns>Wall-clock milliseconds spent in the per-tile sub-pass
    /// (water-distance compute for every surface, plus the riparian seed
    /// when seeding). This is the cost of first-time-populating the
    /// per-tile layer specifically, a subset of the whole warmup; the
    /// startup reporter surfaces it as its own perf line so the per-tile
    /// build cost is visible separately from the per-chunk field work.</returns>
    public double WarmUpNow(float riparianSeedValue) {
      RunCycleNow();

      var seedRiparian = riparianSeedValue > 0f && _waterDistanceSlot >= 0;
      var seedValue = seedRiparian ? riparianSeedValue : 0f;
      var scalars = new float[RegionEcologyField.FixedChannelCount];

      // Phase 1 -- per-chunk scalar channels + validity, synchronously.
      // The biome ticker's warmup reads these to compute suitability; on
      // a new game (no snapshot) a chunk that reads invalid here seeds
      // Maturity at 0. Cheap relative to phase 2 (one read per surface
      // vs. a neighborhood scan), so left on the main thread.
      for (var u = 0; u < _cachedSchedule.Count; u++) {
        var unit = _cachedSchedule[u];
        if (_published.TryGetValue(unit.RegionId, out var field)) {
          var sampleCount = AccumulateChunkScalars(unit.Surfaces, scalars);
          field.WriteScalars(unit.Cx, unit.Cy, sampleCount > 0, sampleCount, scalars);
        }
      }

      // Phase 2 -- per-tile water distance (+ riparian seed), fanned out
      // across the parallelizer's worker pool. This is the dominant
      // warmup cost: a 5x5 height-aware neighborhood scan per surface,
      // the same compute the live rolling sweep runs on worker threads.
      // The warmup runs outside the tick pipeline, so we open our own
      // scheduling phase and Wait() synchronously before returning --
      // downstream warmup steps (biome ticker, rule pass) read this data.
      // Timed around the dispatch+join so the reporter's "Per-tile fields"
      // line reflects wall-clock, not summed worker time.
      var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
      if (_waterDistanceSlot >= 0 && _cachedSchedule.Count > 0) {
        var task = new WaterDistanceWarmupTask(
            _cachedSchedule, _publishedTileData, _waterDistanceSlot,
            _water, _terrain);
        _parallelizer.StartScheduling();
        try {
          _parallelizer.Schedule(0, _cachedSchedule.Count, ParallelBatchSize, ref task);
          _parallelizer.Wait();
        } finally {
          // Always close the scheduling phase, even if a worker threw
          // (Wait rethrows on the main thread). Leaving it open would
          // make the engine's next StartScheduling throw.
          _parallelizer.StopScheduling();
        }

        // Riparian seed write — MAIN THREAD ONLY. SurfaceFieldStore is a
        // main-thread-only store (its arrays are resized by terrain events
        // off the main thread), so it must not be written from the worker
        // pool above. The expensive part (the 5x5 water-distance scan) ran
        // in parallel and is joined; this pass is the cheap per-surface
        // seed write, reading the now-computed water distances.
        if (seedRiparian) {
          for (var u = 0; u < _cachedSchedule.Count; u++) {
            var unit = _cachedSchedule[u];
            if (!_publishedTileData.TryGetValue(unit.RegionId, out var tileData)
                || tileData == null) {
              continue;
            }
            var surfaces = unit.Surfaces;
            for (var i = 0; i < surfaces.Count; i++) {
              var s = surfaces[i];
              if (!_surfaceFields.TryResolveSurfaceIndex(s.X, s.Y, s.Z, out var index3D)) continue;
              var waterDistance = tileData.Get(s.X, s.Y, _waterDistanceSlot);
              // Don't seed riparian on toxic tiles -- it can't establish
              // there, consistent with the toxic fast-decay in the sweep.
              if (RiparianMaturityParameters.IsNearWater(waterDistance) && !IsToxic(s)) {
                _surfaceFields.SetAt(SurfaceField.RiparianMaturity, index3D, seedValue);
              }
            }
          }
        }
      }
      return (System.Diagnostics.Stopwatch.GetTimestamp() - t0)
          * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// Accumulate the fixed scalar channels (moisture, contamination,
    /// water depth/flow) over a chunk's validated surfaces into
    /// <paramref name="scalars"/> (length
    /// <see cref="RegionEcologyField.FixedChannelCount"/>), averaged by
    /// the validated surface count, which is returned. Mirrors the
    /// per-surface accumulation the parallel <see cref="ScalarComputeTask"/>
    /// performs, run synchronously for the startup warmup via the shared
    /// <see cref="ScalarChannelAggregator"/> primitive.
    /// </summary>
    private int AccumulateChunkScalars(List<SurfaceCoord> surfaces, float[] scalars) {
      System.Array.Clear(scalars, 0, scalars.Length);
      var sampleCount = 0;
      for (var i = 0; i < surfaces.Count; i++) {
        var s = surfaces[i];
        if (!_surveyor.Surfaces.TryGet(s, out _)) continue;
        sampleCount++;
        ScalarChannelAggregator.AccumulateSurfaceInto(
            s, _moisture, _contamination, _water, scalars, 0);
      }
      if (sampleCount > 0) {
        var inv = 1f / sampleCount;
        for (var ch = 0; ch < scalars.Length; ch++) scalars[ch] *= inv;
      }
      return sampleCount;
    }

    /// <inheritdoc />
    protected override void OnCycleComplete() {
      var ms = _cycleStopwatch?.ElapsedMilliseconds ?? 0;
      _cycleStopwatch = null;
      var cacheNote = _cycleUsedCache ? " [schedule cached]" : "";
      var entityNote = _entityPassThisCycle ? " [entity pass]" : " [scalar only]";
      KeystoneLog.Verbose(
          $"[Keystone] Field cycle complete: {_cycleRegionsScheduled} regions, " +
          $"{_cycleSurfacesSeen} surfaces ({_cycleSurfacesSkippedSettled} settled skipped), " +
          $"{_cycleChunksScheduled} chunks ({ms} ms wall clock to BuildSchedule)" +
          $"{cacheNote}{entityNote}.");
    }

    #endregion

    #region Batch buffer management

    private void AppendToFillingBatch(
        in ChunkUnit unit, RegionEcologyField field, RegionTileData? tileData,
        int sampleCount, List<SurfaceCoord> surfaces) {
      if (_fillingCount >= _fillingFields.Length) {
        var newCap = _fillingFields.Length * 2;
        Array.Resize(ref _fillingFields, newCap);
        Array.Resize(ref _fillingTileData, newCap);
        Array.Resize(ref _fillingCx, newCap);
        Array.Resize(ref _fillingCy, newCap);
        Array.Resize(ref _fillingSampleCounts, newCap);
        Array.Resize(ref _fillingSurfaceStart, newCap);
        Array.Resize(ref _fillingSurfaceCount, newCap);
        Array.Resize(ref _scalarPool, newCap * RegionEcologyField.FixedChannelCount);
      }

      // Grow surface pool if needed
      var surfCount = surfaces.Count;
      while (_fillingSurfacePoolUsed + surfCount > _fillingSurfacePool.Length) {
        Array.Resize(ref _fillingSurfacePool, _fillingSurfacePool.Length * 2);
      }

      _fillingFields[_fillingCount] = field;
      _fillingTileData[_fillingCount] = tileData;
      _fillingCx[_fillingCount] = unit.Cx;
      _fillingCy[_fillingCount] = unit.Cy;
      _fillingSampleCounts[_fillingCount] = sampleCount;
      _fillingSurfaceStart[_fillingCount] = _fillingSurfacePoolUsed;
      _fillingSurfaceCount[_fillingCount] = surfCount;

      for (var i = 0; i < surfCount; i++) {
        _fillingSurfacePool[_fillingSurfacePoolUsed++] = surfaces[i];
      }

      _fillingCount++;
    }

    #endregion

    #region Per-chunk entity processing

    /// <summary>
    /// Count any natural-resource block objects within this chunk's
    /// column. Probes vertically, dedupes by entity reference.
    /// Keystone-managed entities (Class A/B/C) are excluded.
    /// </summary>
    private void AccumulateEntities(SurfaceCoord s) {
      if (_entityIndexToName.Count == 0) return;
      for (var dz = 0; dz <= VerticalProbeRange; dz++) {
        _enumerator.EnumerateNaturalResourcesAt(s.X, s.Y, s.Z + dz, _accumulateEntityCallback);
      }
    }

    private Action<object, NaturalResourceProbe>? _accumulateEntityCallbackCached;
    private Action<object, NaturalResourceProbe> _accumulateEntityCallback
        => _accumulateEntityCallbackCached ??= AccumulateEntityCallback;

    private Func<string, int?>? _entityIndexLookupCached;
    private Func<string, int?> EntityIndexLookup
        => _entityIndexLookupCached ??=
            name => _entityIndices.TryGetValue(name, out var i) ? i : (int?)null;

    private void AccumulateEntityCallback(object entityKey, NaturalResourceProbe probe) {
      if (!_scratchSeen.Add(entityKey)) return;
      var idx = NaturalResourceRouter.RouteToChannel(
          probe, EntityIndexLookup, _deadEntityIndex);
      if (!idx.HasValue) return;
      _scratchEntities[idx.Value] += 1f;
    }

    #endregion

    #region Schedule helpers

    private sealed class RegionScratch {
      public readonly List<SurfaceCoord> Surfaces = new();
      private int _minX = int.MaxValue, _minY = int.MaxValue;
      private int _maxX = int.MinValue, _maxY = int.MinValue;

      public void Add(SurfaceCoord s) {
        Surfaces.Add(s);
        if (s.X < _minX) _minX = s.X;
        if (s.Y < _minY) _minY = s.Y;
        if (s.X > _maxX) _maxX = s.X;
        if (s.Y > _maxY) _maxY = s.Y;
      }

      public (int originX, int originY, int chunksX, int chunksY) ChunkAlignedBbox() {
        var size = RegionEcologyField.ChunkSize;
        var originX = (_minX / size) * size;
        var originY = (_minY / size) * size;
        var chunksX = ((_maxX - originX) / size) + 1;
        var chunksY = ((_maxY - originY) / size) + 1;
        return (originX, originY, chunksX, chunksY);
      }

      public List<TileCoord> UniqueTileCoords() {
        var seen = new HashSet<long>();
        var result = new List<TileCoord>();
        foreach (var s in Surfaces) {
          var key = ((long)s.X << 32) | (uint)s.Y;
          if (seen.Add(key)) result.Add(new TileCoord(s.X, s.Y));
        }
        return result;
      }
    }

    #endregion

  }

}
