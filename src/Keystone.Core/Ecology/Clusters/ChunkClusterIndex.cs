using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;

namespace Keystone.Core.Ecology.Clusters {

  /// <summary>
  /// Coarse-grained clustering layer above the per-chunk biome field.
  /// Groups adjacent chunks that share the same dominant
  /// (Suitability-argmax) biome AND have that biome's Maturity at or
  /// above a global threshold AND whose dominant biome is on the
  /// <see cref="ClusterableBiomes"/> whitelist AND whose resulting
  /// cluster contains at least two chunks. Chunks that don't qualify
  /// are absent from the index — consumers querying them get
  /// <c>null</c>, the signal "this chunk isn't a member of any
  /// qualifying cluster."
  ///
  /// <para><b>Non-qualifying conditions, exhaustive:</b>
  /// <list type="bullet">
  ///   <item>No dominant biome (every biome's Suitability is 0).</item>
  ///   <item>Dominant biome's Maturity below the rebuild threshold.</item>
  ///   <item>Dominant biome not in <see cref="ClusterableBiomes"/>.
  ///         Today excludes the three aggressors (Badwater, Contaminated,
  ///         Dry) plus Cave and Monoculture — biomes with no current
  ///         consumer. Whitelist by design, see field docstring.</item>
  ///   <item>Resulting connected component has only one chunk.
  ///         Single-chunk clusters carry no signal a single-chunk
  ///         dominance query couldn't supply, so they're dropped
  ///         post-union-find to avoid the allocation and bookkeeping.</item>
  /// </list></para>
  ///
  /// <para><b>Cadence.</b> Rebuilt once per ecology cycle by an
  /// external hook (e.g. an end-of-cycle event in the Mod layer).
  /// Within a cycle, queries are consistent against the snapshot
  /// captured at the most recent <see cref="Rebuild"/>; mid-cycle
  /// chunk value changes are invisible until the next rebuild.</para>
  ///
  /// <para><b>Stability.</b> <see cref="ChunkClusterId"/> values are
  /// NOT stable across rebuilds. A cluster keeping the same member
  /// chunks may be assigned a different id after a rebuild. Consumers
  /// that hold long-lived handles should remember a
  /// <see cref="ChunkCoord"/> (e.g. the fauna's current chunk) and
  /// re-resolve on each periodic check.</para>
  ///
  /// <para><b>Single-region per cluster in v1.</b> The union-find pass
  /// only considers 4-neighbours within the same region's chunk grid.
  /// Two adjacent same-biome chunks in different regions land in
  /// separate clusters today. The API surface is region-agnostic so
  /// cross-region unioning can be retrofitted without breaking
  /// consumers.</para>
  ///
  /// <para><b>Algorithm.</b> Per region:
  /// <list type="number">
  ///   <item>Compute each chunk's qualifying biome (Suitability
  ///         argmax + Maturity ≥ threshold), or null otherwise.</item>
  ///   <item>Union-find over chunks with right/down neighbours that
  ///         share the qualifying biome.</item>
  ///   <item>Walk chunks, group by union-find root into
  ///         <c>ClusterEntry</c> records; allocate
  ///         <see cref="ChunkClusterId"/>s sequentially.</item>
  /// </list>
  /// O(chunks) per rebuild, dominated by the chunk-iteration pass.
  /// Path compression keeps union-find amortised near-constant.</para>
  /// </summary>
  public sealed class ChunkClusterIndex {

    #region Constants

    /// <summary>Biomes the cluster index actually builds clusters for.
    /// Chunks whose dominant biome is NOT in this list don't qualify
    /// regardless of Maturity and are absent from the index.
    ///
    /// <para><b>Semantically distinct from
    /// <c>ChunkBiomeSampler.BiomesByAggressorTier</c>.</b> That list
    /// orders biomes for dominance-argmax tiebreaking — a real
    /// ecological concept (Badwater outranks Contaminated on a fully-
    /// badwater chunk). This list answers a different question:
    /// "which biomes are worth clustering for downstream consumers?"
    /// The two lists must evolve independently; today's overlap
    /// (aggressors out, plus Cave/Monoculture out, rest in) is a
    /// coincidence of v1 consumer needs, not a rule.</para>
    ///
    /// <para><b>Whitelist by design.</b> Adding a new biome to the
    /// project must be a deliberate "do consumers care about clusters
    /// of this?" decision, not a silent inclusion. Cave and Monoculture
    /// are out today because no fauna, wellbeing, or debug consumer
    /// reads cluster info for them; add them back here when one does
    /// and update the rationale.</para></summary>
    public static readonly IReadOnlyList<BiomeKind> ClusterableBiomes = new[] {
        BiomeKind.River,
        BiomeKind.Wetland,
        BiomeKind.Lake,
        BiomeKind.Forest,
        BiomeKind.Grassland,
    };

    /// <summary>Maturity thresholds at which the rebuild caches a
    /// per-cluster "tiles at or above this Maturity" count. The
    /// ordering of this array is the ordering of the parallel array
    /// returned by <see cref="TileCountsAbove"/>.
    ///
    /// <para>The lowest threshold (2.5) is intentionally above the
    /// cluster qualification floor (1.0 in <c>ChunkBiomeTicker</c>) so
    /// it sub-segments meaningfully — every member chunk already clears
    /// the qualification floor, so a threshold AT the floor would
    /// trivially count the whole cluster. If the qualification floor
    /// is ever raised above 2.5, the lowest bucket here becomes
    /// trivial and should be removed.</para></summary>
    public static readonly IReadOnlyList<float> Thresholds =
        new[] { 2.5f, 5.0f, 10.0f, 15.0f, 20.0f };

    /// <summary>Per-threshold quality weight, parallel to
    /// <see cref="Thresholds"/>. A tile contributes
    /// <c>BucketWeights[t]</c> to the cluster's <see cref="RawScore"/>
    /// for every threshold <c>t</c> its dominant-biome Maturity clears.
    /// Linear-in-threshold schedule (weight = threshold / 4): a tile at
    /// Maturity ≥20 clears all five and contributes 0.625 + 1.25 + 2.5
    /// + 3.75 + 5.0 = 13.125; a tile at Maturity ≥2.5 but &lt;5 contributes
    /// 0.625 (so a maturity-22 tile is worth ~21× a barely-qualifying
    /// one — the "quality dominates" axis the player feels).</summary>
    public static readonly IReadOnlyList<float> BucketWeights =
        new[] { 0.625f, 1.25f, 2.5f, 3.75f, 5.0f };

    /// <summary>Raw-score value at which the normalised
    /// <see cref="Score"/> equals 0.5 (Michaelis-Menten half-saturation
    /// constant). Lower = curve saturates sooner (small clusters feel
    /// "full"); higher = curve stays linear longer (only huge or
    /// pristine clusters feel "full"). The score is
    /// <c>raw / (raw + HalfSaturationRaw)</c>, so doubling this
    /// constant moves the whole curve right by the same factor:
    /// score(K) = 0.5, score(2K) ≈ 0.67, score(4K) ≈ 0.80,
    /// score(9K) ≈ 0.90.
    ///
    /// <para>Sanity reference points at the current weight schedule:
    /// a 4-chunk (~64-tile) cluster at full Maturity raw-scores 840;
    /// an 8-chunk cluster at full Maturity raw-scores 1680. With
    /// K=1000 those score 0.46 and 0.63 respectively. Tune K to taste
    /// once the consumers are wired up.</para></summary>
    public const float HalfSaturationRaw = 1000f;

    /// <summary>Default tile count used when the field is in
    /// "valid-flag only" mode (older fixtures that don't carry per-
    /// chunk sample counts). Set to the full chunk's tile capacity so
    /// pre-sample-count snapshots score identically to the old
    /// chunk-counts × 16 convention.</summary>
    private const int FallbackTilesPerChunk = RegionEcologyField.ChunkSize * RegionEcologyField.ChunkSize;

    #endregion

    #region Fields

    private readonly ChunkValueStore _store;
    private readonly IEcologyFieldQuery _fieldQuery;

    /// <summary>Lookup from chunk coord to its cluster's id. Rebuilt
    /// from scratch each cycle; missing entries mean "doesn't
    /// qualify."</summary>
    private Dictionary<ChunkCoord, ChunkClusterId> _chunkToCluster = new();

    /// <summary>Secondary index: per (globalChunkX, globalChunkY), the
    /// list of (region, cluster) pairs at that coord across all
    /// regions. Populated alongside <see cref="_chunkToCluster"/> at
    /// rebuild; lets consumers ask "which clusters sit at this chunk
    /// XY (across regions)?" without scanning the full chunk-to-cluster
    /// dict. The typical entry has 1 element; stacked terraces (cliff
    /// faces routing different Z bands to different regions) and the
    /// settled-vs-wild split at building edges produce 2-3.</summary>
    private Dictionary<(int Cx, int Cy), List<(RegionId Region, ChunkClusterId Cluster)>> _chunksByXY = new();

    /// <summary>Cluster table, indexed by <see cref="ChunkClusterId.Value"/>.</summary>
    private List<ClusterEntry> _clusters = new();

    /// <summary>Shadow buffers used by the incremental rebuild path
    /// (<see cref="BeginRebuild"/> / <see cref="IncludeRegionInRebuild"/> /
    /// <see cref="CommitRebuild"/>). Held in fields so the build state
    /// can span multiple frames between Begin and Commit. Always
    /// non-null and reused across cycles — <see cref="BeginRebuild"/>
    /// clears them, <see cref="CommitRebuild"/> swaps references with
    /// the live snapshot. Queries never read from these — the live
    /// snapshot in <see cref="_chunkToCluster"/> /
    /// <see cref="_clusters"/> is what consumers see until the swap.
    /// <para>The reuse-by-swap pattern saves three Dictionary/List
    /// allocations per cycle (avoiding GC pressure that historically
    /// produced cycle-boundary spikes on big maps).</para></summary>
    private Dictionary<ChunkCoord, ChunkClusterId> _shadowChunkToCluster = new();
    private List<ClusterEntry> _shadowClusters = new();
    private Dictionary<(int Cx, int Cy), List<(RegionId Region, ChunkClusterId Cluster)>> _shadowChunksByXY = new();

    /// <summary>True between <see cref="BeginRebuild"/> and
    /// <see cref="CommitRebuild"/>. Guards the incremental API's
    /// ordering — calling Include or Commit without a prior Begin, or
    /// Begin while one is already in progress, indicates a programmer
    /// error and throws.</summary>
    private bool _rebuildInProgress;

    /// <summary>Scratch arrays + dict reused across
    /// <see cref="ProcessRegion"/> calls within a single rebuild and
    /// across rebuilds. Grow only when <see cref="cellCount"/> exceeds
    /// current capacity. Cleared at the top of every
    /// <see cref="ProcessRegion"/> call so leftover state from the
    /// previous region doesn't contaminate the union-find.
    /// <para>Saves four array + one Dictionary allocation per
    /// region per cycle (roughly N_regions × N_cycles_per_day
    /// allocations eliminated).</para></summary>
    private BiomeKind?[] _scratchBiomes = Array.Empty<BiomeKind?>();
    private float[] _scratchMaturities = Array.Empty<float>();
    private int[] _scratchParent = Array.Empty<int>();
    private int[] _scratchRootSizes = Array.Empty<int>();
    private readonly Dictionary<int, int> _scratchRootToClusterIdx = new();

    /// <summary>Scratch HashSets reused across cycles for the
    /// chunkset-hash churn diff in <see cref="TallyClusterChurn"/>.
    /// Cleared on every call.</summary>
    private readonly HashSet<long> _scratchOldHashes = new();
    private readonly HashSet<long> _scratchNewHashes = new();

    /// <summary>Monotonic counter incremented by <see cref="Rebuild"/>
    /// (atomic path) and <see cref="CommitRebuild"/> (incremental path).
    /// Consumers that hold cluster ids across rebuilds compare this
    /// against their last-seen value to detect that ids have become
    /// invalid — even a stale id that happens to land in-range after a
    /// rebuild may now point at a different cluster (different biome,
    /// different geometry), so version-tripped consumers should drop
    /// or revalidate any cached id.</summary>
    public int Version { get; private set; }

    /// <summary>Cumulative count of <see cref="CommitRebuild"/>
    /// calls. Activity-panel readers diff against a previous snapshot
    /// to compute "rebuilds per game-hour" — sanity check that the
    /// cluster ticker is firing on schedule.</summary>
    public long RebuildsCompleted { get; private set; }

    /// <summary>Cumulative count of cluster compositions that
    /// appeared in a rebuild's output but weren't present in the
    /// previous snapshot. Identity is the exact set of chunks the
    /// cluster covers — a cluster that gains a single chunk between
    /// rebuilds counts as +1 created (and +1 destroyed for the prior
    /// composition).</summary>
    public long ClustersCreatedCumulative { get; private set; }

    /// <summary>Cumulative count of cluster compositions that
    /// existed in a rebuild's input but disappeared in the output.
    /// Pairs with <see cref="ClustersCreatedCumulative"/>; the sum
    /// of the two over a window approximates cluster-level churn.</summary>
    public long ClustersDestroyedCumulative { get; private set; }

    /// <summary>Per-rebuild outcome counters. Reset to zero at the
    /// start of each <see cref="BeginRebuild"/>; report the just-
    /// completed rebuild's tally after <see cref="CommitRebuild"/>.
    /// Surfaces in the activity panel so a wholesale "all clusters
    /// destroyed" event can be diagnosed: a rebuild where
    /// <see cref="LastRebuildRegionsSkippedNoField"/> or
    /// <see cref="LastRebuildRegionsSkippedFewValidChunks"/> jumped
    /// from zero to "every region" is the field-reallocation /
    /// surveyor-churn footprint.</summary>
    public int LastRebuildRegionsIncluded { get; private set; }

    /// <inheritdoc cref="LastRebuildRegionsIncluded" />
    public int LastRebuildRegionsSkippedNoField { get; private set; }

    /// <inheritdoc cref="LastRebuildRegionsIncluded" />
    public int LastRebuildRegionsSkippedFewValidChunks { get; private set; }

    #endregion

    public ChunkClusterIndex(ChunkValueStore store, IEcologyFieldQuery fieldQuery,
        Diagnostics.IPerfScope? perf = null) {
      _store = store;
      _fieldQuery = fieldQuery;
      _perf = perf;
    }

    /// <summary>Optional perf hook for the rebuild internals. When
    /// non-null, <see cref="ProcessRegion"/> and <see cref="CommitRebuild"/>
    /// open a Track scope around each significant stage so the perf
    /// window can split where the work lives. Null in tests; production
    /// passes the Mod-side PerfTracker via Bindito.</summary>
    private readonly Diagnostics.IPerfScope? _perf;

    private const string PerfBase = nameof(ChunkClusterIndex);
    private const string PerfProcessRegion = PerfBase + ".ProcessRegion";
    private const string PerfQualifyingScan = PerfProcessRegion + ".QualifyingScan";
    private const string PerfUnionFind = PerfProcessRegion + ".UnionFind";
    private const string PerfRootSizes = PerfProcessRegion + ".RootSizes";
    private const string PerfCollect = PerfProcessRegion + ".Collect";
    private const string PerfCommit = PerfBase + ".Commit";
    private const string PerfFinalise = PerfCommit + ".Finalise";
    private const string PerfChurn = PerfCommit + ".Churn";

    #region Public API

    /// <summary>Rebuild the entire index from the current snapshot of
    /// the supplied regions' fields and chunk values. Any prior
    /// <see cref="ChunkClusterId"/>s become invalid after this call —
    /// re-resolve via <see cref="ClusterFor"/> from a known
    /// <see cref="ChunkCoord"/>.
    /// <para><b>Filters applied during rebuild (see class docstring
    /// for the full list of non-qualifying conditions):</b> regions
    /// with fewer than two valid chunks are skipped entirely; chunks
    /// whose dominant biome is outside <see cref="ClusterableBiomes"/>
    /// or whose Maturity is below <paramref name="maturityThreshold"/>
    /// are treated as non-qualifying; connected components of only one
    /// chunk are dropped post-union-find.</para></summary>
    /// <param name="regions">Regions to consider. Order doesn't
    /// affect output. Regions with no published field
    /// (<c>FieldFor</c> returns null) are silently skipped, as are
    /// regions whose field has fewer than two valid chunks.</param>
    /// <param name="maturityThreshold">Minimum Maturity (game-days)
    /// the chunk's dominant biome must have accrued for the chunk to
    /// count. Below this, the chunk is treated as "biome not
    /// established here" and is absent from the index.</param>
    public void Rebuild(IEnumerable<RegionId> regions, float maturityThreshold) {
      BeginRebuild();
      foreach (var regionId in regions) {
        IncludeRegionInRebuild(regionId, maturityThreshold);
      }
      CommitRebuild();
    }

    /// <summary>Begin an incremental rebuild. Allocates shadow buffers
    /// that subsequent <see cref="IncludeRegionInRebuild"/> calls fold
    /// regions into. The live snapshot remains visible to queries until
    /// <see cref="CommitRebuild"/> swaps the shadow in.
    ///
    /// <para><b>Why incremental.</b> The atomic <see cref="Rebuild"/>
    /// runs every region's work in one call — convenient for tests and
    /// startup warmup, but on a large map at steady state that's a
    /// single-frame spike. The incremental path lets a driver
    /// (typically a <c>RollingSweepTicker</c>-based scheduler) spread
    /// the per-region work across a cycle's many frames while readers
    /// keep seeing the previous-cycle snapshot consistently.</para>
    ///
    /// <para><b>API ordering.</b> Begin → Include* → Commit. Calling
    /// Begin while a rebuild is already in progress, or Include /
    /// Commit without a prior Begin, throws — those indicate driver
    /// bugs, not runtime conditions.</para></summary>
    public void BeginRebuild() {
      if (_rebuildInProgress) {
        throw new InvalidOperationException(
            "ChunkClusterIndex.BeginRebuild called while a rebuild was already in progress.");
      }
      // Reuse the shadow containers across rebuilds. After last
      // cycle's CommitRebuild, _shadowChunkToCluster/etc. hold the
      // previous-previous live snapshot (swap path); clearing them
      // here resets to "empty shadow ready for the new fold."
      _shadowChunkToCluster.Clear();
      _shadowClusters.Clear();
      _shadowChunksByXY.Clear();
      // Reset per-rebuild outcome counters at the start of every
      // rebuild so the LastRebuild* readouts reflect only the cycle
      // that just completed at the next CommitRebuild.
      _pendingRegionsIncluded = 0;
      _pendingRegionsSkippedNoField = 0;
      _pendingRegionsSkippedFewValidChunks = 0;
      _rebuildInProgress = true;
    }

    private int _pendingRegionsIncluded;
    private int _pendingRegionsSkippedNoField;
    private int _pendingRegionsSkippedFewValidChunks;

    /// <summary>Fold one region into the in-progress shadow rebuild.
    /// Same per-region work and filters as <see cref="Rebuild"/>:
    /// regions with no published field or fewer than two valid chunks
    /// are silently skipped.
    ///
    /// <para>Must be called between <see cref="BeginRebuild"/> and
    /// <see cref="CommitRebuild"/>.</para></summary>
    public void IncludeRegionInRebuild(RegionId regionId, float maturityThreshold) {
      if (!_rebuildInProgress) {
        throw new InvalidOperationException(
            "ChunkClusterIndex.IncludeRegionInRebuild called outside of an active rebuild.");
      }
      var field = _fieldQuery.FieldFor(regionId);
      if (field == null) {
        _pendingRegionsSkippedNoField++;
        return;
      }
      // Region-level early-out: a region with fewer than two valid
      // chunks can never produce a multi-chunk cluster, which is the
      // only kind we emit. Skipping here avoids the per-region array
      // allocations and lookup loop on isolated stack-areas (terrain
      // quirks where a region's field has only one usable chunk).
      if (!HasAtLeastTwoValidChunks(field)) {
        _pendingRegionsSkippedFewValidChunks++;
        return;
      }
      _pendingRegionsIncluded++;
      ProcessRegion(regionId, field, maturityThreshold,
          _shadowChunkToCluster, _shadowChunksByXY, _shadowClusters);
    }

    /// <summary>Finalise the shadow rebuild and swap it in as the new
    /// live snapshot. After this call, queries reflect the freshly-
    /// built index and <see cref="Version"/> has been bumped to signal
    /// id invalidation to consumers.
    ///
    /// <para>Must be called after <see cref="BeginRebuild"/> and any
    /// number of <see cref="IncludeRegionInRebuild"/> calls (including
    /// zero — Begin + Commit with no regions yields an empty index,
    /// matching <c>Rebuild(empty, _)</c>).</para></summary>
    public void CommitRebuild() {
      if (!_rebuildInProgress) {
        throw new InvalidOperationException(
            "ChunkClusterIndex.CommitRebuild called outside of an active rebuild.");
      }
      // Finalise every cluster exactly once over the freshly-populated
      // shadow list. Score-aggregate computation is idempotent, so the
      // order regions were folded in doesn't matter — the score is a
      // pure function of the final TileCountsAbove state.
      var shadowClusters = _shadowClusters;
      using (_perf?.Track(PerfFinalise)) {
        for (var ci = 0; ci < shadowClusters.Count; ci++) {
          shadowClusters[ci].FinaliseScore();
        }
      }
      // Diagnostic counters: cluster-level churn between snapshots.
      // Identity = exact chunkset; a cluster that gains/loses one
      // chunk counts as +1 destroyed and +1 created. Cheap (single
      // hash per cluster), runs once per rebuild.
      using (_perf?.Track(PerfChurn)) {
        TallyClusterChurn(_clusters, shadowClusters);
      }
      // Swap shadow ↔ live. The previous live containers become the
      // new shadow (cleared at next BeginRebuild) so we never allocate
      // fresh Dictionary/List instances for the swap.
      (_chunkToCluster, _shadowChunkToCluster) = (_shadowChunkToCluster, _chunkToCluster);
      (_chunksByXY, _shadowChunksByXY) = (_shadowChunksByXY, _chunksByXY);
      (_clusters, _shadowClusters) = (_shadowClusters, _clusters);
      _rebuildInProgress = false;
      Version++;
      RebuildsCompleted++;
      LastRebuildRegionsIncluded = _pendingRegionsIncluded;
      LastRebuildRegionsSkippedNoField = _pendingRegionsSkippedNoField;
      LastRebuildRegionsSkippedFewValidChunks = _pendingRegionsSkippedFewValidChunks;
    }

    private void TallyClusterChurn(
        List<ClusterEntry> oldClusters, List<ClusterEntry> newClusters) {
      // Reuse instance HashSets across cycles. Cleared at the top
      // every call so leftover hashes from previous churn don't
      // skew the cumulative counters.
      var oldHashes = _scratchOldHashes;
      var newHashes = _scratchNewHashes;
      oldHashes.Clear();
      newHashes.Clear();
      for (var i = 0; i < oldClusters.Count; i++) {
        oldHashes.Add(ChunksetHash(oldClusters[i].Chunks));
      }
      for (var i = 0; i < newClusters.Count; i++) {
        newHashes.Add(ChunksetHash(newClusters[i].Chunks));
      }
      foreach (var h in newHashes) {
        if (!oldHashes.Contains(h)) ClustersCreatedCumulative++;
      }
      foreach (var h in oldHashes) {
        if (!newHashes.Contains(h)) ClustersDestroyedCumulative++;
      }
    }

    /// <summary>Order-independent 64-bit hash of a cluster's chunk
    /// set. Sums per-chunk hashes so the result is invariant under
    /// permutation (which it must be — the chunks list isn't kept in
    /// any particular order across rebuilds, but the SET of chunks is
    /// what defines cluster identity for the churn counters).
    ///
    /// <para><b>Additive, not XOR, combine.</b> XOR is its own inverse,
    /// so two member chunks hashing to the same per-chunk value would
    /// cancel and the cluster would hash as if neither were present;
    /// summing can't self-cancel. Collisions are still possible (any
    /// 64-bit hash can collide), but the only consequence is a
    /// miscounted <see cref="ClustersCreatedCumulative"/> /
    /// <see cref="ClustersDestroyedCumulative"/> — diagnostic counters,
    /// not cluster geometry or scores — so a rare collision is benign.</para></summary>
    private static long ChunksetHash(IReadOnlyList<ChunkCoord> chunks) {
      long h = 0;
      for (var i = 0; i < chunks.Count; i++) {
        var c = chunks[i];
        unchecked {
          // FNV-1a-flavoured per-chunk mix, then SUM for order-independent
          // combination that (unlike XOR) can't self-cancel on a collision.
          long ch = 1469598103934665603L;
          ch = (ch ^ (long)c.RegionId.Value) * 1099511628211L;
          ch = (ch ^ c.GlobalChunkX) * 1099511628211L;
          ch = (ch ^ c.GlobalChunkY) * 1099511628211L;
          h += ch;
        }
      }
      return h;
    }

    /// <summary>Cluster that owns the given chunk, or <c>null</c> if
    /// the chunk doesn't qualify (Maturity below threshold, empty
    /// chunk, or not seen at the last rebuild).</summary>
    public ChunkClusterId? ClusterFor(RegionId region, int chunkX, int chunkY) {
      return _chunkToCluster.TryGetValue(new ChunkCoord(region, chunkX, chunkY), out var id)
          ? id : (ChunkClusterId?)null;
    }

    /// <summary>All clusters at the given global chunk coordinate,
    /// across regions. Typically returns one entry; returns multiple
    /// when stacked terraces, cliff faces, or settled-vs-wild splits
    /// route different surface bands at the same XY into distinct
    /// regions. Returns an empty list when no qualifying cluster
    /// exists at that XY in any region.
    ///
    /// <para><b>Cardinality, by case.</b>
    /// <list type="bullet">
    ///   <item>Flat terrain, unsettled: 1 — the wild region's chunk.</item>
    ///   <item>Flat terrain, town building straddles the chunk: 2 —
    ///         the wild region's chunk (covering unsettled columns)
    ///         and the settled region's chunk (covering settled
    ///         columns).</item>
    ///   <item>Cliff face within the chunk: 2 or more — one per
    ///         distinct Z layer the cliff routes into separate
    ///         regions.</item>
    /// </list></para>
    ///
    /// <para><b>Why this exists.</b> Consumers walking the chunk space
    /// "from above" (e.g. a building looking down for nearby ecology)
    /// need to know every qualifying cluster at a given XY without
    /// pre-knowing which regions cover that XY. Surveyor walks per
    /// column are wasteful and miss the case where a cluster's region
    /// covers the XY through columns the caller didn't sample. This
    /// API is the chunk-level analogue of <c>RegionService.Containing</c>
    /// but for the cluster layer, indexed for O(1) lookup.</para>
    ///
    /// <para>Returned ids are valid until the next <see cref="CommitRebuild"/>
    /// (same lifecycle as <see cref="ClusterFor"/>'s output). The list
    /// is owned by the index — do not mutate.</para></summary>
    public IReadOnlyList<(RegionId Region, ChunkClusterId Cluster)> ChunksAtChunkXY(
        int globalChunkX, int globalChunkY) {
      return _chunksByXY.TryGetValue((globalChunkX, globalChunkY), out var list)
          ? list
          : (IReadOnlyList<(RegionId, ChunkClusterId)>)Array.Empty<(RegionId, ChunkClusterId)>();
    }

    /// <summary>Member chunks of a cluster. Empty for invalid ids.</summary>
    public IReadOnlyList<ChunkCoord> ChunksIn(ChunkClusterId id) {
      return IsValid(id) ? _clusters[id.Value].Chunks : Array.Empty<ChunkCoord>();
    }

    /// <summary>Total tile area covered by the cluster's chunks — sum
    /// of per-chunk <c>ChunkSampleCount</c>s (number of in-region
    /// surfaces that contributed to each chunk's averages). For
    /// flat natural-resource terrain this is ~16 tiles per fully
    /// filled chunk; partially-occupied chunks contribute less.
    /// Consumers that care about a per-tile predicate (water depth
    /// ≥ X) must walk <see cref="ChunksIn"/> and apply it themselves.</summary>
    public int TileCount(ChunkClusterId id) {
      return IsValid(id) ? _clusters[id.Value].TileCount : 0;
    }

    /// <summary>Number of chunks in the cluster. Convenience for
    /// consumers that think in chunks rather than tiles — equivalent
    /// to <see cref="ChunksIn"/>.Count, no allocation.</summary>
    public int ChunkCount(ChunkClusterId id) {
      return IsValid(id) ? _clusters[id.Value].Chunks.Count : 0;
    }

    /// <summary>Dominant biome shared by every chunk in the cluster.
    /// <c>null</c> for invalid ids.</summary>
    public BiomeKind? BiomeFor(ChunkClusterId id) {
      return IsValid(id) ? _clusters[id.Value].Biome : (BiomeKind?)null;
    }

    /// <summary>Mean of the cluster-biome's Maturity across all member
    /// chunks. Returns 0 for invalid ids; never less than the cluster
    /// qualification threshold for valid clusters, since every member
    /// chunk's Maturity is ≥ that threshold by construction.</summary>
    public float AverageMaturity(ChunkClusterId id) {
      if (!IsValid(id)) return 0f;
      var entry = _clusters[id.Value];
      return entry.Chunks.Count == 0 ? 0f : entry.MaturitySum / entry.Chunks.Count;
    }

    /// <summary>Highest cluster-biome Maturity across the cluster's
    /// member chunks. Returns 0 for invalid ids.</summary>
    public float MaxMaturity(ChunkClusterId id) {
      return IsValid(id) ? _clusters[id.Value].MaxMaturity : 0f;
    }

    /// <summary>Per-threshold tile counts: element <c>i</c> is the
    /// sum of <c>ChunkSampleCount</c> across member chunks whose
    /// cluster-biome Maturity is <c>≥ <see cref="Thresholds"/>[i]</c>.
    /// Returns an empty array for invalid ids. Counts are
    /// non-increasing across the array (every tile above 10.0 is also
    /// above 5.0). This is the load-bearing aggregate that feeds
    /// <see cref="RawScore"/> — each tile is multi-counted across the
    /// buckets it clears, so more-mature tiles contribute more.</summary>
    public IReadOnlyList<int> TileCountsAbove(ChunkClusterId id) {
      return IsValid(id)
          ? _clusters[id.Value].TileCountsAbove
          : Array.Empty<int>();
    }

    /// <summary>Per-tier tile counts (NOT cumulative). Element
    /// <c>i</c> is the number of tiles whose Maturity sits in the
    /// half-open band <c>[Thresholds[i], Thresholds[i+1])</c>; the
    /// last element covers <c>[Thresholds[last], ∞)</c>. Useful for
    /// debug overlays that want to show distribution-by-tier rather
    /// than cumulative. Returns an empty array for invalid ids.
    ///
    /// <para>Derived from <see cref="TileCountsAbove"/> by adjacent
    /// differences, so the two views always agree.</para></summary>
    public IReadOnlyList<int> TilesInTier(ChunkClusterId id) {
      return IsValid(id)
          ? _clusters[id.Value].TilesInTier
          : Array.Empty<int>();
    }

    /// <summary>Threshold-weighted sum of tile counts across the
    /// cluster: <c>Σ_t BucketWeights[t] · TileCountsAbove[t]</c>.
    /// Unitless; grows linearly with cluster size at constant
    /// Maturity, non-linearly with Maturity at constant size (high-
    /// Maturity tiles clear more buckets and so contribute multiple
    /// weights). The single number captures "quality × quantity"
    /// without yet applying the per-cluster saturation; consumers
    /// that aggregate across multiple clusters (e.g. stacked-Z biomes
    /// touching one building) should sum raws first and then call
    /// the saturation function. Returns 0 for invalid ids.</summary>
    public float RawScore(ChunkClusterId id) {
      return IsValid(id) ? _clusters[id.Value].RawScore : 0f;
    }

    /// <summary>Per-cluster ecological score in <c>[0, 1)</c>.
    /// Computed from <see cref="RawScore"/> via the hyperbolic
    /// saturation <c>raw / (raw + <see cref="HalfSaturationRaw"/>)</c>
    /// — asymptotic to 1, never reaches it. Single knob, smooth
    /// derivative, no cliff at "saturation." Consumers can multiply
    /// this by their own per-context cap (Nature: PointsPerHour;
    /// fauna: CapacityAtSaturation) to translate "ecological richness"
    /// into a domain-specific number.
    ///
    /// <para><b>When NOT to read Score directly.</b> If a consumer
    /// touches multiple clusters and wants them to compose as one
    /// logical biome (e.g. stacked-Z forests on a terraced slope), it
    /// must aggregate <see cref="RawScore"/> across those clusters
    /// and apply the saturation function once on the sum — summing
    /// per-cluster Scores would not compose correctly (two clusters
    /// at score 0.6 do not equal a single big cluster at score 1.2).
    /// Use <see cref="SaturatedScore"/> for the canonical
    /// raw→[0,1) transform.</para>
    ///
    /// <para>Returns 0 for invalid ids.</para></summary>
    public float Score(ChunkClusterId id) {
      return IsValid(id) ? _clusters[id.Value].Score : 0f;
    }

    /// <summary>The hyperbolic saturation function used to convert a
    /// raw score into a normalised <c>[0, 1)</c> value. Exposed so
    /// consumers that aggregate raw scores across multiple clusters
    /// can apply the canonical transform without re-deriving the
    /// formula. <c>SaturatedScore(0) = 0</c>; <c>SaturatedScore(K) =
    /// 0.5</c>; <c>SaturatedScore(∞) → 1</c>.</summary>
    public static float SaturatedScore(float rawScore) {
      if (rawScore <= 0f) return 0f;
      return rawScore / (rawScore + HalfSaturationRaw);
    }

    /// <summary>Number of distinct clusters in the current
    /// snapshot.</summary>
    public int ClusterCount => _clusters.Count;

    #endregion

    #region Per-region rebuild

    /// <summary>Region-level early-out check used by <see cref="Rebuild"/>:
    /// returns true as soon as a second valid chunk is found. Worst case
    /// O(chunks) when the field is entirely invalid; common case O(1)
    /// for any populated region. Cheaper than entering
    /// <see cref="ProcessRegion"/> and bailing partway through after
    /// the per-region arrays are already allocated.</summary>
    private static bool HasAtLeastTwoValidChunks(RegionEcologyField field) {
      var found = 0;
      for (var cy = 0; cy < field.ChunksY; cy++) {
        for (var cx = 0; cx < field.ChunksX; cx++) {
          if (!field.ChunkValid(cx, cy)) continue;
          found++;
          if (found >= 2) return true;
        }
      }
      return false;
    }

    private void ProcessRegion(
        RegionId regionId,
        RegionEcologyField field,
        float maturityThreshold,
        Dictionary<ChunkCoord, ChunkClusterId> chunkToCluster,
        Dictionary<(int Cx, int Cy), List<(RegionId Region, ChunkClusterId Cluster)>> chunksByXY,
        List<ClusterEntry> clusters) {

      var chunksX = field.ChunksX;
      var chunksY = field.ChunksY;
      var cellCount = chunksX * chunksY;
      var globalChunkX0 = field.OriginX / RegionEcologyField.ChunkSize;
      var globalChunkY0 = field.OriginY / RegionEcologyField.ChunkSize;

      // Reuse instance scratch arrays across regions, growing on demand.
      // Clear the used portion every call so leftover state from the
      // previous region's smaller cellCount can't contaminate this one.
      if (_scratchBiomes.Length < cellCount) {
        _scratchBiomes = new BiomeKind?[cellCount];
        _scratchMaturities = new float[cellCount];
        _scratchParent = new int[cellCount];
        _scratchRootSizes = new int[cellCount];
      } else {
        System.Array.Clear(_scratchBiomes, 0, cellCount);
        System.Array.Clear(_scratchMaturities, 0, cellCount);
        System.Array.Clear(_scratchRootSizes, 0, cellCount);
        // _scratchParent gets overwritten by the identity-init loop
        // below, no clear needed.
      }
      var biomes = _scratchBiomes;
      var maturities = _scratchMaturities;
      var parent = _scratchParent;
      var rootSizes = _scratchRootSizes;

      // biomes[i] = qualifying biome for local chunk i, or null if
      // chunk doesn't qualify (invalid, no dominant biome, or
      // Maturity below threshold). maturities[i] = the chunk's
      // dominant-biome Maturity (parallel to biomes[]; meaningful only
      // when biomes[i] != null). Cached together so the collection
      // pass can aggregate without re-reading from the store.
      using (_perf?.Track(PerfQualifyingScan)) {
        for (var cy = 0; cy < chunksY; cy++) {
          for (var cx = 0; cx < chunksX; cx++) {
            if (!field.ChunkValid(cx, cy)) continue;
            var gcx = globalChunkX0 + cx;
            var gcy = globalChunkY0 + cy;
            var i = cy * chunksX + cx;
            var (biome, maturity) = ComputeQualifyingBiomeAndMaturity(
                regionId, gcx, gcy, maturityThreshold);
            biomes[i] = biome;
            maturities[i] = maturity;
          }
        }
      }

      // Union-find over local chunk indices.
      using (_perf?.Track(PerfUnionFind)) {
        for (var i = 0; i < cellCount; i++) parent[i] = i;

        // For each qualifying chunk, union with same-biome right and
        // down neighbours. Down/right only because every pair is
        // visited from one of its endpoints — saves half the work
        // versus 4-direction.
        for (var cy = 0; cy < chunksY; cy++) {
          for (var cx = 0; cx < chunksX; cx++) {
            var i = cy * chunksX + cx;
            var b = biomes[i];
            if (b == null) continue;
            if (cx + 1 < chunksX) {
              var iRight = cy * chunksX + (cx + 1);
              if (biomes[iRight] == b) Union(parent, i, iRight);
            }
            if (cy + 1 < chunksY) {
              var iDown = (cy + 1) * chunksX + cx;
              if (biomes[iDown] == b) Union(parent, i, iDown);
            }
          }
        }
      }

      // Tally per-root chunk counts so the collect pass can drop
      // single-chunk clusters. We materialise the cluster id, the
      // ClusterEntry, and the chunkToCluster mapping only for roots
      // with size ≥ 2 — singletons carry no signal beyond what a
      // single-chunk dominance query already supplies, so they don't
      // earn their allocation. rootSizes is the scratch array
      // initialised + cleared above; reuse it directly.
      using (_perf?.Track(PerfRootSizes)) {
        for (var cy = 0; cy < chunksY; cy++) {
          for (var cx = 0; cx < chunksX; cx++) {
            var i = cy * chunksX + cx;
            if (biomes[i] == null) continue;
            rootSizes[Find(parent, i)]++;
          }
        }
      }

      // Collect: walk chunks, materialise clusters by union-find root.
      // rootToClusterIdx is per-region scratch held on the instance;
      // clear before reuse so previous regions' root indices don't
      // leak in. Same pattern as the array scratch above.
      var rootToClusterIdx = _scratchRootToClusterIdx;
      rootToClusterIdx.Clear();
      using var _collectScope = _perf?.Track(PerfCollect);
      for (var cy = 0; cy < chunksY; cy++) {
        for (var cx = 0; cx < chunksX; cx++) {
          var i = cy * chunksX + cx;
          var b = biomes[i];
          if (b == null) continue;

          var root = Find(parent, i);
          if (rootSizes[root] < 2) continue;  // skip singleton clusters
          if (!rootToClusterIdx.TryGetValue(root, out var clusterIdx)) {
            clusterIdx = clusters.Count;
            rootToClusterIdx[root] = clusterIdx;
            clusters.Add(new ClusterEntry(b.Value));
          }
          var entry = clusters[clusterIdx];
          var gcx = globalChunkX0 + cx;
          var gcy = globalChunkY0 + cy;
          var coord = new ChunkCoord(regionId, gcx, gcy);
          entry.Chunks.Add(coord);
          var clusterId = new ChunkClusterId(clusterIdx);
          chunkToCluster[coord] = clusterId;
          // Secondary index: append to the per-(chunkX, chunkY) list.
          // Multiple regions can land in the same (chunkX, chunkY) when
          // stacked terraces or settled/wild splits exist at a given XY.
          var xyKey = (gcx, gcy);
          if (!chunksByXY.TryGetValue(xyKey, out var list)) {
            list = new List<(RegionId, ChunkClusterId)>(capacity: 1);
            chunksByXY[xyKey] = list;
          }
          list.Add((regionId, clusterId));

          // Per-chunk tile count drives the cluster's TileCount and
          // the tile-weighted threshold buckets. Defensive fallback
          // to a full chunk's worth of tiles for chunks written
          // through fixtures that don't carry sample counts.
          var sampleCount = field.ChunkSampleCount(cx, cy);
          if (sampleCount == 0) sampleCount = FallbackTilesPerChunk;
          entry.TileCount += sampleCount;

          // Per-chunk Maturity aggregates. AverageMaturity is over
          // chunks (one value per chunk), not over tiles — average
          // maturity of "where the biome lives." TileCountsAbove is
          // weighted by sampleCount so the score formula naturally
          // accounts for partial chunks.
          var m = maturities[i];
          entry.MaturitySum += m;
          if (m > entry.MaxMaturity) entry.MaxMaturity = m;
          for (var t = 0; t < Thresholds.Count; t++) {
            if (m >= Thresholds[t]) entry.TileCountsAbove[t] += sampleCount;
          }
        }
      }

      // Score-aggregate finalisation (RawScore / Score / TilesInTier)
      // is deferred to a single pass at the end of Rebuild over the
      // full clusters list — see the comment in Rebuild for why.
      // Doing it per region would re-finalise every previously-added
      // cluster on each ProcessRegion call (O(R²·K)).
    }

    /// <summary>Chunk-level qualifier returning the dominant biome AND
    /// its Maturity if the chunk qualifies for cluster membership, or
    /// <c>(null, 0)</c> otherwise. Combined so callers can cache both
    /// values from one store traversal.
    ///
    /// <para><b>Dominance is over ALL biomes, then post-filtered to
    /// the clusterable whitelist.</b> Earlier versions of this method
    /// passed <see cref="ClusterableBiomes"/> as the candidate list to
    /// <see cref="ChunkBiomeSampler.DominantByMaturityAtChunk"/> -- the
    /// equivalent of asking "of the biomes I'm willing to cluster,
    /// which has the highest Maturity here?" That coupled the two
    /// orthogonal questions ("what is this chunk?" and "should I
    /// cluster it?") and produced visibly wrong overlays: a chunk
    /// whose true Maturity-argmax was Monoculture got reported as
    /// Grassland (or whatever clusterable biome had the next-highest
    /// residual Maturity) and union-found into adjacent actually-
    /// Grassland chunks, so a "Grassland cluster" physically spanned
    /// Monoculture and Dry chunks. The class docstring warns that
    /// <see cref="ClusterableBiomes"/> and
    /// <see cref="ChunkBiomeSampler.BiomesByAggressorTier"/> "must
    /// evolve independently"; the old call coupled them in exactly
    /// the way the docstring forbids.</para>
    ///
    /// <para><b>Why Maturity-dominance and not Suitability-dominance.</b>
    /// Suitability is a per-tick volatile signal -- a brief weather /
    /// water-sim transient can flip a chunk's Suitability-dominant
    /// biome and (under a Suitability-keyed contract) drop the chunk
    /// from the cluster index. Fauna on the chunk then see
    /// ClusterUnknown on their hourly self-check and despawn, even
    /// though the chunk has been the same Wetland for days by
    /// Maturity. Maturity is integrated on day-scales; picking the
    /// argmax over Maturity gives the cluster identity the stability
    /// fauna affinity needs.</para></summary>
    private (BiomeKind? Biome, float Maturity) ComputeQualifyingBiomeAndMaturity(
        RegionId region, int chunkX, int chunkY, float threshold) {
      var (dominant, maturity) = ChunkBiomeSampler.DominantByMaturityAtChunk(
          new ChunkValueStoreReader(_store), region, chunkX, chunkY, ChunkBiomeSampler.BiomesByAggressorTier);
      if (dominant == null) return (null, 0f);
      // Post-filter: if the true dominant biome isn't clusterable
      // (Monoculture, Dry, Cave, Contaminated, Badwater), the chunk
      // doesn't qualify -- regardless of how much residual Maturity
      // any clusterable biome may still hold on it. The chunk
      // becomes a union-find boundary, so adjacent clusterable
      // chunks on either side land in separate clusters as they
      // should.
      if (!IsClusterable(dominant.Value)) return (null, 0f);
      return maturity >= threshold ? (dominant, maturity) : (null, 0f);
    }

    private static bool IsClusterable(BiomeKind biome) {
      for (var i = 0; i < ClusterableBiomes.Count; i++) {
        if (ClusterableBiomes[i] == biome) return true;
      }
      return false;
    }

    #endregion

    #region Union-find

    private static int Find(int[] parent, int x) {
      while (parent[x] != x) {
        parent[x] = parent[parent[x]];  // path compression (halving)
        x = parent[x];
      }
      return x;
    }

    private static void Union(int[] parent, int a, int b) {
      var ra = Find(parent, a);
      var rb = Find(parent, b);
      if (ra != rb) parent[ra] = rb;
    }

    #endregion

    private bool IsValid(ChunkClusterId id) =>
        id.Value >= 0 && id.Value < _clusters.Count;

    /// <summary>Mutable per-cluster aggregate. Chunk-derived counters
    /// are incremented in lockstep during the collection pass —
    /// <see cref="Chunks"/> is appended, <see cref="TileCount"/> grows
    /// by the chunk's <c>ChunkSampleCount</c>, <see cref="MaturitySum"/>
    /// by the chunk's Maturity (chunk-weighted, not tile-weighted —
    /// average is over chunks), <see cref="MaxMaturity"/> tracks the
    /// running maximum, and <see cref="TileCountsAbove"/>[t] grows by
    /// <c>ChunkSampleCount</c> for every threshold the chunk clears.
    /// Score-derived fields (<see cref="RawScore"/>, <see cref="Score"/>,
    /// <see cref="TilesInTier"/>) are filled by <see cref="FinaliseScore"/>
    /// after the collection loop completes.</summary>
    private sealed class ClusterEntry {
      public BiomeKind Biome { get; }
      public List<ChunkCoord> Chunks { get; } = new();
      public int TileCount { get; set; }
      public float MaturitySum { get; set; }
      public float MaxMaturity { get; set; }
      public int[] TileCountsAbove { get; }
      public int[] TilesInTier { get; }
      public float RawScore { get; private set; }
      public float Score { get; private set; }
      public ClusterEntry(BiomeKind biome) {
        Biome = biome;
        TileCountsAbove = new int[Thresholds.Count];
        TilesInTier = new int[Thresholds.Count];
      }

      /// <summary>Compute <see cref="RawScore"/>, <see cref="Score"/>,
      /// and <see cref="TilesInTier"/> from the post-collection
      /// <see cref="TileCountsAbove"/> array. Called once per cluster
      /// after the chunk-walk completes; idempotent if called again.</summary>
      public void FinaliseScore() {
        var raw = 0f;
        for (var t = 0; t < TileCountsAbove.Length; t++) {
          raw += BucketWeights[t] * TileCountsAbove[t];
        }
        RawScore = raw;
        Score = SaturatedScore(raw);

        // Derive per-tier counts from adjacent differences of the
        // cumulative array. Last tier captures the open-ended ≥highest-
        // threshold range so the histogram sums to the total tile
        // count among tiles that clear the lowest bucket.
        for (var t = 0; t < TileCountsAbove.Length - 1; t++) {
          TilesInTier[t] = TileCountsAbove[t] - TileCountsAbove[t + 1];
        }
        TilesInTier[TileCountsAbove.Length - 1] = TileCountsAbove[TileCountsAbove.Length - 1];
      }
    }

  }

}
