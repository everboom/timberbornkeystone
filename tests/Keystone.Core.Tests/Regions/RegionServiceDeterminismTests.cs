using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Core.Tests.Helpers;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Regions {

  /// <summary>
  /// Pins down the determinism contract <see cref="RegionService"/>
  /// promises: identical terrain produces identical region ids, and
  /// identical incremental-edit batches produce the same id allocations.
  /// This is what lets save/load attach state to specific
  /// <see cref="RegionId"/>s and have them rebind correctly across
  /// runs and machines.
  /// </summary>
  [TestClass]
  public class RegionServiceDeterminismTests {

    #region Index seed order

    [TestMethod]
    public void Index_AssignsIdsInSortedSeedOrder() {
      // Three disjoint single-voxel regions in non-sorted insertion
      // order. After Index, ids must be assigned in (X, Y, Z) seed
      // order regardless of which order the terrain dict was built.
      var terrain = new FakeTerrain(width: 8, height: 1);
      // Insert deliberately out of order to defeat any "happens to be
      // sorted because Dictionary insertion order matches" coincidence.
      terrain.Heights[new TileCoord(7, 0)] = new[] { 5 };
      terrain.Heights[new TileCoord(3, 0)] = new[] { 5 };
      terrain.Heights[new TileCoord(5, 0)] = new[] { 5 };
      var (_, regions) = Setup(terrain);

      regions.Index();

      Assert.AreEqual(3, regions.Count);
      Assert.AreEqual(new RegionId(0), regions.Containing(new SurfaceCoord(3, 0, 5))!.Id,
          "lowest-sorting surface (3,0,5) must seed RegionId 0");
      Assert.AreEqual(new RegionId(1), regions.Containing(new SurfaceCoord(5, 0, 5))!.Id);
      Assert.AreEqual(new RegionId(2), regions.Containing(new SurfaceCoord(7, 0, 5))!.Id);
    }

    [TestMethod]
    public void Index_TwoTerrainsBuiltInDifferentOrders_ProduceSameIds() {
      // Build the same terrain content into two separate FakeTerrain
      // instances, inserting columns in opposite orders. After Index,
      // every common surface must map to the same RegionId.
      var coords = new[] {
          (new TileCoord(0, 0), 5),
          (new TileCoord(2, 0), 5),
          (new TileCoord(0, 2), 5),
          (new TileCoord(2, 2), 5),
          (new TileCoord(1, 1), 4),
      };

      var forward = new FakeTerrain(width: 3, height: 3);
      foreach (var (col, z) in coords) forward.Heights[col] = new[] { z };

      var backward = new FakeTerrain(width: 3, height: 3);
      foreach (var (col, z) in coords.Reverse()) backward.Heights[col] = new[] { z };

      var (_, regionsF) = Setup(forward);
      var (_, regionsB) = Setup(backward);
      regionsF.Index();
      regionsB.Index();

      Assert.AreEqual(regionsF.Count, regionsB.Count);
      foreach (var (col, z) in coords) {
        var sf = new SurfaceCoord(col.X, col.Y, z);
        var idF = regionsF.Containing(sf)!.Id;
        var idB = regionsB.Containing(sf)!.Id;
        Assert.AreEqual(idF, idB, $"surface {sf} must get the same id regardless of terrain build order");
      }
    }

    #endregion

    #region Incremental updates

    [TestMethod]
    public void ProcessChanges_AttachOrderIndependent_ProducesSameIds() {
      // Same starting state, two different attach orders: result must
      // be identical id assignment for the new regions.
      var coords = new[] {
          new TileCoord(1, 0),
          new TileCoord(3, 0),
          new TileCoord(5, 0),
      };

      var (terrainA, surveyorA, regionsA) = SetupEmpty(width: 7, height: 1);
      var (terrainB, surveyorB, regionsB) = SetupEmpty(width: 7, height: 1);
      regionsA.Index();
      regionsB.Index();
      Assert.AreEqual(0, regionsA.Count);
      Assert.AreEqual(0, regionsB.Count);

      // Bring three new disjoint single-voxel surfaces into existence
      // in opposite orders. Each ResurveyColumn returns its own diff;
      // we concatenate them into one ProcessChanges call so the order
      // of the concatenation is what we're varying.
      var diffsA = new List<ColumnDiff>();
      foreach (var c in coords) {
        terrainA.Heights[c] = new[] { 5 };
        diffsA.Add(surveyorA.ResurveyColumn(c));
      }
      var diffsB = new List<ColumnDiff>();
      foreach (var c in coords.Reverse()) {
        terrainB.Heights[c] = new[] { 5 };
        diffsB.Add(surveyorB.ResurveyColumn(c));
      }

      regionsA.ProcessChanges(
          diffsA.SelectMany(d => d.Detached).ToList(),
          diffsA.SelectMany(d => d.Attached).ToList());
      regionsB.ProcessChanges(
          diffsB.SelectMany(d => d.Detached).ToList(),
          diffsB.SelectMany(d => d.Attached).ToList());

      Assert.AreEqual(3, regionsA.Count);
      Assert.AreEqual(3, regionsB.Count);
      foreach (var col in coords) {
        var sf = new SurfaceCoord(col.X, col.Y, 5);
        Assert.AreEqual(regionsA.Containing(sf)!.Id, regionsB.Containing(sf)!.Id,
            $"surface {sf} must get the same id regardless of attach batch order");
      }
    }

    [TestMethod]
    public void Split_KeptIdAssignedToComponentContainingLowestSurface() {
      // Linear 3x1 strip; remove the middle to split. The piece
      // containing the lowest-sorting surface must keep the parent id;
      // the other piece gets a fresh id. This pins down the
      // FindAnyMember min-by-(X,Y,Z) selection that drives split
      // determinism.
      var terrain = new FakeTerrain(width: 3, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var parentId = regions.All.Single(r => r.Z == 5).Id;

      // Drop the middle to z=4, splitting the z=5 strip into
      // {(0,0,5)} and {(2,0,5)}.
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(2, 0, 5))!.Id;
      Assert.AreEqual(parentId, leftId,
          "kept-id piece must contain the lowest-sorting surface (0,0,5)");
      Assert.AreNotEqual(parentId, rightId,
          "the other split piece must get a fresh id");
    }

    #endregion

    #region Canonical-id remapping and representative surfaces

    // These two tests pin `RegionService` properties (the canonical-id
    // map produced after a ProcessChanges + diverging-from-Index() state,
    // and the per-region representative-surface selection). They used to
    // live in `PersistenceIntegrationTests.cs` wrapped in snapshot
    // encode/decode scaffolding; the actual assertions never needed any
    // of that. Relocated per the testability audit so failures here
    // localise to RegionService rather than looking like persistence
    // bugs.

    [TestMethod]
    public void ComputeRepresentativeSurfaces_PicksMinSortedMemberPerRegion() {
      // A 3x1 plateau split into two by dropping column 1. Each piece
      // should have its representative pinned to its lowest-sort-order
      // surface.
      var (terrain, surveyor, regions) = SetupEmpty(width: 3, height: 1);
      terrain.Heights[new TileCoord(0, 0)] = new[] { 5 };
      terrain.Heights[new TileCoord(1, 0)] = new[] { 5 };
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      surveyor.Survey();
      regions.Index();

      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      var reps = regions.ComputeRepresentativeSurfaces();

      var liveA = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var liveB = regions.Containing(new SurfaceCoord(1, 0, 4))!.Id;
      var liveC = regions.Containing(new SurfaceCoord(2, 0, 5))!.Id;

      Assert.AreEqual(new SurfaceCoord(0, 0, 5), reps[liveA],
          "A's representative is its only member, (0,0,5)");
      Assert.AreEqual(new SurfaceCoord(1, 0, 4), reps[liveB],
          "B's representative is its only member at z=4");
      Assert.AreEqual(new SurfaceCoord(2, 0, 5), reps[liveC],
          "C's representative is its only member, (2,0,5)");
    }

    [TestMethod]
    public void ComputeCanonicalIdMap_AfterProcessChanges_RemapsToIndexOutput() {
      // Arrange a state where ProcessChanges leaves live IDs that
      // disagree with a fresh Index(). 3x1 plateau, drop column 0 to
      // z=4: ProcessChanges detaches A's old surface from region 0,
      // attaches A's new (z=4) surface as a fresh region (id 1). After
      // that, the z=5 piece {B, C} keeps id 0, A's z=4 surface gets id
      // 1. A fresh Index() on the final terrain would do the opposite:
      // (0,0,4) is sort-first, so it gets id 0; {B, C} at (1,0,5)+
      // (2,0,5) get id 1. Canonical map should be {0->1, 1->0}.
      var (terrain, surveyor, regions) = SetupEmpty(width: 3, height: 1);
      terrain.Heights[new TileCoord(0, 0)] = new[] { 5 };
      terrain.Heights[new TileCoord(1, 0)] = new[] { 5 };
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      surveyor.Survey();
      regions.Index();

      terrain.Heights[new TileCoord(0, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      var liveA = regions.Containing(new SurfaceCoord(0, 0, 4))!.Id;
      var liveBC = regions.Containing(new SurfaceCoord(1, 0, 5))!.Id;
      Assert.AreEqual(new RegionId(1), liveA, "ProcessChanges allocates fresh id 1 for the new z=4 region");
      Assert.AreEqual(new RegionId(0), liveBC, "remaining z=5 piece keeps the original id 0");

      var canonical = regions.ComputeCanonicalIdMap();

      // Canonical IDs are reassigned by sorted-seed-coord order on the
      // final terrain: A=(0,0,4) seeds first, {B,C} seeds at (1,0,5) second.
      Assert.AreEqual(new RegionId(0), canonical[liveA],
          "A's live id 1 should remap to canonical 0 (its surface sorts first)");
      Assert.AreEqual(new RegionId(1), canonical[liveBC],
          "BC's live id 0 should remap to canonical 1 (its surface sorts second)");
    }

    #endregion

    #region Setup + fakes

    private static (TerrainSurveyor surveyor, RegionService regions) Setup(FakeTerrain terrain) {
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, new FakeClock());
      return (surveyor, regions);
    }

    private static (FakeTerrain terrain, TerrainSurveyor surveyor, RegionService regions) SetupEmpty(int width, int height) {
      var terrain = new FakeTerrain(width, height);
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, new FakeClock());
      return (terrain, surveyor, regions);
    }

    private sealed class FakeTerrain : ITerrainQuery {
      public FakeTerrain(int width, int height) {
        Width = width;
        Height = height;
      }

      public int Width { get; }
      public int Height { get; }
      public int MaxHeight { get; set; } = 16;
      public Dictionary<TileCoord, int[]> Heights { get; } = new();

      public bool Contains(TileCoord column) =>
          column.X >= 0 && column.X < Width && column.Y >= 0 && column.Y < Height;

      public IReadOnlyList<int> SurfaceHeightsAt(TileCoord column) {
        if (!Heights.TryGetValue(column, out var list)) return System.Array.Empty<int>();
        var sorted = (int[])list.Clone();
        System.Array.Sort(sorted);
        return sorted;
      }

      public bool HasTerrainAbove(SurfaceCoord surface) {
        if (!Heights.TryGetValue(surface.Column, out var list)) return false;
        for (var i = 0; i < list.Length; i++) if (list[i] > surface.Z) return true;
        return false;
      }

      public bool IsTerrainVoxel(int x, int y, int z) => false;
    }

    private sealed class FakeMoisture : IMoistureQuery {
      public float MoistureAt(TileCoord column) => 0f;
      public bool IsMoistAt(SurfaceCoord surface) => false;
      public static FakeMoisture UniformDry() => new();
    }

    private sealed class FakeContamination : IContaminationQuery {
      public float ContaminationAt(TileCoord column) => 0f;
      public bool IsContaminatedAt(SurfaceCoord surface) => false;
      public static FakeContamination None() => new();
    }

    private sealed class FakeWater : IWaterQuery {
      public float WaterDepthAt(SurfaceCoord surface) => 0f;
      public float WaterSurfaceHeightAt(SurfaceCoord surface) => 0f;
      public FlowVector FlowAt(SurfaceCoord surface) => FlowVector.Zero;
      public bool HasWaterAtColumn(TileCoord column) => false;
      public float WaterContaminationAt(SurfaceCoord _) => 0f;
      public static FakeWater None() => new();
    }

    private sealed class FakeBuilding : IBuildingQuery {
      public Keystone.Core.Buildings.BuildingKind ClassifyAt(SurfaceCoord voxel) =>
          Keystone.Core.Buildings.BuildingKind.None;
      public static FakeBuilding NothingBuilt() => new();
    }

    private sealed class FakeClock : IClock {
      public GameTimestamp Now { get; set; } = GameTimestamp.Origin;
      public WeatherKind CurrentWeather { get; set; } = WeatherKind.Temperate;
      public float TotalDaysElapsed { get; set; }
    }

    #endregion

  }

}
