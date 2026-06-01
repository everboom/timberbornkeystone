using System;
using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Spatial {

  [TestClass]
  public class WaterDistanceCalculatorTests {

    #region Fakes

    private sealed class FakeWater : IWaterQuery {
      private readonly HashSet<TileCoord> _watered;
      public FakeWater(HashSet<TileCoord> watered) { _watered = watered; }
      public float WaterDepthAt(SurfaceCoord s) =>
          _watered.Contains(new TileCoord(s.X, s.Y)) ? 1f : 0f;
      public float WaterSurfaceHeightAt(SurfaceCoord s) =>
          _watered.Contains(new TileCoord(s.X, s.Y)) ? s.Z + 1f : 0f;
      public FlowVector FlowAt(SurfaceCoord _) => FlowVector.Zero;
      public bool HasWaterAtColumn(TileCoord c) => _watered.Contains(c);
      public float WaterContaminationAt(SurfaceCoord _) => 0f;
    }

    private sealed class FakeTerrain : ITerrainQuery {
      private readonly int _w, _h, _z;
      public FakeTerrain(int w, int h, int z = 0) { _w = w; _h = h; _z = z; }
      public int Width => _w;
      public int Height => _h;
      public int MaxHeight => 16;
      public bool Contains(TileCoord c) =>
          c.X >= 0 && c.X < _w && c.Y >= 0 && c.Y < _h;
      public IReadOnlyList<int> SurfaceHeightsAt(TileCoord _) => new[] { _z };
      public bool HasTerrainAbove(SurfaceCoord _) => false;
      // Terrain is solid for every z below the single surface level, so
      // the derived surface (IsTerrainVoxel(z-1) && !IsTerrainVoxel(z))
      // lands at exactly _z. No z>=0 floor: an abstract test column may
      // sit at z=0, and we still want a surface there (the real game's
      // lowest surface is z=1, but the distance relationships under test
      // are z-translation-invariant, so z=0 is a fine stand-in).
      public bool IsTerrainVoxel(int x, int y, int z) =>
          x >= 0 && x < _w && y >= 0 && y < _h && z < _z;
    }

    private static (IWaterQuery water, ITerrainQuery terrain) Setup(
        int width, int height, params TileCoord[] watered) {
      return (new FakeWater(new HashSet<TileCoord>(watered)),
              new FakeTerrain(width, height));
    }

    private static (IWaterQuery water, ITerrainQuery terrain) SetupAtZ(
        int width, int height, int z, params TileCoord[] watered) {
      return (new FakeWater(new HashSet<TileCoord>(watered)),
              new FakeTerrain(width, height, z));
    }

    /// <summary>Terrain with a per-column top-surface Z: columns listed in
    /// <paramref name="heights"/> sit at the given Z, everything else at
    /// <paramref name="defaultZ"/>. Single surface per column (the model
    /// the water-distance field uses).</summary>
    private sealed class FakeHeightTerrain : ITerrainQuery {
      private readonly int _w, _h, _default;
      private readonly Dictionary<TileCoord, int> _heights;
      public FakeHeightTerrain(int w, int h, int defaultZ, Dictionary<TileCoord, int> heights) {
        _w = w; _h = h; _default = defaultZ; _heights = heights;
      }
      public int Width => _w;
      public int Height => _h;
      public int MaxHeight => 64;
      public bool Contains(TileCoord c) =>
          c.X >= 0 && c.X < _w && c.Y >= 0 && c.Y < _h;
      public IReadOnlyList<int> SurfaceHeightsAt(TileCoord c) =>
          new[] { _heights.TryGetValue(c, out var z) ? z : _default };
      public bool HasTerrainAbove(SurfaceCoord _) => false;
      // Solid below this column's surface level; derived surface lands at
      // that level (see FakeTerrain.IsTerrainVoxel for the z=0 rationale).
      public bool IsTerrainVoxel(int x, int y, int z) {
        if (x < 0 || x >= _w || y < 0 || y >= _h) return false;
        var top = _heights.TryGetValue(new TileCoord(x, y), out var hz) ? hz : _default;
        return z < top;
      }
    }

    private static (IWaterQuery water, ITerrainQuery terrain) SetupHeights(
        int width, int height, int defaultZ,
        Dictionary<TileCoord, int> heights, params TileCoord[] watered) {
      return (new FakeWater(new HashSet<TileCoord>(watered)),
              new FakeHeightTerrain(width, height, defaultZ, heights));
    }

    #endregion

    #region Positive (land) — distance to water

    [TestMethod]
    public void Compute_LandAdjacentToWater_ReturnsOne() {
      var (w, t) = Setup(10, 10, new TileCoord(6, 5));
      Assert.AreEqual(1, WaterDistanceCalculator.Compute(5, 5, 0, w, t));
    }

    [TestMethod]
    public void Compute_LandDiagonalToWater_ReturnsOne() {
      var (w, t) = Setup(10, 10, new TileCoord(6, 6));
      Assert.AreEqual(1, WaterDistanceCalculator.Compute(5, 5, 0, w, t));
    }

    [TestMethod]
    public void Compute_AllEightDirections_AllReturnOne() {
      var deltas = new[] {
          (-1, -1), (0, -1), (1, -1),
          (-1,  0),          (1,  0),
          (-1,  1), (0,  1), (1,  1),
      };
      foreach (var (dx, dy) in deltas) {
        var (w, t) = Setup(10, 10, new TileCoord(5 + dx, 5 + dy));
        Assert.AreEqual(1, WaterDistanceCalculator.Compute(5, 5, 0, w, t),
            $"expected distance 1 at offset ({dx},{dy})");
      }
    }

    [TestMethod]
    public void Compute_LandTwoTilesFromWater_ReturnsTwo() {
      var (w, t) = Setup(10, 10, new TileCoord(7, 5));
      Assert.AreEqual(2, WaterDistanceCalculator.Compute(5, 5, 0, w, t));
    }

    [TestMethod]
    public void Compute_NoWaterAnywhere_ReturnsOutOfRange() {
      var (w, t) = Setup(10, 10);
      Assert.AreEqual(WaterDistanceCalculator.OutOfRange,
          WaterDistanceCalculator.Compute(5, 5, 0, w, t));
    }

    [TestMethod]
    public void Compute_ThreeTilesFromWater_ReturnsOutOfRange() {
      var (w, t) = Setup(10, 10, new TileCoord(8, 5));
      Assert.AreEqual(WaterDistanceCalculator.OutOfRange,
          WaterDistanceCalculator.Compute(5, 5, 0, w, t));
    }

    [TestMethod]
    public void Compute_AtCorner_InBoundsWaterDetected() {
      var (w, t) = Setup(10, 10, new TileCoord(1, 1));
      Assert.AreEqual(1, WaterDistanceCalculator.Compute(0, 0, 0, w, t));
    }

    #endregion

    #region Negative (water) — distance to shore

    [TestMethod]
    public void Compute_WaterAdjacentToLand_ReturnsMinusOne() {
      // Water at (5,5), land neighbor at (6,5) (no water there).
      var (w, t) = Setup(10, 10, new TileCoord(5, 5));
      Assert.AreEqual(-1, WaterDistanceCalculator.Compute(5, 5, 0, w, t));
    }

    [TestMethod]
    public void Compute_WaterSurroundedByWater_OneRingFromLand_ReturnsMinusTwo() {
      // Water at (5,5) and all 8 neighbors. Land at ring 2.
      var watered = new[] {
          new TileCoord(5, 5),
          new TileCoord(4, 4), new TileCoord(5, 4), new TileCoord(6, 4),
          new TileCoord(4, 5),                       new TileCoord(6, 5),
          new TileCoord(4, 6), new TileCoord(5, 6), new TileCoord(6, 6),
      };
      var (w, t) = Setup(10, 10, watered);
      Assert.AreEqual(-2, WaterDistanceCalculator.Compute(5, 5, 0, w, t));
    }

    [TestMethod]
    public void Compute_WaterSurroundedByWaterEverywhere_ReturnsDeepWater() {
      // Fill a 5x5 area with water — (5,5) is far from any land.
      var watered = new List<TileCoord>();
      for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
          watered.Add(new TileCoord(5 + dx, 5 + dy));
      var (w, t) = Setup(10, 10, watered.ToArray());
      Assert.AreEqual(WaterDistanceCalculator.DeepWater,
          WaterDistanceCalculator.Compute(5, 5, 0, w, t));
    }

    #endregion

    #region Height filtering

    [TestMethod]
    public void Compute_WaterOneLevelBelow_ReturnsOne() {
      var (w, t) = SetupAtZ(10, 10, 4, new TileCoord(6, 5));
      Assert.AreEqual(1, WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    [TestMethod]
    public void Compute_WaterTwoLevelsBelow_ReturnsOutOfRange() {
      var (w, t) = SetupAtZ(10, 10, 3, new TileCoord(6, 5));
      Assert.AreEqual(WaterDistanceCalculator.OutOfRange,
          WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    [TestMethod]
    public void Compute_WaterSameLevel_ReturnsOne() {
      var (w, t) = SetupAtZ(10, 10, 5, new TileCoord(6, 5));
      Assert.AreEqual(1, WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    [TestMethod]
    public void Compute_WaterOneLevelAbove_ReturnsOne() {
      var (w, t) = SetupAtZ(10, 10, 6, new TileCoord(6, 5));
      Assert.AreEqual(1, WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    [TestMethod]
    public void Compute_ShoreDistanceAlsoHeightFiltered() {
      // Water tile at Z=5, land neighbor at Z=3 (too far below).
      var (w, t) = SetupAtZ(10, 10, 3, new TileCoord(5, 5));
      Assert.AreEqual(WaterDistanceCalculator.DeepWater,
          WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    #endregion

    #region Path-connected, single-step-vertical (distance 2 over terrain)

    [TestMethod]
    public void Compute_WaterTwoTilesAway_BehindWall_ReturnsOutOfRange() {
      // Water at (7,5) sits at the origin's own level, but the only tiles
      // that can bridge the 2-tile gap — the x=6 column at y 4..6 — are a
      // 3-high wall (z=8), too tall for the single ±1 first step. A pure
      // box scan would call this distance 2; the path-connected rule
      // rejects it because there's no walkable stepping tile.
      var heights = new Dictionary<TileCoord, int> {
          [new TileCoord(6, 4)] = 8,
          [new TileCoord(6, 5)] = 8,
          [new TileCoord(6, 6)] = 8,
      };
      var (w, t) = SetupHeights(12, 12, 5, heights, new TileCoord(7, 5));
      Assert.AreEqual(WaterDistanceCalculator.OutOfRange,
          WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    [TestMethod]
    public void Compute_WaterTwoTilesAway_ClimbOneThenHorizontal_ReturnsTwo() {
      // First step climbs +1 onto (6,5) at z6; a horizontal step then
      // reaches water at (7,5), also z6. One vertical step — allowed.
      var heights = new Dictionary<TileCoord, int> {
          [new TileCoord(6, 5)] = 6,
          [new TileCoord(7, 5)] = 6,
      };
      var (w, t) = SetupHeights(12, 12, 5, heights, new TileCoord(7, 5));
      Assert.AreEqual(2, WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    [TestMethod]
    public void Compute_WaterTwoTilesAway_DescendOneThenHorizontal_ReturnsTwo() {
      // The first step may also drop −1: onto (6,5) at z4, then horizontal
      // to water at (7,5), z4. Confirms the kept downward allowance.
      var heights = new Dictionary<TileCoord, int> {
          [new TileCoord(6, 5)] = 4,
          [new TileCoord(7, 5)] = 4,
      };
      var (w, t) = SetupHeights(12, 12, 5, heights, new TileCoord(7, 5));
      Assert.AreEqual(2, WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    [TestMethod]
    public void Compute_WaterTwoTilesAway_RequiresSecondClimb_ReturnsOutOfRange() {
      // First step climbs +1 to (6,5) at z6; the water at (7,5) is a
      // further +1 up (z7). Reaching it would need a second vertical step,
      // which the horizontal-after-first-step rule forbids — the vertical
      // tolerance must not compound with distance.
      var heights = new Dictionary<TileCoord, int> {
          [new TileCoord(6, 5)] = 6,
          [new TileCoord(7, 5)] = 7,
      };
      var (w, t) = SetupHeights(12, 12, 5, heights, new TileCoord(7, 5));
      Assert.AreEqual(WaterDistanceCalculator.OutOfRange,
          WaterDistanceCalculator.Compute(5, 5, 5, w, t));
    }

    #endregion

  }

}
