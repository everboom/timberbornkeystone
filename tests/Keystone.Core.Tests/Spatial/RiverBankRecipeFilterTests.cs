using System;
using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Spatial {

  /// <summary>
  /// Tests for <see cref="RiverBankRecipeFilter"/>. The filter admits
  /// surfaces that are <i>below</i> a Manhattan neighbour but not
  /// <i>above</i> any -- i.e. the lower side of a step, not a waterfall
  /// edge. Mirrors the fake-driven shape of
  /// <see cref="CliffProximityTests"/>: hand-rolled
  /// <see cref="ITerrainQuery"/> with an explicit voxel set, then
  /// assemble the filter on top of a real <see cref="CliffProximity"/>.
  /// </summary>
  [TestClass]
  public class RiverBankRecipeFilterTests {

    #region Fakes

    /// <summary>Fake terrain: explicit set of solid voxels, bounded
    /// map. Only <c>IsTerrainVoxel</c> and <c>Contains</c> are
    /// meaningful to <see cref="CliffProximity"/>; the rest return
    /// inert values.</summary>
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

    /// <summary>Fake water column: a single uniform depth returned for
    /// every surface. Only <see cref="WaterDepthAt"/> matters to the
    /// filter's shallow-water cap; the rest return inert values.</summary>
    private sealed class FakeWater : IWaterQuery {
      private readonly float _depth;
      public FakeWater(float depth) => _depth = depth;
      public float WaterDepthAt(SurfaceCoord surface) => _depth;
      public float WaterSurfaceHeightAt(SurfaceCoord surface) =>
          _depth > 0f ? surface.Z + _depth : 0f;
      public FlowVector FlowAt(SurfaceCoord surface) => FlowVector.Zero;
      public bool HasWaterAtColumn(TileCoord column) => _depth > 0f;
      public float WaterContaminationAt(SurfaceCoord surface) => 0f;
    }

    /// <summary>Build the filter over a real <see cref="CliffProximity"/>
    /// (fake terrain) and a fake water column. Default helper leaves the
    /// surface dry (depth 0) so the bank-geometry tests are isolated from
    /// the water cap; <see cref="SutWater"/> sets a depth to exercise the
    /// cap.</summary>
    private static RiverBankRecipeFilter Sut(int width, int height,
        params (int X, int Y, int Z)[] solidVoxels) =>
        Build(width, height, 0f, solidVoxels);

    private static RiverBankRecipeFilter SutWater(int width, int height,
        float waterDepth, params (int X, int Y, int Z)[] solidVoxels) =>
        Build(width, height, waterDepth, solidVoxels);

    private static RiverBankRecipeFilter Build(int width, int height,
        float waterDepth, (int X, int Y, int Z)[] solidVoxels) {
      var cliffs = new CliffProximity(
          new FakeTerrain(width, height,
              new HashSet<(int, int, int)>(solidVoxels)));
      return new RiverBankRecipeFilter(cliffs, new FakeWater(waterDepth));
    }

    #endregion

    #region Name

    /// <summary>Filter advertises the stable name handlers index it
    /// by. Hard-coded string -- a rename here is a breaking change for
    /// every recipe that references <c>"RiverBank"</c>.</summary>
    [TestMethod]
    public void Name_IsRiverBank() {
      // Arrange / Act / Assert
      Assert.AreEqual("RiverBank", Sut(1, 1).Name);
    }

    #endregion

    #region IsEligible

    /// <summary>Bank: surface at the bottom of a step-up on one side,
    /// flat on the others (below-neighbour true, above-neighbour false),
    /// under exactly one voxel of water -> eligible. Geometry alone is no
    /// longer sufficient since the water band was added, so this positive
    /// case supplies the in-band depth.</summary>
    [TestMethod]
    public void IsEligible_BankAtBottomOfRise_ReturnsTrue() {
      // Arrange: self at z=5 (floor z=4). North column rises one step
      // (terrain at z=5). All other directions match self's floor. One
      // voxel of water above -> inside the [1, 1] band.
      var sut = SutWater(3, 3, 1f,
          (1, 1, 4),  // self floor
          (0, 1, 4),  // west floor (no drop)
          (2, 1, 4),  // east floor (no drop)
          (1, 0, 4),  // south floor (no drop)
          (1, 2, 4),  // north floor
          (1, 2, 5)); // north terrain top -- one step up (the bank wall)

      // Act
      var result = sut.IsEligible(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsTrue(result);
    }

    /// <summary>Same bank geometry, but under two voxels of water:
    /// ceil(2) = 2 exceeds the river minis' MaxWaterHeight of 1, so
    /// vanilla would flip the flourish to its flooded/dead mesh. The
    /// shallow-water cap excludes the tile at spawn. Pins the
    /// deep-water-rejection half of the filter's water gate.</summary>
    [TestMethod]
    public void IsEligible_BankUnderDeepWater_ReturnsFalse() {
      // Arrange: the eligible bank from above, now under 2 voxels of water.
      var sut = SutWater(3, 3, 2f,
          (1, 1, 4), (0, 1, 4), (2, 1, 4), (1, 0, 4), (1, 2, 4), (1, 2, 5));

      // Act
      var result = sut.IsEligible(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    /// <summary>Same bank geometry, but a dry tile (0 voxels of water):
    /// ceil(0) = 0 is below MinWaterHeight = 1, so vanilla reads it as
    /// dry and would wilt it. The band's lower bound excludes the tile at
    /// spawn. Pins the dry-rejection half of the water band.</summary>
    [TestMethod]
    public void IsEligible_BankButDryTile_ReturnsFalse() {
      // Arrange: the otherwise-eligible bank, with no water above it.
      var sut = SutWater(3, 3, 0f,
          (1, 1, 4), (0, 1, 4), (2, 1, 4), (1, 0, 4), (1, 2, 4), (1, 2, 5));

      // Act
      var result = sut.IsEligible(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    /// <summary>Flat plain: neither above nor below any neighbour.
    /// Not a bank.</summary>
    [TestMethod]
    public void IsEligible_FlatPlain_ReturnsFalse() {
      // Arrange: 3x3 patch of solid floor at z=4.
      var voxels = new List<(int, int, int)>();
      for (var x = 0; x < 3; x++)
        for (var y = 0; y < 3; y++)
          voxels.Add((x, y, 4));
      var sut = Sut(3, 3, voxels.ToArray());

      // Act
      var result = sut.IsEligible(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    /// <summary>Cliff top: above a neighbour (a drop on one side) but
    /// not below any -- excluded. This is the "looking down off a
    /// ledge" case; the bank decoration wants the lower side, not the
    /// upper.</summary>
    [TestMethod]
    public void IsEligible_CliffTop_ReturnsFalse() {
      // Arrange: self at z=5 (floor z=4). East column is empty at z=4
      // (drop east). All other directions match self's floor; no rise.
      var sut = Sut(3, 3,
          (1, 1, 4),  // self
          (0, 1, 4),  // west floor
          (1, 0, 4),  // south floor
          (1, 2, 4)); // north floor
                       // east column (2, 1, *) absent -- drop east

      // Act
      var result = sut.IsEligible(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    /// <summary>Waterfall lip: both above one neighbour (the basin
    /// below) AND below another (the wall above). The filter excludes
    /// this -- that's the docstring's explicit motivating case.</summary>
    [TestMethod]
    public void IsEligible_WaterfallLip_ReturnsFalse() {
      // Arrange: self at z=5 (floor z=4). East: no floor (drop east).
      // West: rise one step (west terrain at z=5, walkable at z=6).
      // So self is simultaneously above east (drop) and below west (rise).
      var sut = Sut(3, 3,
          (1, 1, 4),  // self
          (0, 1, 4),  // west floor
          (0, 1, 5),  // west terrain top -- one step up
          (1, 0, 4),  // south floor
          (1, 2, 4)); // north floor
                       // (2, 1, *) absent -- drop east

      // Act
      var result = sut.IsEligible(new SurfaceCoord(1, 1, 5));

      // Assert
      Assert.IsFalse(result);
    }

    /// <summary>Map-edge tile: out-of-bounds neighbours count as empty
    /// (drop) for <c>IsAboveNeighbor</c>. Always above a phantom
    /// neighbour, never below -- so map-edge tiles never qualify as
    /// banks, even when they're at the bottom of a real rise on the
    /// in-bounds side.</summary>
    [TestMethod]
    public void IsEligible_AtMapEdge_ReturnsFalse() {
      // Arrange: 1x1 map. The tile is its own world -- no in-bounds
      // neighbours. IsBelowNeighbor=false (no in-bounds rise) and
      // IsAboveNeighbor=true (every OOB neighbour reads as drop).
      var sut = Sut(1, 1, (0, 0, 4));

      // Act
      var result = sut.IsEligible(new SurfaceCoord(0, 0, 5));

      // Assert
      Assert.IsFalse(result);
    }

    #endregion

  }

}
