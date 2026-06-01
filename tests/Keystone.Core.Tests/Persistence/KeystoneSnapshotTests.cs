using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Direct unit tests for <see cref="KeystoneSnapshot"/> and the
  /// <see cref="RegionPersistedRecord.HasRepresentative"/> sentinel
  /// predicate. The same paths are exercised transitively via
  /// <c>SnapshotCodecTests</c> and <c>PersistenceIntegrationTests</c>,
  /// but those tests also depend on encoder/decoder behaviour; this
  /// file pins the buffer's own invariants directly so a save-load
  /// regression rooted in <see cref="KeystoneSnapshot"/>'s shape
  /// surfaces independently of codec changes.
  /// </summary>
  [TestClass]
  public class KeystoneSnapshotTests {

    #region IsEmpty — all combinations of populated sub-collections

    /// <summary>
    /// Pins that a freshly-constructed <see cref="KeystoneSnapshot"/>
    /// reports <see cref="KeystoneSnapshot.IsEmpty"/> true. Mod-side
    /// persistence layer uses this to skip writing a sentinel "empty
    /// blob" out when nothing has been persisted yet (e.g. mod just
    /// installed, first autosave after enabling).
    /// </summary>
    [TestMethod]
    public void IsEmpty_FreshlyConstructed_IsTrue() {
      // Arrange
      var snapshot = new KeystoneSnapshot();

      // Assert
      Assert.IsTrue(snapshot.IsEmpty);
      Assert.AreEqual(0, snapshot.Regions.Count);
      Assert.AreEqual(0, snapshot.RegionValues.Count);
      Assert.AreEqual(0, snapshot.ChunkValues.Count);
    }

    /// <summary>
    /// Pins that <see cref="KeystoneSnapshot.IsEmpty"/> returns false
    /// when only the regions list is populated. Exercises the first
    /// short-circuit arm of the three-way conjunction.
    /// </summary>
    [TestMethod]
    public void IsEmpty_OnlyRegionsPopulated_IsFalse() {
      // Arrange
      var snapshot = new KeystoneSnapshot();
      snapshot.Regions.Add(MakeRecord(7));

      // Assert
      Assert.IsFalse(snapshot.IsEmpty);
    }

    /// <summary>
    /// Pins that <see cref="KeystoneSnapshot.IsEmpty"/> returns false
    /// when only region-scope values are populated. Exercises the
    /// middle arm of the three-way conjunction so a regression that
    /// drops region-values from the IsEmpty check (and silently treats
    /// a save with values as empty) is caught here, not later via a
    /// missing-data symptom in PostLoad.
    /// </summary>
    [TestMethod]
    public void IsEmpty_OnlyRegionValuesPopulated_IsFalse() {
      // Arrange
      var snapshot = new KeystoneSnapshot();
      snapshot.RegionValues[new RegionValueKey(new RegionId(1), "keystone.test")] = 1.0f;

      // Assert
      Assert.IsFalse(snapshot.IsEmpty);
    }

    /// <summary>
    /// Pins that <see cref="KeystoneSnapshot.IsEmpty"/> returns false
    /// when only chunk-scope values are populated. Exercises the third
    /// arm — necessary because a regression that omits ChunkValues from
    /// IsEmpty would cause chunk-scope state to be silently dropped at
    /// save time when no regions/region-values are present (e.g. an
    /// early-load corner where chunk values are computed before regions
    /// are added).
    /// </summary>
    [TestMethod]
    public void IsEmpty_OnlyChunkValuesPopulated_IsFalse() {
      // Arrange
      var snapshot = new KeystoneSnapshot();
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 0, 0, "keystone.chunk.test")] = 2.0f;

      // Assert
      Assert.IsFalse(snapshot.IsEmpty);
    }

    /// <summary>
    /// Pins that <see cref="KeystoneSnapshot.IsEmpty"/> is sensitive to
    /// every sub-collection simultaneously. A fully-populated snapshot
    /// must not appear empty.
    /// </summary>
    [TestMethod]
    public void IsEmpty_AllThreePopulated_IsFalse() {
      // Arrange
      var snapshot = new KeystoneSnapshot();
      snapshot.Regions.Add(MakeRecord(1));
      snapshot.RegionValues[new RegionValueKey(new RegionId(1), "keystone.test")] = 1.0f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 0, 0, "keystone.chunk.test")] = 2.0f;

      // Assert
      Assert.IsFalse(snapshot.IsEmpty);
    }

    #endregion

    #region Clear

    /// <summary>
    /// Pins that <see cref="KeystoneSnapshot.Clear"/> drains every
    /// sub-collection so the buffer round-trips back to
    /// <see cref="KeystoneSnapshot.IsEmpty"/> = true. The Mod-side
    /// persistence layer calls Clear after PostLoad drains the buffer;
    /// a regression that forgot one sub-collection would silently keep
    /// stale state alive across loads.
    /// </summary>
    [TestMethod]
    public void Clear_AllSubCollectionsPopulated_RestoresIsEmpty() {
      // Arrange
      var snapshot = new KeystoneSnapshot();
      snapshot.Regions.Add(MakeRecord(1));
      snapshot.RegionValues[new RegionValueKey(new RegionId(1), "keystone.test")] = 1.0f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 0, 0, "keystone.chunk.test")] = 2.0f;
      Assert.IsFalse(snapshot.IsEmpty);

      // Act
      snapshot.Clear();

      // Assert
      Assert.IsTrue(snapshot.IsEmpty);
      Assert.AreEqual(0, snapshot.Regions.Count);
      Assert.AreEqual(0, snapshot.RegionValues.Count);
      Assert.AreEqual(0, snapshot.ChunkValues.Count);
    }

    #endregion

    #region RegionPersistedRecord — representative-vs-no-representative

    /// <summary>
    /// Pins that <see cref="RegionPersistedRecord.HasRepresentative"/>
    /// returns true for a real surface coordinate. The
    /// <c>Representative</c> field is the v2-save fallback for the
    /// save→load remap: a real coord is "use this surface to find the
    /// live region containing it" and the load layer routes through
    /// the surface→region map.
    /// </summary>
    [TestMethod]
    public void HasRepresentative_RealSurfaceCoord_IsTrue() {
      // Arrange — any Z that isn't int.MinValue counts as a real coord;
      // Z=0 is the most common ground level.
      var record = new RegionPersistedRecord(
          Id: new RegionId(1),
          CreatedAt: new GameTimestamp(0, 0, 0f),
          WeatherAtCreation: WeatherKind.Temperate,
          TotalDaysAtCreation: 0f,
          Representative: new SurfaceCoord(5, 7, 0));

      // Assert
      Assert.IsTrue(record.HasRepresentative);
    }

    /// <summary>
    /// Pins that <see cref="RegionPersistedRecord.HasRepresentative"/>
    /// returns false for the
    /// <see cref="RegionPersistedRecord.NoRepresentative"/> sentinel.
    /// Decoded from a v1 save, no fallback was carried -- the load
    /// layer must drop rather than try the surface-based lookup with
    /// a junk coord.
    /// </summary>
    [TestMethod]
    public void HasRepresentative_NoRepresentativeSentinel_IsFalse() {
      // Arrange
      var record = new RegionPersistedRecord(
          Id: new RegionId(1),
          CreatedAt: new GameTimestamp(0, 0, 0f),
          WeatherAtCreation: WeatherKind.Temperate,
          TotalDaysAtCreation: 0f,
          Representative: RegionPersistedRecord.NoRepresentative);

      // Assert — sentinel's Z == int.MinValue, so HasRepresentative is
      // false and the v2 fallback path is skipped.
      Assert.IsFalse(record.HasRepresentative);
      Assert.AreEqual(int.MinValue, RegionPersistedRecord.NoRepresentative.Z,
          "NoRepresentative sentinel must use Z=int.MinValue; the predicate "
          + "keys off this exact value.");
    }

    /// <summary>
    /// Pins that
    /// <see cref="RegionPersistedRecord.HasRepresentative"/> only
    /// keys on <see cref="SurfaceCoord.Z"/>, not X or Y. A boundary
    /// coord at (intMinValue, intMinValue, 0) is still a real
    /// representative because Z is not the sentinel value.
    /// </summary>
    [TestMethod]
    public void HasRepresentative_KeysOnZOnly_NotXOrY() {
      // Arrange — X and Y at extreme values, but Z is a normal ground
      // height.
      var record = new RegionPersistedRecord(
          Id: new RegionId(1),
          CreatedAt: new GameTimestamp(0, 0, 0f),
          WeatherAtCreation: WeatherKind.Temperate,
          TotalDaysAtCreation: 0f,
          Representative: new SurfaceCoord(int.MinValue, int.MinValue, 0));

      // Assert
      Assert.IsTrue(record.HasRepresentative,
          "HasRepresentative must key on Z only -- X and Y can take any "
          + "int value including int.MinValue for legitimately-placed regions.");
    }

    #endregion

    #region Helpers

    private static RegionPersistedRecord MakeRecord(int id) =>
        new(
            Id: new RegionId(id),
            CreatedAt: new GameTimestamp(0, 0, 0f),
            WeatherAtCreation: WeatherKind.Temperate,
            TotalDaysAtCreation: 0f,
            Representative: RegionPersistedRecord.NoRepresentative);

    #endregion

  }

}
