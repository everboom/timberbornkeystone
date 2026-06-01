using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Tiles {

  /// <summary>
  /// Pins <see cref="FlowVector"/>'s magnitude math, zero detection, and
  /// the <see cref="FlowVector.Zero"/> default. Small substrate type but
  /// load-bearing for flow-based recipe filters and attrition ScaleBy
  /// channels.
  /// </summary>
  [TestClass]
  public class FlowVectorTests {

    [TestMethod]
    public void Zero_IsDefaultStruct() {
      Assert.AreEqual(default(FlowVector), FlowVector.Zero);
      Assert.AreEqual(0f, FlowVector.Zero.X);
      Assert.AreEqual(0f, FlowVector.Zero.Y);
    }

    [TestMethod]
    public void IsZero_AllZero_True() {
      Assert.IsTrue(new FlowVector(0f, 0f).IsZero);
      Assert.IsTrue(FlowVector.Zero.IsZero);
    }

    [TestMethod]
    public void IsZero_AnyComponentNonZero_False() {
      Assert.IsFalse(new FlowVector(1f, 0f).IsZero);
      Assert.IsFalse(new FlowVector(0f, 1f).IsZero);
      Assert.IsFalse(new FlowVector(-1e-9f, 0f).IsZero,
          "IsZero is strict — even a tiny non-zero counts as flowing.");
    }

    [TestMethod]
    public void Magnitude_OfZeroVector_IsZero() {
      Assert.AreEqual(0f, FlowVector.Zero.Magnitude);
    }

    [TestMethod]
    public void Magnitude_3_4_5_PythagoreanTriple() {
      Assert.AreEqual(5f, new FlowVector(3f, 4f).Magnitude, 1e-6f);
    }

    [TestMethod]
    public void Magnitude_NegativeComponents_IsPositive() {
      Assert.AreEqual(5f, new FlowVector(-3f, -4f).Magnitude, 1e-6f);
    }

    [TestMethod]
    public void Magnitude_UnitVectorOnXAxis() {
      Assert.AreEqual(1f, new FlowVector(1f, 0f).Magnitude, 1e-6f);
    }

    [TestMethod]
    public void Magnitude_UnitVectorOnYAxis() {
      Assert.AreEqual(1f, new FlowVector(0f, 1f).Magnitude, 1e-6f);
    }

  }

}
