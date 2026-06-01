using Keystone.Core.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Buildings {

  /// <summary>
  /// Pins the per-BO footprint policy in
  /// <see cref="BlockObjectClassifier.Classify"/> against regression.
  /// The tests walk the priority order explicitly — component-natural
  /// over everything (real trees / crops never anchor settlement),
  /// explicit Keystone tags over both the EnterableSpec discriminator
  /// and every heuristic, EnterableSpec over the no-BuildingSpec
  /// heuristic and over the structural-path name match, name match
  /// only when the voxel is also a path. Each test names the
  /// regression it's defending against.
  ///
  /// <para><b>Why this is the regression-critical layer.</b> The
  /// priority order is encoded in the function's if/return chain;
  /// any future re-ordering would silently shift classifications
  /// without a build failure. These tests are the durable record of
  /// what beats what.</para>
  /// </summary>
  [TestClass]
  public class BlockObjectClassifierTests {

    #region Helpers

    private static BlockObjectSignals Signals(
        bool isNaturalComponent = false,
        bool isTaggedTransparent = false,
        bool isTaggedNoAura = false,
        bool hasEnterableSpec = false,
        bool lacksBuildingSpec = false,
        bool voxelIsPath = false,
        bool matchesStructuralPathName = false) {
      return new BlockObjectSignals(
          IsNaturalComponent: isNaturalComponent,
          IsTaggedTransparent: isTaggedTransparent,
          IsTaggedNoAura: isTaggedNoAura,
          HasEnterableSpec: hasEnterableSpec,
          LacksBuildingSpec: lacksBuildingSpec,
          VoxelIsPath: voxelIsPath,
          MatchesStructuralPathName: matchesStructuralPathName);
    }

    #endregion

    #region Component-natural — highest priority

    [TestMethod]
    public void NaturalComponent_AlwaysSkips_EvenWithEverythingElseSet() {
      // REGRESSION-SENSITIVE: a BO carrying a vanilla NaturalResource /
      // Crop / Gatherable / Growable / Yielder component is a real
      // plant / yielder and must never anchor settlement, regardless
      // of any explicit Keystone tag or heuristic match. A misconfigured
      // tag on a vanilla tree would otherwise turn the tree into an
      // aura anchor.
      Assert.AreEqual(BlockObjectFootprint.Skip,
          BlockObjectClassifier.Classify(Signals(
              isNaturalComponent: true,
              isTaggedTransparent: true,
              isTaggedNoAura: true,
              hasEnterableSpec: true,
              lacksBuildingSpec: true,
              voxelIsPath: true,
              matchesStructuralPathName: true)));
    }

    #endregion

    #region Transparent spec — beats everything below

    [TestMethod]
    public void Transparent_BeatsEnterableAndStructuralPath() {
      // A Keystone-tagged transparent building should be skipped even
      // if it's also enterable or matches the structural-path heuristic.
      // Order: NaturalComponent > Transparent > NoAura > Enterable
      //         > LacksBuildingSpec > StructuralPathName > Default.
      Assert.AreEqual(BlockObjectFootprint.Skip,
          BlockObjectClassifier.Classify(Signals(
              isTaggedTransparent: true,
              hasEnterableSpec: true,
              voxelIsPath: true,
              matchesStructuralPathName: true)));
    }

    [TestMethod]
    public void Transparent_BeatsLacksBuildingSpecHeuristic() {
      // A no-BuildingSpec BO that carries an explicit Transparent tag
      // (or a transparent name) must classify as Skip via the tag,
      // not be confused with a spec-only natural via the heuristic.
      // Order matters — would-be-natural-via-heuristic stays Skip
      // here, but the OUTPUT of the classifier is the same; the
      // semantic difference shows up downstream (RegionUpdater
      // optimisations consult the tag/heuristic split via
      // BlockObjectClassification, not the classifier).
      Assert.AreEqual(BlockObjectFootprint.Skip,
          BlockObjectClassifier.Classify(Signals(
              isTaggedTransparent: true,
              lacksBuildingSpec: true)));
    }

    #endregion

    #region NoAura spec — beats Enterable, the no-BuildingSpec heuristic, and the name heuristic

    [TestMethod]
    public void NoAuraSpec_BeatsEnterableSpec() {
      // Explicit Keystone tag should win over the EnterableSpec
      // heuristic. (Today no entry is both enterable AND tagged no-
      // aura, but this pins the priority defensively.)
      Assert.AreEqual(BlockObjectFootprint.NoAura,
          BlockObjectClassifier.Classify(Signals(
              isTaggedNoAura: true,
              hasEnterableSpec: true)));
    }

    [TestMethod]
    public void NoAuraSpec_PromotesEvenOnPath() {
      // A no-aura-tagged BO on a path voxel classifies as NoAura,
      // not Path. This is how decorations sitting on a path tile
      // still settle their voxel.
      Assert.AreEqual(BlockObjectFootprint.NoAura,
          BlockObjectClassifier.Classify(Signals(
              isTaggedNoAura: true,
              voxelIsPath: true)));
    }

    [TestMethod]
    public void NoAuraSpec_BeatsLacksBuildingSpecHeuristic() {
      // REGRESSION-SENSITIVE: the load-bearing case for the
      // tags-beat-heuristic ordering. Third-party mod BOs that ship
      // without a BuildingSpec but DO want to settle as no-aura (Tree
      // of Life is the canonical example) MUST classify as NoAura
      // here, not Skip — otherwise the tag has no effect and the BO
      // contributes nothing to settled state.
      Assert.AreEqual(BlockObjectFootprint.NoAura,
          BlockObjectClassifier.Classify(Signals(
              isTaggedNoAura: true,
              lacksBuildingSpec: true)));
    }

    #endregion

    #region Enterable spec — beats the no-BuildingSpec heuristic, the name heuristic, and the path fallback

    [TestMethod]
    public void Enterable_BeatsStructuralPathNameHeuristic() {
      // If a future blueprint were both Enterable AND name-matched
      // (e.g. a "TubeStation" mod variant that adds EnterableSpec),
      // it should classify as a full Building, not NoAura.
      Assert.AreEqual(BlockObjectFootprint.Building,
          BlockObjectClassifier.Classify(Signals(
              hasEnterableSpec: true,
              voxelIsPath: true,
              matchesStructuralPathName: true)));
    }

    [TestMethod]
    public void Enterable_ClassifiesAsBuildingEvenOnPath() {
      // District center is enterable AND occupies as a path — must
      // classify as Building (not Path) so it anchors settlement.
      Assert.AreEqual(BlockObjectFootprint.Building,
          BlockObjectClassifier.Classify(Signals(
              hasEnterableSpec: true,
              voxelIsPath: true)));
    }

    #endregion

    #region LacksBuildingSpec heuristic — fires when no explicit override

    [TestMethod]
    public void LacksBuildingSpec_WithoutTag_Skips() {
      // The bare heuristic case: a spec-only natural (rock, badwater
      // residue, natural water source, natural ramp) has no component-
      // natural signal and no Keystone tag. The no-BuildingSpec
      // heuristic catches it as Skip.
      Assert.AreEqual(BlockObjectFootprint.Skip,
          BlockObjectClassifier.Classify(Signals(
              lacksBuildingSpec: true)));
    }

    #endregion

    #region Structural-path name heuristic — fires only on path voxels

    [TestMethod]
    public void StructuralPathName_OnPath_PromotesToNoAura() {
      // ZiplineBeam etc. — name matches, voxel is a path → NoAura.
      // Without this rule the BO would fall through to the path
      // fallback and be Skip'd, leaving the zipline's tile unsettled.
      Assert.AreEqual(BlockObjectFootprint.NoAura,
          BlockObjectClassifier.Classify(Signals(
              voxelIsPath: true,
              matchesStructuralPathName: true)));
    }

    [TestMethod]
    public void StructuralPathName_OffPath_HasNoEffect_FallsToDefaultBuilding() {
      // A name match on a non-path voxel is meaningless — the BO
      // would already classify as Building via the default fallback.
      // This pin guards against a future expansion of the heuristic
      // to non-path voxels (which would needlessly downgrade real
      // buildings whose names happen to contain a heuristic substring).
      Assert.AreEqual(BlockObjectFootprint.Building,
          BlockObjectClassifier.Classify(Signals(
              voxelIsPath: false,
              matchesStructuralPathName: true)));
    }

    #endregion

    #region Default fallback

    [TestMethod]
    public void NonPath_NoSignals_ClassifiesAsBuilding() {
      // A non-natural, non-tagged, non-enterable BO on a non-path
      // voxel defaults to Building. This is how decorative buildings
      // (without an EnterableSpec) classify under the existing rule.
      Assert.AreEqual(BlockObjectFootprint.Building,
          BlockObjectClassifier.Classify(Signals()));
    }

    [TestMethod]
    public void PathOnly_NoOtherSignals_ClassifiesAsSkip() {
      // A pure path tile (Path.blueprint, Stairs, Platform) has no
      // other signal — it contributes nothing to settlement.
      // REGRESSION-SENSITIVE: if this asserts Building or NoAura,
      // every path tile would start anchoring settlement.
      Assert.AreEqual(BlockObjectFootprint.Skip,
          BlockObjectClassifier.Classify(Signals(voxelIsPath: true)));
    }

    #endregion

  }

}
