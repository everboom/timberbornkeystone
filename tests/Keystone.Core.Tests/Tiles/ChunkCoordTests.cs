using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Tiles {

  /// <summary>
  /// Pins <see cref="ChunkCoord"/> equality across all three components.
  /// The (RegionId, GlobalChunkX, GlobalChunkY) tuple is the work-unit
  /// identity for the chunk-driven rules scheduler — different regions
  /// hosting the same global-chunk position must hash and compare
  /// distinctly.
  /// </summary>
  [TestClass]
  public class ChunkCoordTests {

    [TestMethod]
    public void Equality_AllThreeComponentsMatter() {
      var a = new ChunkCoord(new RegionId(1), 5, 7);
      Assert.AreEqual(a, new ChunkCoord(new RegionId(1), 5, 7));
      Assert.AreNotEqual(a, new ChunkCoord(new RegionId(2), 5, 7));
      Assert.AreNotEqual(a, new ChunkCoord(new RegionId(1), 6, 7));
      Assert.AreNotEqual(a, new ChunkCoord(new RegionId(1), 5, 8));
    }

    [TestMethod]
    public void Equality_SameGlobalChunkInDifferentRegions_NotEqual() {
      // A single 2D chunk on the global lattice can host surfaces in
      // multiple regions when terrain has vertical separations. Each
      // (region, chunk) pair must be its own identity.
      var plateau = new ChunkCoord(new RegionId(10), 3, 4);
      var pit = new ChunkCoord(new RegionId(11), 3, 4);
      Assert.AreNotEqual(plateau, pit);
      Assert.AreNotEqual(plateau.GetHashCode(), pit.GetHashCode(),
          "Different regions at the same global chunk must hash distinctly so the scheduler doesn't conflate them.");
    }

    [TestMethod]
    public void HashCode_EqualValues_AgreeOnHash() {
      var a = new ChunkCoord(new RegionId(42), -3, 8);
      var b = new ChunkCoord(new RegionId(42), -3, 8);
      Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

  }

}
