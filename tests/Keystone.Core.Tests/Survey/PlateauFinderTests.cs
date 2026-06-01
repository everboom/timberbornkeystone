using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Ports;
using Keystone.Core.Survey;
using Keystone.Core.Tests.Helpers;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Survey {

  /// <summary>
  /// Drives <see cref="PlateauFinder"/> on small hand-built terrains via a
  /// real <see cref="TerrainSurveyor"/> and verifies the flood-fill respects
  /// (Z, IsCave) equality.
  /// </summary>
  [TestClass]
  public class PlateauFinderTests {

    #region Tests

    [TestMethod]
    public void Find_FlatPlateau_ReturnsAllSurfaces() {
      // Arrange — 3x3 flat map at z=4.
      var (surveyor, finder) = Setup(FakeTerrain.Flat(width: 3, height: 3, surfaceZ: 4));

      // Act
      var plateau = finder.Find(new SurfaceCoord(1, 1, 4));

      // Assert
      Assert.AreEqual(9, plateau.Count);
      for (var x = 0; x < 3; x++) {
        for (var y = 0; y < 3; y++) {
          Assert.IsTrue(plateau.Contains(new SurfaceCoord(x, y, 4)), $"missing ({x},{y},4)");
        }
      }
    }

    [TestMethod]
    public void Find_HeightStepBoundary_StopsAtTheStep() {
      // Arrange — left half at z=4, right half at z=6.
      // Plateau from a left-half seed should include only z=4 surfaces.
      var terrain = new FakeTerrain(width: 4, height: 2);
      for (var x = 0; x < 4; x++) {
        for (var y = 0; y < 2; y++) {
          terrain.Heights[new TileCoord(x, y)] = new[] { x < 2 ? 4 : 6 };
        }
      }
      var (_, finder) = Setup(terrain);

      // Act
      var plateau = finder.Find(new SurfaceCoord(0, 0, 4));

      // Assert
      Assert.AreEqual(4, plateau.Count); // 2x2 left half
      Assert.IsTrue(plateau.All(s => s.Z == 4));
    }

    [TestMethod]
    public void Find_CaveBoundary_StopsAtCaveTransition() {
      // Arrange — 3x1 row at z=5. Middle column has an upper surface above,
      // so its z=5 surface tags as cave; the others don't. Plateau should
      // split: the seed (left, non-cave) covers only the leftmost surface.
      var terrain = new FakeTerrain(width: 3, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5, 9 },  // stacked → z=5 is cave
              [new TileCoord(2, 0)] = new[] { 5 },
          },
      };
      var (_, finder) = Setup(terrain);

      // Act
      var leftPlateau = finder.Find(new SurfaceCoord(0, 0, 5));
      var middlePlateau = finder.Find(new SurfaceCoord(1, 0, 5));
      var rightPlateau = finder.Find(new SurfaceCoord(2, 0, 5));

      // Assert — left and right are isolated by the middle cave; middle is alone.
      Assert.AreEqual(1, leftPlateau.Count);
      Assert.IsTrue(leftPlateau.Contains(new SurfaceCoord(0, 0, 5)));
      Assert.AreEqual(1, rightPlateau.Count);
      Assert.IsTrue(rightPlateau.Contains(new SurfaceCoord(2, 0, 5)));
      Assert.AreEqual(1, middlePlateau.Count);
      Assert.IsTrue(middlePlateau.Contains(new SurfaceCoord(1, 0, 5)));
    }

    [TestMethod]
    public void Find_CavePlateauInStackedColumn_GroupsCaveSurfaces() {
      // Arrange — 3x1 row, all stacked: each column has surfaces at z=2 and z=8.
      // The z=2 layer is uniformly "cave" (because z=8 is above each).
      // The z=8 layer is uniformly non-cave (top of each column).
      var terrain = new FakeTerrain(width: 3, height: 1);
      for (var x = 0; x < 3; x++) {
        terrain.Heights[new TileCoord(x, 0)] = new[] { 2, 8 };
      }
      var (_, finder) = Setup(terrain);

      // Act
      var caveFloor = finder.Find(new SurfaceCoord(1, 0, 2));
      var topPlateau = finder.Find(new SurfaceCoord(1, 0, 8));

      // Assert
      Assert.AreEqual(3, caveFloor.Count);
      Assert.IsTrue(caveFloor.All(s => s.Z == 2));
      Assert.AreEqual(3, topPlateau.Count);
      Assert.IsTrue(topPlateau.All(s => s.Z == 8));
    }

    [TestMethod]
    public void Find_SingleTilePlateau_ReturnsJustTheSeed() {
      // Arrange — single column.
      var terrain = FakeTerrain.Single(x: 0, y: 0, surfaceZs: new[] { 3 });
      var (_, finder) = Setup(terrain);

      // Act
      var plateau = finder.Find(new SurfaceCoord(0, 0, 3));

      // Assert
      Assert.AreEqual(1, plateau.Count);
      Assert.IsTrue(plateau.Contains(new SurfaceCoord(0, 0, 3)));
    }

    [TestMethod]
    public void Find_SeedNotInSurvey_ReturnsEmpty() {
      // Arrange
      var terrain = FakeTerrain.Flat(width: 2, height: 2, surfaceZ: 4);
      var (_, finder) = Setup(terrain);

      // Act — pick a coord that isn't surveyed (wrong Z).
      var plateau = finder.Find(new SurfaceCoord(0, 0, 99));

      // Assert
      Assert.AreEqual(0, plateau.Count);
    }

    [TestMethod]
    public void Find_RespectsMaxFillCap() {
      // Arrange — 10x10 flat plateau, cap fill at 7.
      var terrain = FakeTerrain.Flat(width: 10, height: 10, surfaceZ: 1);
      var (_, finder) = Setup(terrain);

      // Act
      var plateau = finder.Find(new SurfaceCoord(5, 5, 1), maxFillSize: 7);

      // Assert
      Assert.AreEqual(7, plateau.Count);
    }

    [TestMethod]
    public void Find_DiagonallyTouchingPlateaus_DoNotMerge() {
      // Arrange — 3x3, only diagonal cells (0,0) and (1,1) and (2,2) at z=4; rest empty.
      // 4-connected fill from (0,0) should NOT reach (1,1) since they're diagonal.
      var terrain = new FakeTerrain(width: 3, height: 3) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 4 },
              [new TileCoord(1, 1)] = new[] { 4 },
              [new TileCoord(2, 2)] = new[] { 4 },
          },
      };
      var (_, finder) = Setup(terrain);

      // Act
      var plateau = finder.Find(new SurfaceCoord(0, 0, 4));

      // Assert
      Assert.AreEqual(1, plateau.Count);
      Assert.IsTrue(plateau.Contains(new SurfaceCoord(0, 0, 4)));
    }

    #endregion

    #region Setup

    private static (TerrainSurveyor surveyor, PlateauFinder finder) Setup(FakeTerrain terrain) {
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      return (surveyor, new PlateauFinder(surveyor));
    }

    #endregion

    #region Fakes (shared shape with TerrainSurveyorTests)

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
          return System.Array.Empty<int>();
        }
        var sorted = (int[])list.Clone();
        System.Array.Sort(sorted);
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

      public static FakeTerrain Single(int x, int y, int[] surfaceZs) {
        var t = new FakeTerrain(x + 1, y + 1);
        t.Heights[new TileCoord(x, y)] = surfaceZs;
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
      public Keystone.Core.Tiles.FlowVector FlowAt(SurfaceCoord surface) => Keystone.Core.Tiles.FlowVector.Zero;
      public bool HasWaterAtColumn(Keystone.Core.Tiles.TileCoord column) => false;
      public float WaterContaminationAt(SurfaceCoord surface) => 0f;
      public static FakeWater None() => new();
    }

    private sealed class FakeBuilding : IBuildingQuery {
      public Keystone.Core.Buildings.BuildingKind ClassifyAt(SurfaceCoord voxel) =>
          Keystone.Core.Buildings.BuildingKind.None;
      public static FakeBuilding NothingBuilt() => new();
    }

    #endregion

  }

}
