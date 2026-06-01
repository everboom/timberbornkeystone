using System;
using System.Collections.Generic;
using Keystone.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Unit tests for <see cref="ChunkValueRegistry"/>: registration,
  /// freeze semantics, ordinal lookup, and name round-tripping.
  /// </summary>
  [TestClass]
  public class ChunkValueRegistryTests {

    #region Registration

    [TestMethod]
    public void Register_ReturnsSequentialOrdinals() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act
      var first = registry.Register("a");
      var second = registry.Register("b");
      var third = registry.Register("c");

      // Assert
      Assert.AreEqual(0, first);
      Assert.AreEqual(1, second);
      Assert.AreEqual(2, third);
    }

    [TestMethod]
    public void Register_SameNameTwice_ReturnsSameOrdinal() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act
      var first = registry.Register("keystone.chunk.suitability.forest");
      var duplicate = registry.Register("keystone.chunk.suitability.forest");

      // Assert
      Assert.AreEqual(first, duplicate);
      Assert.AreEqual(1, registry.SlotCount);
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void Register_AfterFreeze_Throws() {
      // Arrange
      var registry = new ChunkValueRegistry();
      registry.Register("a");
      registry.Freeze();

      // Act
      registry.Register("b");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void Register_NullName_Throws() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act
      registry.Register(null!);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void Register_EmptyName_Throws() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act
      registry.Register("");
    }

    #endregion

    #region Freeze

    [TestMethod]
    public void Freeze_SetsIsFrozen() {
      // Arrange
      var registry = new ChunkValueRegistry();
      Assert.IsFalse(registry.IsFrozen);

      // Act
      registry.Freeze();

      // Assert
      Assert.IsTrue(registry.IsFrozen);
    }

    #endregion

    #region SlotCount

    [TestMethod]
    public void SlotCount_MatchesRegistrations() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act
      registry.Register("a");
      registry.Register("b");
      registry.Register("c");

      // Assert
      Assert.AreEqual(3, registry.SlotCount);
    }

    [TestMethod]
    public void SlotCount_IdempotentRegistration_DoesNotDoubleCount() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act
      registry.Register("a");
      registry.Register("a");

      // Assert
      Assert.AreEqual(1, registry.SlotCount);
    }

    #endregion

    #region OrdinalFor

    [TestMethod]
    public void OrdinalFor_KnownName_ReturnsCorrectOrdinal() {
      // Arrange
      var registry = new ChunkValueRegistry();
      var expected = registry.Register("test.kind");

      // Act
      var actual = registry.OrdinalFor("test.kind");

      // Assert
      Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    [ExpectedException(typeof(KeyNotFoundException))]
    public void OrdinalFor_UnknownName_Throws() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act
      registry.OrdinalFor("nonexistent");
    }

    #endregion

    #region TryOrdinalFor

    [TestMethod]
    public void TryOrdinalFor_KnownName_ReturnsOrdinal() {
      // Arrange
      var registry = new ChunkValueRegistry();
      var expected = registry.Register("test.kind");

      // Act
      var result = registry.TryOrdinalFor("test.kind");

      // Assert
      Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void TryOrdinalFor_UnknownName_ReturnsNull() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act / Assert
      Assert.IsNull(registry.TryOrdinalFor("nonexistent"));
    }

    [TestMethod]
    public void TryOrdinalFor_NullName_ReturnsNull() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act / Assert
      Assert.IsNull(registry.TryOrdinalFor(null!));
    }

    [TestMethod]
    public void TryOrdinalFor_EmptyName_ReturnsNull() {
      // Arrange
      var registry = new ChunkValueRegistry();

      // Act / Assert
      Assert.IsNull(registry.TryOrdinalFor(""));
    }

    #endregion

    #region NameFor

    [TestMethod]
    public void NameFor_ValidOrdinal_ReturnsName() {
      // Arrange
      var registry = new ChunkValueRegistry();
      registry.Register("first");
      registry.Register("second");

      // Act / Assert
      Assert.AreEqual("first", registry.NameFor(0));
      Assert.AreEqual("second", registry.NameFor(1));
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void NameFor_NegativeOrdinal_Throws() {
      // Arrange
      var registry = new ChunkValueRegistry();
      registry.Register("a");

      // Act
      registry.NameFor(-1);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void NameFor_OrdinalOutOfRange_Throws() {
      // Arrange
      var registry = new ChunkValueRegistry();
      registry.Register("a");

      // Act
      registry.NameFor(1);
    }

    #endregion

    #region AllNames

    [TestMethod]
    public void AllNames_ReflectsRegistrationOrder() {
      // Arrange
      var registry = new ChunkValueRegistry();
      registry.Register("c");
      registry.Register("a");
      registry.Register("b");

      // Act
      var names = registry.AllNames;

      // Assert
      Assert.AreEqual(3, names.Count);
      Assert.AreEqual("c", names[0]);
      Assert.AreEqual("a", names[1]);
      Assert.AreEqual("b", names[2]);
    }

    #endregion

    #region RoleOf

    [TestMethod]
    public void RoleOf_DefaultsToOther_WhenRoleNotSpecified() {
      // Arrange
      var registry = new ChunkValueRegistry();
      var ord = registry.Register("external.mod.kind");

      // Act / Assert — external-mod slots that don't declare a role stay Other.
      Assert.AreEqual(ChunkValueRole.Other, registry.RoleOf(ord));
    }

    [TestMethod]
    public void RoleOf_ReturnsDeclaredRole() {
      // Arrange
      var registry = new ChunkValueRegistry();
      var matOrd = registry.Register("keystone.chunk.maturity.forest", ChunkValueRole.Maturity);
      var suitOrd = registry.Register("keystone.chunk.suitability.forest", ChunkValueRole.Suitability);

      // Act / Assert
      Assert.AreEqual(ChunkValueRole.Maturity, registry.RoleOf(matOrd));
      Assert.AreEqual(ChunkValueRole.Suitability, registry.RoleOf(suitOrd));
    }

    [TestMethod]
    public void Register_SameNameTwice_FirstRoleWins_PassedRoleIgnored() {
      // Pins the documented idempotency: re-registering an existing name
      // returns the same ordinal and keeps the first-declared role.
      // Arrange
      var registry = new ChunkValueRegistry();
      var first = registry.Register("k", ChunkValueRole.Maturity);

      // Act — re-register with a different role.
      var again = registry.Register("k", ChunkValueRole.Other);

      // Assert — same slot, original role retained.
      Assert.AreEqual(first, again);
      Assert.AreEqual(1, registry.SlotCount);
      Assert.AreEqual(ChunkValueRole.Maturity, registry.RoleOf(first));
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void RoleOf_NegativeOrdinal_Throws() {
      // Arrange
      var registry = new ChunkValueRegistry();
      registry.Register("a", ChunkValueRole.Maturity);

      // Act
      registry.RoleOf(-1);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void RoleOf_OrdinalOutOfRange_Throws() {
      // Arrange
      var registry = new ChunkValueRegistry();
      registry.Register("a", ChunkValueRole.Maturity);

      // Act
      registry.RoleOf(1);
    }

    #endregion

  }

}
