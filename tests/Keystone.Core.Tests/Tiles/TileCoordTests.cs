using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Tiles {

  /// <summary>
  /// Pins <see cref="TileCoord"/>'s record-struct equality and identity.
  /// Cheap insurance for the substrate everything else builds on.
  /// </summary>
  [TestClass]
  public class TileCoordTests {

    [TestMethod]
    public void Equality_SameComponents_AreEqual() {
      Assert.AreEqual(new TileCoord(3, -7), new TileCoord(3, -7));
    }

    [TestMethod]
    public void Equality_DifferentX_NotEqual() {
      Assert.AreNotEqual(new TileCoord(3, 5), new TileCoord(4, 5));
    }

    [TestMethod]
    public void Equality_DifferentY_NotEqual() {
      Assert.AreNotEqual(new TileCoord(3, 5), new TileCoord(3, 6));
    }

    [TestMethod]
    public void HashCode_EqualValues_AgreeOnHash() {
      // Record structs auto-implement GetHashCode; equal values must
      // agree (basic equality contract). Without this, TileMap lookups
      // would silently miss.
      var a = new TileCoord(123, -456);
      var b = new TileCoord(123, -456);
      Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void Equality_HandlesNegativeAndZero() {
      Assert.AreEqual(new TileCoord(0, 0), new TileCoord(0, 0));
      Assert.AreEqual(new TileCoord(-1, -1), new TileCoord(-1, -1));
    }

  }

}
