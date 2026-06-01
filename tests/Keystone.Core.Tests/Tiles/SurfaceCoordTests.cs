using System.Collections.Generic;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Tiles {

  /// <summary>
  /// Pins <see cref="SurfaceCoord"/>'s identity contract:
  /// <list type="bullet">
  ///   <item><see cref="SurfaceCoord.Column"/> drops Z to the 2D parent.</item>
  ///   <item><see cref="SurfaceCoord.CompareTo"/> is a strict X, then Y,
  ///         then Z lexicographic order — load-bearing for
  ///         <c>RegionService.Index()</c>'s deterministic seed pick.</item>
  ///   <item>Record-struct equality treats all three components.</item>
  /// </list>
  /// </summary>
  [TestClass]
  public class SurfaceCoordTests {

    #region Column projection

    [TestMethod]
    public void Column_DropsZ() {
      var s = new SurfaceCoord(3, -2, 7);
      Assert.AreEqual(new TileCoord(3, -2), s.Column);
    }

    #endregion

    #region Equality

    [TestMethod]
    public void Equality_AllThreeComponentsMatter() {
      var baseline = new SurfaceCoord(1, 2, 3);
      Assert.AreEqual(baseline, new SurfaceCoord(1, 2, 3));
      Assert.AreNotEqual(baseline, new SurfaceCoord(1, 2, 4));
      Assert.AreNotEqual(baseline, new SurfaceCoord(1, 3, 3));
      Assert.AreNotEqual(baseline, new SurfaceCoord(0, 2, 3));
    }

    #endregion

    #region CompareTo

    [TestMethod]
    public void CompareTo_OrdersByXFirst() {
      var lo = new SurfaceCoord(0, 99, 99);
      var hi = new SurfaceCoord(1, 0, 0);
      Assert.IsTrue(lo.CompareTo(hi) < 0);
      Assert.IsTrue(hi.CompareTo(lo) > 0);
    }

    [TestMethod]
    public void CompareTo_TiesXThenOrdersByY() {
      var lo = new SurfaceCoord(5, 0, 99);
      var hi = new SurfaceCoord(5, 1, 0);
      Assert.IsTrue(lo.CompareTo(hi) < 0);
    }

    [TestMethod]
    public void CompareTo_TiesXandYThenOrdersByZ() {
      var lo = new SurfaceCoord(5, 7, 0);
      var hi = new SurfaceCoord(5, 7, 1);
      Assert.IsTrue(lo.CompareTo(hi) < 0);
    }

    [TestMethod]
    public void CompareTo_EqualSurfaces_ReturnsZero() {
      var s = new SurfaceCoord(1, 2, 3);
      Assert.AreEqual(0, s.CompareTo(new SurfaceCoord(1, 2, 3)));
    }

    [TestMethod]
    public void CompareTo_HandlesNegativeCoordinates() {
      // Lexicographic on signed ints: (-1, _, _) < (0, _, _).
      var negative = new SurfaceCoord(-1, 100, 100);
      var zero = new SurfaceCoord(0, -100, -100);
      Assert.IsTrue(negative.CompareTo(zero) < 0);
    }

    #endregion

    #region Sort stability

    [TestMethod]
    public void CompareTo_ProducesDeterministicSortAcrossRuns() {
      // The total order is what RegionService.Index() relies on for
      // deterministic flood-fill seed selection. If the order ever
      // changed, region ids would shuffle across runs.
      var input = new List<SurfaceCoord> {
          new(2, 0, 0),
          new(0, 5, 1),
          new(1, 3, 7),
          new(0, 5, 0),
          new(1, 3, 2),
      };
      input.Sort();
      var expected = new List<SurfaceCoord> {
          new(0, 5, 0),
          new(0, 5, 1),
          new(1, 3, 2),
          new(1, 3, 7),
          new(2, 0, 0),
      };
      CollectionAssert.AreEqual(expected, input);
    }

    #endregion

  }

}
