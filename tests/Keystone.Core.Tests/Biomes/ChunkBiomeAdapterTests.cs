using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Flora;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Spatial;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Pins <see cref="ChunkBiomeAdapter.Build"/>'s derivation of
  /// <see cref="ChunkBiomeInputs"/> from channel values + entity counts
  /// + planting marks. These are the per-chunk derivations the biome
  /// suitability targets read; previously they were exercised only by
  /// running the game with the Mod-side adapter wired up. Now testable
  /// after the Tier-2 audit relocated the adapter into Core behind the
  /// three needed ports (<see cref="IEcologyFieldQuery"/>,
  /// <see cref="IPlantingMarkQuery"/>, <see cref="INaturalResourceAtTileQuery"/>).
  /// </summary>
  [TestClass]
  public class ChunkBiomeAdapterTests {

    #region Fakes

    private sealed class FakeFieldQuery : IEcologyFieldQuery {

      private readonly Dictionary<RegionId, RegionEcologyField> _fields = new();
      private readonly Dictionary<string, int> _entityIndices = new();
      private readonly List<string> _knownBlueprints = new();

      public void RegisterBlueprint(string name) {
        if (_entityIndices.ContainsKey(name)) return;
        _entityIndices[name] = _knownBlueprints.Count;
        _knownBlueprints.Add(name);
      }

      public void SetField(RegionId region, RegionEcologyField field) {
        _fields[region] = field;
      }

      public RegionEcologyField? FieldFor(RegionId region) =>
          _fields.TryGetValue(region, out var f) ? f : null;

      public int? EntityIndex(string blueprintName) =>
          _entityIndices.TryGetValue(blueprintName, out var idx) ? idx : (int?)null;

      public IReadOnlyList<string> KnownEntityBlueprints => _knownBlueprints;

      /// <summary>Synthetic mature-trees channel index. Null by default
      /// (most tests don't exercise the mature gate); set explicitly in
      /// the mature-count test.</summary>
      public int? MatureTreeEntityIndex { get; set; }

      public int FieldShapeVersion { get; set; }

      public RegionTileData? TileDataFor(RegionId region) => null;

    }

    private sealed class FakeMarks : IPlantingMarkQuery {

      public List<(int X, int Y, int Z, string Species)> Marks { get; } = new();

      public bool IsMarked(int x, int y, int z) {
        foreach (var m in Marks) {
          if (m.X == x && m.Y == y && m.Z == z) return true;
        }
        return false;
      }

      public string MarkedSpecies(int x, int y, int z) {
        foreach (var m in Marks) {
          if (m.X == x && m.Y == y && m.Z == z) return m.Species;
        }
        return "";
      }

      public IEnumerable<(int X, int Y, int Z, string Species)> MarksInTileRect(
          int minX, int minY, int maxX, int maxY) {
        foreach (var m in Marks) {
          if (m.X < minX || m.X > maxX || m.Y < minY || m.Y > maxY) continue;
          yield return m;
        }
      }

    }

    private sealed class FakeNaturalResources : INaturalResourceAtTileQuery {

      public HashSet<(int X, int Y, int Z)> Occupied { get; } = new();

      public bool HasNaturalResourceAt(int x, int y, int z) =>
          Occupied.Contains((x, y, z));

    }

    private static FloraEntry FloraEntryOf(string blueprintName, FloraKind kind) {
      return new FloraEntry(
          blueprintName: blueprintName,
          templateName: blueprintName,
          faction: null,
          kind: kind,
          plantableGroups: Array.Empty<string>(),
          growthTimeInDays: null,
          daysToDieDry: null,
          minWaterHeight: null,
          maxWaterHeight: null,
          daysToDieFlooded: null,
          isCuttable: false,
          removeOnCut: null,
          isGatherable: false,
          yieldGrowthTimeInDays: null,
          cutYield: null,
          gatherYield: null);
    }

    /// <summary>Build a 1×1 field, register the given blueprints as
    /// entity channels in order, and write the given scalar values +
    /// per-entity counts into chunk (0, 0). Returns a configured
    /// adapter pointed at the resulting field.</summary>
    private (ChunkBiomeAdapter adapter, RegionEcologyField field,
             FakeFieldQuery fieldQuery, FakeMarks marks, FakeNaturalResources natural)
        Setup(
            float[] scalars,
            params (string blueprint, FloraKind kind, float count)[] entities) {
      var catalog = new FloraCatalog();
      var fieldQuery = new FakeFieldQuery();
      var marks = new FakeMarks();
      var natural = new FakeNaturalResources();

      var entries = new List<FloraEntry>();
      foreach (var (bp, kind, _) in entities) {
        entries.Add(FloraEntryOf(bp, kind));
        fieldQuery.RegisterBlueprint(bp);
      }
      catalog.Populate(entries);

      var field = new RegionEcologyField(
          originX: 0, originY: 0,
          chunksX: 1, chunksY: 1,
          entityChannelCount: entities.Length);
      var entityCounts = new float[entities.Length];
      for (var i = 0; i < entities.Length; i++) entityCounts[i] = entities[i].count;
      field.WriteChunk(0, 0, valid: true, sampleCount: 16,
          scalarValues: scalars, entityCounts: entityCounts);
      fieldQuery.SetField(new RegionId(1), field);

      var adapter = new ChunkBiomeAdapter(catalog, fieldQuery, marks, natural);
      return (adapter, field, fieldQuery, marks, natural);
    }

    /// <summary>Five-element array matching <see cref="EcologyChannel"/>
    /// ordinal order: WaterDepth, WaterFlowMagnitude, Moisture,
    /// Contamination, WaterContamination.</summary>
    private static float[] Scalars(
        float waterDepth = 0f,
        float waterFlow = 0f,
        float moisture = 0f,
        float contamination = 0f,
        float waterContamination = 0f) {
      return new[] {
          waterDepth, waterFlow, moisture, contamination,
          waterContamination,
      };
    }

    #endregion

    #region Water presence binary

    [TestMethod]
    public void Build_DryChunk_IrrigatedFollowsMoistureAndWaterFractionIsZero() {
      // No water; moisture 0.6 → irrigated 0.6, dry 0.4, water 0.
      var (adapter, field, _, _, _) = Setup(Scalars(moisture: 0.6f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0.6f, inputs.IrrigatedFraction, 1e-5f);
      Assert.AreEqual(0.4f, inputs.DryLandFraction, 1e-5f);
      Assert.AreEqual(0f, inputs.WaterFraction);
    }

    [TestMethod]
    public void Build_WaterBearingChunk_IrrigatedAndDryZeroedRegardlessOfMoisture() {
      // Once a chunk has water, irrigated/dry are forced to 0 — the
      // water axis takes over. A moisture reading of 0.9 must be
      // ignored in that case.
      var (adapter, field, _, _, _) = Setup(Scalars(waterDepth: 0.5f, moisture: 0.9f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0f, inputs.IrrigatedFraction,
          "Irrigated must be zeroed on water-bearing chunks.");
      Assert.AreEqual(0f, inputs.DryLandFraction,
          "Dry must be zeroed on water-bearing chunks.");
      Assert.AreEqual(1f, inputs.WaterFraction);
    }

    [TestMethod]
    public void Build_WaterPresenceThreshold_ExactlyAtOrBelowReadsAsLand() {
      // The threshold is 0.05f; the comparison is strict `>`, so
      // depth of exactly 0.05 reads as land.
      var (adapter, field, _, _, _) = Setup(Scalars(waterDepth: 0.05f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0f, inputs.WaterFraction);
    }

    [TestMethod]
    public void Build_WaterPresenceThreshold_JustAboveReadsAsWater() {
      var (adapter, field, _, _, _) = Setup(Scalars(waterDepth: 0.051f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(1f, inputs.WaterFraction);
    }

    #endregion

    #region Shallow/Deep × HighFlow split

    [TestMethod]
    public void Build_ShallowSlowWater_OnlyShallowSlowFractionIsOne() {
      // Depth 0.5 (≤ 1.0 → shallow), low flow.
      var (adapter, field, _, _, _) = Setup(Scalars(waterDepth: 0.5f, waterFlow: 0.05f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(1f, inputs.ShallowSlowWaterFraction);
      Assert.AreEqual(0f, inputs.ShallowHighFlowWaterFraction);
      Assert.AreEqual(0f, inputs.DeepSlowWaterFraction);
      Assert.AreEqual(0f, inputs.DeepHighFlowWaterFraction);
    }

    [TestMethod]
    public void Build_DeepHighFlowWater_OnlyDeepHighFlowFractionIsOne() {
      // Depth 2.0 (> 1.0 → deep), high flow 0.5.
      var (adapter, field, _, _, _) = Setup(Scalars(waterDepth: 2.0f, waterFlow: 0.5f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0f, inputs.ShallowSlowWaterFraction);
      Assert.AreEqual(0f, inputs.ShallowHighFlowWaterFraction);
      Assert.AreEqual(0f, inputs.DeepSlowWaterFraction);
      Assert.AreEqual(1f, inputs.DeepHighFlowWaterFraction);
    }

    [TestMethod]
    public void Build_DepthExactlyAtDeepThreshold_StaysShallow() {
      // DeepDepthThreshold is 1.0, comparison strict `>`. Depth 1.0 →
      // shallow (Wetland), not Lake.
      var (adapter, field, _, _, _) = Setup(Scalars(waterDepth: 1.0f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(1f, inputs.ShallowSlowWaterFraction);
      Assert.AreEqual(0f, inputs.DeepSlowWaterFraction);
    }

    [TestMethod]
    public void Build_FlowExactlyAtHighFlowThreshold_StaysSlow() {
      // HighFlowThreshold is 0.10, strict `>`. Flow 0.10 → slow.
      var (adapter, field, _, _, _) = Setup(Scalars(waterDepth: 0.5f, waterFlow: 0.10f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(1f, inputs.ShallowSlowWaterFraction);
      Assert.AreEqual(0f, inputs.ShallowHighFlowWaterFraction);
    }

    #endregion

    #region Contamination channels

    [TestMethod]
    public void Build_ContaminatedWater_PassesThroughDirectlyFromWaterChannel() {
      // The historical bug this guards against: ContaminatedWaterFraction
      // used to be derived as `contamFrac * hasWater`, which produced 0
      // on fresh badwater pools (water toxic but soil plume not yet
      // bloomed). The fix samples the dedicated WaterContamination
      // channel directly.
      var (adapter, field, _, _, _) = Setup(
          Scalars(waterDepth: 0.5f, contamination: 0f, waterContamination: 0.8f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0.8f, inputs.ContaminatedWaterFraction, 1e-5f);
    }

    [TestMethod]
    public void Build_SoilContamination_PassesThroughIndependentOfWater() {
      var (adapter, field, _, _, _) = Setup(Scalars(contamination: 0.4f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0.4f, inputs.ContaminatedFraction, 1e-5f);
    }

    #endregion

    #region Tree / plantable counts

    [TestMethod]
    public void Build_TreeCount_SumsAllTreeKindChannels() {
      var (adapter, field, _, _, _) = Setup(
          Scalars(),
          ("Birch", FloraKind.Tree, 3f),
          ("Maple", FloraKind.Tree, 2f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(5, inputs.TreeCount);
      Assert.AreEqual(2, inputs.TreeSpeciesCount);
    }

    [TestMethod]
    public void Build_MatureTreeCount_ReadFromMatureChannel() {
      // The adapter reads the producer's synthetic mature-trees
      // aggregate channel (resolved via MatureTreeEntityIndex) into
      // MatureTreeCount, and MatureTreeFraction divides by TreeCount.
      var catalog = new FloraCatalog();
      var fieldQuery = new FakeFieldQuery();
      var marks = new FakeMarks();
      var natural = new FakeNaturalResources();

      catalog.Populate(new List<FloraEntry> { FloraEntryOf("Birch", FloraKind.Tree) });
      fieldQuery.RegisterBlueprint("Birch");      // entity channel 0
      fieldQuery.MatureTreeEntityIndex = 1;       // synthetic aggregate channel

      // Two channels: [0] Birch live count = 8, [1] mature aggregate = 3.
      var field = new RegionEcologyField(
          originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 2);
      field.WriteChunk(0, 0, valid: true, sampleCount: 16,
          scalarValues: Scalars(moisture: 1f),
          entityCounts: new[] { 8f, 3f });
      fieldQuery.SetField(new RegionId(1), field);

      var adapter = new ChunkBiomeAdapter(catalog, fieldQuery, marks, natural);
      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(8, inputs.TreeCount);
      Assert.AreEqual(3, inputs.MatureTreeCount);
      Assert.AreEqual(3f / 8f, inputs.MatureTreeFraction, 1e-5f);
    }

    [TestMethod]
    public void Build_MatureTreeCount_ZeroWhenChannelUnregistered() {
      // When the producer hasn't registered the mature channel
      // (MatureTreeEntityIndex == null), MatureTreeCount reads 0 rather
      // than throwing -- the too-early window before PostLoad.
      var (adapter, field, _, _, _) = Setup(
          Scalars(),
          ("Birch", FloraKind.Tree, 5f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(5, inputs.TreeCount);
      Assert.AreEqual(0, inputs.MatureTreeCount);
      Assert.AreEqual(0f, inputs.MatureTreeFraction);
    }

    [TestMethod]
    public void Build_TreeCount_GroundCoverNotCountedAsTree() {
      var (adapter, field, _, _, _) = Setup(
          Scalars(),
          ("Birch", FloraKind.Tree, 3f),
          ("Grass", FloraKind.GroundCover, 100f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(3, inputs.TreeCount, "GroundCover never counts as a tree.");
      Assert.AreEqual(1, inputs.TreeSpeciesCount);
    }

    [TestMethod]
    public void Build_PlantableCount_IncludesTreeBushCropButNotGroundCover() {
      var (adapter, field, _, _, _) = Setup(
          Scalars(),
          ("Birch", FloraKind.Tree, 2f),
          ("BlueberryBush", FloraKind.Bush, 1f),
          ("Wheat", FloraKind.Crop, 3f),
          ("Grass", FloraKind.GroundCover, 50f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(6, inputs.PlantableCount,
          "Tree(2) + Bush(1) + Crop(3) — GroundCover excluded.");
      Assert.AreEqual(3, inputs.PlantableSpeciesCount);
    }

    #endregion

    #region Simpson dominance

    [TestMethod]
    public void Build_Dominance_SingleSpecies_IsOne() {
      var (adapter, field, _, _, _) = Setup(
          Scalars(),
          ("Birch", FloraKind.Tree, 10f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(1f, inputs.PlantableDominance, 1e-5f,
          "Single-species → Simpson D = 1.");
    }

    [TestMethod]
    public void Build_Dominance_TwoSpeciesEvenSplit_IsHalf() {
      var (adapter, field, _, _, _) = Setup(
          Scalars(),
          ("Birch", FloraKind.Tree, 5f),
          ("Maple", FloraKind.Tree, 5f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0.5f, inputs.PlantableDominance, 1e-5f,
          "Two species at equal counts → Simpson D = 0.5 (1/N).");
    }

    [TestMethod]
    public void Build_Dominance_FourSpeciesEvenSplit_IsQuarter() {
      var (adapter, field, _, _, _) = Setup(
          Scalars(),
          ("A", FloraKind.Tree, 4f),
          ("B", FloraKind.Tree, 4f),
          ("C", FloraKind.Tree, 4f),
          ("D", FloraKind.Tree, 4f));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0.25f, inputs.PlantableDominance, 1e-5f);
    }

    [TestMethod]
    public void Build_Dominance_NoPlantables_IsZero() {
      // Defensive — when there's nothing to count, Simpson D is 0
      // (downstream Monoculture multiplies by saturation which is
      // also 0, so the value doesn't matter, but the formula must
      // not divide by zero).
      var (adapter, field, _, _, _) = Setup(Scalars());

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0f, inputs.PlantableDominance);
    }

    #endregion

    #region Mark / entity dedup

    [TestMethod]
    public void Build_MarkOnEmptyTile_CountsAsPlantable() {
      var (adapter, field, _, marks, _) = Setup(Scalars());
      marks.Marks.Add((2, 3, 0, "Birch"));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(1, inputs.PlantableCount);
      Assert.AreEqual(1, inputs.PlantableSpeciesCount);
    }

    [TestMethod]
    public void Build_MarkOnTileWithExistingEntity_NotDoubleCounted() {
      // Bug being guarded against: a mark and a realised entity on
      // the same tile must count as one, not two. The adapter
      // consults INaturalResourceAtTileQuery to dedup.
      var (adapter, field, _, marks, natural) = Setup(
          Scalars(),
          ("Birch", FloraKind.Tree, 1f));
      marks.Marks.Add((2, 3, 0, "Birch"));
      natural.Occupied.Add((2, 3, 0));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(1, inputs.PlantableCount,
          "Mark on a tile that already has a natural-resource entity must not double-count.");
    }

    [TestMethod]
    public void Build_MarkOutsideChunkBounds_NotCounted() {
      // 1x1 field starts at (0, 0); chunk 0,0 covers tiles 0..3 in X
      // and 0..3 in Y. A mark at tile (10, 10) is outside.
      var (adapter, field, _, marks, _) = Setup(Scalars());
      marks.Marks.Add((10, 10, 0, "Birch"));

      var inputs = adapter.Build(field, 0, 0);

      Assert.AreEqual(0, inputs.PlantableCount);
    }

    #endregion

  }

}
