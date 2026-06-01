using System;
using Keystone.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  [TestClass]
  public class ChunkDataTests {

    #region Construction

    [TestMethod]
    public void Constructor_CreatesZeroInitializedArray() {
      // Act
      var data = new ChunkData(5);

      // Assert
      Assert.AreEqual(5, data.SlotCount);
      for (var i = 0; i < data.SlotCount; i++)
        Assert.AreEqual(0f, data.Get(i));
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void Constructor_NegativeSlotCount_Throws() {
      new ChunkData(-1);
    }

    [TestMethod]
    public void Constructor_ZeroSlotCount_CreatesEmptyArray() {
      var data = new ChunkData(0);
      Assert.AreEqual(0, data.SlotCount);
    }

    #endregion

    #region Get / Set

    [TestMethod]
    public void Set_Get_RoundTrips() {
      // Arrange
      var data = new ChunkData(3);

      // Act
      data.Set(0, 1.5f);
      data.Set(1, 2.5f);
      data.Set(2, 3.5f);

      // Assert
      Assert.AreEqual(1.5f, data.Get(0));
      Assert.AreEqual(2.5f, data.Get(1));
      Assert.AreEqual(3.5f, data.Get(2));
    }

    [TestMethod]
    public void Set_OverwritesPreviousValue() {
      // Arrange
      var data = new ChunkData(1);
      data.Set(0, 1.0f);

      // Act
      data.Set(0, 9.9f);

      // Assert
      Assert.AreEqual(9.9f, data.Get(0));
    }

    [TestMethod]
    public void Values_ExposesBackingArray() {
      // Arrange
      var data = new ChunkData(2);
      data.Set(0, 5f);

      // Act
      var arr = data.Values;

      // Assert
      Assert.AreEqual(5f, arr[0]);
      Assert.AreEqual(2, arr.Length);
    }

    #endregion

    #region CopyFrom

    [TestMethod]
    public void CopyFrom_CopiesAllValues() {
      // Arrange
      var source = new ChunkData(3);
      source.Set(0, 1f);
      source.Set(1, 2f);
      source.Set(2, 3f);
      var dest = new ChunkData(3);

      // Act
      dest.CopyFrom(source);

      // Assert
      Assert.AreEqual(1f, dest.Get(0));
      Assert.AreEqual(2f, dest.Get(1));
      Assert.AreEqual(3f, dest.Get(2));
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void CopyFrom_SlotCountMismatch_Throws() {
      // Arrange
      var source = new ChunkData(3);
      var dest = new ChunkData(2);

      // Act
      dest.CopyFrom(source);
    }

    [TestMethod]
    public void CopyFrom_CarriesZ() {
      // Z is identity (the chunk's layer), not a value slot, but it must
      // survive a CopyFrom so a re-homed chunk keeps the Z that lets it
      // reconcile again later. See ChunkReconciler.
      // Arrange
      var source = new ChunkData(1) { Z = 7 };
      var dest = new ChunkData(1) { Z = 0 };

      // Act
      dest.CopyFrom(source);

      // Assert
      Assert.AreEqual(7, dest.Z);
    }

    #endregion

    #region Z

    [TestMethod]
    public void Z_DefaultsToZero_AndRoundTrips() {
      // Arrange
      var data = new ChunkData(1);

      // Assert (default)
      Assert.AreEqual(0, data.Z);

      // Act
      data.Z = 12;

      // Assert
      Assert.AreEqual(12, data.Z);
    }

    [TestMethod]
    public void Clear_LeavesZUntouched() {
      // Z is identity, not data — Clear zeroes value slots but must not
      // forget which layer the chunk belongs to.
      // Arrange
      var data = new ChunkData(2) { Z = 9 };
      data.Set(0, 5f);

      // Act
      data.Clear();

      // Assert
      Assert.AreEqual(0f, data.Get(0));
      Assert.AreEqual(9, data.Z);
    }

    #endregion

    #region Clear

    [TestMethod]
    public void Clear_ZerosAllSlots() {
      // Arrange
      var data = new ChunkData(3);
      data.Set(0, 1f);
      data.Set(1, 2f);
      data.Set(2, 3f);

      // Act
      data.Clear();

      // Assert
      for (var i = 0; i < data.SlotCount; i++)
        Assert.AreEqual(0f, data.Get(i));
    }

    #endregion

  }

}
