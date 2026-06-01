using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Survey;
using Timberborn.BlockSystem;
using Timberborn.Effects;
using Timberborn.EnterableSystem;
using Timberborn.EntitySystem;
using Timberborn.NeedSystem;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;

namespace Keystone.Mod.Wellbeing {

  /// <summary>
  /// Per-building tickable that drives biome-keyed Nature-need
  /// satisfaction on visiting beavers. Attached to any blueprint
  /// carrying <see cref="KeystoneNatureSourceSpec"/> via the decorator
  /// binding in <c>KeystoneTemplateModuleProvider</c>.
  ///
  /// <para><b>Lifecycle.</b> On <see cref="InitializeEntity"/> the
  /// component caches refs to <c>BlockObject</c>, <c>Enterable</c>, and
  /// its own spec, plus parses the source list into <see cref="BiomeKind"/>
  /// values (unknown biome names are dropped with a one-shot warning).
  /// On <see cref="Tick"/> it (a) periodically refreshes the current
  /// winning source based on the cluster index, and (b) every tick
  /// applies the current source's <c>ContinuousEffect</c> to every
  /// beaver inside the <c>Enterable</c>.</para>
  ///
  /// <para><b>Cluster-score-driven, vantage-filtered selection.</b>
  /// Once per <see cref="RefreshIntervalTicks"/> ticks, the refresh
  /// runs in three passes:
  /// <list type="number">
  /// <item><b>Footprint walk.</b> Build the de-duped 2×2 chunk
  ///       neighbourhood across the building's foundation tiles and
  ///       capture the foundation's top Z (vantage height). Foundation
  ///       rather than all occupied tiles keeps tall BOs' canopy from
  ///       inflating the XY footprint (Tree of Life: 3×3 plate, 11×11
  ///       canopy — we want the plate).</item>
  /// <item><b>Chunk-XY enumeration with Z filter.</b> For each
  ///       (chunkX, chunkY) in the neighbourhood, ask
  ///       <see cref="ChunkClusterIndex.ChunksAtChunkXY"/> for every
  ///       (region, cluster) sitting at that XY across all regions.
  ///       Filter by <c>region.Z &lt;= foundationTopZ</c> ("gaze
  ///       down, not up") and collect surviving cluster ids. The
  ///       XY-level lookup is what handles stacked-on-top BOs
  ///       correctly: a RooftopTerrace's columns sit in a settled
  ///       region under the lodge, but the wild ecology region also
  ///       has a chunk-level entry at the same (chunkX, chunkY)
  ///       covering whichever columns aren't settled — the
  ///       enumeration finds it without per-column scanning.</item>
  /// <item><b>Per-biome raw-score aggregation + single saturation.</b>
  ///       For each eligible source, sum
  ///       <see cref="ChunkClusterIndex.RawScore"/> across all touched
  ///       same-biome clusters, then apply the canonical saturation
  ///       once:
  ///       <code>
  ///       sumRaw = Σ_clusters cluster.RawScore           (matching biome)
  ///       score  = ChunkClusterIndex.SaturatedScore(sumRaw)   // raw / (raw + K)
  ///       rate   = score · PointsPerHour
  ///       </code>
  ///       Per-cluster <c>RawScore</c> is precomputed during index
  ///       rebuild (weighted sum of threshold-bucket tile counts), so
  ///       this loop is just an addition per touched cluster.
  ///       Summing raws then saturating once preserves the
  ///       "stacked-Z biomes feel like one big biome" property —
  ///       two stacked forests don't sum to score 1.2; they compose
  ///       into one larger sumRaw that the hyperbolic curve then
  ///       maps into <c>[0, 1)</c>.</item>
  /// </list>
  /// The source with the highest rate wins; the building applies that
  /// one need's <c>ContinuousEffect</c> to every enterer per tick.
  /// Single-winner is by design: the player perceives "this spot is
  /// well-placed for X biome" rather than fractional credit across
  /// multiple needs.</para>
  ///
  /// <para><b>Cluster contribution is whole-cluster.</b> A cluster
  /// touched even once by a sample contributes its full
  /// <see cref="ChunkClusterIndex.TileCountsAbove"/> — every member
  /// chunk's tile counts at every maturity bucket the chunk clears,
  /// across the cluster's entire extent. A building at the corner of
  /// a 50-chunk pristine Forest scores the same as a building deep
  /// inside it; both touch the same cluster, both read the same
  /// aggregate. This matches the player intuition "I'm near a real
  /// mature Forest" — proximity to one chunk of a large biome is the
  /// signal, not how many chunks sit directly under the footprint.</para>
  ///
  /// <para><b>Bypasses AttractionSpec.</b> Vanilla <c>Attraction.Effects</c>
  /// and its associated entertainment satisfaction is left untouched —
  /// this component never reads or writes it. Players who load saves
  /// with leisure buildings placed in "wrong" biomes simply get no
  /// Nature satisfaction from them; entertainment continues working
  /// exactly as it always did.</para>
  /// </summary>
  public sealed class KeystoneNatureSource : TickableComponent, IInitializableEntity {

    #region Constants

    /// <summary>How often to recompute the winning source. Game ticks
    /// run at ~5/sec at 1× speed, so 600 ticks ≈ 2 game-minutes at 1×
    /// — generous given Maturity moves on a day-scale, but cheap.</summary>
    private const int RefreshIntervalTicks = 600;

    #endregion

    #region Injected services

    private readonly IDayNightCycle _dayNightCycle;
    private readonly KeystoneSurveyor _surveyor;
    private readonly ChunkClusterIndex _clusterIndex;

    #endregion

    #region Per-instance state

    private BlockObject? _blockObject;
    private Enterable? _enterable;
    private KeystoneNatureSourceSpec? _spec;
    private readonly List<ResolvedSource> _resolved = new();

    private string? _currentNeedId;
    private float _currentPointsPerHour;
    private BiomeKind? _currentBiome;
    private float _currentScore;
    private int _currentChunkCount;
    private float _currentAverageMaturity;
    private int _ticksUntilRefresh;

    /// <summary>De-dup set so unknown-biome warnings log once per
    /// instance per biome name, not per tick.</summary>
    private readonly HashSet<string> _warnedUnknownBiomes = new();

    /// <summary>Reused (chunkX, chunkY) set deduped from the building's
    /// per-tile nearest-chunk neighbourhoods. Held as a field so the
    /// refresh doesn't allocate per call.</summary>
    private readonly HashSet<(int Cx, int Cy)> _scratchChunks = new();

    /// <summary>Reused set of cluster ids the current
    /// <see cref="ChunkClusterIndex.ChunksAtChunkXY"/> sweep collects.
    /// De-dupes across the chunk neighbourhood: stacked terraces or
    /// settled/wild splits can route multiple (region, cluster) tuples
    /// at one XY, and the same cluster can show up across adjacent
    /// XYs if it spans them. Cluster ids are globally unique within a
    /// rebuild snapshot so a HashSet keyed on the id alone is
    /// sufficient.</summary>
    private readonly HashSet<ChunkClusterId> _scratchClusterIds = new();

    #endregion

    #region Construction

    public KeystoneNatureSource(
        IDayNightCycle dayNightCycle,
        KeystoneSurveyor surveyor,
        ChunkClusterIndex clusterIndex) {
      _dayNightCycle = dayNightCycle;
      _surveyor = surveyor;
      _clusterIndex = clusterIndex;
    }

    #endregion

    #region Entity lifecycle

    public void InitializeEntity() {
      // Outermost try/catch: spec parsing or component lookup throwing
      // would otherwise leave the entity in a half-init state with
      // _resolved empty AND no _spec/_enterable, while Bindito has
      // already added it to the tick list. Catch + log + record so the
      // entity exists but skips Keystone wiring; the Tick guard
      // (_spec == null || _resolved.Count == 0) early-exits cleanly.
      try {
        _blockObject = GetComponent<BlockObject>();
        _enterable = GetComponent<Enterable>();
        var spec = GetComponent<BlockObjectSpec>();
        _spec = spec != null ? spec.GetSpec<KeystoneNatureSourceSpec>() : null;
        if (_spec == null) {
          KeystoneLog.Error($"[Keystone] KeystoneNatureSource on '{Name}': " +
              "no KeystoneNatureSourceSpec on the BlockObjectSpec. Component disabled.");
          return;
        }
        if (_enterable == null) {
          KeystoneLog.Error($"[Keystone] KeystoneNatureSource on '{Name}': " +
              "no Enterable component. Beavers won't enter, no satisfaction will fire.");
        }

        // Parse spec entries once; drop any with unparseable BiomeKind.
        var sources = _spec.Sources;
        if (sources.IsDefault) return;
        for (var i = 0; i < sources.Length; i++) {
          var entry = sources[i];
          if (!Enum.TryParse<BiomeKind>(entry.Biome, ignoreCase: false, out var kind)) {
            if (_warnedUnknownBiomes.Add(entry.Biome)) {
              KeystoneLog.Error($"[Keystone] KeystoneNatureSource on '{Name}': " +
                  $"unknown biome '{entry.Biome}'; entry dropped.");
            }
            continue;
          }
          _resolved.Add(new ResolvedSource(kind, entry.NeedId, entry.PointsPerHour));
        }
      } catch (System.Exception ex) {
        LifecycleGuard.HandleError($"KeystoneNatureSource.InitializeEntity on '{Name}'", "Per-entity init errors", ex);
      }
    }

    #endregion

    #region TickableComponent

    /// <inheritdoc />
    public override void Tick() {
      if (_spec == null || _enterable == null || _resolved.Count == 0) return;
      // Outermost try/catch added below at the end of the method body;
      // the existing per-enterer inner catch handles individual failed
      // beavers, but a throw from RefreshWinningSource (cluster index
      // lookup, region service access) would otherwise escape the
      // tick handler.
      try {

      if (--_ticksUntilRefresh <= 0) {
        RefreshWinningSource();
        _ticksUntilRefresh = RefreshIntervalTicks;
      }

      if (_currentNeedId == null) return;

      var effect = new ContinuousEffect(_currentNeedId, _currentPointsPerHour);
      var deltaHours = _dayNightCycle.FixedDeltaTimeInHours;
      // Per-enterer isolation: one beaver's GetComponent / ApplyEffect
      // failure (corrupted state, mod-shipped NeedManager that throws
      // on lookup, etc.) shouldn't skip the rest of the queue. Catch +
      // log once per source-entity; subsequent enterers continue.
      foreach (var enterer in _enterable.EnterersInside) {
        if (enterer == null) continue;
        try {
          var nm = enterer.GetComponent<NeedManager>();
          if (nm != null && nm.HasNeed(_currentNeedId)) {
            nm.ApplyEffect(effect, deltaHours);
          }
        } catch (System.Exception ex) {
          if (!_entererFailureLogged) {
            _entererFailureLogged = true;
            KeystoneLog.Error(
                $"[Keystone] KeystoneNatureSource.Tick on '{Name}': enterer " +
                $"need-effect application threw {ex.GetType().Name}: {ex.Message}. " +
                "Skipping this enterer; subsequent enterers continue.");
            KeystoneIntegrationHealth.TryRecord("Per-entity tick errors", $"NatureSource enterer at {Name}");
          }
        }
      }
      } catch (System.Exception ex) {
        LifecycleGuard.HandleErrorOnce("KeystoneNatureSource.Tick", "Per-entity tick errors", ex, ref _tickOuterFailureLogged);
      }
    }

    private bool _tickOuterFailureLogged;

    /// <summary>One-shot rate-limit so a persistently-failing enterer
    /// inside this source doesn't spam <c>Player.log</c> every tick.
    /// Cleared only when the source entity is destroyed.</summary>
    private bool _entererFailureLogged;

    #endregion

    #region Winning-source selection

    private void RefreshWinningSource() {
      var bo = _blockObject;
      if (bo == null || bo.PositionedBlocks == null) {
        ClearCurrent();
        return;
      }

      // Pass 1: chunk neighbourhood + foundationTopZ from the BO's
      // FOUNDATION tiles only — the subset of blocks that form the
      // BO's base plate. Foundation rather than all occupied tiles
      // keeps tall BOs' canopy from inflating the XY footprint (Tree
      // of Life: 3x3 plate, 11x11 canopy — we want the plate). For
      // single-Z BOs (ContemplationSpot, Lido, Campfire, RooftopTerrace)
      // foundation == occupied so the chunk set is unchanged.
      //
      // foundationTopZ is the HIGHEST Z across the foundation — for
      // a flat-bottomed BO this equals every foundation tile's Z; for
      // a foundation with mild Z variation it picks the top step. The
      // "you gaze down, not up" rule below uses it as the vantage cap.
      _scratchChunks.Clear();
      var foundationTopZ = int.MinValue;
      foreach (var coord in bo.PositionedBlocks.GetFoundationCoordinates()) {
        if (coord.z > foundationTopZ) foundationTopZ = coord.z;
        foreach (var chunk in ComputeFourNearestChunks(coord.x, coord.y)) {
          _scratchChunks.Add(chunk);
        }
      }
      if (_scratchChunks.Count == 0) {
        ClearCurrent();
        return;
      }

      // Pass 2: chunk-XY enumeration. For each (chunkX, chunkY) in the
      // neighbourhood, ask the cluster index for every (region, cluster)
      // sitting at that XY across all regions. Filter by region.Z so
      // we never score against clusters above the BO ("gaze down, not
      // up"); collect surviving cluster ids.
      //
      // Why XY-level rather than per-column-surface: the old per-column
      // scan asked "what region is the surface in this column at this
      // Z?" — columns directly under a stacked-on-top BO (RooftopTerrace
      // on a lodge) sit in the SETTLED region, not the wild ecology
      // region, even though the wild region also has a chunk-level entry
      // at the same (chunkX, chunkY) covering whichever columns aren't
      // settled. The old scan never reached that wild-region chunk; the
      // new one does. Region identity already includes Z (one region
      // per Z layer), so the Z filter against region.Z is equivalent
      // to the per-surface filter the old code was applying.
      _scratchClusterIds.Clear();
      foreach (var chunk in _scratchChunks) {
        var clustersHere = _clusterIndex.ChunksAtChunkXY(chunk.Cx, chunk.Cy);
        for (var i = 0; i < clustersHere.Count; i++) {
          var (regionId, clusterId) = clustersHere[i];
          var region = _surveyor.Regions.Get(regionId);
          if (region == null || region.Z > foundationTopZ) continue;
          _scratchClusterIds.Add(clusterId);
        }
      }
      if (_scratchClusterIds.Count == 0) {
        ClearCurrent();
        return;
      }

      // Pass 4: per source, sum the per-cluster RawScore across all
      // touched same-biome clusters, then apply the canonical
      // saturation function once. Stacked-Z forests on a terraced
      // slope read as one logical Forest to the player and the math
      // agrees (raw is additive across clusters; the curve is applied
      // to the sum, not the per-cluster scores). Pick the
      // highest-rate source as the single winner.
      string? bestNeed = null;
      BiomeKind? bestBiome = null;
      var bestRate = 0f;
      var bestScore = 0f;
      var bestChunkCount = 0;
      var bestAverageMaturity = 0f;

      for (var srcIdx = 0; srcIdx < _resolved.Count; srcIdx++) {
        var src = _resolved[srcIdx];
        var sumRaw = 0f;
        var totalChunks = 0;
        var maturityWeightedSum = 0f;

        foreach (var id in _scratchClusterIds) {
          var biome = _clusterIndex.BiomeFor(id);
          if (biome != src.Biome) continue;
          sumRaw += _clusterIndex.RawScore(id);
          var chunkCount = _clusterIndex.ChunkCount(id);
          totalChunks += chunkCount;
          maturityWeightedSum += _clusterIndex.AverageMaturity(id) * chunkCount;
        }
        if (sumRaw <= 0f) continue;

        var score = ChunkClusterIndex.SaturatedScore(sumRaw);
        var rate = score * src.PointsPerHour;
        if (rate > bestRate) {
          bestRate = rate;
          bestScore = score;
          bestNeed = src.NeedId;
          bestBiome = src.Biome;
          bestChunkCount = totalChunks;
          bestAverageMaturity = totalChunks > 0 ? maturityWeightedSum / totalChunks : 0f;
        }
      }

      _currentNeedId = bestNeed;
      _currentPointsPerHour = bestRate;
      _currentBiome = bestBiome;
      _currentScore = bestScore;
      _currentChunkCount = bestChunkCount;
      _currentAverageMaturity = bestAverageMaturity;
    }

    private void ClearCurrent() {
      _currentNeedId = null;
      _currentPointsPerHour = 0f;
      _currentBiome = null;
      _currentScore = 0f;
      _currentChunkCount = 0;
      _currentAverageMaturity = 0f;
    }

    /// <summary>Pick the 2x2 block of global chunks closest to the
    /// building's tile (the containing chunk plus its 3 neighbours in
    /// the direction the building offsets from chunk centre).</summary>
    private static (int Cx, int Cy)[] ComputeFourNearestChunks(int tileX, int tileY) {
      const int cs = RegionEcologyField.ChunkSize;
      var cx0 = FloorDiv(tileX, cs);
      var cy0 = FloorDiv(tileY, cs);
      var subX = tileX - cx0 * cs;            // [0, cs)
      var subY = tileY - cy0 * cs;            // [0, cs)
      var half = cs / 2;
      var cx1 = subX >= half ? cx0 + 1 : cx0 - 1;
      var cy1 = subY >= half ? cy0 + 1 : cy0 - 1;
      return new[] { (cx0, cy0), (cx1, cy0), (cx0, cy1), (cx1, cy1) };
    }

    /// <summary>Integer floor division that handles negative tile
    /// coordinates correctly. C#'s <c>/</c> rounds toward zero, which
    /// would split chunks across the origin in the wrong direction.</summary>
    private static int FloorDiv(int a, int b) =>
        a >= 0 ? a / b : (a - b + 1) / b;

    #endregion

    #region Diagnostics

    /// <summary>Currently winning need-id, or null if no source is
    /// active. Used by the debug overlay; the runtime tick reads
    /// <see cref="_currentNeedId"/> directly.</summary>
    public string? CurrentNeedId => _currentNeedId;

    /// <summary>Currently effective PointsPerHour after score scaling.
    /// Zero when no source is active.</summary>
    public float CurrentPointsPerHour => _currentPointsPerHour;

    /// <summary>Biome that's currently winning the source selection,
    /// or null if no source is active. Same selection that drives
    /// <see cref="CurrentNeedId"/>; exposed separately so the describer
    /// doesn't have to map need-id back to biome.</summary>
    public BiomeKind? CurrentBiome => _currentBiome;

    /// <summary>Continuous score in <c>[0, 1)</c> for the winning
    /// source — <see cref="ChunkClusterIndex.SaturatedScore"/> applied
    /// to the sum of per-cluster raw scores across all touched
    /// same-biome clusters. Asymptotic to 1, never reaches it. Zero
    /// when no source is active. The describer buckets this into a
    /// tier label (Minor / Medium / Major).</summary>
    public float CurrentScore => _currentScore;

    /// <summary>Chunk count of the winning cluster — drives the
    /// describer's size adjective (small / medium / large). Zero when
    /// no source is active.</summary>
    public int CurrentChunkCount => _currentChunkCount;

    /// <summary>Average Maturity (game-days) of the winning cluster —
    /// drives the describer's maturity adjective (immature / healthy
    /// / mature). Zero when no source is active.</summary>
    public float CurrentAverageMaturity => _currentAverageMaturity;

    /// <summary>True once <see cref="InitializeEntity"/> has resolved
    /// the spec into the runtime source list — i.e., this is a real
    /// placed entity rather than a build-menu preview. The describer
    /// uses this to decide whether to surface "what could this do"
    /// (preview / menu mode) vs "what's it doing right now" (placed
    /// mode).</summary>
    public bool IsInWorld => _resolved.Count > 0;

    /// <summary>Run a fresh diagnostic scan and return the per-pass
    /// counters plus the sample surfaces that contributed. Used by the
    /// tile-debug panel and the highlighter when the cursor is over
    /// this BO. Allocates a new <see cref="InspectionResult"/>
    /// (debug-only path, allocation cost is fine). Returns
    /// <c>HasResult=false</c> when there's no BlockObject or no
    /// PositionedBlocks yet (preview / not-fully-initialised).
    ///
    /// <para>Mirrors <see cref="RefreshWinningSource"/> exactly — same
    /// vantage filter (<c>sz &lt;= buildingBaseZ</c>), same
    /// 4-nearest-chunk neighbourhood per footprint tile, same
    /// region-then-cluster resolution, same per-source raw + saturated
    /// scoring. The duplication is deliberate: this method is read by
    /// debug surfaces and shouldn't perturb the runtime tick's
    /// internal state.</para></summary>
    public InspectionResult RunInspectionScan() {
      var bo = _blockObject;
      if (bo == null || bo.PositionedBlocks == null) return InspectionResult.Empty;

      // Pass 1: chunk neighbourhood + foundationTopZ from the BO's
      // foundation tiles — see RefreshWinningSource for the
      // foundation-vs-occupied rationale.
      var chunks = new HashSet<(int Cx, int Cy)>();
      var foundationTopZ = int.MinValue;
      foreach (var coord in bo.PositionedBlocks.GetFoundationCoordinates()) {
        if (coord.z > foundationTopZ) foundationTopZ = coord.z;
        foreach (var c in ComputeFourNearestChunks(coord.x, coord.y)) {
          chunks.Add(c);
        }
      }
      if (chunks.Count == 0) return InspectionResult.Empty;

      // Pass 2: chunk-XY enumeration with Z filter. Mirror the runtime
      // scan (see RefreshWinningSource for the algorithmic notes). The
      // diagnostic additionally records the per-(region, chunk) tuples
      // it kept, so the highlighter can paint the surfaces those
      // clusters cover in the next step.
      var clusterIds = new HashSet<ChunkClusterId>();
      var keptTuples = new List<(RegionId Region, int Cx, int Cy, int RegionZ)>();
      var chunksConsidered = 0;
      var chunksAboveZ = 0;
      foreach (var chunk in chunks) {
        var clustersHere = _clusterIndex.ChunksAtChunkXY(chunk.Cx, chunk.Cy);
        for (var i = 0; i < clustersHere.Count; i++) {
          chunksConsidered++;
          var (regionId, clusterId) = clustersHere[i];
          var region = _surveyor.Regions.Get(regionId);
          if (region == null) continue;
          if (region.Z > foundationTopZ) {
            chunksAboveZ++;
            continue;
          }
          clusterIds.Add(clusterId);
          keptTuples.Add((regionId, chunk.Cx, chunk.Cy, region.Z));
        }
      }

      // Pass 3: surface-fill list for the overlay. For each kept
      // (region, chunk) tuple, walk the chunk's 16 columns and paint
      // every surface at the region's Z. The runtime tick doesn't do
      // this work — only the diagnostic does, since the visual goal
      // is "show me which surfaces this cluster covers in this chunk."
      const int cs = RegionEcologyField.ChunkSize;
      var sampledSurfaces = new List<SurfaceCoord>();
      foreach (var (regionId, cx, cy, regionZ) in keptTuples) {
        var xMin = cx * cs;
        var yMin = cy * cs;
        for (var dx = 0; dx < cs; dx++) {
          for (var dy = 0; dy < cs; dy++) {
            var column = new TileCoord(xMin + dx, yMin + dy);
            var heights = _surveyor.Core.ColumnSurfaceHeights(column);
            for (var i = 0; i < heights.Count; i++) {
              if (heights[i] != regionZ) continue;
              var surf = new SurfaceCoord(column.X, column.Y, regionZ);
              var owner = _surveyor.Regions.Containing(surf);
              if (owner != null && owner.Id.Equals(regionId)) {
                sampledSurfaces.Add(surf);
              }
            }
          }
        }
      }

      // Pass 4: per source, sum raw, saturate, multiply. Mirror the
      // production scoring exactly so the diagnostic shows what the
      // runtime would have produced.
      var perSource = new List<BiomeInspection>(_resolved.Count);
      for (var srcIdx = 0; srcIdx < _resolved.Count; srcIdx++) {
        var src = _resolved[srcIdx];
        var sumRaw = 0f;
        var matchingClusters = 0;
        foreach (var id in clusterIds) {
          var biome = _clusterIndex.BiomeFor(id);
          if (biome != src.Biome) continue;
          sumRaw += _clusterIndex.RawScore(id);
          matchingClusters++;
        }
        var score = sumRaw > 0f ? ChunkClusterIndex.SaturatedScore(sumRaw) : 0f;
        var rate = score * src.PointsPerHour;
        perSource.Add(new BiomeInspection(
            src.NeedId, src.Biome, matchingClusters, sumRaw, score, rate));
      }

      return new InspectionResult(
          hasResult: true,
          foundationTopZ: foundationTopZ,
          chunkNeighborhood: new List<(int, int)>(chunks),
          chunksConsidered: chunksConsidered,
          chunksAboveZ: chunksAboveZ,
          chunksKept: keptTuples.Count,
          sampledSurfaces: sampledSurfaces,
          clusterCount: clusterIds.Count,
          perSource: perSource);
    }

    #endregion

    #region Diagnostic result types

    /// <summary>Per-pass output of <see cref="RunInspectionScan"/>.
    /// Read by the tile-debug panel for the textual readout and by
    /// <see cref="Keystone.Mod.Visualization.PlateauHighlighter"/>
    /// for the sample-surface overlay.</summary>
    public sealed class InspectionResult {
      public static readonly InspectionResult Empty = new(
          hasResult: false,
          foundationTopZ: 0,
          chunkNeighborhood: System.Array.Empty<(int, int)>(),
          chunksConsidered: 0,
          chunksAboveZ: 0,
          chunksKept: 0,
          sampledSurfaces: System.Array.Empty<SurfaceCoord>(),
          clusterCount: 0,
          perSource: System.Array.Empty<BiomeInspection>());

      public InspectionResult(
          bool hasResult,
          int foundationTopZ,
          IReadOnlyList<(int Cx, int Cy)> chunkNeighborhood,
          int chunksConsidered,
          int chunksAboveZ,
          int chunksKept,
          IReadOnlyList<SurfaceCoord> sampledSurfaces,
          int clusterCount,
          IReadOnlyList<BiomeInspection> perSource) {
        HasResult = hasResult;
        FoundationTopZ = foundationTopZ;
        ChunkNeighborhood = chunkNeighborhood;
        ChunksConsidered = chunksConsidered;
        ChunksAboveZ = chunksAboveZ;
        ChunksKept = chunksKept;
        SampledSurfaces = sampledSurfaces;
        ClusterCount = clusterCount;
        PerSource = perSource;
      }

      /// <summary>False if the BO isn't fully placed (no PositionedBlocks)
      /// — callers should suppress all readouts.</summary>
      public bool HasResult { get; }
      /// <summary>Highest Z across the BO's foundation tiles (the
      /// base plate — Timberborn's <c>PositionedBlocks.GetFoundationCoordinates</c>).
      /// Pass 2's vantage filter drops surfaces with
      /// <c>sz &gt; FoundationTopZ</c> — a beaver on the BO sees
      /// anything at or below its base plate. Scoping to the
      /// foundation (rather than every occupied tile) is what
      /// keeps tall BOs from picking the canopy as their vantage
      /// and from inflating the chunk neighbourhood to the canopy's
      /// XY spread.</summary>
      public int FoundationTopZ { get; }
      /// <summary>Pass 1: the union of per-tile 4-nearest-chunks across
      /// the BO's footprint. Pass 2 enumerates clusters at every XY in
      /// here.</summary>
      public IReadOnlyList<(int Cx, int Cy)> ChunkNeighborhood { get; }
      /// <summary>Pass 2: total (region, cluster) entries the cluster
      /// index returned across the chunk neighbourhood (before the Z
      /// filter). One entry per (region, chunkX, chunkY) the index
      /// knows about at the touched XYs.</summary>
      public int ChunksConsidered { get; }
      /// <summary>Pass 2: entries dropped because their region's Z is
      /// above the BO's foundation top — "gaze down, not up" rejects.</summary>
      public int ChunksAboveZ { get; }
      /// <summary>Pass 2: entries kept after the Z filter. These are
      /// the (region, chunk) tuples that contributed cluster ids to
      /// scoring. Pass 3 walks these to populate
      /// <see cref="SampledSurfaces"/>.</summary>
      public int ChunksKept { get; }
      /// <summary>Pass 3: every surface coord covered by a kept
      /// (region, chunk) tuple — i.e. the surfaces the highlighter
      /// paints to show "what we scored against." Generated by
      /// walking each kept chunk's 16 columns and matching surfaces
      /// at the chunk's region Z. Diagnostic-only; the runtime tick
      /// doesn't compute this list.</summary>
      public IReadOnlyList<SurfaceCoord> SampledSurfaces { get; }
      /// <summary>Distinct cluster ids the kept (region, chunk)
      /// tuples resolved to. May be lower than <see cref="ChunksKept"/>
      /// when one cluster spans multiple chunks in the
      /// neighbourhood.</summary>
      public int ClusterCount { get; }
      /// <summary>Pass 4: per-source raw + saturated + rate, in
      /// the order of the BO's spec. Inspection with no matching
      /// biome clusters yields entries with <c>SumRaw == 0</c>.</summary>
      public IReadOnlyList<BiomeInspection> PerSource { get; }
    }

    public readonly struct BiomeInspection {
      public BiomeInspection(string needId, BiomeKind biome, int clusterCount,
                             float sumRaw, float score, float rate) {
        NeedId = needId;
        Biome = biome;
        ClusterCount = clusterCount;
        SumRaw = sumRaw;
        Score = score;
        Rate = rate;
      }
      public string NeedId { get; }
      public BiomeKind Biome { get; }
      /// <summary>How many touched clusters had matching biome.</summary>
      public int ClusterCount { get; }
      /// <summary>Sum of per-cluster RawScores across matching-biome clusters.</summary>
      public float SumRaw { get; }
      /// <summary>Hyperbolic-saturated score in [0, 1).</summary>
      public float Score { get; }
      /// <summary>Score × source.PointsPerHour — the rate this source
      /// would apply if it wins.</summary>
      public float Rate { get; }
    }

    #endregion

    private readonly struct ResolvedSource {
      public ResolvedSource(BiomeKind biome, string needId, float pointsPerHour) {
        Biome = biome;
        NeedId = needId;
        PointsPerHour = pointsPerHour;
      }
      public BiomeKind Biome { get; }
      public string NeedId { get; }
      public float PointsPerHour { get; }
    }

  }

}
