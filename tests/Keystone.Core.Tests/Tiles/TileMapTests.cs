using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Tiles {

  /// <summary>
  /// Pins <see cref="TileMap{TKey, TValue}"/>'s contract: it's a sparse
  /// dictionary-shaped map with Set / TryGet / Remove / Clear / Count /
  /// Entries. Substrate that several Keystone subsystems annotate
  /// terrain through; bugs here would show up as confusing failures in
  /// dependent tests.
  /// </summary>
  [TestClass]
  public class TileMapTests {

    [TestMethod]
    public void NewMap_IsEmpty() {
      var m = new TileMap<TileCoord, int>();
      Assert.AreEqual(0, m.Count);
    }

    [TestMethod]
    public void Set_AddsEntry_CountReflects() {
      var m = new TileMap<TileCoord, int>();
      m.Set(new TileCoord(1, 2), 99);
      Assert.AreEqual(1, m.Count);
    }

    [TestMethod]
    public void Set_OverwritesExistingValueAtSameKey() {
      var m = new TileMap<TileCoord, int>();
      var c = new TileCoord(1, 2);
      m.Set(c, 1);
      m.Set(c, 2);
      Assert.AreEqual(1, m.Count, "Overwrite should not add a second entry.");
      m.TryGet(c, out var v);
      Assert.AreEqual(2, v);
    }

    [TestMethod]
    public void TryGet_PresentKey_ReturnsTrueAndValue() {
      var m = new TileMap<TileCoord, int>();
      m.Set(new TileCoord(5, 7), 42);
      Assert.IsTrue(m.TryGet(new TileCoord(5, 7), out var v));
      Assert.AreEqual(42, v);
    }

    [TestMethod]
    public void TryGet_MissingKey_ReturnsFalseAndDefaultValue() {
      var m = new TileMap<TileCoord, int>();
      Assert.IsFalse(m.TryGet(new TileCoord(0, 0), out var v));
      Assert.AreEqual(0, v);
    }

    [TestMethod]
    public void Remove_PresentKey_ReturnsTrueAndRemoves() {
      var m = new TileMap<TileCoord, int>();
      var c = new TileCoord(3, 3);
      m.Set(c, 100);
      Assert.IsTrue(m.Remove(c));
      Assert.AreEqual(0, m.Count);
      Assert.IsFalse(m.TryGet(c, out _));
    }

    [TestMethod]
    public void Remove_MissingKey_ReturnsFalse() {
      var m = new TileMap<TileCoord, int>();
      Assert.IsFalse(m.Remove(new TileCoord(99, 99)));
    }

    [TestMethod]
    public void Clear_RemovesAllEntries() {
      var m = new TileMap<TileCoord, int>();
      m.Set(new TileCoord(0, 0), 1);
      m.Set(new TileCoord(1, 1), 2);
      m.Set(new TileCoord(2, 2), 3);
      m.Clear();
      Assert.AreEqual(0, m.Count);
    }

    [TestMethod]
    public void Entries_EnumeratesAllSetPairs() {
      var m = new TileMap<TileCoord, int>();
      m.Set(new TileCoord(0, 0), 10);
      m.Set(new TileCoord(1, 1), 20);
      var seen = new System.Collections.Generic.List<int>();
      foreach (var kv in m.Entries) seen.Add(kv.Value);
      CollectionAssert.AreEquivalent(new[] { 10, 20 }, seen);
    }

    [TestMethod]
    public void WorksWith_SurfaceCoordKey() {
      // The map is generic; SurfaceCoord-keyed usage is one of the two
      // documented patterns (per-surface data).
      var m = new TileMap<SurfaceCoord, float>();
      m.Set(new SurfaceCoord(1, 2, 3), 0.5f);
      Assert.IsTrue(m.TryGet(new SurfaceCoord(1, 2, 3), out var v));
      Assert.AreEqual(0.5f, v);
    }

  }

}
