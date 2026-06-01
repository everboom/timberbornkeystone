using System;
using System.Linq;
using Keystone.Core.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Buildings {

  /// <summary>
  /// Locks down <see cref="BuildingCatalog"/>'s read API: lookup by
  /// blueprint name, role-overlap counting, planters-by-group join,
  /// populate-replaces semantics. The catalog has no Timberborn
  /// dependency, so this test drives it with hand-built
  /// <see cref="BuildingEntry"/> instances.
  /// </summary>
  [TestClass]
  public class BuildingCatalogTests {

    #region Helpers

    private static BuildingEntry MakeEntry(
        string name,
        BuildingRoles roles = BuildingRoles.Decoration,
        string? plantableGroup = null,
        string? faction = null) =>
        new BuildingEntry(
            blueprintName: name,
            templateName: null,
            faction: faction,
            roles: roles,
            plantableGroup: plantableGroup,
            rawCapabilities: Array.Empty<string>());

    #endregion

    [TestMethod]
    public void EmptyCatalog_HasZeroCount_AndMissesAllLookups() {
      var c = new BuildingCatalog();

      Assert.AreEqual(0, c.Count);
      Assert.IsFalse(c.Contains("Lodge"));
      Assert.IsNull(c.Get("Lodge"));
      Assert.AreEqual(0, c.PlantersByGroup("Forester").Count);
      Assert.AreEqual(0, c.CountWithAnyRole(BuildingRoles.Dwelling));
    }

    [TestMethod]
    public void Populate_FillsCatalog_AndAllowsLookupByName() {
      var c = new BuildingCatalog();
      c.Populate(new[] {
          MakeEntry("Lodge.Folktails", BuildingRoles.Dwelling),
          MakeEntry("Path.Folktails", BuildingRoles.Path),
      });

      Assert.AreEqual(2, c.Count);
      Assert.IsTrue(c.Contains("Lodge.Folktails"));
      Assert.AreEqual(BuildingRoles.Dwelling, c.Get("Lodge.Folktails")!.Roles);
      Assert.AreEqual("Folktails", c.Get("Lodge.Folktails")!.BlueprintName.Split('.')[1]);
    }

    [TestMethod]
    public void CountWithAnyRole_MatchesAnyOverlapInTheFlagSet() {
      var c = new BuildingCatalog();
      c.Populate(new[] {
          MakeEntry("Lodge",        BuildingRoles.Dwelling),
          MakeEntry("HybridLodge",  BuildingRoles.Dwelling | BuildingRoles.Path),
          MakeEntry("Path",         BuildingRoles.Path),
          MakeEntry("Forester",     BuildingRoles.Workplace | BuildingRoles.Farming),
          MakeEntry("Statue",       BuildingRoles.Decoration),
      });

      // Single-flag query: any entry whose Roles & Dwelling != 0.
      Assert.AreEqual(2, c.CountWithAnyRole(BuildingRoles.Dwelling));
      Assert.AreEqual(2, c.CountWithAnyRole(BuildingRoles.Path));
      Assert.AreEqual(1, c.CountWithAnyRole(BuildingRoles.Farming));

      // Multi-flag query: any entry that matches at least one of the asked flags.
      // Lodge (Dwelling), HybridLodge (Dwelling+Path), Path (Path) -> 3.
      Assert.AreEqual(3,
          c.CountWithAnyRole(BuildingRoles.Dwelling | BuildingRoles.Path));
    }

    [TestMethod]
    public void PlantersByGroup_IndexesByPlantableGroup() {
      var c = new BuildingCatalog();
      c.Populate(new[] {
          MakeEntry("Forester.Folktails",   BuildingRoles.Farming, plantableGroup: "Forester"),
          MakeEntry("Forester.IronTeeth",   BuildingRoles.Farming, plantableGroup: "Forester"),
          MakeEntry("Farmhouse.Folktails",  BuildingRoles.Farming, plantableGroup: "Farmhouse"),
          MakeEntry("Lodge.Folktails",      BuildingRoles.Dwelling),
      });

      var foresters = c.PlantersByGroup("Forester");
      Assert.AreEqual(2, foresters.Count);
      CollectionAssert.AreEquivalent(
          new[] { "Forester.Folktails", "Forester.IronTeeth" },
          foresters.Select(e => e.BlueprintName).ToList());

      var farmhouses = c.PlantersByGroup("Farmhouse");
      Assert.AreEqual(1, farmhouses.Count);
      Assert.AreEqual("Farmhouse.Folktails", farmhouses[0].BlueprintName);

      Assert.AreEqual(0, c.PlantersByGroup("Greenhouse").Count);
    }

    [TestMethod]
    public void Populate_ReplacesPriorSnapshot_AndRebuildsPlanterIndex() {
      var c = new BuildingCatalog();
      c.Populate(new[] { MakeEntry("Forester.Folktails", BuildingRoles.Farming, plantableGroup: "Forester") });
      Assert.AreEqual(1, c.PlantersByGroup("Forester").Count);

      // Re-populate with a different planter pointing at a different group.
      c.Populate(new[] { MakeEntry("Greenhouse.Folktails", BuildingRoles.Farming, plantableGroup: "Greenhouse") });

      // Old planter group fully cleared, new one indexed.
      Assert.AreEqual(0, c.PlantersByGroup("Forester").Count);
      Assert.AreEqual(1, c.PlantersByGroup("Greenhouse").Count);
    }

    [TestMethod]
    public void Entries_EnumeratesAllPopulatedRecords() {
      var c = new BuildingCatalog();
      c.Populate(new[] {
          MakeEntry("Lodge"),
          MakeEntry("Forester", BuildingRoles.Farming, plantableGroup: "Forester"),
          MakeEntry("Statue"),
      });

      var names = c.Entries.Select(e => e.BlueprintName).ToList();

      CollectionAssert.AreEquivalent(
          new[] { "Lodge", "Forester", "Statue" },
          names);
    }

  }

}
