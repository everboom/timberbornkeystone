using Keystone.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Time {

  /// <summary>
  /// Pins <see cref="GameTimestamp"/>'s equality contract and the
  /// <see cref="GameTimestamp.Origin"/> sentinel. Used as the canonical
  /// "when did this happen" stamp on regions and ecology history; the
  /// equality contract is what makes those keys hash correctly in
  /// persistence stores.
  /// </summary>
  [TestClass]
  public class GameTimestampTests {

    [TestMethod]
    public void Origin_IsCycle0Day0Partial0() {
      var origin = GameTimestamp.Origin;
      Assert.AreEqual(0, origin.Cycle);
      Assert.AreEqual(0, origin.CycleDay);
      Assert.AreEqual(0f, origin.PartialCycleDay);
    }

    [TestMethod]
    public void Origin_EqualsDefaultStruct() {
      Assert.AreEqual(default(GameTimestamp), GameTimestamp.Origin);
    }

    [TestMethod]
    public void Equality_AllThreeComponentsMatter() {
      var baseline = new GameTimestamp(2, 5, 0.5f);
      Assert.AreEqual(baseline, new GameTimestamp(2, 5, 0.5f));
      Assert.AreNotEqual(baseline, new GameTimestamp(3, 5, 0.5f));
      Assert.AreNotEqual(baseline, new GameTimestamp(2, 6, 0.5f));
      Assert.AreNotEqual(baseline, new GameTimestamp(2, 5, 0.6f));
    }

    [TestMethod]
    public void HashCode_EqualValues_AgreeOnHash() {
      var a = new GameTimestamp(7, 3, 0.25f);
      var b = new GameTimestamp(7, 3, 0.25f);
      Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void Constructor_AcceptsAnyComponentValues() {
      // No validation — the timestamp is a passive value type; the
      // game provides values from GameCycleService. Test the
      // documented "negative-component values pass through" behaviour
      // by constructing edge cases.
      var ts = new GameTimestamp(0, 0, 0f);
      Assert.AreEqual(0, ts.Cycle);
      var late = new GameTimestamp(int.MaxValue, int.MaxValue, 0.9999f);
      Assert.AreEqual(int.MaxValue, late.Cycle);
    }

  }

}
