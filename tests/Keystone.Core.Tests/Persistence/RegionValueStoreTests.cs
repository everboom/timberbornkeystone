using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Unit tests for <see cref="RegionValueStore"/>: round-trip,
  /// overwrite semantics, defensive null/empty handling, and the
  /// deterministic enumeration order that makes <see cref="SnapshotCodec.Encode"/>
  /// produce stable output regardless of insertion order.
  /// </summary>
  [TestClass]
  public class RegionValueStoreTests {

    #region Round-trip and overwrite

    [TestMethod]
    public void Set_Get_RoundTripsValue() {
      // Arrange
      var store = new RegionValueStore();

      // Act
      store.Set(new RegionId(3), "keystone.region.ageDays", 4.25f);

      // Assert
      Assert.AreEqual(4.25f, store.Get(new RegionId(3), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void Set_SameRegionAndKind_Overwrites() {
      // Arrange
      var store = new RegionValueStore();
      store.Set(new RegionId(0), "k", 1f);

      // Act
      store.Set(new RegionId(0), "k", 2f);

      // Assert
      Assert.AreEqual(2f, store.Get(new RegionId(0), "k"));
      Assert.AreEqual(1, store.Count, "overwrite shouldn't add a second entry");
    }

    [TestMethod]
    public void Get_UnknownKey_ReturnsNull() {
      // Arrange
      var store = new RegionValueStore();

      // Act
      var v = store.Get(new RegionId(7), "nope");

      // Assert
      Assert.IsNull(v);
    }

    #endregion

    #region Validation

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Set_EmptyKind_Throws() {
      // Arrange
      var store = new RegionValueStore();

      // Act
      store.Set(new RegionId(0), "", 1f);
    }

    #endregion

    #region Determinism / iteration order

    [TestMethod]
    public void Entries_IsSortedByRegionIdThenKind() {
      // Arrange — mixed insertion order.
      var store = new RegionValueStore();
      store.Set(new RegionId(2), "b", 1f);
      store.Set(new RegionId(1), "a", 2f);
      store.Set(new RegionId(2), "a", 3f);
      store.Set(new RegionId(1), "b", 4f);

      // Act
      var entries = store.Entries.ToList();

      // Assert — (1,a), (1,b), (2,a), (2,b).
      Assert.AreEqual(4, entries.Count);
      Assert.AreEqual(new RegionValueKey(new RegionId(1), "a"), entries[0].Key);
      Assert.AreEqual(new RegionValueKey(new RegionId(1), "b"), entries[1].Key);
      Assert.AreEqual(new RegionValueKey(new RegionId(2), "a"), entries[2].Key);
      Assert.AreEqual(new RegionValueKey(new RegionId(2), "b"), entries[3].Key);
    }

    #endregion

    #region Bulk operations

    [TestMethod]
    public void Clear_RemovesAllEntries() {
      // Arrange
      var store = new RegionValueStore();
      store.Set(new RegionId(0), "k", 1f);
      store.Set(new RegionId(1), "k", 2f);

      // Act
      store.Clear();

      // Assert
      Assert.AreEqual(0, store.Count);
      Assert.IsNull(store.Get(new RegionId(0), "k"));
    }

    [TestMethod]
    public void RemoveAllValuesFor_DropsEveryEntryUnderRegionId_OthersUntouched() {
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "k1", 1f);
      store.Set(new RegionId(3), "k2", 2f);
      store.Set(new RegionId(7), "k1", 5f);

      store.RemoveAllValuesFor(new RegionId(3));

      Assert.IsNull(store.Get(new RegionId(3), "k1"));
      Assert.IsNull(store.Get(new RegionId(3), "k2"));
      Assert.AreEqual(5f, store.Get(new RegionId(7), "k1"));
    }

    [TestMethod]
    public void RemoveAllValuesFor_UnknownId_NoOp() {
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "k1", 1f);

      store.RemoveAllValuesFor(new RegionId(99));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(1f, store.Get(new RegionId(3), "k1"));
    }

    [TestMethod]
    public void PruneToLiveRegions_DropsEntriesNotInLiveSet() {
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "k1", 1f);
      store.Set(new RegionId(7), "k1", 2f);
      store.Set(new RegionId(99), "k1", 3f); // dead region

      var pruned = store.PruneToLiveRegions(
          new HashSet<RegionId> { new RegionId(3), new RegionId(7) });

      Assert.AreEqual(1, pruned);
      Assert.AreEqual(1f, store.Get(new RegionId(3), "k1"));
      Assert.AreEqual(2f, store.Get(new RegionId(7), "k1"));
      Assert.IsNull(store.Get(new RegionId(99), "k1"));
    }

    [TestMethod]
    public void PruneToLiveRegions_AllAlive_NoChange() {
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "k1", 1f);
      store.Set(new RegionId(7), "k1", 2f);

      var pruned = store.PruneToLiveRegions(
          new HashSet<RegionId> { new RegionId(3), new RegionId(7) });

      Assert.AreEqual(0, pruned);
      Assert.AreEqual(2, store.Count);
    }

    [TestMethod]
    public void Inherit_CopiesEverySourceEntryToDestination() {
      // Arrange — source has multiple kinds; destination has nothing.
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "keystone.region.ageDays", 12.5f);
      store.Set(new RegionId(3), "keystone.region.forestScore", 0.7f);
      store.Set(new RegionId(99), "keystone.region.ageDays", 1.0f); // unrelated, shouldn't move

      // Act
      store.Inherit(source: new RegionId(3), destination: new RegionId(7));

      // Assert
      Assert.AreEqual(12.5f, store.Get(new RegionId(7), "keystone.region.ageDays"));
      Assert.AreEqual(0.7f, store.Get(new RegionId(7), "keystone.region.forestScore"));
      // Source entries are preserved (kept-id piece still uses them).
      Assert.AreEqual(12.5f, store.Get(new RegionId(3), "keystone.region.ageDays"));
      Assert.AreEqual(0.7f, store.Get(new RegionId(3), "keystone.region.forestScore"));
      // Unrelated regions are untouched.
      Assert.AreEqual(1.0f, store.Get(new RegionId(99), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void MergeFrom_MovesNonCollidingLoserEntriesAndDropsLoserKeys() {
      // Arrange — loser has two kinds, survivor has one different kind.
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "keystone.region.ageDays", 12.5f);
      store.Set(new RegionId(3), "keystone.region.forestScore", 0.7f);
      store.Set(new RegionId(7), "keystone.region.swampiness", 0.4f);
      store.Set(new RegionId(99), "keystone.region.ageDays", 1.0f); // unrelated

      // Act
      store.MergeFrom(loser: new RegionId(3), survivor: new RegionId(7));

      // Assert — loser entries gone, survivor adopted both, unrelated untouched.
      Assert.IsNull(store.Get(new RegionId(3), "keystone.region.ageDays"));
      Assert.IsNull(store.Get(new RegionId(3), "keystone.region.forestScore"));
      Assert.AreEqual(12.5f, store.Get(new RegionId(7), "keystone.region.ageDays"));
      Assert.AreEqual(0.7f, store.Get(new RegionId(7), "keystone.region.forestScore"));
      Assert.AreEqual(0.4f, store.Get(new RegionId(7), "keystone.region.swampiness"));
      Assert.AreEqual(1.0f, store.Get(new RegionId(99), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void MergeFrom_SurvivorWinsOnPerKindCollision() {
      // Arrange — loser and survivor both have entries for the same kind.
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "keystone.region.ageDays", 12.5f);
      store.Set(new RegionId(7), "keystone.region.ageDays", 99f);

      // Act
      store.MergeFrom(loser: new RegionId(3), survivor: new RegionId(7));

      // Assert — survivor's pre-merge value wins; loser entry is dropped.
      Assert.AreEqual(99f, store.Get(new RegionId(7), "keystone.region.ageDays"));
      Assert.IsNull(store.Get(new RegionId(3), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void MergeFrom_NoLoserEntries_LeavesStoreUnchanged() {
      var store = new RegionValueStore();
      store.Set(new RegionId(7), "keystone.region.ageDays", 5f);

      store.MergeFrom(loser: new RegionId(3), survivor: new RegionId(7));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(7), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void MergeFrom_LoserEqualsSurvivor_NoOp() {
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "keystone.region.ageDays", 5f);

      store.MergeFrom(loser: new RegionId(3), survivor: new RegionId(3));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(3), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void Inherit_OverwritesExistingDestinationEntries() {
      // Arrange — destination already has a stale value for a kind also on source.
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "keystone.region.ageDays", 12.5f);
      store.Set(new RegionId(7), "keystone.region.ageDays", 99f);

      // Act
      store.Inherit(source: new RegionId(3), destination: new RegionId(7));

      // Assert — destination's prior value is overwritten by the source's.
      Assert.AreEqual(12.5f, store.Get(new RegionId(7), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void Inherit_NoSourceEntries_LeavesStoreUnchanged() {
      var store = new RegionValueStore();
      store.Set(new RegionId(7), "keystone.region.ageDays", 5f);

      store.Inherit(source: new RegionId(3), destination: new RegionId(7));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(7), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void Inherit_SourceEqualsDestination_NoOp() {
      var store = new RegionValueStore();
      store.Set(new RegionId(3), "keystone.region.ageDays", 5f);

      store.Inherit(source: new RegionId(3), destination: new RegionId(3));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(3), "keystone.region.ageDays"));
    }

    [TestMethod]
    public void RehydrateFrom_ReplacesAllEntries() {
      // Arrange — store has prior content; rehydrate must wipe it.
      var store = new RegionValueStore();
      store.Set(new RegionId(0), "stale", 99f);
      store.Set(new RegionId(0), "alsoStale", 99f);

      var fresh = new List<KeyValuePair<RegionValueKey, float>> {
          new(new RegionValueKey(new RegionId(7), "kept"), 1f),
          new(new RegionValueKey(new RegionId(7), "also-kept"), 2f),
      };

      // Act
      store.RehydrateFrom(fresh);

      // Assert
      Assert.AreEqual(2, store.Count);
      Assert.IsNull(store.Get(new RegionId(0), "stale"));
      Assert.AreEqual(1f, store.Get(new RegionId(7), "kept"));
      Assert.AreEqual(2f, store.Get(new RegionId(7), "also-kept"));
    }

    #endregion

  }

}
