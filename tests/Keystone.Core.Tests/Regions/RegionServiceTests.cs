using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Ports;
using Keystone.Core.Tests.Helpers;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Regions {

  /// <summary>
  /// Drives <see cref="RegionService"/> against hand-built terrain via a
  /// real <see cref="TerrainSurveyor"/> and verifies indexing behavior.
  /// </summary>
  [TestClass]
  public class RegionServiceTests {

    #region Indexing

    [TestMethod]
    public void Index_FlatMap_ProducesOneRegion() {
      // Arrange — 3x3 flat plateau at z=4.
      var (_, regions) = Setup(FakeTerrain.Flat(width: 3, height: 3, surfaceZ: 4));

      // Act
      regions.Index();

      // Assert
      Assert.AreEqual(1, regions.Count);
      var only = regions.All.Single();
      Assert.AreEqual(9, only.Size);
      Assert.AreEqual(4, only.Z);
      Assert.IsFalse(only.IsCave);
    }

    [TestMethod]
    public void Index_HeightStep_ProducesTwoRegions() {
      // Arrange — left half at z=4, right half at z=6.
      var terrain = new FakeTerrain(width: 4, height: 2);
      for (var x = 0; x < 4; x++) {
        for (var y = 0; y < 2; y++) {
          terrain.Heights[new TileCoord(x, y)] = new[] { x < 2 ? 4 : 6 };
        }
      }
      var (_, regions) = Setup(terrain);

      // Act
      regions.Index();

      // Assert
      Assert.AreEqual(2, regions.Count);
      var left = regions.All.Single(r => r.Z == 4);
      var right = regions.All.Single(r => r.Z == 6);
      Assert.AreEqual(4, left.Size);
      Assert.AreEqual(4, right.Size);
    }

    [TestMethod]
    public void Index_StackedColumns_SplitsByZAndCave() {
      // Arrange — 3x1, every column has surfaces at z=2 and z=8.
      // The z=2 layer is uniformly cave, z=8 layer is uniformly non-cave.
      var terrain = new FakeTerrain(width: 3, height: 1);
      for (var x = 0; x < 3; x++) {
        terrain.Heights[new TileCoord(x, 0)] = new[] { 2, 8 };
      }
      var (_, regions) = Setup(terrain);

      // Act
      regions.Index();

      // Assert — exactly two regions: cave floor and open top.
      Assert.AreEqual(2, regions.Count);
      var caveFloor = regions.All.Single(r => r.Z == 2);
      var openTop = regions.All.Single(r => r.Z == 8);
      Assert.IsTrue(caveFloor.IsCave);
      Assert.IsFalse(openTop.IsCave);
      Assert.AreEqual(3, caveFloor.Size);
      Assert.AreEqual(3, openTop.Size);
    }

    [TestMethod]
    public void Index_TripleStackColumn_ProducesThreeRegions() {
      // Arrange — single column with three stacked surfaces at z=1, z=4, z=7.
      // Both the bottom and middle surface have terrain above them, so both are caves;
      // the top surface is open. They're all different Zs, so three regions regardless.
      var terrain = new FakeTerrain(width: 1, height: 1) {
          Heights = { [new TileCoord(0, 0)] = new[] { 1, 4, 7 } },
      };
      var (surveyor, regions) = Setup(terrain);

      // Act
      regions.Index();

      // Assert
      Assert.AreEqual(3, regions.Count);
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 1), out var bottom));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 4), out var middle));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 7), out var top));
      Assert.IsTrue(bottom.IsCave);
      Assert.IsTrue(middle.IsCave);
      Assert.IsFalse(top.IsCave);
      foreach (var r in regions.All) {
        Assert.AreEqual(1, r.Size);
      }
    }

    [TestMethod]
    public void Index_CaveAdjacentToOpenAtSameZ_ProducesTwoRegions() {
      // Arrange — 3x1 row at z=5, middle column has terrain above so its
      // z=5 surface is cave, others are open. Three regions: open-left,
      // cave-middle, open-right.
      var terrain = new FakeTerrain(width: 3, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5, 9 },  // stacked → z=5 is cave
              [new TileCoord(2, 0)] = new[] { 5 },
          },
      };
      var (_, regions) = Setup(terrain);

      // Act
      regions.Index();

      // Assert — open-left (1 surface), cave-middle (1 at z=5), open-middle-top (1 at z=9), open-right (1 at z=5).
      Assert.AreEqual(4, regions.Count);
      var byZAndCave = regions.All.GroupBy(r => (r.Z, r.IsCave))
          .ToDictionary(g => g.Key, g => g.Count());
      // Two distinct open-z=5 regions (split by the cave between them).
      Assert.AreEqual(2, byZAndCave[(5, false)]);
      // One cave-z=5 region.
      Assert.AreEqual(1, byZAndCave[(5, true)]);
      // One open-z=9 region (the top of the stacked column).
      Assert.AreEqual(1, byZAndCave[(9, false)]);
    }

    [TestMethod]
    public void Index_Diagonally_NotConnected() {
      // Arrange — a checker pattern: surfaces at (0,0), (1,1), (2,2). 4-connected, so each is its own region.
      var terrain = new FakeTerrain(width: 3, height: 3) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 4 },
              [new TileCoord(1, 1)] = new[] { 4 },
              [new TileCoord(2, 2)] = new[] { 4 },
          },
      };
      var (_, regions) = Setup(terrain);

      // Act
      regions.Index();

      // Assert
      Assert.AreEqual(3, regions.Count);
      foreach (var r in regions.All) {
        Assert.AreEqual(1, r.Size);
      }
    }

    [TestMethod]
    public void Index_Twice_ReproducesSameRegions() {
      // Arrange
      var (_, regions) = Setup(FakeTerrain.Flat(width: 2, height: 2, surfaceZ: 3));

      // Act
      regions.Index();
      var firstCount = regions.Count;
      regions.Index();
      var secondCount = regions.Count;

      // Assert — count is stable; ids reset on each pass (this is documented behavior, not stability).
      Assert.AreEqual(firstCount, secondCount);
      Assert.AreEqual(1, secondCount);
    }

    #endregion

    #region Identity

    [TestMethod]
    public void RegionIds_AreUnique() {
      // Arrange — three disjoint plateaus.
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 1 },
              [new TileCoord(2, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 9 },
          },
      };
      var (_, regions) = Setup(terrain);

      // Act
      regions.Index();

      // Assert
      var ids = regions.All.Select(r => r.Id).ToList();
      Assert.AreEqual(ids.Count, ids.Distinct().Count());
    }

    [TestMethod]
    public void Containing_ReturnsRegionForSurveyedSurface() {
      // Arrange
      var (_, regions) = Setup(FakeTerrain.Flat(width: 2, height: 2, surfaceZ: 3));
      regions.Index();

      // Act
      var r = regions.Containing(new SurfaceCoord(1, 1, 3));

      // Assert
      Assert.IsNotNull(r);
      Assert.AreEqual(4, r!.Size);
    }

    [TestMethod]
    public void Containing_ReturnsNullForUnsurveyedSurface() {
      // Arrange
      var (_, regions) = Setup(FakeTerrain.Flat(width: 1, height: 1, surfaceZ: 3));
      regions.Index();

      // Act
      var r = regions.Containing(new SurfaceCoord(99, 99, 99));

      // Assert
      Assert.IsNull(r);
    }

    #endregion

    #region Clock context

    [TestMethod]
    public void Index_StampsCreatedAtAndWeatherFromClock() {
      // Arrange — fake clock returns a specific timestamp + weather.
      var clock = new FakeClock {
          Now = new GameTimestamp(Cycle: 4, CycleDay: 7, PartialCycleDay: 0.25f),
          CurrentWeather = WeatherKind.Drought,
      };
      var (_, regions) = Setup(FakeTerrain.Flat(width: 1, height: 1, surfaceZ: 1), clock);

      // Act
      regions.Index();

      // Assert
      var r = regions.All.Single();
      Assert.AreEqual(new GameTimestamp(4, 7, 0.25f), r.CreatedAt);
      Assert.AreEqual(WeatherKind.Drought, r.WeatherAtCreation);
    }

    [TestMethod]
    public void Index_AllRegionsInOnePassShareTimestamp() {
      // Arrange
      var clock = new FakeClock {
          Now = new GameTimestamp(2, 3, 0.5f),
          CurrentWeather = WeatherKind.Badtide,
      };
      var terrain = new FakeTerrain(width: 3, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 1 },
              [new TileCoord(2, 0)] = new[] { 5 },
          },
      };
      var (_, regions) = Setup(terrain, clock);

      // Act
      regions.Index();

      // Assert
      Assert.AreEqual(2, regions.Count);
      foreach (var r in regions.All) {
        Assert.AreEqual(new GameTimestamp(2, 3, 0.5f), r.CreatedAt);
        Assert.AreEqual(WeatherKind.Badtide, r.WeatherAtCreation);
      }
    }

    #endregion

    #region Setup

    private static (TerrainSurveyor surveyor, RegionService regions) Setup(
        FakeTerrain terrain, IClock? clock = null) {
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, clock ?? new FakeClock());
      return (surveyor, regions);
    }

    #endregion

    #region Fakes

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

      public static FakeTerrain Flat(int width, int height, int surfaceZ) {
        var t = new FakeTerrain(width, height);
        for (var x = 0; x < width; x++) {
          for (var y = 0; y < height; y++) {
            t.Heights[new TileCoord(x, y)] = new[] { surfaceZ };
          }
        }
        return t;
      }
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
