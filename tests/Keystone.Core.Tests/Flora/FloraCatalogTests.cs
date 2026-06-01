using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Flora;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Flora {

  /// <summary>
  /// Locks down the public read API of <see cref="FloraCatalog"/> --
  /// lookup by name, kind counting, populate-replaces-prior-snapshot
  /// semantics. The catalog has no Timberborn dependency, so this test
  /// drives it with hand-built <see cref="FloraEntry"/> instances.
  /// </summary>
  [TestClass]
  public class FloraCatalogTests {

    #region Helpers

    private static FloraEntry MakeEntry(string name, FloraKind kind = FloraKind.GroundCover) =>
        new FloraEntry(
            blueprintName: name,
            templateName: null,
            faction: null,
            kind: kind,
            plantableGroups: Array.Empty<string>(),
            growthTimeInDays: null,
            daysToDieDry: null,
            minWaterHeight: null,
            maxWaterHeight: null,
            daysToDieFlooded: null,
            isCuttable: false,
            removeOnCut: null,
            isGatherable: false,
            yieldGrowthTimeInDays: null,
            cutYield: null,
            gatherYield: null);

    #endregion

    [TestMethod]
    public void EmptyCatalog_HasZeroCount_AndMissesAllLookups() {
      var c = new FloraCatalog();

      Assert.AreEqual(0, c.Count);
      Assert.IsFalse(c.Contains("Pine"));
      Assert.IsNull(c.Get("Pine"));
      Assert.AreEqual(0, c.CountOfKind(FloraKind.Tree));
    }

    [TestMethod]
    public void Populate_FillsCatalog_AndAllowsLookupByName() {
      var c = new FloraCatalog();
      c.Populate(new[] {
          MakeEntry("Pine", FloraKind.Tree),
          MakeEntry("Blueberry", FloraKind.Bush),
      });

      Assert.AreEqual(2, c.Count);
      Assert.IsTrue(c.Contains("Pine"));
      Assert.AreEqual(FloraKind.Tree, c.Get("Pine")!.Kind);
      Assert.AreEqual(FloraKind.Bush, c.Get("Blueberry")!.Kind);
    }

    [TestMethod]
    public void CountOfKind_ReportsPerKindCorrectly() {
      var c = new FloraCatalog();
      c.Populate(new[] {
          MakeEntry("Pine",      FloraKind.Tree),
          MakeEntry("Birch",     FloraKind.Tree),
          MakeEntry("Blueberry", FloraKind.Bush),
          MakeEntry("Wheat",     FloraKind.Crop),
          MakeEntry("Cattail",   FloraKind.GroundCover),
      });

      Assert.AreEqual(2, c.CountOfKind(FloraKind.Tree));
      Assert.AreEqual(1, c.CountOfKind(FloraKind.Bush));
      Assert.AreEqual(1, c.CountOfKind(FloraKind.Crop));
      Assert.AreEqual(1, c.CountOfKind(FloraKind.GroundCover));
    }

    [TestMethod]
    public void Populate_ReplacesPriorSnapshot() {
      // Re-population is allowed (e.g., for a future late-arriving spec
      // story). Verify the prior snapshot is discarded.
      var c = new FloraCatalog();
      c.Populate(new[] { MakeEntry("Pine", FloraKind.Tree) });
      Assert.AreEqual(1, c.Count);

      c.Populate(new[] { MakeEntry("Birch", FloraKind.Tree),
                         MakeEntry("Mushroom", FloraKind.GroundCover) });

      Assert.AreEqual(2, c.Count);
      Assert.IsFalse(c.Contains("Pine"));
      Assert.IsTrue(c.Contains("Birch"));
      Assert.IsTrue(c.Contains("Mushroom"));
    }

    [TestMethod]
    public void Entries_EnumeratesAllPopulatedRecords() {
      var c = new FloraCatalog();
      c.Populate(new[] {
          MakeEntry("Pine"),
          MakeEntry("Blueberry"),
          MakeEntry("Cattail"),
      });

      var names = c.Entries.Select(e => e.BlueprintName).ToList();

      CollectionAssert.AreEquivalent(
          new List<string> { "Pine", "Blueberry", "Cattail" },
          names);
    }

  }

}
