using System.Linq;
using Keystone.Core.Buildings.Factions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Buildings {

  [TestClass]
  public class FactionRegistryTests {

    #region Emberpelts farmhouse regression

    [TestMethod]
    public void IsNoAura_EmberpeltsFarmhouse_MatchesEitherCasing() {
      // Regression for the original bug: the Emberpelts farmhouse's runtime
      // Blueprint.Name is "Farmhouse" (lowercase h), while its catalog path
      // (and the old list entry) said "FarmHouse". The list now carries the
      // exact runtime spelling; the catalog-path casing still resolves via
      // the case-insensitive fallback.
      Assert.IsTrue(FactionRegistry.IsNoAura("Farmhouse.Emberpelts"));  // exact runtime name
      Assert.IsTrue(FactionRegistry.IsNoAura("FarmHouse.Emberpelts"));  // catalog-path casing -> fallback
    }

    #endregion

    #region FindCasingDrift

    [TestMethod]
    public void FindCasingDrift_DriftedName_YieldsCorrection() {
      // "lantern.folktails" matches the no-aura entry "Lantern.Folktails"
      // only via the case-insensitive fallback, so it's reported as drift.
      var drift = FactionRegistry.FindCasingDrift(new[] { "lantern.folktails" }).ToList();
      Assert.AreEqual(1, drift.Count);
      Assert.AreEqual("lantern.folktails", drift[0].Actual);
      Assert.AreEqual("Lantern.Folktails", drift[0].Listed);
      Assert.AreEqual("no-aura", drift[0].List);
    }

    [TestMethod]
    public void FindCasingDrift_ExactName_YieldsNothing() {
      // An exact-casing match is not drift.
      var drift = FactionRegistry.FindCasingDrift(new[] { "Lantern.Folktails" }).ToList();
      Assert.AreEqual(0, drift.Count);
    }

    [TestMethod]
    public void FindCasingDrift_UnrelatedNames_YieldNothing() {
      var drift = FactionRegistry.FindCasingDrift(
          new[] { "Lodge.Folktails", "NotAThing", "" }).ToList();
      Assert.AreEqual(0, drift.Count);
    }

    #endregion
  }

}
