using System.Linq;
using Keystone.Core.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Buildings {

  /// <summary>
  /// Pins the two name-based ecology policies in
  /// <see cref="BlueprintNamePolicy"/>: the blocking-natural whitelist
  /// (exact match against <see cref="BlueprintNamePolicy.BlockingNaturalNames"/>)
  /// and the structural-path substring heuristic
  /// (<see cref="BlueprintNamePolicy.StructuralPathTokens"/>). Each
  /// test names the regression it defends — a name silently dropping
  /// out of the blocking set, an accidental substring widening, the
  /// case-sensitivity contract.
  ///
  /// <para>Also surfaces collisions by inspection: if a name ever
  /// appears in BOTH the blocking set and matches the structural-path
  /// substring, that would be a categorisation bug (a natural
  /// obstacle classified as a building). Today no overlap exists; the
  /// test class is the place to add an "AssertNoOverlap" check if the
  /// surface grows.</para>
  /// </summary>
  [TestClass]
  public class BlueprintNamePolicyTests {

    #region IsBlockingNatural — exact match against the whitelist

    [TestMethod]
    public void IsBlockingNatural_CanonicalBlockingNames_All_ReturnTrue() {
      // REGRESSION-SENSITIVE: each of these matches a real vanilla
      // blueprint whose tile MUST be excluded from regions. Dropping
      // any of them would cause natural dams / blockages / geysers
      // to silently merge into adjacent regions.
      Assert.IsTrue(BlueprintNamePolicy.IsBlockingNatural("Blockage"));
      Assert.IsTrue(BlueprintNamePolicy.IsBlockingNatural("NaturalDam"));
      Assert.IsTrue(BlueprintNamePolicy.IsBlockingNatural("NaturalOverhang2x1"));
      Assert.IsTrue(BlueprintNamePolicy.IsBlockingNatural("NaturalOverhang3x1"));
      Assert.IsTrue(BlueprintNamePolicy.IsBlockingNatural("NaturalOverhang4x1"));
      Assert.IsTrue(BlueprintNamePolicy.IsBlockingNatural("UnstableCore"));
      Assert.IsTrue(BlueprintNamePolicy.IsBlockingNatural("GeothermalField"));
    }

    [TestMethod]
    public void IsBlockingNatural_UnrelatedNames_ReturnFalse() {
      // A few canonical non-blocking blueprints — both vanilla
      // structures and naturals that are passable. None should
      // collide with the blocking set.
      Assert.IsFalse(BlueprintNamePolicy.IsBlockingNatural("Pine"));        // passable natural
      Assert.IsFalse(BlueprintNamePolicy.IsBlockingNatural("Lodge.Folktails"));
      Assert.IsFalse(BlueprintNamePolicy.IsBlockingNatural("Path"));
      Assert.IsFalse(BlueprintNamePolicy.IsBlockingNatural("RuinColumn"));  // intentionally not blocking
      Assert.IsFalse(BlueprintNamePolicy.IsBlockingNatural(""));
    }

    [TestMethod]
    public void IsBlockingNatural_CaseSensitive() {
      // Exact ordinal match — the whitelist is built with
      // StringComparer.Ordinal because Timberborn's Blueprint.Name is
      // a canonical filename-derived identifier; a different case
      // would indicate a typo or a mod content collision.
      Assert.IsFalse(BlueprintNamePolicy.IsBlockingNatural("blockage"));
      Assert.IsFalse(BlueprintNamePolicy.IsBlockingNatural("BLOCKAGE"));
      Assert.IsFalse(BlueprintNamePolicy.IsBlockingNatural("naturaldam"));
    }

    #endregion

    #region IsStructuralPath — substring match (case-insensitive)

    [TestMethod]
    public void IsStructuralPath_KnownVanillaZiplines_All_ReturnTrue() {
      // The three Zipline family members in vanilla.
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("ZiplineBeam.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("ZiplinePylon.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("ZiplineStation.Folktails"));
    }

    [TestMethod]
    public void IsStructuralPath_KnownVanillaTubeways_All_ReturnTrue() {
      // IronTeeth's tubeway equivalents — the "Tube" token catches
      // Tubeway, TubewayStation, VerticalTubeway.
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("Tubeway.IronTeeth"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("TubewayStation.IronTeeth"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("VerticalTubeway.IronTeeth"));
    }

    [TestMethod]
    public void IsStructuralPath_KnownVanillaOverhangs_All_ReturnTrue() {
      // Overhang2x1..6x1 in both factions.
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("Overhang2x1.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("Overhang6x1.IronTeeth"));
    }

    [TestMethod]
    public void IsStructuralPath_KnownVanillaSuspensionBridges_All_ReturnTrue() {
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("SuspensionBridge1x1.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("SuspensionBridge6x1.IronTeeth"));
    }

    [TestMethod]
    public void IsStructuralPath_LeafCoatsBranchBridges_All_ReturnTrue() {
      // LeafCoats tree-faction branch bridges connect elevated tree
      // builds; functionally identical to SuspensionBridges. The
      // "Branch.Bridge" token catches the 1x1..6x1 series plus the
      // Branch.Bridge.Stairs variant.
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("Branch.Bridge1x1.LeafCoats"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("Branch.Bridge6x1.LeafCoats"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("Branch.Bridge.Stairs.LeafCoats"));
    }

    [TestMethod]
    public void IsStructuralPath_BranchBridgeToken_DoesNotMatchBareBranch() {
      // REGRESSION-SENSITIVE: the token is "Branch.Bridge" (specific)
      // not "Branch" (broad). A bare "Branch.LeafCoats" element and
      // "ContemplationSpot.Branch.LeafCoats" must NOT be promoted to
      // no-aura by this heuristic — they have their own (default /
      // Nature) categorisations.
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("Branch.LeafCoats"));
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("ContemplationSpot.Branch.LeafCoats"));
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("TreeTrunk.Side.Branch.LeafCoats"));
    }

    [TestMethod]
    public void IsStructuralPath_CaseInsensitive() {
      // Substring match uses OrdinalIgnoreCase; protects against
      // future mod content with varied capitalisation.
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("ziplinebeam"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("VERTICALTUBEWAY"));
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("Suspensionbridge1x1"));
    }

    [TestMethod]
    public void IsStructuralPath_UnrelatedNames_ReturnFalse() {
      // Canonical non-matching names — vanilla buildings whose names
      // should never accidentally match a substring.
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("Path"));
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("Stairs.Folktails"));
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("Platform.Folktails"));
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("Lodge.Folktails"));
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("Gate.Folktails"));
    }

    [TestMethod]
    public void IsStructuralPath_EmptyOrNull_ReturnsFalse() {
      // Adapter sometimes can't resolve a blueprint name (returns
      // empty) — must not panic and must not match.
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath(""));
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath(null!));
    }

    #endregion

    #region Structural-path exclusion list (opt-out for false positives)

    [TestMethod]
    public void StructuralPathExclusions_ContainsDocumentedEmberpeltsEntries() {
      // The exclusion list opts specific mod blueprints out of the
      // substring-path heuristic. Today's two entries are Emberpelts'
      // DistrictCrossingTubeway / DistrictGateTubeway: their name
      // contains "Tubeway" so the heuristic would auto-promote them
      // to BuildingNoAura, but they're functionally district gates
      // with full settle semantics.
      // REGRESSION-SENSITIVE: if either name silently drops from the
      // exclusion list, the corresponding Emberpelts building flips
      // from settled to no-aura without anyone noticing in-game.
      Assert.IsTrue(
          BlueprintNamePolicy.StructuralPathExclusions.Contains(
              "DistrictCrossingTubeway.Emberpelts"),
          "DistrictCrossingTubeway.Emberpelts must stay in the exclusion list "
          + "or the structural-path heuristic will reclassify it as no-aura.");
      Assert.IsTrue(
          BlueprintNamePolicy.StructuralPathExclusions.Contains(
              "DistrictGateTubeway.Emberpelts"),
          "DistrictGateTubeway.Emberpelts must stay in the exclusion list "
          + "or the structural-path heuristic will reclassify it as no-aura.");
    }

    [TestMethod]
    public void StructuralPathExclusions_EmberpeltsEntries_RoundTripThroughIsStructuralPath() {
      // End-to-end: with the production exclusion set, the two
      // Emberpelts gate-tubeway names should NOT classify as
      // structural-path despite matching the "Tube" substring token.
      Assert.IsFalse(
          BlueprintNamePolicy.IsStructuralPath("DistrictCrossingTubeway.Emberpelts"));
      Assert.IsFalse(
          BlueprintNamePolicy.IsStructuralPath("DistrictGateTubeway.Emberpelts"));
      // Sanity: other Tube-named Emberpelts blueprints still match.
      Assert.IsTrue(
          BlueprintNamePolicy.IsStructuralPath("Tubeway.Emberpelts"));
      Assert.IsTrue(
          BlueprintNamePolicy.IsStructuralPath("TubewayLevee.Emberpelts"));
    }

    [TestMethod]
    public void IsStructuralPath_WithExclusion_ReturnsFalseEvenOnSubstringMatch() {
      // Verifies the opt-out actually short-circuits the substring
      // match. Hypothetical mod blueprint "TubeFactory" matches the
      // "Tube" token; adding it to the exclusions makes IsStructuralPath
      // return false. REGRESSION-SENSITIVE: if a future refactor
      // reorders the exclusion check past the substring loop, this
      // test catches it.
      var exclusions = new System.Collections.Generic.HashSet<string> {
        "TubeFactory",
      };
      Assert.IsFalse(BlueprintNamePolicy.IsStructuralPath("TubeFactory", exclusions),
          "Name in exclusion list should suppress the substring match.");
      // Sanity: other Tube-named blueprints still match.
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("Tubeway.IronTeeth", exclusions),
          "Other Tube-named blueprints should still match when not in exclusions.");
    }

    [TestMethod]
    public void IsStructuralPath_ExclusionIsCaseSensitive() {
      // Exclusion uses ordinal (case-sensitive) match because
      // Timberborn's Blueprint.Name is a canonical filename-derived
      // identifier. A different-cased entry wouldn't match the real
      // blueprint and would be a typo, not an intended opt-out.
      var exclusions = new System.Collections.Generic.HashSet<string> {
        "TubeFactory",
      };
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("tubefactory", exclusions),
          "Lower-cased name doesn't match the exclusion (ordinal compare) — substring still wins.");
    }

    #endregion

    #region Keystone-tagged transparent/no-aura name lookups

    [TestMethod]
    public void IsTransparentByName_CanonicalEntries_ReturnTrue() {
      // Anchor a couple of well-known entries against the list.
      // REGRESSION-SENSITIVE: if any of these silently drop out of
      // the list, the corresponding buildings would start sterilizing
      // their tiles (transparency would no longer apply).
      Assert.IsTrue(BlueprintNamePolicy.IsTransparentByName("Beehive.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsTransparentByName("Scarecrow.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsTransparentByName("Dynamite.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsTransparentByName("TripleDynamite.IronTeeth"));
    }

    [TestMethod]
    public void IsTransparentByName_UnrelatedNames_ReturnFalse() {
      Assert.IsFalse(BlueprintNamePolicy.IsTransparentByName("Lodge.Folktails"));
      Assert.IsFalse(BlueprintNamePolicy.IsTransparentByName("Lantern.Folktails"));  // in NoAura list, not transparent
      Assert.IsFalse(BlueprintNamePolicy.IsTransparentByName(""));
      Assert.IsFalse(BlueprintNamePolicy.IsTransparentByName(null!));
    }

    [TestMethod]
    public void IsNoAuraByName_CanonicalEntries_ReturnTrue() {
      // Coverage of the major categories in the no-aura list:
      // designation flags, wild-resource production, farmhouses,
      // decorations, automation sensors. If any of these drops out,
      // the corresponding building's aura would silently start
      // sterilizing neighbors.
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("GathererFlag.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("Forester.IronTeeth"));
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("FarmHouse.IronTeeth"));
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("Lantern.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("StreamGauge.Folktails"));
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("WoodFence.IronTeeth"));
    }

    [TestMethod]
    public void IsNoAuraByName_UnrelatedNames_ReturnFalse() {
      Assert.IsFalse(BlueprintNamePolicy.IsNoAuraByName("Lodge.Folktails"));
      Assert.IsFalse(BlueprintNamePolicy.IsNoAuraByName("Beehive.Folktails"));  // in transparent list, not no-aura
      Assert.IsFalse(BlueprintNamePolicy.IsNoAuraByName(""));
      Assert.IsFalse(BlueprintNamePolicy.IsNoAuraByName(null!));
    }

    [TestMethod]
    public void IsNoAuraByName_ExactFirstWithCaseInsensitiveFallback() {
      // DESIGN CHANGE (v0.6.6): the no-aura check was an ordinal compare;
      // it is now exact-first with a case-insensitive fallback. A name
      // that differs only by case from a listed entry now classifies as
      // no-aura. This tolerates casing drift between our hand-maintained
      // lists and a mod's real runtime Blueprint.Name -- the concrete
      // trigger was Emberpelts shipping "Farmhouse" while its catalog
      // path (and our list) said "FarmHouse". The exact spelling still
      // wins when present; the fallback only fires when there's no exact
      // match (see CaseTolerantNameSet), and a fired fallback is reported
      // as casing drift by FactionRegistry.FindCasingDrift.
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("Lantern.Folktails"));  // exact
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("lantern.folktails"));  // CI fallback
      Assert.IsTrue(BlueprintNamePolicy.IsNoAuraByName("LANTERN.FOLKTAILS"));  // CI fallback
      // A name that doesn't fold to any entry still returns false.
      Assert.IsFalse(BlueprintNamePolicy.IsNoAuraByName("Lodge.Folktails"));
    }

    [TestMethod]
    public void TransparentBuildings_AreNotBlockingNaturals() {
      // REGRESSION-SENSITIVE: a Transparent-named building also
      // appearing in BlockingNaturalNames would route through the
      // region-exclusion path (excluded from region) AND the
      // ecology-transparent path (skip the BO entirely) at the same
      // time. Today the names are visually disjoint; pinning catches
      // a future addition collision.
      foreach (var name in BlueprintNamePolicy.TransparentBuildingNames) {
        Assert.IsFalse(
            BlueprintNamePolicy.IsBlockingNatural(name),
            $"'{name}' appears in BOTH TransparentBuildingNames AND "
            + "BlockingNaturalNames. Pick one categorisation.");
      }
    }

    [TestMethod]
    public void NoAuraBuildings_AreNotBlockingNaturals() {
      // REGRESSION-SENSITIVE: a NoAura-named building also appearing
      // in BlockingNaturalNames would double-categorise (no-aura
      // building AND region-blocking natural). Today the names are
      // visually disjoint; pinning catches a future collision.
      foreach (var name in BlueprintNamePolicy.NoAuraBuildingNames) {
        Assert.IsFalse(
            BlueprintNamePolicy.IsBlockingNatural(name),
            $"'{name}' appears in BOTH NoAuraBuildingNames AND "
            + "BlockingNaturalNames. Pick one categorisation.");
      }
    }

    [TestMethod]
    public void TransparentAndNoAura_AreDisjoint() {
      // REGRESSION-SENSITIVE: a name in both lists is a configuration
      // bug — the adapter would set both signals, but the classifier's
      // priority gives Transparent precedence, so the building would
      // silently classify as transparent instead of no-aura (or vice
      // versa). Pin the lists are disjoint.
      foreach (var transparent in BlueprintNamePolicy.TransparentBuildingNames) {
        Assert.IsFalse(
            BlueprintNamePolicy.IsNoAuraByName(transparent),
            $"'{transparent}' appears in BOTH TransparentBuildingNames AND "
            + "NoAuraBuildingNames. Pick one categorisation.");
      }
    }

    #endregion

    #region Cross-policy: known benign overlap

    [TestMethod]
    public void KnownOverlap_NaturalOverhangAlsoMatchesStructuralPath() {
      // The blocking-natural set includes "NaturalOverhang2x1" etc.,
      // and the structural-path substring "Overhang" matches them.
      // This is a deliberate-but-benign collision: in the live
      // classification pipeline (BlockObjectClassifier.Classify), the
      // IsNatural signal is the highest-priority skip, so a
      // NaturalOverhang is short-circuited to Skip before the
      // structural-path check is ever consulted. Tightening the
      // substring to avoid the collision would risk missing future
      // mod-content variants whose names contain "Overhang"; the
      // pipeline's priority order handles it cleanly. This test
      // documents the overlap as expected so future contributors
      // don't try to "fix" the substring.
      Assert.IsTrue(BlueprintNamePolicy.IsBlockingNatural("NaturalOverhang2x1"),
          "Documented blocking-natural — must stay in the blocking set.");
      Assert.IsTrue(BlueprintNamePolicy.IsStructuralPath("NaturalOverhang2x1"),
          "Substring match is expected; pipeline's Natural-first priority handles it.");

      // No OTHER blocking-natural overlaps with the substring set:
      // Blockage / NaturalDam / UnstableCore / GeothermalField are
      // structurally disjoint. Catches a future addition that would
      // double-categorise.
      foreach (var name in BlueprintNamePolicy.BlockingNaturalNames) {
        if (name.IndexOf("Overhang", System.StringComparison.OrdinalIgnoreCase) >= 0) {
          continue;  // the documented exception
        }
        Assert.IsFalse(
            BlueprintNamePolicy.IsStructuralPath(name),
            $"Unexpected substring overlap: blocking-natural '{name}' matches the "
            + "structural-path heuristic, and is not the documented NaturalOverhang case. "
            + "Either pick one categorisation or extend the documented-exception list.");
      }
    }

    #endregion

  }

}
