using System.Collections.Generic;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Flora;
using Keystone.Core.Ports;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Translates per-chunk <see cref="RegionEcologyField"/> channel
  /// values plus per-tile player planting marks into
  /// <see cref="ChunkBiomeInputs"/> consumable by
  /// <see cref="BiomeSuitabilityUpdater"/>.
  ///
  /// <para><b>What's plumbed today.</b> The four scalar channels
  /// (<see cref="EcologyChannel.Moisture"/>,
  /// <see cref="EcologyChannel.Contamination"/>,
  /// <see cref="EcologyChannel.WaterDepth"/>,
  /// <see cref="EcologyChannel.WaterFlowMagnitude"/>) plus the
  /// per-blueprint entity counts and the planting-mark service.
  /// Plantable counts (<see cref="ChunkBiomeInputs.PlantableCount"/>,
  /// <see cref="ChunkBiomeInputs.PlantableSpeciesCount"/>) sum
  /// realised entities (Tree, Bush, Crop -- not GroundCover) and
  /// player-drawn marks, deduped per tile so a marked-and-planted
  /// tile counts once.</para>
  ///
  /// <para><b>Still missing.</b> Aquatic-plant counts (so Wetland's
  /// diversity check stays inert) and cave fraction.</para>
  ///
  /// <para><b>Water-presence approximation.</b> Per-chunk water
  /// *fraction* isn't directly represented in the field (only the
  /// mean depth). Approximated as binary "chunk has water" based on
  /// a depth threshold. Mixed water/land chunks classify all-or-
  /// nothing on the water axis. Will be refined when the field
  /// grows a presence-fraction channel.</para>
  /// </summary>
  public sealed class ChunkBiomeAdapter {

    #region Approximation thresholds

    /// <summary>Minimum mean depth for a chunk to be considered "has
    /// water" at all. Below this, the chunk is treated as land.</summary>
    private const float WaterPresenceThreshold = 0.05f;

    /// <summary>Depth threshold separating Wetland from Lake. Strictly
    /// greater than this counts as deep (Lake); at or below counts as
    /// shallow (Wetland). Hard binary -- no grace range -- so water
    /// at exactly depth 1 is unambiguously Wetland.</summary>
    private const float DeepDepthThreshold = 1.0f;

    /// <summary>Flow magnitude above which a chunk reads as
    /// "high-flow water" (drives <see cref="BiomeKind.River"/>);
    /// below it counts toward Wetland/Lake. Tunable from in-game
    /// observation -- raise if too many slow side-channels are
    /// classifying as River, lower if too many fast channels are
    /// classifying as Wetland.</summary>
    private const float HighFlowThreshold = 0.10f;

    #endregion

    private readonly FloraCatalog _flora;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IPlantingMarkQuery _marks;
    private readonly INaturalResourceAtTileQuery _naturalResources;

    /// <summary>Cached entity-channel indices for tree-kind flora
    /// (drives <see cref="ChunkBiomeInputs.TreeCount"/> /
    /// <see cref="ChunkBiomeInputs.TreeSpeciesCount"/> -- the
    /// canopy-specific signals Forest and Grassland use).</summary>
    private (int Index, string BlueprintName)[]? _treeIndex;

    /// <summary>Cached entity-channel indices for any plantable flora
    /// (Tree, Bush, Crop -- excludes GroundCover). Drives
    /// <see cref="ChunkBiomeInputs.PlantableCount"/> /
    /// <see cref="ChunkBiomeInputs.PlantableSpeciesCount"/>, which
    /// is the unified count Monoculture uses (where all cultivation
    /// counts equally regardless of plant kind).</summary>
    private (int Index, string BlueprintName)[]? _plantableIndex;

    /// <summary>Reusable per-chunk scratch for per-species counts.
    /// Cleared at the start of each <see cref="AggregatePlantables"/>
    /// call. Holds the species-name → in-chunk-count map used to
    /// compute Simpson's diversity index and the distinct-species
    /// count for <see cref="ChunkBiomeInputs"/>. Reused across chunks
    /// to avoid GC pressure (~hundreds of chunks per cycle).</summary>
    private readonly Dictionary<string, int> _speciesCountScratch = new();

    public ChunkBiomeAdapter(
        FloraCatalog flora,
        IEcologyFieldQuery fieldQuery,
        IPlantingMarkQuery marks,
        INaturalResourceAtTileQuery naturalResources) {
      _flora = flora;
      _fieldQuery = fieldQuery;
      _marks = marks;
      _naturalResources = naturalResources;
    }

    public ChunkBiomeInputs Build(RegionEcologyField field, int cx, int cy) {
      // Populate the index caches before either aggregation reads them.
      // _treeIndex is consumed by AggregateFromChannels below; the lazy
      // init used to live inside AggregatePlantables, which meant the
      // first Build call passed a still-null _treeIndex into the tree
      // aggregation and got back (0, 0) -- silently zeroing the tree
      // contribution for one tick.
      EnsurePlantableIndexCached();

      var moistureFrac = field.ChunkValue(EcologyChannel.Moisture, cx, cy);
      var contamFrac = field.ChunkValue(EcologyChannel.Contamination, cx, cy);
      var waterContamFrac = field.ChunkValue(EcologyChannel.WaterContamination, cx, cy);
      var meanDepth = field.ChunkValue(EcologyChannel.WaterDepth, cx, cy);
      var meanFlow = field.ChunkValue(EcologyChannel.WaterFlowMagnitude, cx, cy);

      var hasWater = meanDepth > WaterPresenceThreshold;
      var highFlow = meanFlow > HighFlowThreshold;

      // Hard depth threshold: depth > 1 -> Lake, depth <= 1 -> Wetland.
      var isDeep = hasWater && meanDepth > DeepDepthThreshold;
      var deepFactor = isDeep ? 1f : 0f;
      var shallowFactor = hasWater && !isDeep ? 1f : 0f;

      var waterFraction = hasWater ? 1f : 0f;
      var shallowSlow = highFlow ? 0f : shallowFactor;
      var shallowHighFlow = highFlow ? shallowFactor : 0f;
      var deepSlow = highFlow ? 0f : deepFactor;
      var deepHighFlow = highFlow ? deepFactor : 0f;

      var irrigated = hasWater ? 0f : moistureFrac;
      var dryLand = hasWater ? 0f : System.Math.Max(0f, 1f - moistureFrac);
      // ContaminatedWaterFraction is the fraction of in-region tiles
      // whose water column is contaminated -- sampled directly from
      // the WaterContamination channel rather than derived from the
      // soil-side Contamination channel × hasWater. The soil-side
      // derivation produced a 0 reading for fresh badwater pools
      // (water already toxic, soil plume not yet) which collapsed
      // Badwater suitability to 0 even on clearly-badwater chunks.
      var contaminatedWater = waterContamFrac;

      var (treeCount, treeSpecies) = AggregateFromChannels(field, cx, cy, _treeIndex);
      var (plantableCount, plantableSpecies, plantableDominance) =
          AggregatePlantables(field, cx, cy);

      return new ChunkBiomeInputs {
          DryLandFraction = dryLand,
          IrrigatedFraction = irrigated,
          WaterFraction = waterFraction,
          CaveFraction = 0f,
          ShallowSlowWaterFraction = shallowSlow,
          ShallowHighFlowWaterFraction = shallowHighFlow,
          DeepSlowWaterFraction = deepSlow,
          DeepHighFlowWaterFraction = deepHighFlow,
          ContaminatedFraction = contamFrac,
          ContaminatedWaterFraction = contaminatedWater,
          TreeCount = treeCount,
          TreeSpeciesCount = treeSpecies,
          PlantableCount = plantableCount,
          PlantableSpeciesCount = plantableSpecies,
          PlantableDominance = plantableDominance,
      };
    }

    /// <summary>Sum the per-blueprint channel counts in this chunk
    /// for the given index, plus the count of distinct blueprints
    /// with at least one instance. Used by both the tree-only and
    /// the unified-plantable aggregations.</summary>
    private static (int count, int species) AggregateFromChannels(
        RegionEcologyField field, int cx, int cy,
        (int Index, string BlueprintName)[]? index) {
      if (index == null || index.Length == 0) return (0, 0);
      var totalFloat = 0f;
      var species = 0;
      for (var i = 0; i < index.Length; i++) {
        var idx = index[i].Index;
        if (idx >= field.EntityChannelCount) continue;
        var c = field.ChunkValueEntity(idx, cx, cy);
        if (c <= 0f) continue;
        totalFloat += c;
        species++;
      }
      return ((int)totalFloat, species);
    }

    /// <summary>Combine realised plantable entities (from per-blueprint
    /// channel counts) with player-drawn planting marks (from the
    /// planting-mark port), deduped per tile so a marked-and-planted
    /// tile counts once. Tracks per-species counts in
    /// <see cref="_speciesCountScratch"/> so the caller can compute
    /// both the distinct-species count and Simpson's diversity index
    /// (returned as <c>dominance</c>).</summary>
    private (int count, int species, float dominance) AggregatePlantables(
        RegionEcologyField field, int cx, int cy) {
      EnsurePlantableIndexCached();

      _speciesCountScratch.Clear();
      var totalCount = 0;

      // Pass 1: realised entities, per-species channel sums.
      if (_plantableIndex != null) {
        for (var i = 0; i < _plantableIndex.Length; i++) {
          var entry = _plantableIndex[i];
          if (entry.Index >= field.EntityChannelCount) continue;
          var c = (int)field.ChunkValueEntity(entry.Index, cx, cy);
          if (c <= 0) continue;
          totalCount += c;
          _speciesCountScratch.TryGetValue(entry.BlueprintName, out var existing);
          _speciesCountScratch[entry.BlueprintName] = existing + c;
        }
      }

      // Pass 2: marks in the chunk's tile rect; dedup against tiles
      // that already contain a plantable entity (counted via the
      // channel pass). The port's MarksInTileRect is bucket-indexed
      // so the per-chunk cost scales with marks-in-chunk, not with
      // the global mark count -- relevant on large maps with
      // extensive Forester areas.
      const int chunkSize = RegionEcologyField.ChunkSize;
      var minX = field.OriginX + cx * chunkSize;
      var maxX = minX + chunkSize - 1;
      var minY = field.OriginY + cy * chunkSize;
      var maxY = minY + chunkSize - 1;
      foreach (var (mx, my, mz, species) in _marks.MarksInTileRect(minX, minY, maxX, maxY)) {
        if (TileHasPlantableEntity(mx, my, mz)) continue;
        totalCount++;
        if (string.IsNullOrEmpty(species)) continue;
        _speciesCountScratch.TryGetValue(species, out var existing);
        _speciesCountScratch[species] = existing + 1;
      }

      // Simpson's D = Σ (count_i / total)². 1.0 = single species;
      // 1/N for a perfectly even N-species mix. 0 when the chunk has
      // no plantables (defensive -- the caller's saturation factor
      // also goes to 0 there, so downstream Monoculture is 0 either
      // way).
      var dominance = 0f;
      if (totalCount > 0) {
        var inv = 1f / totalCount;
        foreach (var kv in _speciesCountScratch) {
          var p = kv.Value * inv;
          dominance += p * p;
        }
      }

      return (totalCount, _speciesCountScratch.Count, dominance);
    }

    /// <summary>True if the tile carries any natural-resource-bearing
    /// entity. Used to drop double-counts when a marked tile already
    /// has the entity the mark designated. Coarse classification (any
    /// natural resource, not just plantable kinds) -- correct for our
    /// use case because non-plantable natural resources can't be marked
    /// in the first place. Delegates to
    /// <see cref="INaturalResourceAtTileQuery"/>; the Mod-side adapter
    /// wraps Timberborn's block service.</summary>
    private bool TileHasPlantableEntity(int x, int y, int z) =>
        _naturalResources.HasNaturalResourceAt(x, y, z);

    private void EnsurePlantableIndexCached() {
      // FloraCatalog populates at PostLoad; if it's empty we're being
      // called too early -- leave the caches null and try again next
      // tick. Same for the field updater's index map.
      if (_flora.Count == 0) return;
      // Retry if the cache is missing OR if it captured an empty result
      // while the catalog has entries (PostLoad ordering -- this adapter
      // or its field-updater dependency may have first-cached when one
      // of them was still empty). The retry exits cheaply once the cache
      // contains at least one entry, which it should as soon as both
      // upstreams are populated.
      if (_plantableIndex != null && _plantableIndex.Length > 0) return;

      var trees = new List<(int, string)>();
      var plantables = new List<(int, string)>();
      foreach (var entry in _flora.Entries) {
        if (entry.Kind == FloraKind.GroundCover) continue;
        var idx = _fieldQuery.EntityIndex(entry.BlueprintName);
        if (!idx.HasValue) continue;
        plantables.Add((idx.Value, entry.BlueprintName));
        if (entry.Kind == FloraKind.Tree) trees.Add((idx.Value, entry.BlueprintName));
      }
      _treeIndex = trees.ToArray();
      _plantableIndex = plantables.ToArray();
    }

  }

}
