using Keystone.Core.Ecology.Fields;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Ecology.Fields {

  /// <summary>
  /// Pins the water-badwater threshold and predicate. Project memory
  /// <c>project_water_contamination_threshold.md</c> documents the
  /// historical regression: an earlier version used a strict
  /// <c>&gt; 0</c> predicate, which painted every tile in a touched
  /// pool as badwater from trace diffusion. The fix introduced the
  /// 0.05 floor with an inclusive comparison; these tests pin both
  /// so the regression can't sneak back.
  /// </summary>
  [TestClass]
  public class WaterContaminationTests {

    #region Threshold value

    [TestMethod]
    public void Threshold_IsExactlyZeroPoint05() {
      // The 0.05 value is the load-bearing cutoff between "trace
      // contamination from diffusion" and "actual badwater." Changing
      // this constant changes how aggressively the water layer paints
      // pools as badwater. Pin the exact value so any drift surfaces.
      Assert.AreEqual(0.05f, WaterContamination.Threshold);
    }

    #endregion

    #region IsBadwater predicate — boundary

    [TestMethod]
    public void IsBadwater_ExactlyAtThreshold_True() {
      // The comparison is inclusive (>=), so a tile saturated to
      // exactly the threshold counts as badwater. This is the boundary
      // that must not regress to strict > — a previous version of the
      // predicate used > which silently moved the cutoff one epsilon
      // above 0.05.
      Assert.IsTrue(WaterContamination.IsBadwater(WaterContamination.Threshold));
    }

    [TestMethod]
    public void IsBadwater_JustBelowThreshold_False() {
      // 0.049 is below the floor; a strict-> regression would still
      // return false here but only because the value is below the
      // floor — the boundary at exactly 0.05 is where strict-vs-
      // inclusive divergence shows.
      Assert.IsFalse(WaterContamination.IsBadwater(0.049f));
    }

    [TestMethod]
    public void IsBadwater_JustAboveThreshold_True() {
      Assert.IsTrue(WaterContamination.IsBadwater(0.051f));
    }

    #endregion

    #region IsBadwater predicate — value range

    [TestMethod]
    public void IsBadwater_Zero_False() {
      Assert.IsFalse(WaterContamination.IsBadwater(0f));
    }

    [TestMethod]
    public void IsBadwater_TraceDiffusion_False() {
      // The whole point of the threshold: a touched pool gets diffuse
      // fractional contamination everywhere. Values like 0.01 are
      // "barely touched" and should NOT light up as badwater.
      Assert.IsFalse(WaterContamination.IsBadwater(0.01f));
      Assert.IsFalse(WaterContamination.IsBadwater(0.04f));
    }

    [TestMethod]
    public void IsBadwater_FullySaturated_True() {
      Assert.IsTrue(WaterContamination.IsBadwater(1f));
    }

    [TestMethod]
    public void IsBadwater_ModerateContamination_True() {
      Assert.IsTrue(WaterContamination.IsBadwater(0.3f));
      Assert.IsTrue(WaterContamination.IsBadwater(0.5f));
    }

    #endregion

  }

}
