using System;
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
  /// Drives <see cref="RegionService.ProcessChanges"/> through scripted
  /// terrain mutations to verify the incremental detach/attach/merge/split
  /// logic. The harness mutates a <see cref="FakeTerrain"/>, calls
  /// <c>TerrainSurveyor.ResurveyColumn</c> on affected columns, and feeds
  /// the resulting diff into the service.
  /// </summary>
  [TestClass]
  public class RegionServiceIncrementalTests {

    #region Simple add / remove / IsCave flip

    [TestMethod]
    public void ProcessChanges_AddSurfaceAdjacentToRegion_JoinsThatRegion() {
      // Arrange — flat 2x1 plateau at z=5; no surface yet at (2, 0).
      var terrain = new FakeTerrain(width: 3, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      Assert.AreEqual(1, regions.Count);
      var originalRegionId = regions.All.Single().Id;

      // Act — extend with a new surface at (2, 0, 5).
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert
      Assert.AreEqual(1, regions.Count);
      var region = regions.All.Single();
      Assert.AreEqual(originalRegionId, region.Id, "ID stable across additive change");
      Assert.AreEqual(3, region.Size);
      Assert.AreEqual(originalRegionId, regions.Containing(new SurfaceCoord(2, 0, 5))!.Id);
    }

    [TestMethod]
    public void ProcessChanges_RemoveSurfaceFromRegionEdge_ShrinksRegionNoSplit() {
      // Arrange — flat 3x1 plateau.
      var terrain = new FakeTerrain(width: 3, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var originalRegionId = regions.All.Single().Id;

      // Act — remove the rightmost surface (no Z=5 in column 2 anymore; survey will report empty heights).
      terrain.Heights.Remove(new TileCoord(2, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — same region, smaller, no split.
      Assert.AreEqual(1, regions.Count);
      var region = regions.All.Single();
      Assert.AreEqual(originalRegionId, region.Id);
      Assert.AreEqual(2, region.Size);
      Assert.IsNull(regions.Containing(new SurfaceCoord(2, 0, 5)));
    }

    [TestMethod]
    public void ProcessChanges_RemoveOnlySurface_DeletesRegion() {
      // Arrange — single-tile region.
      var terrain = new FakeTerrain(width: 1, height: 1) {
          Heights = { [new TileCoord(0, 0)] = new[] { 5 } },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      Assert.AreEqual(1, regions.Count);

      // Act
      terrain.Heights.Remove(new TileCoord(0, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert
      Assert.AreEqual(0, regions.Count);
    }

    [TestMethod]
    public void ProcessChanges_IsCaveFlip_LeavesOldRegionAndJoinsNew() {
      // Arrange — single open surface at (0, 0, 5).
      var terrain = new FakeTerrain(width: 1, height: 1) {
          Heights = { [new TileCoord(0, 0)] = new[] { 5 } },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var openRegion = regions.All.Single();
      Assert.IsFalse(openRegion.IsCave);

      // Act — stack a new surface above; the original (0,0,5) becomes a cave floor.
      terrain.Heights[new TileCoord(0, 0)] = new[] { 5, 8 };
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — the original open region is gone (its only member became a cave); two new regions exist.
      Assert.AreEqual(2, regions.Count);
      var caveRegion = regions.All.Single(r => r.Z == 5);
      var topRegion = regions.All.Single(r => r.Z == 8);
      Assert.IsTrue(caveRegion.IsCave);
      Assert.IsFalse(topRegion.IsCave);
      Assert.AreEqual(1, caveRegion.Size);
      Assert.AreEqual(1, topRegion.Size);
      Assert.AreNotEqual(openRegion.Id, caveRegion.Id, "IsCave flip detaches and re-attaches; new region id");
    }

    #endregion

    #region Merge

    [TestMethod]
    public void ProcessChanges_BridgeTwoRegions_MergesIntoOne() {
      // Arrange — two plateaus separated by a single-column gap at lower Z.
      // (0,0,5)(1,0,5) | (2,0,4) | (3,0,5)(4,0,5)
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 4 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      Assert.AreEqual(3, regions.Count); // two z=5 plateaus + one z=4 island

      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;
      Assert.AreNotEqual(leftId, rightId);

      // Act — raise (2, 0) to z=5, bridging the two plateaus.
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — one big z=5 region (the lower z=4 island is gone since (2,0,4) detached).
      Assert.AreEqual(1, regions.Count);
      var merged = regions.All.Single();
      Assert.AreEqual(5, merged.Z);
      Assert.AreEqual(5, merged.Size);
      // Survivor policy: largest (tied at 2) wins, ties broken by lowest id.
      var expectedSurvivor = leftId.Value < rightId.Value ? leftId : rightId;
      Assert.AreEqual(expectedSurvivor, merged.Id, "merge survivor is the lower-id region (tie on size)");
    }

    [TestMethod]
    public void ProcessChanges_BridgeJoinsThreeRegions_AllMerge() {
      // Arrange — three plateaus around a single empty cell.
      //   .X.
      //   X.X
      //   .X.   (X = surface at z=5; . = no z=5 surface; everything else is fluff)
      // Plus the gap-filling column at (1,1) is at z=4.
      var terrain = new FakeTerrain(width: 3, height: 3) {
          Heights = {
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(0, 1)] = new[] { 5 },
              [new TileCoord(1, 1)] = new[] { 4 },
              [new TileCoord(2, 1)] = new[] { 5 },
              [new TileCoord(1, 2)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      // Each z=5 surface is alone (4 separate single-surface regions) + 1 z=4 region = 5 regions.
      Assert.AreEqual(5, regions.Count);

      // Act — raise (1, 1) to z=5, joining all four neighbors.
      terrain.Heights[new TileCoord(1, 1)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 1));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — one z=5 region of size 5; z=4 island is gone.
      Assert.AreEqual(1, regions.Count);
      var merged = regions.All.Single();
      Assert.AreEqual(5, merged.Z);
      Assert.AreEqual(5, merged.Size);
    }

    #endregion

    #region Split

    [TestMethod]
    public void ProcessChanges_RemoveBisectingSurface_SplitsRegionIntoTwo() {
      // Arrange — linear 3x1 plateau, all in one region.
      var terrain = new FakeTerrain(width: 3, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var originalId = regions.All.Single().Id;
      var originalCreated = regions.All.Single().CreatedAt;

      // Act — drop the middle column to z=4, breaking the line.
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — two z=5 regions plus one z=4 region.
      var atFiveRegions = regions.All.Where(r => r.Z == 5).ToList();
      Assert.AreEqual(2, atFiveRegions.Count, "region at z=5 split in two");
      Assert.IsTrue(atFiveRegions.Any(r => r.Id == originalId), "original ID preserved on one side");
      var newSide = atFiveRegions.Single(r => r.Id != originalId);
      Assert.AreEqual(originalCreated, newSide.CreatedAt, "split inherits parent's CreatedAt");
      Assert.AreEqual(1, atFiveRegions[0].Size + atFiveRegions[1].Size - 1); // 1 + 1 = 2 total at z=5
      Assert.AreEqual(2, atFiveRegions.Sum(r => r.Size));
    }

    [TestMethod]
    public void ProcessChanges_RemoveTileFromMiddleOfBigRegion_NoSplitWhenNotBisecting() {
      // Arrange — 3x3 flat plateau (all 9 surfaces in one region).
      var terrain = new FakeTerrain(width: 3, height: 3);
      for (var x = 0; x < 3; x++) {
        for (var y = 0; y < 3; y++) {
          terrain.Heights[new TileCoord(x, y)] = new[] { 5 };
        }
      }
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var originalId = regions.All.Single(r => r.Z == 5).Id;

      // Act — drop the center tile (1,1).
      terrain.Heights[new TileCoord(1, 1)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 1));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — z=5 region still single (donut shape is connected through the rim) plus a new z=4 island.
      var atFive = regions.All.Where(r => r.Z == 5).ToList();
      Assert.AreEqual(1, atFive.Count, "donut is still one region");
      Assert.AreEqual(originalId, atFive[0].Id);
      Assert.AreEqual(8, atFive[0].Size);
    }

    #endregion

    #region Building (IsSettled) flips

    [TestMethod]
    public void ProcessChanges_BuildingPlacedMidPlateau_SplitsOutSettledSubregion() {
      // Arrange — 5x1 plateau at z=5, no buildings yet. Width chosen so
      // that the building's 1-step halo doesn't reach the edges -- with
      // a 3-wide plateau the entire surface ends up in the halo and no
      // split occurs.
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var building = new MutableFakeBuilding();
      var (surveyor, regions) = SetupWithBuilding(terrain, building);
      regions.Index();
      Assert.AreEqual(1, regions.Count);

      // Act — place a building at the centre column's surface voxel (2, 0, 5).
      // The Settled halo covers (1, 0), (2, 0), (3, 0); (0, 0) and (4, 0)
      // remain unsettled. Region splits into {left}, {settled centre},
      // {right}. Resurvey the building's column AND its halo neighbours --
      // the production RegionUpdater dirties the same set on a BO event
      // because the halo rule means neighbours' IsSettled also flips.
      building.Voxels[new SurfaceCoord(2, 0, 5)] = Keystone.Core.Buildings.BuildingKind.Building;
      ResurveyAndApply(surveyor, regions, new TileCoord(1, 0), new TileCoord(2, 0), new TileCoord(3, 0));

      // Assert
      Assert.AreEqual(3, regions.Count, "settled centre splits the plateau");
      Assert.AreEqual(3, regions.All.Single(r => r.IsSettled).Size, "settled region is the building + its halo");
      Assert.AreEqual(2, regions.All.Where(r => !r.IsSettled).Sum(r => r.Size), "two unsettled edges, one tile each");
    }

    [TestMethod]
    public void ProcessChanges_BuildingDemolished_RemergesIntoNaturalPlateau() {
      // Arrange — 5x1 plateau with a building above the centre column.
      // (Width 5 so the halo doesn't engulf the whole plateau -- see the
      // sibling test for the same reasoning.)
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var building = new MutableFakeBuilding();
      building.Voxels[new SurfaceCoord(2, 0, 5)] = Keystone.Core.Buildings.BuildingKind.Building;
      var (surveyor, regions) = SetupWithBuilding(terrain, building);
      regions.Index();
      Assert.AreEqual(3, regions.Count);

      // Act — demolish the building. The centre voxel flips IsSettled
      // false, the halo neighbours follow, and the three regions merge
      // back into a single natural plateau. Resurvey the centre column
      // plus its halo (mirrors the production RegionUpdater).
      building.Voxels.Remove(new SurfaceCoord(2, 0, 5));
      ResurveyAndApply(surveyor, regions, new TileCoord(1, 0), new TileCoord(2, 0), new TileCoord(3, 0));

      // Assert
      Assert.AreEqual(1, regions.Count, "merge back into single natural plateau");
      var merged = regions.All.Single();
      Assert.AreEqual(5, merged.Size);
      Assert.IsFalse(merged.IsSettled);
    }

    #endregion

    #region Stable IDs

    [TestMethod]
    public void StableIds_UnaffectedRegionsKeepTheirIds() {
      // Arrange — two disjoint plateaus at different Zs.
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 1 },
              [new TileCoord(2, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 9 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var idAt5 = regions.Containing(new SurfaceCoord(2, 0, 5))!.Id;
      var idAt9 = regions.Containing(new SurfaceCoord(4, 0, 9))!.Id;

      // Act — modify the z=1 region (remove its single surface).
      terrain.Heights.Remove(new TileCoord(0, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — the other two regions kept their IDs.
      Assert.AreEqual(idAt5, regions.Containing(new SurfaceCoord(2, 0, 5))!.Id);
      Assert.AreEqual(idAt9, regions.Containing(new SurfaceCoord(4, 0, 9))!.Id);
    }

    #endregion

    #region Setup + fakes (mostly shared shape with RegionServiceTests; kept local for cohesion)

    /// <summary>
    /// Resurvey each of <paramref name="columns"/> and feed the
    /// concatenated diff into <paramref name="regions"/> in one
    /// <c>ProcessChanges</c> call -- mirroring how the production
    /// <c>RegionUpdater</c> batches a flush across all dirty columns.
    /// Tests use this when a single mutation invalidates more than one
    /// column (e.g., the halo neighbours of a placed/demolished building).
    /// </summary>
    private static void ResurveyAndApply(
        TerrainSurveyor surveyor, RegionService regions, params TileCoord[] columns) {
      var detached = new List<SurfaceCoord>();
      var attached = new List<SurfaceCoord>();
      foreach (var col in columns) {
        var diff = surveyor.ResurveyColumn(col);
        detached.AddRange(diff.Detached);
        attached.AddRange(diff.Attached);
      }
      regions.ProcessChanges(detached, attached);
    }

    private static (TerrainSurveyor surveyor, RegionService regions) Setup(FakeTerrain terrain) {
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, new FakeClock());
      return (surveyor, regions);
    }

    private static (TerrainSurveyor surveyor, RegionService regions) SetupWithBuilding(
        FakeTerrain terrain, MutableFakeBuilding building) {
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, new FakeClock());
      return (surveyor, regions);
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
        if (!Heights.TryGetValue(column, out var list)) {
          return Array.Empty<int>();
        }
        var sorted = (int[])list.Clone();
        Array.Sort(sorted);
        return sorted;
      }

      public bool HasTerrainAbove(SurfaceCoord surface) {
        if (!Heights.TryGetValue(surface.Column, out var list)) {
          return false;
        }
        for (var i = 0; i < list.Length; i++) {
          if (list[i] > surface.Z) {
            return true;
          }
        }
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

    /// <summary>Mutable fake so tests can simulate building placement/demolition between Resurvey calls.</summary>
    private sealed class MutableFakeBuilding : IBuildingQuery {
      public Dictionary<SurfaceCoord, Keystone.Core.Buildings.BuildingKind> Voxels { get; } = new();
      public Keystone.Core.Buildings.BuildingKind ClassifyAt(SurfaceCoord voxel) =>
          Voxels.TryGetValue(voxel, out var k) ? k : Keystone.Core.Buildings.BuildingKind.None;
    }

    private sealed class FakeClock : IClock {
      public GameTimestamp Now { get; set; } = GameTimestamp.Origin;
      public WeatherKind CurrentWeather { get; set; } = WeatherKind.Temperate;
      public float TotalDaysElapsed { get; set; }
    }

    #endregion

  }

}
