using System.Collections.Generic;
using Keystone.Core.Fauna;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Fauna {

  /// <summary>
  /// Pins the 1-tile-inset rule of
  /// <see cref="InteriorOnlyTopology"/>. The whole reason this filter
  /// exists is so spawn-side tile validation matches the agent's own
  /// walkability predicate — a tile and all four cardinal neighbours
  /// must be in-region. Spawning on a region-edge tile strands the
  /// agent (pathfinder can't accept edge sources) and triggers the
  /// hourly stuck-despawn check, producing a spawn/despawn loop.
  /// </summary>
  [TestClass]
  public class InteriorOnlyTopologyTests {

    #region Helpers

    private sealed class FakeTopology : IRegionTopologyQuery {

      private readonly HashSet<(RegionId Region, int X, int Y)> _members = new();

      public void Add(RegionId region, int x, int y) {
        _members.Add((region, x, y));
      }

      public bool ContainsTile(RegionId region, int x, int y) {
        return _members.Contains((region, x, y));
      }

    }

    private static readonly RegionId Region1 = new(1);

    /// <summary>Populate <paramref name="topo"/> with a square block of
    /// in-region tiles spanning <c>[x0..x1] × [y0..y1]</c> inclusive.</summary>
    private static void FillRect(FakeTopology topo, int x0, int x1, int y0, int y1) {
      for (var x = x0; x <= x1; x++) {
        for (var y = y0; y <= y1; y++) {
          topo.Add(Region1, x, y);
        }
      }
    }

    #endregion

    #region Interior detection

    [TestMethod]
    public void Interior_TileWithFourCardinalNeighboursIn_True() {
      var topo = new FakeTopology();
      // 3x3 block centered at (5, 5): (5,5) has all four cardinal
      // neighbours present.
      FillRect(topo, 4, 6, 4, 6);
      var filter = new InteriorOnlyTopology(topo);

      Assert.IsTrue(filter.ContainsTile(Region1, 5, 5));
    }

    [TestMethod]
    public void Interior_RegionEdgeTile_FalseBecauseSomeNeighbourMissing() {
      var topo = new FakeTopology();
      // 3x3 block: the corner (4, 4) lacks (3, 4) and (4, 3) neighbours.
      FillRect(topo, 4, 6, 4, 6);
      var filter = new InteriorOnlyTopology(topo);

      Assert.IsFalse(filter.ContainsTile(Region1, 4, 4));
    }

    [TestMethod]
    public void Interior_OnEachCardinalEdge_FalseForEdgeTiles() {
      var topo = new FakeTopology();
      // 5x5 block; edge tiles each miss exactly one cardinal neighbour.
      FillRect(topo, 0, 4, 0, 4);
      var filter = new InteriorOnlyTopology(topo);

      // North edge of block: (2, 4) lacks (2, 5).
      Assert.IsFalse(filter.ContainsTile(Region1, 2, 4));
      // South edge: (2, 0) lacks (2, -1).
      Assert.IsFalse(filter.ContainsTile(Region1, 2, 0));
      // East edge: (4, 2) lacks (5, 2).
      Assert.IsFalse(filter.ContainsTile(Region1, 4, 2));
      // West edge: (0, 2) lacks (-1, 2).
      Assert.IsFalse(filter.ContainsTile(Region1, 0, 2));
    }

    [TestMethod]
    public void Interior_OneTileWideStrip_AllTilesFail() {
      // Documented use case: a one-tile-wide gap (cliff edge, narrow
      // bridge) where motion would clip. The inset rule rejects every
      // tile in the strip because they each miss the perpendicular
      // cardinal neighbour.
      var topo = new FakeTopology();
      for (var x = 0; x < 10; x++) topo.Add(Region1, x, 5);
      var filter = new InteriorOnlyTopology(topo);

      for (var x = 0; x < 10; x++) {
        Assert.IsFalse(filter.ContainsTile(Region1, x, 5),
            $"Tile ({x}, 5) on a one-wide strip should be rejected.");
      }
    }

    [TestMethod]
    public void Interior_TileNotInInnerTopology_AlwaysFalse() {
      // Even if all four cardinal neighbours are in-region, the centre
      // itself must be in-region too. The InteriorOnly filter is an
      // AND of the centre and the four neighbours.
      var topo = new FakeTopology();
      // Cross shape: 4 cardinal neighbours but no centre.
      topo.Add(Region1, 1, 0);
      topo.Add(Region1, -1, 0);
      topo.Add(Region1, 0, 1);
      topo.Add(Region1, 0, -1);
      var filter = new InteriorOnlyTopology(topo);

      Assert.IsFalse(filter.ContainsTile(Region1, 0, 0),
          "Centre tile must also be in-region; missing centre fails the AND.");
    }

    [TestMethod]
    public void Interior_DifferentRegion_FalseEvenWhenTopologyContains() {
      var topo = new FakeTopology();
      FillRect(topo, 0, 2, 0, 2);
      var filter = new InteriorOnlyTopology(topo);

      // Other region asks about a tile that's in Region1, but not in
      // Region2. The fake's ContainsTile returns false for (Region2, *)
      // because the membership tuple includes the region id.
      Assert.IsFalse(filter.ContainsTile(new RegionId(2), 1, 1));
    }

    #endregion

  }

}
