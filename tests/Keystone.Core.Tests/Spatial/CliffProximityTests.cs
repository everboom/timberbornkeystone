using System;
using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Spatial {

  /// <summary>
  /// Tests for <see cref="CliffProximity"/>. Uses a hand-rolled
  /// <see cref="ITerrainQuery"/> whose only meaningful state is a set
  /// of (x, y, z) tuples marked as natural terrain voxels --
  /// <see cref="IsAboveNeighbor"/> and <see cref="IsBelowNeighbor"/>
  /// consult exactly that surface, so the fake stays minimal.
  /// </summary>
  [TestClass]
  public class CliffProximityTests {

    #region Fakes

    /// <summary>
    /// Fake terrain: explicit set of solid voxels. <c>IsTerrainVoxel</c>
    /// queries the set; everything else returns inert values. Bounded
    /// width/height enforces map-edge semantics.
    /// </summary>
    private sealed class FakeTerrain : ITerrainQuery {
      private readonly HashSet<(int X, int Y, int Z)> _solid;
      private readonly int _w, _h;

      public FakeTerrain(int width, int height, HashSet<(int, int, int)> solid) {
        _w = width;
        _h = height;
        _solid = solid;
      }

      public int Width => _w;
      public int Height => _h;
      public int MaxHeight => 64;

      public bool Contains(TileCoord c) =>
          c.X >= 0 && c.X < _w && c.Y >= 0 && c.Y < _h;

      public IReadOnlyList<int> SurfaceHeightsAt(TileCoord _) =>
          Array.Empty<int>();

      public bool HasTerrainAbove(SurfaceCoord _) => false;

      public bool IsTerrainVoxel(int x, int y, int z) {
        if (x < 0 || x >= _w || y < 0 || y >= _h) return false;
        return _solid.Contains((x, y, z));
      }
    }

    private static CliffProximity Sut(int width, int height,
        params (int X, int Y, int Z)[] solidVoxels) {
      return new CliffProximity(
          new FakeTerrain(width, height,
              new HashSet<(int, int, int)>(solidVoxels)));
    }

    #endregion

    #region IsAboveNeighbor

    /// <summary>Flat terrain in all four directions: every neighbour has
    /// a solid voxel at <c>z-1</c>, so the tile is not above any.</summary>
    [TestMethod]
    public void IsAboveNeighbor_FlatPlain_ReturnsFalse() {
      // Arrange: 3x3 patch with solid floor at z=4 everywhere.
      var voxels = new List<(int, int, int)>();
      for (var x = 0; x < 3; x++)
        for (var y = 0; y < 3; y++)
          voxels.Add((x, y, 4));
      var sut = Sut(3, 3, voxels.ToArray());

      // Act
      var result = sut.IsAboveNeighbor(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    /// <summary>East neighbour has empty voxel at <c>z-1</c>: a drop.</summary>
    [TestMethod]
    public void IsAboveNeighbor_StepDownOnEast_ReturnsTrue() {
      // Arrange: solid floor at z=4 for self + all neighbours except east.
      // The east column has no voxel at z=4 -- step down.
      var sut = Sut(3, 3,
          (1, 1, 4),  // self
          (0, 1, 4),  // west
          (1, 0, 4),  // south
          (1, 2, 4)); // north
                       // (2, 1, 4) deliberately omitted

      // Act
      var result = sut.IsAboveNeighbor(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsTrue(result);
    }

    /// <summary>Map-edge neighbour: out-of-bounds counts as empty, so a
    /// tile at the map boundary registers as above its phantom neighbour.</summary>
    [TestMethod]
    public void IsAboveNeighbor_AtMapEdge_ReturnsTrue() {
      // Arrange: 1x1 map -- the tile has no in-bounds neighbours at all,
      // so every direction is out-of-bounds and treated as empty.
      var sut = Sut(1, 1, (0, 0, 4));

      // Act
      var result = sut.IsAboveNeighbor(new SurfaceCoord(0, 0, 5));

      // Assert
      Assert.IsTrue(result);
    }

    #endregion

    #region IsBelowNeighbor

    /// <summary>Flat terrain: no neighbour has a solid voxel at
    /// <c>z+1</c>, so the tile is not below any.</summary>
    [TestMethod]
    public void IsBelowNeighbor_FlatPlain_ReturnsFalse() {
      // Arrange: 3x3 patch with solid floor only at z=4 -- nothing above.
      var voxels = new List<(int, int, int)>();
      for (var x = 0; x < 3; x++)
        for (var y = 0; y < 3; y++)
          voxels.Add((x, y, 4));
      var sut = Sut(3, 3, voxels.ToArray());

      // Act
      var result = sut.IsBelowNeighbor(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    /// <summary>
    /// One-step rise: north column's terrain top is at <c>z=5</c> (our
    /// walkable Z), so its walkable surface sits at <c>z=6</c>. The
    /// predicate probes <c>(nx, ny, z) = (1, 2, 5)</c> directly. This
    /// case also guards against the historical off-by-one where the
    /// predicate probed <c>z+1</c> and would have returned false for a
    /// one-step rise.
    /// </summary>
    [TestMethod]
    public void IsBelowNeighbor_OneStepRiseOnNorth_ReturnsTrue() {
      // Arrange: north column has solid voxels at z=4 and z=5; nothing
      // at z=6. Walkable on the north column would be at z=6 -- exactly
      // one step above our z=5.
      var sut = Sut(3, 3,
          (1, 1, 4),  // self floor (under walkable z=5)
          (1, 2, 4),  // north floor
          (1, 2, 5)); // north terrain top -- one step up

      // Act
      var result = sut.IsBelowNeighbor(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsTrue(result);
    }

    /// <summary>Map edge: out-of-bounds neighbour is not a rise.</summary>
    [TestMethod]
    public void IsBelowNeighbor_AtMapEdge_ReturnsFalse() {
      // Arrange: 1x1 map -- all neighbours OOB.
      var sut = Sut(1, 1, (0, 0, 4));

      // Act
      var result = sut.IsBelowNeighbor(new SurfaceCoord(0, 0, 5));

      // Assert
      Assert.IsFalse(result);
    }

    #endregion

    #region Overhang / floating geometry

    /// <summary>
    /// Neighbour column has solid terrain far above this surface but no
    /// voxel at <c>z</c> -- the topmost surface is higher, but locally
    /// the air space adjacent to this tile is empty. Surface-Z
    /// comparison would mis-report this as "below"; voxel probes get it
    /// right.
    /// </summary>
    [TestMethod]
    public void IsBelowNeighbor_NeighborHasFloatingPlatformAbove_ReturnsFalse() {
      // Arrange: self at z=5 walkable. North neighbour has terrain only
      // at z=20 (a floating platform); the voxel at (1, 2, 5) is empty.
      // So locally there's no step up at this Z, despite the neighbour
      // column's topmost surface being far higher.
      var sut = Sut(3, 3,
          (1, 1, 4),   // self floor
          (1, 2, 20)); // floating platform far above neighbour column

      // Act
      var result = sut.IsBelowNeighbor(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    /// <summary>
    /// Mirror case: neighbour has terrain far below this surface but is
    /// still solid at <c>z-1</c> (e.g., a thick column with a notch
    /// somewhere lower). No drop at this level despite the neighbour's
    /// "average" being lower.
    /// </summary>
    [TestMethod]
    public void IsAboveNeighbor_NeighborSolidImmediatelyBelowDespiteDeeperGaps_ReturnsFalse() {
      // Arrange: self at z=5 walkable (floor at z=4). All four neighbours
      // have a solid voxel at z=4 (matching floor immediately below us),
      // but their columns are otherwise hollow.
      var sut = Sut(3, 3,
          (1, 1, 4),  // self
          (0, 1, 4),  // west floor
          (2, 1, 4),  // east floor
          (1, 0, 4),  // south floor
          (1, 2, 4)); // north floor

      // Act
      var result = sut.IsAboveNeighbor(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    #endregion

    #region Combined

    /// <summary>A ledge: drop on one side and rise on the other -- both
    /// predicates fire simultaneously.</summary>
    [TestMethod]
    public void BothPredicates_LedgeWithDropAndRise_BothTrue() {
      // Arrange: self at z=5 (floor at z=4). East: no floor (drop).
      // West: terrain top at z=5 (one-step rise -- walkable z=6).
      var sut = Sut(3, 3,
          (1, 1, 4),  // self
          (0, 1, 4),  // west floor
          (0, 1, 5),  // west terrain top -- one step up
          (1, 0, 4),  // south floor
          (1, 2, 4)); // north floor
                       // (2, 1, 4) deliberately omitted -> drop east

      // Act
      var above = sut.IsAboveNeighbor(new SurfaceCoord(1, 1, 5));
      var below = sut.IsBelowNeighbor(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsTrue(above, "Expected above-neighbour due to east drop.");
      Assert.IsTrue(below, "Expected below-neighbour due to west rise.");
    }

    #endregion

  }

}
