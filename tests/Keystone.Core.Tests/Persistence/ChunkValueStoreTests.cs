using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Unit tests for <see cref="ChunkValueStore"/>: round-trip,
  /// overwrite semantics, defensive null/empty handling, and the
  /// deterministic enumeration order that makes <see cref="SnapshotCodec.Encode"/>
  /// produce stable output regardless of insertion order.
  /// </summary>
  [TestClass]
  public class ChunkValueStoreTests {

    #region Round-trip and overwrite

    [TestMethod]
    public void Set_Get_RoundTripsValue() {
      // Arrange
      var store = new ChunkValueStore();

      // Act
      store.Set(new RegionId(3), 4, 5, "keystone.chunk.test.demo", 4.25f);

      // Assert
      Assert.AreEqual(4.25f, store.Get(new RegionId(3), 4, 5, "keystone.chunk.test.demo"));
    }

    [TestMethod]
    public void Set_SameKey_Overwrites() {
      // Arrange
      var store = new ChunkValueStore();
      store.Set(new RegionId(0), 1, 2, "k", 1f);

      // Act
      store.Set(new RegionId(0), 1, 2, "k", 2f);

      // Assert
      Assert.AreEqual(2f, store.Get(new RegionId(0), 1, 2, "k"));
      Assert.AreEqual(1, store.Count, "overwrite shouldn't add a second entry");
    }

    [TestMethod]
    public void Get_UnknownKey_ReturnsNull() {
      // Arrange
      var store = new ChunkValueStore();

      // Act
      var v = store.Get(new RegionId(7), 0, 0, "nope");

      // Assert
      Assert.IsNull(v);
    }

    #endregion

    #region Validation

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Set_EmptyKind_Throws() {
      // Arrange
      var store = new ChunkValueStore();

      // Act
      store.Set(new RegionId(0), 0, 0, "", 1f);
    }

    #endregion

    #region Determinism / iteration order

    [TestMethod]
    public void SortedSnapshot_IsSortedByRegionThenChunkXThenChunkYThenKind() {
      // Arrange — mixed insertion order spanning two regions, two chunk
      // X's, two chunk Y's, two kinds.
      var store = new ChunkValueStore();
      store.Set(new RegionId(2), 1, 0, "b", 1f);
      store.Set(new RegionId(1), 0, 1, "a", 2f);
      store.Set(new RegionId(2), 0, 0, "a", 3f);
      store.Set(new RegionId(1), 0, 0, "b", 4f);
      store.Set(new RegionId(1), 0, 0, "a", 5f);
      store.Set(new RegionId(1), 1, 0, "a", 6f);

      // Act
      var entries = store.SortedSnapshot();

      // Assert — primary by RegionId, then ChunkX, then ChunkY, then Kind.
      Assert.AreEqual(6, entries.Count);
      Assert.AreEqual(new ChunkValueKey(new RegionId(1), 0, 0, "a"), entries[0].Key);
      Assert.AreEqual(new ChunkValueKey(new RegionId(1), 0, 0, "b"), entries[1].Key);
      Assert.AreEqual(new ChunkValueKey(new RegionId(1), 0, 1, "a"), entries[2].Key);
      Assert.AreEqual(new ChunkValueKey(new RegionId(1), 1, 0, "a"), entries[3].Key);
      Assert.AreEqual(new ChunkValueKey(new RegionId(2), 0, 0, "a"), entries[4].Key);
      Assert.AreEqual(new ChunkValueKey(new RegionId(2), 1, 0, "b"), entries[5].Key);
    }

    #endregion

    #region Bulk operations

    [TestMethod]
    public void Clear_RemovesAllEntries() {
      // Arrange
      var store = new ChunkValueStore();
      store.Set(new RegionId(0), 0, 0, "k", 1f);
      store.Set(new RegionId(1), 1, 1, "k", 2f);

      // Act
      store.Clear();

      // Assert
      Assert.AreEqual(0, store.Count);
      Assert.IsNull(store.Get(new RegionId(0), 0, 0, "k"));
    }

    [TestMethod]
    public void RehydrateFrom_ReplacesAllEntries() {
      // Arrange — store has prior content; rehydrate must wipe it.
      var store = new ChunkValueStore();
      store.Set(new RegionId(0), 0, 0, "stale", 99f);
      store.Set(new RegionId(0), 1, 1, "alsoStale", 99f);

      var fresh = new List<KeyValuePair<ChunkValueKey, float>> {
          new(new ChunkValueKey(new RegionId(7), 2, 3, "kept"), 1f),
          new(new ChunkValueKey(new RegionId(7), 2, 3, "also-kept"), 2f),
      };

      // Act
      store.RehydrateFrom(fresh);

      // Assert
      Assert.AreEqual(2, store.Count);
      Assert.IsNull(store.Get(new RegionId(0), 0, 0, "stale"));
      Assert.AreEqual(1f, store.Get(new RegionId(7), 2, 3, "kept"));
      Assert.AreEqual(2f, store.Get(new RegionId(7), 2, 3, "also-kept"));
    }

    [TestMethod]
    public void Inherit_CopiesEverySourceEntryToDestination() {
      // Arrange — source has multiple kinds across multiple chunks;
      // destination has nothing.
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.test.demo", 12.5f);
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.forestScore", 0.7f);
      store.Set(new RegionId(3), 1, 2, "keystone.chunk.test.demo", 4.0f);
      store.Set(new RegionId(99), 0, 0, "keystone.chunk.test.demo", 1.0f); // unrelated

      // Act
      store.Inherit(source: new RegionId(3), destination: new RegionId(7));

      // Assert
      Assert.AreEqual(12.5f, store.Get(new RegionId(7), 0, 0, "keystone.chunk.test.demo"));
      Assert.AreEqual(0.7f, store.Get(new RegionId(7), 0, 0, "keystone.chunk.forestScore"));
      Assert.AreEqual(4.0f, store.Get(new RegionId(7), 1, 2, "keystone.chunk.test.demo"));
      // Source entries are preserved (kept-id piece still uses them).
      Assert.AreEqual(12.5f, store.Get(new RegionId(3), 0, 0, "keystone.chunk.test.demo"));
      Assert.AreEqual(0.7f, store.Get(new RegionId(3), 0, 0, "keystone.chunk.forestScore"));
      Assert.AreEqual(4.0f, store.Get(new RegionId(3), 1, 2, "keystone.chunk.test.demo"));
      // Unrelated regions are untouched.
      Assert.AreEqual(1.0f, store.Get(new RegionId(99), 0, 0, "keystone.chunk.test.demo"));
    }

    [TestMethod]
    public void MergeFrom_MovesNonCollidingLoserEntriesAndDropsLoserKeys() {
      // Arrange — loser has entries on two chunks; survivor has one
      // entry on a different chunk.
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.test.demo", 12.5f);
      store.Set(new RegionId(3), 1, 2, "keystone.chunk.test.demo", 4.0f);
      store.Set(new RegionId(7), 5, 5, "keystone.chunk.test.demo", 7.5f);
      store.Set(new RegionId(99), 0, 0, "keystone.chunk.test.demo", 1.0f); // unrelated

      // Act
      store.MergeFrom(loser: new RegionId(3), survivor: new RegionId(7));

      // Assert — loser keys gone, survivor adopted both, unrelated untouched.
      Assert.IsNull(store.Get(new RegionId(3), 0, 0, "keystone.chunk.test.demo"));
      Assert.IsNull(store.Get(new RegionId(3), 1, 2, "keystone.chunk.test.demo"));
      Assert.AreEqual(12.5f, store.Get(new RegionId(7), 0, 0, "keystone.chunk.test.demo"));
      Assert.AreEqual(4.0f, store.Get(new RegionId(7), 1, 2, "keystone.chunk.test.demo"));
      Assert.AreEqual(7.5f, store.Get(new RegionId(7), 5, 5, "keystone.chunk.test.demo"));
      Assert.AreEqual(1.0f, store.Get(new RegionId(99), 0, 0, "keystone.chunk.test.demo"));
    }

    [TestMethod]
    public void MergeFrom_SurvivorWinsOnPerChunkKindCollision() {
      // Arrange — both regions hold the same (chunkX, chunkY, kind) entry.
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.test.demo", 12.5f);
      store.Set(new RegionId(7), 0, 0, "keystone.chunk.test.demo", 99f);

      // Act
      store.MergeFrom(loser: new RegionId(3), survivor: new RegionId(7));

      // Assert — survivor's pre-merge value wins.
      Assert.AreEqual(99f, store.Get(new RegionId(7), 0, 0, "keystone.chunk.test.demo"));
      Assert.IsNull(store.Get(new RegionId(3), 0, 0, "keystone.chunk.test.demo"));
    }

    [TestMethod]
    public void MergeFrom_NoLoserEntries_LeavesStoreUnchanged() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(7), 0, 0, "keystone.chunk.test.demo", 5f);

      store.MergeFrom(loser: new RegionId(3), survivor: new RegionId(7));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(7), 0, 0, "keystone.chunk.test.demo"));
    }

    [TestMethod]
    public void MergeFrom_LoserEqualsSurvivor_NoOp() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.test.demo", 5f);

      store.MergeFrom(loser: new RegionId(3), survivor: new RegionId(3));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(3), 0, 0, "keystone.chunk.test.demo"));
    }

    [TestMethod]
    public void RemoveAllValuesFor_DropsEveryEntryUnderRegionId() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "k", 1f);
      store.Set(new RegionId(3), 1, 1, "k", 2f);
      store.Set(new RegionId(7), 0, 0, "k", 5f);

      store.RemoveAllValuesFor(new RegionId(3));

      Assert.IsNull(store.Get(new RegionId(3), 0, 0, "k"));
      Assert.IsNull(store.Get(new RegionId(3), 1, 1, "k"));
      Assert.AreEqual(5f, store.Get(new RegionId(7), 0, 0, "k"));
    }

    [TestMethod]
    public void PruneToLiveRegions_DropsEntriesNotInLiveSet() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "k", 1f);
      store.Set(new RegionId(7), 0, 0, "k", 2f);
      store.Set(new RegionId(99), 0, 0, "k", 3f); // dead region

      var pruned = store.PruneToLiveRegions(
          new HashSet<RegionId> { new RegionId(3), new RegionId(7) });

      Assert.AreEqual(1, pruned);
      Assert.AreEqual(1f, store.Get(new RegionId(3), 0, 0, "k"));
      Assert.AreEqual(2f, store.Get(new RegionId(7), 0, 0, "k"));
      Assert.IsNull(store.Get(new RegionId(99), 0, 0, "k"));
    }

    [TestMethod]
    public void Inherit_OverwritesExistingDestinationEntries() {
      // Arrange — destination already has a stale value at the same
      // (chunkX, chunkY, kind).
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.test.demo", 12.5f);
      store.Set(new RegionId(7), 0, 0, "keystone.chunk.test.demo", 99f);

      // Act
      store.Inherit(source: new RegionId(3), destination: new RegionId(7));

      // Assert — destination's prior value is overwritten by the source's.
      Assert.AreEqual(12.5f, store.Get(new RegionId(7), 0, 0, "keystone.chunk.test.demo"));
    }

    [TestMethod]
    public void Inherit_NoSourceEntries_LeavesStoreUnchanged() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(7), 0, 0, "keystone.chunk.test.demo", 5f);

      store.Inherit(source: new RegionId(3), destination: new RegionId(7));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(7), 0, 0, "keystone.chunk.test.demo"));
    }

    [TestMethod]
    public void Inherit_SourceEqualsDestination_NoOp() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.test.demo", 5f);

      store.Inherit(source: new RegionId(3), destination: new RegionId(3));

      Assert.AreEqual(1, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(3), 0, 0, "keystone.chunk.test.demo"));
    }

    #endregion

    #region Empty/null kind guards

    /// <summary>
    /// Pins that <see cref="ChunkValueStore.Get"/> returns <c>null</c>
    /// (not throws) when the caller passes an empty kind string. Callers
    /// that probe other mods' values optimistically (see class docstring)
    /// should be able to ask "is there a value here?" without their
    /// probe being able to crash on an empty kind that surfaced from a
    /// data error.
    /// </summary>
    [TestMethod]
    public void Get_EmptyKind_ReturnsNullWithoutThrowing() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.test.demo", 5f);

      Assert.IsNull(store.Get(new RegionId(3), 0, 0, ""));
    }

    /// <summary>
    /// Pins that <see cref="ChunkValueStore.Remove"/> returns
    /// <c>false</c> without throwing when passed an empty kind. The
    /// empty-kind guard mirrors <see cref="ChunkValueStore.Get"/>'s
    /// defensive behaviour so a producer running on a bad value name
    /// can't accidentally crash on a "remove if zero" cleanup pass.
    /// </summary>
    [TestMethod]
    public void Remove_EmptyKind_ReturnsFalseWithoutThrowing() {
      var store = new ChunkValueStore();

      Assert.IsFalse(store.Remove(new RegionId(3), 0, 0, ""));
    }

    #endregion

    #region Remove — extant vs absent

    /// <summary>
    /// Pins that <see cref="ChunkValueStore.Remove"/> returns
    /// <c>true</c> and drops the entry when one exists. The bool
    /// return is the signal producers use to know whether their
    /// "value dropped to zero" cleanup actually freed an entry.
    /// </summary>
    [TestMethod]
    public void Remove_ExistingEntry_ReturnsTrueAndDropsIt() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 4, 5, "keystone.chunk.test.demo", 4.25f);

      var removed = store.Remove(new RegionId(3), 4, 5, "keystone.chunk.test.demo");

      Assert.IsTrue(removed);
      Assert.IsNull(store.Get(new RegionId(3), 4, 5, "keystone.chunk.test.demo"));
      Assert.AreEqual(0, store.Count);
    }

    /// <summary>
    /// Pins that <see cref="ChunkValueStore.Remove"/> returns
    /// <c>false</c> when no entry exists for the key. Distinguishes
    /// "I removed an entry" from "there was nothing to remove" so
    /// callers can avoid a wasted log/event on the no-op case.
    /// </summary>
    [TestMethod]
    public void Remove_AbsentEntry_ReturnsFalse() {
      var store = new ChunkValueStore();

      Assert.IsFalse(store.Remove(new RegionId(3), 4, 5, "never.set"));
    }

    #endregion

    #region RemoveAllValuesFor — no-match early return

    /// <summary>
    /// Pins that <see cref="ChunkValueStore.RemoveAllValuesFor"/> is
    /// a safe no-op when no entries match the given region. The
    /// lifecycle handler may call this for regions that never carried
    /// any chunk values (e.g., a region that despawned before any
    /// producer ran on it); the early return avoids allocating a
    /// throwaway removal list.
    /// </summary>
    [TestMethod]
    public void RemoveAllValuesFor_NoMatchingEntries_LeavesStoreUnchanged() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(7), 0, 0, "k", 1f);
      store.Set(new RegionId(7), 1, 1, "k", 2f);

      store.RemoveAllValuesFor(new RegionId(99));  // region with no entries

      Assert.AreEqual(2, store.Count);
      Assert.AreEqual(1f, store.Get(new RegionId(7), 0, 0, "k"));
      Assert.AreEqual(2f, store.Get(new RegionId(7), 1, 1, "k"));
    }

    #endregion

    #region EntriesForChunk — per-(region, chunk) filtered enumeration

    /// <summary>
    /// Pins that <see cref="ChunkValueStore.EntriesForChunk"/> yields
    /// exactly the entries whose <see cref="ChunkValueKey.RegionId"/>,
    /// <see cref="ChunkValueKey.ChunkX"/> and
    /// <see cref="ChunkValueKey.ChunkY"/> all match the query triple,
    /// across multiple kinds. This is the per-frame fast path the
    /// debug overlay uses; the class docstring calls it out
    /// specifically as the way to avoid <see cref="ChunkValueStore.SortedSnapshot"/>'s
    /// full-store allocation. A regression that broadened the filter
    /// (e.g. dropping the region check) would surface here as the
    /// "unrelated region" row leaking into the output.
    /// </summary>
    [TestMethod]
    public void EntriesForChunk_YieldsAllKindsForMatchingRegionAndChunkOnly() {
      // Arrange — three entries on the target (region, chunk), one
      // entry on the same region but different chunk, one entry on a
      // different region but matching chunk coords, one entry on the
      // same region and different chunk coords.
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.kindA", 1f);   // match
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.kindB", 2f);   // match
      store.Set(new RegionId(3), 0, 0, "keystone.chunk.kindC", 3f);   // match
      store.Set(new RegionId(3), 1, 0, "keystone.chunk.kindA", 9f);   // same region, different chunkX
      store.Set(new RegionId(3), 0, 1, "keystone.chunk.kindA", 8f);   // same region, different chunkY
      store.Set(new RegionId(7), 0, 0, "keystone.chunk.kindA", 7f);   // different region, same chunk coords

      // Act
      var matches = store.EntriesForChunk(new RegionId(3), 0, 0).ToList();

      // Assert — exactly the three target entries appear.
      Assert.AreEqual(3, matches.Count);
      var byKind = matches.ToDictionary(kv => kv.Key.Kind, kv => kv.Value);
      Assert.AreEqual(1f, byKind["keystone.chunk.kindA"]);
      Assert.AreEqual(2f, byKind["keystone.chunk.kindB"]);
      Assert.AreEqual(3f, byKind["keystone.chunk.kindC"]);
    }

    /// <summary>
    /// Pins that <see cref="ChunkValueStore.EntriesForChunk"/> returns
    /// the empty sequence when the (region, chunk) triple has no
    /// entries, rather than throwing. The debug overlay calls this on
    /// every visible chunk regardless of whether the chunk has any
    /// Keystone-owned values, so a chunk with no entries must
    /// short-circuit silently.
    /// </summary>
    [TestMethod]
    public void EntriesForChunk_NoMatches_ReturnsEmpty() {
      var store = new ChunkValueStore();
      store.Set(new RegionId(3), 0, 0, "k", 1f);

      Assert.AreEqual(0, store.EntriesForChunk(new RegionId(99), 0, 0).Count(),
          "Unknown region must yield zero entries (not throw).");
      Assert.AreEqual(0, store.EntriesForChunk(new RegionId(3), 5, 5).Count(),
          "Known region but unknown chunk coords must yield zero entries.");
    }

    #endregion

  }

}
