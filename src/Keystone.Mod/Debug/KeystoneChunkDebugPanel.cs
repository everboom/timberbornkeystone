using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Core.Ports;
using Keystone.Core.Time;
using Keystone.Core.Regions;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;
using Keystone.Mod.Recipes;
using Keystone.Mod.Settings;
using Keystone.Mod.Survey;
using Timberborn.CursorToolSystem;
using Timberborn.DebuggingUI;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Chunk- and column-scope half of the Keystone debug overlay.
  /// Three sections: the cursor column's surveyed surfaces (with each
  /// surface's region + survey staleness), the bilinear-sampled
  /// ecology-field readout (entity densities), and the per-chunk
  /// <see cref="ChunkValueStore"/> entries (per-biome Suitability +
  /// Maturity, etc.). Companion to <see cref="KeystoneTileDebugPanel"/>;
  /// see that file for the activity-side-channel rationale.
  /// </summary>
  public sealed class KeystoneChunkDebugPanel : ILoadableSingleton, IDebuggingPanel {

    #region Constants

    private const string PanelName = "Keystone (chunk)";

    /// <summary>Threshold below which a 0..1-fraction sample is hidden
    /// from the field readout.</summary>
    private const float FractionPresentThreshold = 0.005f;

    #endregion

    #region Fields

    private readonly DebuggingPanel _panel;
    private readonly CursorDebugger _cursor;
    private readonly KeystoneSurveyor _surveyor;
    private readonly ITerrainQuery _terrain;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly ChunkValueStore _chunkValues;
    private readonly ChunkDataStore _chunkData;
    private readonly ChunkClusterIndex _clusterIndex;
    private readonly FlourishCatalog _catalog;
    private readonly BiomeLevelTable _levelTable;
    private readonly KeystoneFaunaSettings _faunaSettings;
    private readonly IClock _clock;
    private readonly KeystoneChunkPanelActivity _activity;
    private readonly StringBuilder _buffer = new();

    #endregion

    #region Construction

    public KeystoneChunkDebugPanel(
        DebuggingPanel panel,
        CursorDebugger cursor,
        KeystoneSurveyor surveyor,
        ITerrainQuery terrain,
        IEcologyFieldQuery fieldQuery,
        ChunkValueStore chunkValues,
        ChunkDataStore chunkData,
        ChunkClusterIndex clusterIndex,
        FlourishCatalog catalog,
        BiomeLevelTable levelTable,
        KeystoneFaunaSettings faunaSettings,
        IClock clock,
        KeystoneChunkPanelActivity activity) {
      _panel = panel;
      _cursor = cursor;
      _surveyor = surveyor;
      _terrain = terrain;
      _fieldQuery = fieldQuery;
      _chunkValues = chunkValues;
      _chunkData = chunkData;
      _clusterIndex = clusterIndex;
      _catalog = catalog;
      _levelTable = levelTable;
      _faunaSettings = faunaSettings;
      _clock = clock;
      _activity = activity;
    }

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      _panel.AddDebuggingPanel(this, PanelName);
    }

    #endregion

    #region IDebuggingPanel

    /// <inheritdoc />
    public string GetText() {
      _activity.MarkActive();
      _buffer.Clear();

      if (!_cursor.Active) {
        _buffer.Append("(hover the map to inspect a chunk)");
        return _buffer.ToString();
      }

      // Section layout convention (mirrors KeystoneTileDebugPanel):
      //   0-indent: section header or top-level fact
      //   2-indent: item within a section
      //   4-indent: sub-detail of an item
      // Blank line between sections.
      var c = _cursor.Coordinates;
      var column = new TileCoord(c.x, c.y);

      AppendStalenessReportAndColumn(column, c.z);

      _buffer.AppendLine();
      AppendFieldSample(column, c.z);

      _buffer.AppendLine();
      AppendChunkValuesAtCursor(column, c.z);

      _buffer.AppendLine();
      AppendClusterInfo(column, c.z);

      if (_buffer.Length > 0 && _buffer[_buffer.Length - 1] == '\n') {
        _buffer.Length--;
      }
      return _buffer.ToString();
    }

    /// <summary>Report the cluster (if any) that the cursor's chunk
    /// belongs to. Cluster ids are snapshot-scoped per
    /// <see cref="ChunkClusterIndex.Rebuild"/>; the panel just reads
    /// the latest snapshot.</summary>
    private void AppendClusterInfo(TileCoord column, int cursorZ) {
      var region = PickRegionAtCursor(column, cursorZ);
      if (region == null) {
        _buffer.AppendLine("Cluster: (no region)");
        return;
      }
      var globalCx = column.X / RegionEcologyField.ChunkSize;
      var globalCy = column.Y / RegionEcologyField.ChunkSize;
      var clusterId = _clusterIndex.ClusterFor(region.Id, globalCx, globalCy);
      if (clusterId == null) {
        _buffer.AppendLine(
            $"Cluster (region {region.Id}, chunk {globalCx},{globalCy}): " +
            "chunk not in any cluster (immature or no dominant biome)");
        return;
      }
      var biome = _clusterIndex.BiomeFor(clusterId.Value);
      var chunks = _clusterIndex.ChunksIn(clusterId.Value);
      var tiles = _clusterIndex.TileCount(clusterId.Value);
      var rawScore = _clusterIndex.RawScore(clusterId.Value);
      var score = _clusterIndex.Score(clusterId.Value);
      _buffer.AppendLine(
          $"Cluster (region {region.Id}, chunk {globalCx},{globalCy}): id={clusterId.Value.Value}");
      _buffer.AppendLine(
          $"  biome  = {biome}, score = {score.ToString("F3", CultureInfo.InvariantCulture)} " +
          $"(raw {rawScore.ToString("F0", CultureInfo.InvariantCulture)})");
      _buffer.AppendLine($"  chunks = {chunks.Count}, tiles = {tiles}");
      AppendTierHistogram(clusterId.Value);
      AppendFaunaDiagnosis(clusterId.Value, biome, chunks, score);
    }

    /// <summary>For each Class E (fauna) bucket in this cluster's
    /// biome, print expected capacity and — if the bucket would spawn
    /// nothing — the precise reason. Surfaces the "I expect cattle
    /// here, why isn't anything spawning?" question without making the
    /// player tail Player.log for the dawn diagnostic. Mirrors the
    /// gating logic in
    /// <c>FaunaDayCycleHandler.ReconcileCapacityAtDawn</c> exactly;
    /// if the two diverge, this panel is the lie.</summary>
    private void AppendFaunaDiagnosis(
        ChunkClusterId clusterId,
        BiomeKind? biome,
        IReadOnlyList<ChunkCoord> chunks,
        float clusterScore) {
      if (!biome.HasValue) return;
      // Group Class E recipes for this biome by levelId; one diagnostic
      // block per bucket (matches the bucket grouping in the dawn
      // reconcile pass). FlourishCatalog asserts at load that all
      // recipes in a (biome, levelId) bucket share the same Category,
      // so capturing the first recipe's category for the bucket is
      // well-defined and matches the production multiplier lookup at
      // FaunaCycleTicker.cs:276.
      var bucketsByLevel = new Dictionary<string, (List<string> Members, string Category)>();
      foreach (var recipe in _catalog.ClassEForBiome(biome.Value)) {
        if (!bucketsByLevel.TryGetValue(recipe.LevelId, out var bucket)) {
          bucket = (new List<string>(), recipe.Category);
          bucketsByLevel[recipe.LevelId] = bucket;
        }
        bucket.Members.Add(recipe.BlueprintName + (recipe.Weight != 1f
            ? "×" + recipe.Weight.ToString("F0", CultureInfo.InvariantCulture) : ""));
      }
      if (bucketsByLevel.Count == 0) return;

      _buffer.AppendLine($"  fauna for {biome}:");
      foreach (var (levelId, bucket) in bucketsByLevel) {
        var memberList = string.Join("+", bucket.Members);
        var level = _levelTable.Find(biome.Value, levelId);
        if (level == null) {
          _buffer.AppendLine($"    {levelId} ({memberList}) -- level missing from table");
          continue;
        }
        // Per-category abundance multiplier from the player setting.
        // Surfaced in every diagnostic line so the user can verify the
        // slider takes effect, and folded into the capacity math
        // exactly as the dawn reconcile pass does it.
        var multiplier = _faunaSettings.MultiplierFor(bucket.Category);
        var multiplierLabel =
            $"{bucket.Category} {(multiplier * 100f).ToString("F0", CultureInfo.InvariantCulture)}%";

        _buffer.Append("    ").Append(levelId).Append(" (").Append(memberList).Append("): ");

        // Replay the dawn reconcile gating in order.
        if (level.FaunaCapacityAtSaturation == 0) {
          _buffer.AppendLine("cap@sat=0, level spawns no fauna");
          continue;
        }
        if (clusterScore < level.FaunaMinScore) {
          _buffer.AppendLine(
              $"0 -- below score floor (" +
              $"{clusterScore.ToString("F2", CultureInfo.InvariantCulture)} < " +
              $"{level.FaunaMinScore.ToString("F2", CultureInfo.InvariantCulture)})");
          continue;
        }
        if (multiplier <= 0f) {
          _buffer.AppendLine(
              $"0 -- multiplier {multiplierLabel} (master toggle off or category at 0%)");
          continue;
        }
        var capacity = (int)Math.Floor(
            clusterScore * level.FaunaCapacityAtSaturation * multiplier);
        if (capacity == 0) {
          _buffer.AppendLine(
              $"0 -- score {clusterScore.ToString("F2", CultureInfo.InvariantCulture)} × " +
              $"cap@sat {level.FaunaCapacityAtSaturation} × " +
              $"{multiplierLabel} floors to 0");
          continue;
        }
        // Placement filter: scan member chunks for at least one meeting
        // level.LowerMaturity. Match the consumer's "≥" comparison.
        var maturityKind = BiomeValueKinds.ForMaturity(biome.Value);
        var qualifyingChunks = 0;
        var maxMat = 0f;
        for (var i = 0; i < chunks.Count; i++) {
          var c = chunks[i];
          var m = _chunkValues.Get(c.RegionId, c.GlobalChunkX, c.GlobalChunkY, maturityKind) ?? 0f;
          if (m > maxMat) maxMat = m;
          if (m >= level.LowerMaturity) qualifyingChunks++;
        }
        if (qualifyingChunks == 0) {
          _buffer.AppendLine(
              $"0 -- score passes ({clusterScore.ToString("F2", CultureInfo.InvariantCulture)} ≥ " +
              $"{level.FaunaMinScore.ToString("F2", CultureInfo.InvariantCulture)}) but no chunk " +
              $"meets placement maturity {level.LowerMaturity.ToString("F1", CultureInfo.InvariantCulture)} " +
              $"(max={maxMat.ToString("F1", CultureInfo.InvariantCulture)})");
          continue;
        }
        _buffer.AppendLine(
            $"capacity = {capacity} " +
            $"(score {clusterScore.ToString("F2", CultureInfo.InvariantCulture)} × " +
            $"cap@sat {level.FaunaCapacityAtSaturation} × " +
            $"{multiplierLabel}, " +
            $"{qualifyingChunks}/{chunks.Count} chunks pass placement ≥" +
            $"{level.LowerMaturity.ToString("F1", CultureInfo.InvariantCulture)})");
      }
    }

    /// <summary>One-line histogram showing tile distribution across
    /// the cluster's maturity tiers. Pairs every threshold band with
    /// the count of tiles whose Maturity sits in that band. Lets the
    /// user see, next to the score, exactly which tiers are pulling
    /// the score up or down — e.g., "is this cluster only scoring
    /// well because of a single pristine chunk?"</summary>
    private void AppendTierHistogram(ChunkClusterId clusterId) {
      var tier = _clusterIndex.TilesInTier(clusterId);
      var thresholds = ChunkClusterIndex.Thresholds;
      if (tier.Count == 0 || tier.Count != thresholds.Count) return;
      _buffer.Append("  tiers  =");
      for (var i = 0; i < tier.Count; i++) {
        var lo = thresholds[i].ToString("F1", CultureInfo.InvariantCulture);
        var hi = i + 1 < thresholds.Count
            ? thresholds[i + 1].ToString("F1", CultureInfo.InvariantCulture)
            : "+";
        var label = i + 1 < thresholds.Count ? $"[{lo}-{hi})" : $"[{lo}+)";
        _buffer.Append(' ').Append(label).Append(':').Append(tier[i]);
      }
      _buffer.AppendLine();
    }

    #endregion

    #region Column + staleness

    /// <summary>Compare the cached survey state for the cursor column
    /// against the live <see cref="ITerrainQuery"/> state, surface
    /// any drift loudly, then emit the per-surface details (region
    /// id + size, creation cycle).</summary>
    private void AppendStalenessReportAndColumn(TileCoord column, int cursorZ) {
      var cachedHeights = _surveyor.Core.ColumnSurfaceHeights(column);
      var liveHeights = _terrain.Contains(column)
          ? _terrain.SurfaceHeightsAt(column)
          : (IReadOnlyList<int>)Array.Empty<int>();

      var driftDetected = !SameHeights(cachedHeights, liveHeights);
      var cursorOnCachedSurface = ContainsZ(cachedHeights, cursorZ);
      var cursorOnLiveSurface = ContainsZ(liveHeights, cursorZ);

      if (driftDetected) {
        _buffer.AppendLine("STALE: surveyed column != live terrain");
        _buffer.AppendLine($"  cached Z=[{Join(cachedHeights)}]  live Z=[{Join(liveHeights)}]");
      }
      if (!cursorOnCachedSurface) {
        _buffer.AppendLine($"STALE: cursor Z={cursorZ} is not a surveyed surface in this column");
        if (cursorOnLiveSurface) {
          _buffer.AppendLine("  (it IS in live terrain -- the survey is behind reality here)");
        }
      }

      if (cachedHeights.Count == 0) {
        _buffer.AppendLine($"Column ({column.X},{column.Y}): no survey data");
        return;
      }

      _buffer.AppendLine($"Column ({column.X},{column.Y}): {cachedHeights.Count} cached surface(s)");
      for (var i = 0; i < cachedHeights.Count; i++) {
        var z = cachedHeights[i];
        var surfaceCoord = new SurfaceCoord(column.X, column.Y, z);
        if (!_surveyor.Core.Surfaces.TryGet(surfaceCoord, out _)) continue;
        var region = _surveyor.Regions.Containing(surfaceCoord);
        if (region != null) {
          _buffer.AppendLine($"  z={z}  region={region.Id} (size={region.Size})");
          _buffer.AppendLine(
              $"    created cycle {region.CreatedAt.Cycle} day {region.CreatedAt.CycleDay} " +
              $"({region.WeatherAtCreation})");
          _buffer.AppendLine($"    {RegionTypeLabel(region)}");
        } else {
          _buffer.AppendLine($"  z={z}  region=none");
        }
      }
    }

    private static string RegionTypeLabel(Region region) {
      if (region.IsSettled) return "settled";
      if (region.IsCave) return "cave";
      return "wild";
    }

    private static bool SameHeights(IReadOnlyList<int> a, IReadOnlyList<int> b) {
      if (a.Count != b.Count) return false;
      for (var i = 0; i < a.Count; i++) {
        if (a[i] != b[i]) return false;
      }
      return true;
    }

    private static bool ContainsZ(IReadOnlyList<int> heights, int z) {
      for (var i = 0; i < heights.Count; i++) {
        if (heights[i] == z) return true;
      }
      return false;
    }

    private static string Join(IReadOnlyList<int> heights) {
      if (heights.Count == 0) return "";
      var sb = new StringBuilder();
      for (var i = 0; i < heights.Count; i++) {
        if (i > 0) sb.Append(',');
        sb.Append(heights[i]);
      }
      return sb.ToString();
    }

    #endregion

    #region Field sample

    /// <summary>Bilinear-sample the cursor region's ecology field at
    /// <c>(column.X, column.Y)</c> and print each known entity-density
    /// channel above <see cref="FractionPresentThreshold"/> as a single
    /// comma-separated line.</summary>
    private void AppendFieldSample(TileCoord column, int cursorZ) {
      var region = PickRegionAtCursor(column, cursorZ);
      if (region == null) return;

      var field = _fieldQuery.FieldFor(region.Id);
      if (field == null) {
        _buffer.AppendLine(region.IsSettled
            ? $"Field (region {region.Id}): settled -- ecology fields skipped"
            : $"Field (region {region.Id}): not yet sampled");
        return;
      }

      _buffer.AppendLine($"Field (region {region.Id}, {column.X},{column.Y}):");

      var blueprints = _fieldQuery.KnownEntityBlueprints;
      var chunkArea = (float)(RegionEcologyField.ChunkSize * RegionEcologyField.ChunkSize);
      var any = false;
      _buffer.Append("  ");
      for (var i = 0; i < blueprints.Count; i++) {
        var count = field.SampleEntity(i, column.X, column.Y);
        var density = count / chunkArea;
        if (density > FractionPresentThreshold) {
          if (any) _buffer.Append(", ");
          _buffer.Append(blueprints[i]).Append(' ').Append(Pct(density));
          any = true;
        }
      }
      if (!any) _buffer.Append("(none)");
      _buffer.AppendLine();
    }

    private static string Pct(float v) {
      var p = v * 100f;
      return p >= 1f
          ? p.ToString("F0", CultureInfo.InvariantCulture) + "%"
          : p.ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    #endregion

    #region Chunk values

    /// <summary>List every non-zero <see cref="ChunkValueStore"/> entry
    /// against the cursor's chunk. Biome entries (suitability +
    /// maturity for the same biome) collapse into a single line of the
    /// form <c>"biome: maturity at suitability"</c>; non-biome entries
    /// fall through to the <c>"kind = value"</c> layout.</summary>
    private void AppendChunkValuesAtCursor(TileCoord column, int cursorZ) {
      var region = PickRegionAtCursor(column, cursorZ);
      if (region == null) return;

      var field = _fieldQuery.FieldFor(region.Id);
      int globalCx, globalCy;
      if (field != null) {
        var fieldCx = (column.X - field.OriginX) / RegionEcologyField.ChunkSize;
        var fieldCy = (column.Y - field.OriginY) / RegionEcologyField.ChunkSize;
        globalCx = (field.OriginX / RegionEcologyField.ChunkSize) + fieldCx;
        globalCy = (field.OriginY / RegionEcologyField.ChunkSize) + fieldCy;
      } else {
        globalCx = column.X / RegionEcologyField.ChunkSize;
        globalCy = column.Y / RegionEcologyField.ChunkSize;
      }

      // Two passes: first, fold biome suitability/maturity pairs into a
      // per-biome dict; non-biome entries go to a separate list. Then
      // emit biomes (sorted by enum order) followed by other entries
      // (sorted by kind). Either bucket may be empty.
      Dictionary<BiomeKind, (float Maturity, float Suitability)>? byBiome = null;
      List<KeyValuePair<ChunkValueKey, float>>? others = null;

      foreach (var kv in _chunkValues.EntriesForChunk(region.Id, globalCx, globalCy)) {
        if (kv.Value <= 0f) continue;
        if (BiomeValueKinds.TryParseMaturity(kv.Key.Kind, out var matBiome)) {
          byBiome ??= new Dictionary<BiomeKind, (float, float)>();
          byBiome.TryGetValue(matBiome, out var cur);
          byBiome[matBiome] = (kv.Value, cur.Suitability);
        } else if (BiomeValueKinds.TryParseSuitability(kv.Key.Kind, out var suitBiome)) {
          byBiome ??= new Dictionary<BiomeKind, (float, float)>();
          byBiome.TryGetValue(suitBiome, out var cur);
          byBiome[suitBiome] = (cur.Maturity, kv.Value);
        } else {
          (others ??= new List<KeyValuePair<ChunkValueKey, float>>()).Add(kv);
        }
      }

      var chunkDataEntry = _chunkData.Get(region.Id, globalCx, globalCy);
      if (chunkDataEntry != null && chunkDataEntry.LastUpdatedDay > 0f) {
        var ageHours = (_clock.TotalDaysElapsed - chunkDataEntry.LastUpdatedDay) * 24f;
        _buffer.AppendLine(
            $"Chunk values (region {region.Id}, chunk {globalCx},{globalCy}) " +
            $"-- age {ageHours.ToString("F1", CultureInfo.InvariantCulture)}h:");
      } else {
        _buffer.AppendLine(
            $"Chunk values (region {region.Id}, chunk {globalCx},{globalCy}) " +
            "-- never updated:");
      }
      if (byBiome == null && others == null) {
        _buffer.AppendLine("  (none)");
        return;
      }

      if (byBiome != null) {
        // Sort biomes by enum order so the rows don't shuffle as
        // values come and go.
        var biomes = new List<BiomeKind>(byBiome.Keys);
        biomes.Sort();
        foreach (var biome in biomes) {
          var (maturity, suitability) = byBiome[biome];
          _buffer.AppendLine(
              $"  {biome.ToString().ToLowerInvariant()}: " +
              $"{maturity.ToString("F2", CultureInfo.InvariantCulture)} at " +
              $"{suitability.ToString("F2", CultureInfo.InvariantCulture)}");
        }
      }
      if (others != null) {
        others.Sort((a, b) => string.CompareOrdinal(a.Key.Kind, b.Key.Kind));
        foreach (var kv in others) {
          _buffer.AppendLine(
              $"  {StripChunkPrefix(kv.Key.Kind)} = {kv.Value.ToString("F2", CultureInfo.InvariantCulture)}");
        }
      }
    }

    private static string StripChunkPrefix(string kind) {
      const string prefix = "keystone.chunk.";
      return kind.StartsWith(prefix, StringComparison.Ordinal)
          ? kind.Substring(prefix.Length)
          : kind;
    }

    /// <summary>Pick the surveyed surface in the cursor's column whose
    /// Z is closest to <paramref name="cursorZ"/>, then return the
    /// region that contains that surface. Used by the field-sample
    /// and chunk-values sections so they agree on which region the
    /// cursor is "in" for stacked columns.</summary>
    private Region? PickRegionAtCursor(TileCoord column, int cursorZ) {
      var heights = _surveyor.Core.ColumnSurfaceHeights(column);
      if (heights.Count == 0) return null;

      var bestZ = heights[0];
      var bestDist = Math.Abs(bestZ - cursorZ);
      for (var i = 1; i < heights.Count; i++) {
        var d = Math.Abs(heights[i] - cursorZ);
        if (d < bestDist) {
          bestDist = d;
          bestZ = heights[i];
        }
      }
      var surfaceCoord = new SurfaceCoord(column.X, column.Y, bestZ);
      return _surveyor.Regions.Containing(surfaceCoord);
    }

    #endregion

  }

}
