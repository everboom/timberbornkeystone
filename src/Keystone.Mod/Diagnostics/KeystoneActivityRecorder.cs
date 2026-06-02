using System.Collections.Generic;
using System.Text;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Time;
using Keystone.Mod.Biomes;
using Keystone.Mod.Ecology;
using Keystone.Mod.Fauna;
using Keystone.Mod.Flourish;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Pull-model activity snapshot for the diagnostic panel. Holds
  /// references to every subsystem that exposes a cumulative event
  /// counter (rolling-sweep tickers, RegionService, fauna registry)
  /// plus the live-state queryables (region count, cluster count,
  /// fauna count). Computes a <see cref="Snapshot"/> on demand —
  /// effectively free until the panel decides to call it.
  ///
  /// <para><b>Pull, not push.</b> The recorder does not subscribe to
  /// events. The originating subsystems each maintain a cheap
  /// monotonic counter (single <c>++</c> per event); the recorder
  /// reads those counters when asked. When the panel is closed and
  /// nobody calls <see cref="TakeSnapshot"/>, this class costs
  /// nothing — DI-injected references aside.</para>
  ///
  /// <para><b>Day-delta accounting belongs to the panel.</b> This
  /// class returns instantaneous cumulative counts; the panel diffs
  /// successive snapshots at game-day boundaries to compute "X today"
  /// rollups. Keeps this class stateless beyond DI; the buffer of
  /// daily rows lives wherever the panel decides.</para>
  /// </summary>
  public sealed class KeystoneActivityRecorder {

    private readonly IClock _clock;
    private readonly EcologyFieldUpdater _ecologyField;
    private readonly ChunkBiomeTicker _biomeTicker;
    private readonly ChunkClusterTicker _clusterTicker;
    private readonly FaunaCycleTicker _faunaTicker;
    private readonly KeystoneFlourishDecayTicker _flourishDecayTicker;
    private readonly RegionService _regions;
    private readonly ChunkClusterIndex _clusterIndex;
    private readonly KeystoneFaunaRegistry _faunaRegistry;
    private readonly ChunkValueStore _chunkValues;

    /// <summary>Maturity thresholds the per-biome breakdown reports
    /// chunk counts above. These align with the biome ladders' level
    /// boundaries — Grassland L1=0.5, L2=2.5; Wetland L3=5, L4=15;
    /// Forest L1=2, L2=12 — so the columns directly answer
    /// "how many chunks of biome X are past level Y."</summary>
    public static readonly IReadOnlyList<float> MaturityThresholds =
        new[] { 1f, 2.5f, 5f, 10f, 20f };

    public KeystoneActivityRecorder(
        IClock clock,
        EcologyFieldUpdater ecologyField,
        ChunkBiomeTicker biomeTicker,
        ChunkClusterTicker clusterTicker,
        FaunaCycleTicker faunaTicker,
        KeystoneFlourishDecayTicker flourishDecayTicker,
        RegionService regions,
        ChunkClusterIndex clusterIndex,
        KeystoneFaunaRegistry faunaRegistry,
        ChunkValueStore chunkValues) {
      _clock = clock;
      _ecologyField = ecologyField;
      _biomeTicker = biomeTicker;
      _clusterTicker = clusterTicker;
      _faunaTicker = faunaTicker;
      _flourishDecayTicker = flourishDecayTicker;
      _regions = regions;
      _clusterIndex = clusterIndex;
      _faunaRegistry = faunaRegistry;
      _chunkValues = chunkValues;
    }

    /// <summary>Read every counter and live-state value into a single
    /// <see cref="ActivitySnapshot"/>. Cheap — six counter reads + four
    /// <c>.Count</c> property reads.</summary>
    public ActivitySnapshot TakeSnapshot() {
      return new ActivitySnapshot(
          TotalDaysElapsed: _clock.TotalDaysElapsed,
          EcologyFieldCycles: _ecologyField.CyclesCompleted,
          BiomeTickerCycles: _biomeTicker.CyclesCompleted,
          ClusterTickerCycles: _clusterTicker.CyclesCompleted,
          FaunaCycleTickerCycles: _faunaTicker.CyclesCompleted,
          FlourishDecayCycles: _flourishDecayTicker.CyclesCompleted,
          RegionsCreated: _regions.RegionsCreatedCount,
          RegionSplits: _regions.RegionSplitCount,
          RegionMerges: _regions.RegionMergedCount,
          RegionsRemoved: _regions.RegionRemovedCount,
          FaunaAdded: _faunaRegistry.AddedCount,
          FaunaRemoved: _faunaRegistry.RemovedCount,
          LiveRegions: _regions.Count,
          LiveClusters: _clusterIndex.ClusterCount,
          LiveFauna: _faunaRegistry.Count,
          ClusterRebuilds: _clusterIndex.RebuildsCompleted,
          ClustersCreated: _clusterIndex.ClustersCreatedCumulative,
          ClustersDestroyed: _clusterIndex.ClustersDestroyedCumulative,
          LastRebuildRegionsIncluded: _clusterIndex.LastRebuildRegionsIncluded,
          LastRebuildRegionsSkippedNoField: _clusterIndex.LastRebuildRegionsSkippedNoField,
          LastRebuildRegionsSkippedFewValidChunks: _clusterIndex.LastRebuildRegionsSkippedFewValidChunks,
          FieldShapeVersion: _ecologyField.FieldShapeVersion);
    }

    /// <summary>Per-biome chunk-maturity breakdown: for each biome
    /// that has at least one chunk with a Maturity entry, bin
    /// chunks into <b>exclusive ranges</b> between adjacent
    /// thresholds. <see cref="BiomeMaturityRow.CountsInBin"/>[i]
    /// reports the count for the half-open interval
    /// <c>(MaturityThresholds[i], MaturityThresholds[i+1]]</c>; the
    /// last bin (<c>i = N-1</c>) is the open right side, counting
    /// <c>maturity &gt; MaturityThresholds[N-1]</c>.
    ///
    /// <para><b>Why exclusive bins.</b> Cumulative "above T" counts
    /// double-report — a chunk at maturity 30 would contribute to
    /// every column from "above 1" through "above 20", so the table
    /// reads as five copies of the same number trailing off. With
    /// exclusive bins each chunk lands in exactly one column, which
    /// answers "where does the population live" directly.</para>
    ///
    /// <para><b>Edge convention: <c>(a, b]</c>.</b> Lower edge
    /// exclusive, upper edge inclusive. A chunk at exactly threshold
    /// T counts in the bin below T (the upper-inclusive side). Matches
    /// the ladder intent — thresholds are level floors, and a chunk
    /// at the floor hasn't crossed it yet. Chunks at or below the
    /// lowest threshold (<c>maturity ≤ MaturityThresholds[0]</c>)
    /// are not counted in any bin (they haven't reached any tracked
    /// tier).</para>
    ///
    /// <para>Walks <see cref="ChunkValueStore"/> entries once —
    /// O(N) over total entries. Typical map: a few thousand
    /// entries, microseconds. Cheap at the perf window's ~2 Hz
    /// refresh.</para></summary>
    public IReadOnlyList<BiomeMaturityRow> TakeBiomeMaturityBreakdown() {
      var byBiome = new Dictionary<BiomeKind, int[]>();
      foreach (var entry in _chunkValues.SortedSnapshot()) {
        if (!BiomeValueKinds.TryParseMaturity(entry.Key.Kind, out var biome)) continue;
        var maturity = entry.Value;
        if (maturity <= MaturityThresholds[0]) continue;
        if (!byBiome.TryGetValue(biome, out var counts)) {
          counts = new int[MaturityThresholds.Count];
          byBiome[biome] = counts;
        }
        // Find the bin: highest threshold the chunk strictly exceeds.
        // Default to the top bin; walk up looking for the first
        // threshold the value is <= and stop one bin below it.
        var bin = MaturityThresholds.Count - 1;
        for (var i = 1; i < MaturityThresholds.Count; i++) {
          if (maturity <= MaturityThresholds[i]) { bin = i - 1; break; }
        }
        counts[bin]++;
      }
      var rows = new List<BiomeMaturityRow>(byBiome.Count);
      foreach (var kv in byBiome) {
        rows.Add(new BiomeMaturityRow(kv.Key, kv.Value));
      }
      return rows;
    }

    /// <summary>Per-reason fauna-despawn breakdown. One row per
    /// <see cref="FaunaDespawnReason"/> that has any cumulative count
    /// — reasons that haven't fired don't appear. Used by the activity
    /// panel's despawn-reason histogram to answer "why are fauna
    /// disappearing?" without grepping Player.log. Diagnostic-only:
    /// no simulation logic reads this.</summary>
    public IReadOnlyList<FaunaDespawnReasonRow> TakeDespawnReasonBreakdown() {
      var rows = new List<FaunaDespawnReasonRow>(_faunaRegistry.RemovedByReason.Count);
      foreach (var kv in _faunaRegistry.RemovedByReason) {
        rows.Add(new FaunaDespawnReasonRow(kv.Key, kv.Value));
      }
      return rows;
    }

    /// <summary>Per-blueprint fauna breakdown: live count (from a
    /// walk of <see cref="KeystoneFaunaRegistry.Entries"/>) plus
    /// cumulative spawned / despawned from the registry's per-
    /// blueprint counters. Returns one row per blueprint that has
    /// ever appeared (live or historical); species that have never
    /// spawned at all are omitted. Stable ordering is the caller's
    /// responsibility — the panel sorts by blueprint name.</summary>
    public IReadOnlyList<FaunaSpeciesRow> TakeFaunaSpeciesBreakdown() {
      var live = new Dictionary<string, int>();
      foreach (var entry in _faunaRegistry.Entries) {
        live.TryGetValue(entry.BlueprintName, out var n);
        live[entry.BlueprintName] = n + 1;
      }
      var species = new HashSet<string>(live.Keys);
      foreach (var kv in _faunaRegistry.AddedByBlueprint) species.Add(kv.Key);
      var rows = new List<FaunaSpeciesRow>(species.Count);
      foreach (var name in species) {
        var liveCount = live.TryGetValue(name, out var n) ? n : 0;
        _faunaRegistry.AddedByBlueprint.TryGetValue(name, out var added);
        _faunaRegistry.RemovedByBlueprint.TryGetValue(name, out var removed);
        rows.Add(new FaunaSpeciesRow(name, liveCount, added, removed));
      }
      return rows;
    }

    /// <summary>Multi-line text dump of the current snapshot plus the
    /// derived per-day rates from <paramref name="previousAtPriorDay"/>
    /// when supplied. Intended for the initial "verify the data is
    /// being captured correctly" probe; the UI panel will format the
    /// same fields with sparklines and per-day rollup columns.</summary>
    public string DumpText(ActivitySnapshot? previousAtPriorDay = null) {
      var s = TakeSnapshot();
      var sb = new StringBuilder();
      sb.AppendLine($"Game-day {s.TotalDaysElapsed:F2}");
      sb.AppendLine();
      sb.AppendLine("Ticker cycles (cumulative):");
      sb.AppendLine($"  EcologyFieldUpdater: {s.EcologyFieldCycles}");
      sb.AppendLine($"  ChunkBiomeTicker:    {s.BiomeTickerCycles}");
      sb.AppendLine($"  ChunkClusterTicker:  {s.ClusterTickerCycles}");
      sb.AppendLine($"  FaunaCycleTicker:    {s.FaunaCycleTickerCycles}");
      sb.AppendLine($"  FlourishDecayTicker: {s.FlourishDecayCycles}");
      sb.AppendLine();
      sb.AppendLine("Region churn (cumulative):");
      sb.AppendLine($"  Created:  {s.RegionsCreated}");
      sb.AppendLine($"  Splits:   {s.RegionSplits}");
      sb.AppendLine($"  Merges:   {s.RegionMerges}");
      sb.AppendLine($"  Removed:  {s.RegionsRemoved}");
      sb.AppendLine();
      sb.AppendLine("Fauna lifecycle (cumulative):");
      sb.AppendLine($"  Added:    {s.FaunaAdded}");
      sb.AppendLine($"  Removed:  {s.FaunaRemoved}");
      sb.AppendLine();
      sb.AppendLine("Live state:");
      sb.AppendLine($"  Regions:  {s.LiveRegions}");
      sb.AppendLine($"  Clusters: {s.LiveClusters}");
      sb.AppendLine($"  Fauna:    {s.LiveFauna}");
      if (previousAtPriorDay.HasValue) {
        var prev = previousAtPriorDay.Value;
        var elapsed = s.TotalDaysElapsed - prev.TotalDaysElapsed;
        if (elapsed > 0f) {
          sb.AppendLine();
          sb.AppendLine($"Per-day rates (over {elapsed:F2} game-days):");
          sb.AppendLine($"  EcologyFieldUpdater: {(s.EcologyFieldCycles - prev.EcologyFieldCycles) / elapsed:F1} cycles/day");
          sb.AppendLine($"  ChunkBiomeTicker:    {(s.BiomeTickerCycles - prev.BiomeTickerCycles) / elapsed:F1} cycles/day");
          sb.AppendLine($"  ChunkClusterTicker:  {(s.ClusterTickerCycles - prev.ClusterTickerCycles) / elapsed:F1} cycles/day");
          sb.AppendLine($"  FaunaCycleTicker:    {(s.FaunaCycleTickerCycles - prev.FaunaCycleTickerCycles) / elapsed:F1} cycles/day");
          sb.AppendLine($"  FlourishDecayTicker: {(s.FlourishDecayCycles - prev.FlourishDecayCycles) / elapsed:F1} cycles/day");
          sb.AppendLine($"  Region churn:        +{s.RegionsCreated - prev.RegionsCreated} created, " +
              $"{s.RegionSplits - prev.RegionSplits} splits, {s.RegionMerges - prev.RegionMerges} merges, " +
              $"{s.RegionsRemoved - prev.RegionsRemoved} removed");
          sb.AppendLine($"  Fauna churn:         +{s.FaunaAdded - prev.FaunaAdded}, -{s.FaunaRemoved - prev.FaunaRemoved}");
        }
      }
      return sb.ToString();
    }

  }

  /// <summary>Instantaneous activity reading. Cumulative counters
  /// since map load (or session start for non-persisted counts) plus
  /// live counts at the moment of the snapshot. Per-day rates are
  /// derived by diffing successive snapshots at day boundaries.</summary>
  public readonly record struct ActivitySnapshot(
      float TotalDaysElapsed,
      long EcologyFieldCycles,
      long BiomeTickerCycles,
      long ClusterTickerCycles,
      long FaunaCycleTickerCycles,
      long FlourishDecayCycles,
      long RegionsCreated,
      long RegionSplits,
      long RegionMerges,
      long RegionsRemoved,
      long FaunaAdded,
      long FaunaRemoved,
      int LiveRegions,
      int LiveClusters,
      int LiveFauna,
      long ClusterRebuilds,
      long ClustersCreated,
      long ClustersDestroyed,
      int LastRebuildRegionsIncluded,
      int LastRebuildRegionsSkippedNoField,
      int LastRebuildRegionsSkippedFewValidChunks,
      int FieldShapeVersion);

  /// <summary>One row of the per-biome maturity breakdown.
  /// <see cref="CountsInBin"/>[i] is the count of chunks with this
  /// biome's Maturity in the half-open interval
  /// <c>(MaturityThresholds[i], MaturityThresholds[i+1]]</c>; the
  /// last entry (<c>i = N-1</c>) is the count of chunks with
  /// Maturity strictly greater than the top threshold. See
  /// <see cref="KeystoneActivityRecorder.TakeBiomeMaturityBreakdown"/>
  /// for the bin-edge convention.</summary>
  public readonly record struct BiomeMaturityRow(
      BiomeKind Biome,
      IReadOnlyList<int> CountsInBin);

  /// <summary>One row of the despawn-reason histogram.
  /// <see cref="Count"/> is the cumulative number of fauna removed
  /// from the registry with this reason category since the registry
  /// was constructed.</summary>
  public readonly record struct FaunaDespawnReasonRow(
      Keystone.Mod.Fauna.FaunaDespawnReason Reason,
      long Count);

  /// <summary>One row of the per-species fauna breakdown.
  /// <see cref="Live"/> is the current count from a registry walk;
  /// <see cref="CumulativeSpawned"/> and
  /// <see cref="CumulativeDespawned"/> are monotonic counters
  /// covering the whole session. The difference between the two
  /// cumulative numbers equals <see cref="Live"/> by definition
  /// (modulo a freshly-removed entity caught mid-update).</summary>
  public readonly record struct FaunaSpeciesRow(
      string BlueprintName,
      int Live,
      long CumulativeSpawned,
      long CumulativeDespawned);

}
