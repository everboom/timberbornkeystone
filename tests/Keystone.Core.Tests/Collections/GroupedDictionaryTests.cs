using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Collections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Collections {

  /// <summary>
  /// Tests for <see cref="GroupedDictionary{TKey,TGroup,TValue}"/>. The
  /// load-bearing property is the invariant — the group index must stay
  /// consistent with the primary map across every mutation — because the
  /// whole point of the type is to make that consistency impossible to break
  /// by hand. The grouped key here is a string prefix (the part before
  /// <c>':'</c>), standing in for the real (footprint / region) projections
  /// the persistence stores use.
  /// </summary>
  [TestClass]
  public sealed class GroupedDictionaryTests {

    #region Helpers

    /// <summary>A dictionary grouping <c>"group:item"</c> keys by their
    /// prefix.</summary>
    private static GroupedDictionary<string, string, int> NewDict() =>
        new(key => key.Substring(0, key.IndexOf(':')));

    /// <summary>Materialise a group's entries into a sorted list of keys so
    /// assertions are order-independent.</summary>
    private static List<string> GroupKeys(GroupedDictionary<string, string, int> d, string group) =>
        d.EntriesForGroup(group).Select(kv => kv.Key).OrderBy(k => k).ToList();

    #endregion

    #region Add / overwrite

    [TestMethod]
    public void Set_AddsEntryAndIndexesByGroup() {
      // Arrange
      var d = NewDict();

      // Act
      d["a:1"] = 10;
      d["a:2"] = 20;
      d["b:1"] = 30;

      // Assert
      Assert.AreEqual(3, d.Count);
      Assert.AreEqual(2, d.GroupCount);
      CollectionAssert.AreEqual(new[] { "a:1", "a:2" }, GroupKeys(d, "a"));
      CollectionAssert.AreEqual(new[] { "b:1" }, GroupKeys(d, "b"));
    }

    [TestMethod]
    public void Set_Overwrite_DoesNotDuplicateInGroup() {
      // Arrange
      var d = NewDict();
      d["a:1"] = 10;

      // Act
      d["a:1"] = 99;

      // Assert
      Assert.AreEqual(1, d.Count);
      Assert.AreEqual(99, d["a:1"]);
      CollectionAssert.AreEqual(new[] { "a:1" }, GroupKeys(d, "a"));
    }

    #endregion

    #region Remove

    [TestMethod]
    public void Remove_DropsEntryAndDeindexes() {
      // Arrange
      var d = NewDict();
      d["a:1"] = 10;
      d["a:2"] = 20;

      // Act
      var removed = d.Remove("a:1");

      // Assert
      Assert.IsTrue(removed);
      Assert.AreEqual(1, d.Count);
      Assert.IsFalse(d.ContainsKey("a:1"));
      CollectionAssert.AreEqual(new[] { "a:2" }, GroupKeys(d, "a"));
    }

    [TestMethod]
    public void Remove_LastKeyInGroup_PrunesGroup() {
      // Arrange
      var d = NewDict();
      d["a:1"] = 10;

      // Act
      d.Remove("a:1");

      // Assert: the group is gone, not left as an empty bucket.
      Assert.AreEqual(0, d.GroupCount);
      Assert.IsNull(d.KeysForGroup("a"));
      Assert.IsFalse(d.EntriesForGroup("a").Any());
    }

    [TestMethod]
    public void Remove_AbsentKey_ReturnsFalseAndLeavesStoreIntact() {
      // Arrange
      var d = NewDict();
      d["a:1"] = 10;

      // Act
      var removed = d.Remove("a:2");

      // Assert
      Assert.IsFalse(removed);
      Assert.AreEqual(1, d.Count);
      CollectionAssert.AreEqual(new[] { "a:1" }, GroupKeys(d, "a"));
    }

    #endregion

    #region Queries

    [TestMethod]
    public void EntriesForGroup_UnknownGroup_YieldsNothing() {
      // Arrange
      var d = NewDict();
      d["a:1"] = 10;

      // Act / Assert
      Assert.IsFalse(d.EntriesForGroup("zzz").Any());
    }

    [TestMethod]
    public void EntriesForGroup_ReturnsValuesNotJustKeys() {
      // Arrange
      var d = NewDict();
      d["a:1"] = 10;
      d["a:2"] = 20;

      // Act
      var byKey = d.EntriesForGroup("a").ToDictionary(kv => kv.Key, kv => kv.Value);

      // Assert
      Assert.AreEqual(10, byKey["a:1"]);
      Assert.AreEqual(20, byKey["a:2"]);
    }

    [TestMethod]
    public void TryGetValue_HitAndMiss() {
      // Arrange
      var d = NewDict();
      d["a:1"] = 10;

      // Act / Assert
      Assert.IsTrue(d.TryGetValue("a:1", out var v));
      Assert.AreEqual(10, v);
      Assert.IsFalse(d.TryGetValue("a:2", out _));
    }

    #endregion

    #region Clear

    [TestMethod]
    public void Clear_EmptiesBothPrimaryAndIndex() {
      // Arrange
      var d = NewDict();
      d["a:1"] = 10;
      d["b:1"] = 20;

      // Act
      d.Clear();

      // Assert
      Assert.AreEqual(0, d.Count);
      Assert.AreEqual(0, d.GroupCount);
      Assert.IsFalse(d.EntriesForGroup("a").Any());
      Assert.IsFalse(d.Entries.Any());
    }

    #endregion

    #region Invariant under churn

    [TestMethod]
    public void Invariant_HoldsAfterInterleavedAddsAndRemoves() {
      // Arrange
      var d = NewDict();

      // Act: churn — add a batch, remove half, re-add, across several groups.
      for (var g = 0; g < 5; g++) {
        for (var i = 0; i < 10; i++) {
          d[$"g{g}:{i}"] = g * 100 + i;
        }
      }
      for (var g = 0; g < 5; g++) {
        for (var i = 0; i < 10; i += 2) {
          d.Remove($"g{g}:{i}");
        }
      }
      for (var g = 0; g < 5; g++) {
        d[$"g{g}:0"] = -1; // re-add one removed key per group
      }

      // Assert: reconstruct the expected group→keys map from the flat Entries
      // view and check it matches what the index reports for every group.
      var expected = d.Entries
          .GroupBy(kv => kv.Key.Substring(0, kv.Key.IndexOf(':')))
          .ToDictionary(grp => grp.Key, grp => grp.Select(kv => kv.Key).OrderBy(k => k).ToList());

      Assert.AreEqual(expected.Count, d.GroupCount,
          "GroupCount must equal the number of distinct groups present in Entries.");
      foreach (var (group, keys) in expected) {
        CollectionAssert.AreEqual(keys, GroupKeys(d, group),
            $"Index for group '{group}' diverged from the primary map.");
      }
    }

    #endregion

  }

}
