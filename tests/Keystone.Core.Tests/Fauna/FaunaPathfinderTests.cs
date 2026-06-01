using System.Collections.Generic;
using Keystone.Core.Fauna;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Fauna {

  /// <summary>
  /// Unit tests for <see cref="FaunaPathfinder"/>. All scenarios run
  /// against the in-memory <see cref="FakeTopology"/>: explicit
  /// walkable set, single region, no I/O or game state. Each test
  /// pins one specific behaviour of either the raw A* stage or the
  /// line-of-sight smoothing stage.
  /// </summary>
  [TestClass]
  public class FaunaPathfinderTests {

    private static readonly RegionId R = new(7);

    #region Endpoint validation

    [TestMethod]
    public void StartEqualsGoal_ReturnsSingletonPath() {
      // Arrange
      var topo = Rect(0, 0, 3, 3);

      // Act
      var path = FaunaPathfinder.FindPath(topo, R, new TileCoord(1, 1), new TileCoord(1, 1));

      // Assert
      Assert.IsNotNull(path);
      Assert.AreEqual(1, path.Count);
      Assert.AreEqual(new TileCoord(1, 1), path[0]);
    }

    [TestMethod]
    public void StartOutsideRegion_ReturnsNull() {
      // Arrange
      var topo = Rect(0, 0, 3, 3);

      // Act
      var path = FaunaPathfinder.FindPath(topo, R, new TileCoord(-1, 0), new TileCoord(1, 1));

      // Assert
      Assert.IsNull(path);
    }

    [TestMethod]
    public void GoalOutsideRegion_ReturnsNull() {
      // Arrange
      var topo = Rect(0, 0, 3, 3);

      // Act
      var path = FaunaPathfinder.FindPath(topo, R, new TileCoord(0, 0), new TileCoord(5, 5));

      // Assert
      Assert.IsNull(path);
    }

    #endregion

    #region Raw A*

    [TestMethod]
    public void RawPath_StraightLine_FollowsManhattan() {
      // Arrange — a 5x1 strip of walkable tiles
      var topo = Rect(0, 0, 5, 1);

      // Act
      var raw = FaunaPathfinder.FindRawPath(topo, R, new TileCoord(0, 0), new TileCoord(4, 0));

      // Assert — 5 waypoints (start through goal, one per tile)
      Assert.IsNotNull(raw);
      Assert.AreEqual(5, raw.Count);
      Assert.AreEqual(new TileCoord(0, 0), raw[0]);
      Assert.AreEqual(new TileCoord(4, 0), raw[4]);
    }

    [TestMethod]
    public void RawPath_UShapedRegion_FindsLongWayAround() {
      // Arrange — a U-shape: bottom row 0..4, plus vertical walls at x=0 and x=4 going up to y=3.
      //
      //   .....
      //   X...X
      //   X...X
      //   XXXXX        (row 0)
      //
      // start at top-left corridor (0, 3), goal at top-right corridor (4, 3). A* must travel down
      // the left wall, across the bottom, and back up the right wall.
      var topo = new FakeTopology(R);
      for (var x = 0; x <= 4; x++) topo.Add(x, 0);    // bottom row
      for (var y = 1; y <= 3; y++) {
        topo.Add(0, y);   // left wall
        topo.Add(4, y);   // right wall
      }

      // Act
      var raw = FaunaPathfinder.FindRawPath(topo, R, new TileCoord(0, 3), new TileCoord(4, 3));

      // Assert — minimum walking distance is 10 edges (down 3, across 4,
      // up 3), so the path is 11 waypoints.
      Assert.IsNotNull(raw);
      Assert.AreEqual(11, raw.Count);
      // Verify every waypoint is in the region.
      foreach (var w in raw) {
        Assert.IsTrue(topo.ContainsTile(R, w.X, w.Y), $"Waypoint {w} not in region");
      }
    }

    [TestMethod]
    public void RawPath_DisconnectedTiles_ReturnsNull() {
      // Arrange — two unconnected single tiles registered under the same region id.
      // (A real Keystone region is always connected; this guards the search-termination
      // path against a port that returns inconsistent membership.)
      var topo = new FakeTopology(R);
      topo.Add(0, 0);
      topo.Add(5, 5);

      // Act
      var raw = FaunaPathfinder.FindRawPath(topo, R, new TileCoord(0, 0), new TileCoord(5, 5));

      // Assert
      Assert.IsNull(raw);
    }

    #endregion

    #region Smoothing

    [TestMethod]
    public void Smooth_StraightCorridor_CollapsesToEndpoints() {
      // Arrange — a 5x5 open rectangle. The raw path from corner to
      // corner is a stairstep of ~9 waypoints; smoothing should
      // collapse it to just [start, goal] because the straight diagonal
      // stays entirely inside the region.
      var topo = Rect(0, 0, 5, 5);
      var start = new TileCoord(0, 0);
      var goal = new TileCoord(4, 4);

      // Act
      var path = FaunaPathfinder.FindPath(topo, R, start, goal);

      // Assert
      Assert.IsNotNull(path);
      Assert.AreEqual(2, path.Count, "Smoothing should collapse an open rectangle to endpoints");
      Assert.AreEqual(start, path[0]);
      Assert.AreEqual(goal, path[1]);
    }

    [TestMethod]
    public void Smooth_LShapedRegion_KeepsCornerWaypoint() {
      // Arrange — an L: row 0 from x=0..4, plus column at x=4 from y=0..4.
      // The direct diagonal from (0,0) to (4,4) crosses outside the L,
      // so smoothing must retain a waypoint near the corner.
      var topo = new FakeTopology(R);
      for (var x = 0; x <= 4; x++) topo.Add(x, 0);    // bottom row
      for (var y = 1; y <= 4; y++) topo.Add(4, y);    // right column

      // Act
      var path = FaunaPathfinder.FindPath(topo, R, new TileCoord(0, 0), new TileCoord(4, 4));

      // Assert — start, goal, plus at least one intermediate waypoint
      Assert.IsNotNull(path);
      Assert.IsTrue(path.Count >= 3, $"Expected >=3 waypoints around the L corner, got {path.Count}");
      Assert.AreEqual(new TileCoord(0, 0), path[0]);
      Assert.AreEqual(new TileCoord(4, 4), path[path.Count - 1]);
      // Every retained waypoint must be in the region.
      foreach (var w in path) {
        Assert.IsTrue(topo.ContainsTile(R, w.X, w.Y), $"Smoothed waypoint {w} not in region");
      }
    }

    [TestMethod]
    public void Smooth_PathSegmentsStayInsideRegion() {
      // Arrange — same L region.
      var topo = new FakeTopology(R);
      for (var x = 0; x <= 4; x++) topo.Add(x, 0);
      for (var y = 1; y <= 4; y++) topo.Add(4, y);

      // Act
      var path = FaunaPathfinder.FindPath(topo, R, new TileCoord(0, 0), new TileCoord(4, 4));

      // Assert — every Bresenham step of every smoothed segment is in the region.
      // This is the corner-cutting guard: even though smoothing prunes
      // intermediate waypoints, the segments it retains must not pass
      // through tiles outside the region.
      Assert.IsNotNull(path);
      for (var i = 0; i < path.Count - 1; i++) {
        AssertLineInsideRegion(topo, path[i], path[i + 1]);
      }
    }

    #endregion

    #region Helpers

    /// <summary>Build a fully-walkable axis-aligned rectangle as a region.</summary>
    private static FakeTopology Rect(int x0, int y0, int width, int height) {
      var t = new FakeTopology(R);
      for (var x = x0; x < x0 + width; x++) {
        for (var y = y0; y < y0 + height; y++) {
          t.Add(x, y);
        }
      }
      return t;
    }

    /// <summary>Walk every tile under the Bresenham line from
    /// <paramref name="a"/> to <paramref name="b"/> and assert each
    /// is a member of the region.</summary>
    private static void AssertLineInsideRegion(FakeTopology topo, TileCoord a, TileCoord b) {
      var x = a.X;
      var y = a.Y;
      var dx = System.Math.Abs(b.X - a.X);
      var dy = System.Math.Abs(b.Y - a.Y);
      var sx = a.X < b.X ? 1 : -1;
      var sy = a.Y < b.Y ? 1 : -1;
      var err = dx - dy;
      while (true) {
        Assert.IsTrue(topo.ContainsTile(R, x, y),
            $"Segment {a}->{b} passes through ({x},{y}) which is outside the region");
        if (x == b.X && y == b.Y) return;
        var e2 = 2 * err;
        if (e2 > -dy) { err -= dy; x += sx; }
        if (e2 < dx) { err += dx; y += sy; }
      }
    }

    /// <summary>Single-region topology fake. Add(x, y) registers a tile
    /// as a member of <see cref="FaunaPathfinderTests.R"/>; everything
    /// else returns false.</summary>
    private sealed class FakeTopology : IRegionTopologyQuery {
      private readonly RegionId _region;
      private readonly HashSet<(int X, int Y)> _members = new();

      public FakeTopology(RegionId region) {
        _region = region;
      }

      public void Add(int x, int y) => _members.Add((x, y));

      public bool ContainsTile(RegionId region, int x, int y) =>
          region.Equals(_region) && _members.Contains((x, y));
    }

    #endregion

  }

}
