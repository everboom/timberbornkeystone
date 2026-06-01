using Keystone.Core.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Buildings {

  /// <summary>
  /// Locks down <see cref="BuildingClassifier.Classify"/> against
  /// regression. The dual-classification rule (a voxel that is both a
  /// building and a path is classified as Building) is the regression-
  /// sensitive case the user explicitly flagged -- if a future refactor
  /// flips the priority, this test will fail loudly.
  /// </summary>
  [TestClass]
  public class BuildingClassifierTests {

    [TestMethod]
    public void Empty_ClassifiesAsNone() {
      // hasBuildingSpec=F, isPath=F → empty space, or natural element
      // (tree, crop, plant) that lacks BuildingSpec.
      Assert.AreEqual(BuildingKind.None,
          BuildingClassifier.Classify(hasBuilding: false, hasNoAuraBuilding: false, isPath: false));
    }

    [TestMethod]
    public void PureBuilding_ClassifiesAsBuilding() {
      // hasBuildingSpec=T, isPath=F → vanilla building or any
      // mod-added type that uses the standard BuildingSpec component.
      Assert.AreEqual(BuildingKind.Building,
          BuildingClassifier.Classify(hasBuilding: true, hasNoAuraBuilding: false, isPath: false));
    }

    [TestMethod]
    public void PurePath_ClassifiesAsPath() {
      // hasBuildingSpec=F, isPath=T → pure path tile.
      Assert.AreEqual(BuildingKind.Path,
          BuildingClassifier.Classify(hasBuilding: false, hasNoAuraBuilding: false, isPath: true));
    }

    [TestMethod]
    public void DualBuildingAndPath_ClassifiesAsBuilding() {
      // hasBuildingSpec=T, isPath=T → both registered as a path AND
      // carrying a BuildingSpec. Per the user's rule: prefer Building.
      // This is the case mods could trigger (a building you can also
      // walk through registers itself with both systems).
      // ===
      // REGRESSION-SENSITIVE: if this asserts Path, we've inverted
      // the precedence and would mis-classify dual objects.
      Assert.AreEqual(BuildingKind.Building,
          BuildingClassifier.Classify(hasBuilding: true, hasNoAuraBuilding: false, isPath: true));
    }

    [TestMethod]
    public void PureNoAuraBuilding_ClassifiesAsBuildingNoAura() {
      // hasNoAuraBuilding=T alone → BuildingNoAura. The voxel still
      // settles in the surveyor's self-check; the surveyor's neighbor
      // check skips it (no aura propagation).
      Assert.AreEqual(BuildingKind.BuildingNoAura,
          BuildingClassifier.Classify(hasBuilding: false, hasNoAuraBuilding: true, isPath: false));
    }

    [TestMethod]
    public void NoAuraOnPath_ClassifiesAsBuildingNoAura() {
      // A no-aura building on a path voxel: structurally a building, not
      // subordinated to its path aspect (same rule that gives a normal
      // building precedence over a path).
      Assert.AreEqual(BuildingKind.BuildingNoAura,
          BuildingClassifier.Classify(hasBuilding: false, hasNoAuraBuilding: true, isPath: true));
    }

    [TestMethod]
    public void BuildingDominatesNoAura_ClassifiesAsBuilding() {
      // hasBuilding=T AND hasNoAuraBuilding=T → Building. A voxel
      // hosting both (a fence stacked under a house, say) resolves to
      // the more-restrictive Building classification — the normal
      // building's aura claim dominates the no-aura tag.
      // ===
      // REGRESSION-SENSITIVE: if this asserts BuildingNoAura, we've
      // flipped the precedence and would silently drop the aura on
      // voxels that genuinely host a normal building too.
      Assert.AreEqual(BuildingKind.Building,
          BuildingClassifier.Classify(hasBuilding: true, hasNoAuraBuilding: true, isPath: false));
    }

  }

}
