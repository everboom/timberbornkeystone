using Keystone.Core.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Buildings {

  /// <summary>
  /// Pins the JSON shapes emitted by
  /// <see cref="NatureSpecJsonBuilder"/> against regression. The
  /// emitted strings flow through Timberborn's
  /// <c>SpecService.Deserialize</c> at load time; a missing comma,
  /// wrong field name, or shifted brace silently breaks Nature
  /// integration (the building's spec doesn't deserialize, the
  /// player gets no Nature need fill, no error is logged).
  ///
  /// <para>Each test names the exact JSON it expects so a future
  /// reader can see at a glance what the Spec deserializer is being
  /// fed. The "transparent + noAura both true" combination is
  /// reachable through this function but the Mod-side caller asserts
  /// against it — these tests document what would happen if the
  /// assertion were bypassed (transparent wins, no-aura is dropped).</para>
  /// </summary>
  [TestClass]
  public class NatureSpecJsonBuilderTests {

    #region BuildNeedCollectionAppend

    [TestMethod]
    public void NeedCollectionAppend_SingleBiome_EmitsExpectedShape() {
      var json = NatureSpecJsonBuilder.BuildNeedCollectionAppend(
          collectionId: "Folktails",
          needIdPrefix: "KeystoneNature.",
          biomes: new[] { "Forest" });
      Assert.AreEqual(
          "{\"NeedCollectionSpec\":{\"CollectionId\":\"Folktails\",\"Needs#append\":[\"KeystoneNature.Forest\"]}}",
          json);
    }

    [TestMethod]
    public void NeedCollectionAppend_MultipleBiomes_CommaSeparated() {
      var json = NatureSpecJsonBuilder.BuildNeedCollectionAppend(
          collectionId: "Folktails",
          needIdPrefix: "KeystoneNature.",
          biomes: new[] { "Forest", "Grassland", "Wetland" });
      Assert.AreEqual(
          "{\"NeedCollectionSpec\":{\"CollectionId\":\"Folktails\","
          + "\"Needs#append\":[\"KeystoneNature.Forest\",\"KeystoneNature.Grassland\",\"KeystoneNature.Wetland\"]}}",
          json);
    }

    [TestMethod]
    public void NeedCollectionAppend_EmptyBiomes_EmitsEmptyAppendArray() {
      // Defensive: caller's responsibility to skip emission when
      // there are no biomes, but if they don't, the JSON must still
      // be well-formed.
      var json = NatureSpecJsonBuilder.BuildNeedCollectionAppend(
          collectionId: "Folktails",
          needIdPrefix: "KeystoneNature.",
          biomes: new string[0]);
      Assert.AreEqual(
          "{\"NeedCollectionSpec\":{\"CollectionId\":\"Folktails\",\"Needs#append\":[]}}",
          json);
    }

    #endregion

    #region BuildBuildingSpec — footprint variants

    [TestMethod]
    public void BuildingSpec_NoAura_AppendsNoAuraSpec() {
      // The most common case in production today: every Nature
      // building uses NoAura.
      var json = NatureSpecJsonBuilder.BuildBuildingSpec(
          biomes: new[] { "Forest", "Grassland" },
          needIdPrefix: "KeystoneNature.",
          pointsPerHour: 4.0f,
          transparent: false,
          noAura: true);
      Assert.AreEqual(
          "{\"KeystoneNatureSourceSpec\":{\"Sources\":["
          + "{\"Biome\":\"Forest\",\"NeedId\":\"KeystoneNature.Forest\",\"PointsPerHour\":4.0},"
          + "{\"Biome\":\"Grassland\",\"NeedId\":\"KeystoneNature.Grassland\",\"PointsPerHour\":4.0}"
          + "]},\"KeystoneEcologyNoAuraSpec\":{}}",
          json);
    }

    [TestMethod]
    public void BuildingSpec_Transparent_AppendsTransparentSpec() {
      // The historical path; preserved for future use cases (and the
      // rare "Nature source that's actually wholly invisible to the
      // surveyor" case).
      var json = NatureSpecJsonBuilder.BuildBuildingSpec(
          biomes: new[] { "Wetland" },
          needIdPrefix: "KeystoneNature.",
          pointsPerHour: 4.0f,
          transparent: true,
          noAura: false);
      Assert.AreEqual(
          "{\"KeystoneNatureSourceSpec\":{\"Sources\":["
          + "{\"Biome\":\"Wetland\",\"NeedId\":\"KeystoneNature.Wetland\",\"PointsPerHour\":4.0}"
          + "]},\"KeystoneEcologyTransparentSpec\":{}}",
          json);
    }

    [TestMethod]
    public void BuildingSpec_NeitherFlag_EmitsSourceSpecOnly() {
      // "Settles with full aura" — for a Nature source that's
      // genuinely industrial settlement (RooftopTerrace on top of a
      // real lodge, etc.). No footprint marker appended.
      var json = NatureSpecJsonBuilder.BuildBuildingSpec(
          biomes: new[] { "Forest" },
          needIdPrefix: "KeystoneNature.",
          pointsPerHour: 4.0f,
          transparent: false,
          noAura: false);
      Assert.AreEqual(
          "{\"KeystoneNatureSourceSpec\":{\"Sources\":["
          + "{\"Biome\":\"Forest\",\"NeedId\":\"KeystoneNature.Forest\",\"PointsPerHour\":4.0}"
          + "]}}",
          json);
    }

    [TestMethod]
    public void BuildingSpec_TransparentDominatesNoAura_WhenBothTrue() {
      // The combination is a configuration error that the Mod-side
      // caller asserts against — but the builder is reachable with
      // both flags. Documents that transparent wins; if a future
      // reader sees BOTH specs emitted on a building, it's the
      // assertion that's been disabled, not this function.
      var json = NatureSpecJsonBuilder.BuildBuildingSpec(
          biomes: new[] { "Forest" },
          needIdPrefix: "KeystoneNature.",
          pointsPerHour: 4.0f,
          transparent: true,
          noAura: true);
      Assert.IsTrue(json.Contains("KeystoneEcologyTransparentSpec"),
          "Transparent must win the if/else chain.");
      Assert.IsFalse(json.Contains("KeystoneEcologyNoAuraSpec"),
          "NoAura must be dropped when transparent is also true.");
    }

    #endregion

    #region BuildBuildingSpec — formatting details

    [TestMethod]
    public void BuildingSpec_PointsPerHour_FormatsAsInvariantCultureF1() {
      // Critical: this is what flows into the JSON. If the host
      // culture comes with a comma decimal separator and we used
      // ToString() without InvariantCulture, the JSON would emit
      // "4,0" which fails deserialization. Pin the formatting.
      var json = NatureSpecJsonBuilder.BuildBuildingSpec(
          biomes: new[] { "Forest" },
          needIdPrefix: "KeystoneNature.",
          pointsPerHour: 4.0f,
          transparent: false,
          noAura: true);
      Assert.IsTrue(json.Contains("\"PointsPerHour\":4.0"),
          "PointsPerHour must use invariant-culture decimal point.");
      Assert.IsFalse(json.Contains("4,0"),
          "Comma decimal separator would break JSON parsing.");
    }

    [TestMethod]
    public void BuildingSpec_NonDefaultPointsPerHour_FlowsThrough() {
      // Confirms the value is taken from the parameter, not hard-
      // coded. If someone refactored and accidentally swapped the
      // parameter usage for a constant, this would fail.
      var json = NatureSpecJsonBuilder.BuildBuildingSpec(
          biomes: new[] { "Forest" },
          needIdPrefix: "KeystoneNature.",
          pointsPerHour: 12.5f,
          transparent: false,
          noAura: true);
      Assert.IsTrue(json.Contains("\"PointsPerHour\":12.5"),
          "PointsPerHour value should be the parameter, not a constant.");
    }

    [TestMethod]
    public void BuildingSpec_NeedIdPrefix_ParameterizedCorrectly() {
      // If someone hardcodes "KeystoneNature." instead of using the
      // parameter, a renamed prefix would silently break.
      var json = NatureSpecJsonBuilder.BuildBuildingSpec(
          biomes: new[] { "Forest" },
          needIdPrefix: "TestPrefix.",
          pointsPerHour: 4.0f,
          transparent: false,
          noAura: true);
      Assert.IsTrue(json.Contains("\"NeedId\":\"TestPrefix.Forest\""),
          "NeedId must use the supplied prefix.");
    }

    #endregion

  }

}
