using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  [TestClass]
  public class ChunkDataStoreTests {

    private static ChunkValueRegistry FrozenRegistry(int slots = 4) {
      var registry = new ChunkValueRegistry();
      for (var i = 0; i < slots; i++) registry.Register($"slot.{i}");
      registry.Freeze();
      return registry;
    }

    #region GetOrCreate / Get

    [TestMethod]
    public void GetOrCreate_CreatesNewEntry() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      var coord = new ChunkCoord(new RegionId(1), 2, 3);

      // Act
      var data = store.GetOrCreate(coord);

      // Assert
      Assert.IsNotNull(data);
      Assert.AreEqual(4, data.SlotCount);
      Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public void GetOrCreate_ReturnsSameInstance() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      var coord = new ChunkCoord(new RegionId(1), 2, 3);

      // Act
      var first = store.GetOrCreate(coord);
      var second = store.GetOrCreate(coord);

      // Assert
      Assert.AreSame(first, second);
      Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public void Get_ExistingEntry_ReturnsData() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      var coord = new ChunkCoord(new RegionId(1), 2, 3);
      var created = store.GetOrCreate(coord);
      created.Set(0, 42f);

      // Act
      var retrieved = store.Get(coord);

      // Assert
      Assert.IsNotNull(retrieved);
      Assert.AreEqual(42f, retrieved!.Get(0));
    }

    [TestMethod]
    public void Get_MissingEntry_ReturnsNull() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());

      // Act / Assert
      Assert.IsNull(store.Get(new ChunkCoord(new RegionId(1), 0, 0)));
    }

    [TestMethod]
    public void Get_ByComponents_Works() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 2, 3).Set(0, 7f);

      // Act
      var data = store.Get(new RegionId(1), 2, 3);

      // Assert
      Assert.IsNotNull(data);
      Assert.AreEqual(7f, data!.Get(0));
    }

    #endregion

    #region RemoveAllFor

    [TestMethod]
    public void RemoveAllFor_DropsRegionEntries() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 0, 0);
      store.GetOrCreate(new RegionId(1), 1, 0);
      store.GetOrCreate(new RegionId(2), 0, 0);

      // Act
      store.RemoveAllFor(new RegionId(1));

      // Assert
      Assert.AreEqual(1, store.Count);
      Assert.IsNull(store.Get(new RegionId(1), 0, 0));
      Assert.IsNotNull(store.Get(new RegionId(2), 0, 0));
    }

    #endregion

    #region Inherit

    [TestMethod]
    public void Inherit_CopiesValuesToDestination() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 0, 0).Set(0, 5f);
      store.GetOrCreate(new RegionId(1), 1, 0).Set(0, 6f);

      // Act
      store.Inherit(new RegionId(1), new RegionId(2));

      // Assert — source kept, destination created
      Assert.AreEqual(4, store.Count);
      Assert.AreEqual(5f, store.Get(new RegionId(1), 0, 0)!.Get(0));
      Assert.AreEqual(5f, store.Get(new RegionId(2), 0, 0)!.Get(0));
      Assert.AreEqual(6f, store.Get(new RegionId(2), 1, 0)!.Get(0));
    }

    [TestMethod]
    public void Inherit_OverwritesExistingDestination() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 0, 0).Set(0, 5f);
      store.GetOrCreate(new RegionId(2), 0, 0).Set(0, 99f);

      // Act
      store.Inherit(new RegionId(1), new RegionId(2));

      // Assert — destination overwritten by source
      Assert.AreEqual(5f, store.Get(new RegionId(2), 0, 0)!.Get(0));
    }

    [TestMethod]
    public void Inherit_SameSourceAndDest_NoOp() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 0, 0).Set(0, 5f);

      // Act
      store.Inherit(new RegionId(1), new RegionId(1));

      // Assert
      Assert.AreEqual(1, store.Count);
    }

    #endregion

    #region MergeFrom

    [TestMethod]
    public void MergeFrom_MovesLoserToSurvivor_SurvivorWins() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 0, 0).Set(0, 10f); // loser
      store.GetOrCreate(new RegionId(2), 0, 0).Set(0, 20f); // survivor — should win
      store.GetOrCreate(new RegionId(1), 1, 0).Set(0, 30f); // loser, no collision

      // Act
      store.MergeFrom(new RegionId(1), new RegionId(2));

      // Assert — loser entries removed
      Assert.IsNull(store.Get(new RegionId(1), 0, 0));
      Assert.IsNull(store.Get(new RegionId(1), 1, 0));
      // survivor keeps its own value on collision
      Assert.AreEqual(20f, store.Get(new RegionId(2), 0, 0)!.Get(0));
      // non-colliding loser entry moved to survivor
      Assert.AreEqual(30f, store.Get(new RegionId(2), 1, 0)!.Get(0));
      Assert.AreEqual(2, store.Count);
    }

    #endregion

    #region PruneToLiveRegions

    [TestMethod]
    public void PruneToLiveRegions_DropsOrphans() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 0, 0);
      store.GetOrCreate(new RegionId(2), 0, 0);
      store.GetOrCreate(new RegionId(3), 0, 0);
      var live = new HashSet<RegionId> { new RegionId(1), new RegionId(3) };

      // Act
      var pruned = store.PruneToLiveRegions(live);

      // Assert
      Assert.AreEqual(1, pruned);
      Assert.AreEqual(2, store.Count);
      Assert.IsNull(store.Get(new RegionId(2), 0, 0));
    }

    #endregion

    #region Clear

    [TestMethod]
    public void Clear_DropsEverything() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 0, 0);
      store.GetOrCreate(new RegionId(2), 1, 1);

      // Act
      store.Clear();

      // Assert
      Assert.AreEqual(0, store.Count);
    }

    #endregion

    #region Entries

    [TestMethod]
    public void Entries_EnumeratesAll() {
      // Arrange
      var store = new ChunkDataStore(FrozenRegistry());
      store.GetOrCreate(new RegionId(1), 0, 0);
      store.GetOrCreate(new RegionId(2), 1, 1);

      // Act
      var entries = store.Entries.ToList();

      // Assert
      Assert.AreEqual(2, entries.Count);
    }

    #endregion

  }

}
