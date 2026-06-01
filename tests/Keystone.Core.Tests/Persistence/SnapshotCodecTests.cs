using System.Collections.Generic;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Round-trip and determinism guarantees for <see cref="SnapshotCodec"/>.
  /// These pin the contract that lets save files diff cleanly across
  /// runs and that catches malformed save blobs at decode time rather
  /// than producing silently-wrong runtime state.
  /// </summary>
  [TestClass]
  public class SnapshotCodecTests {

    #region Round-trip

    [TestMethod]
    public void Encode_Decode_RoundTripsEmptySnapshot() {
      // Arrange
      var snapshot = new KeystoneSnapshot();

      // Act
      var payload = SnapshotCodec.Encode(snapshot);
      var roundTrip = SnapshotCodec.Decode(payload);

      // Assert
      Assert.AreEqual(SnapshotCodec.CurrentSchemaVersion, payload.SchemaVersion);
      Assert.AreEqual(0, roundTrip.Regions.Count);
      Assert.AreEqual(0, roundTrip.RegionValues.Count);
      Assert.IsTrue(roundTrip.IsEmpty);
    }

    [TestMethod]
    public void Encode_Decode_RoundTripsSingleRegionWithStamp() {
      // Arrange
      var snapshot = new KeystoneSnapshot();
      snapshot.Regions.Add(new RegionPersistedRecord(
          new RegionId(7),
          new GameTimestamp(2, 3, 0.25f),
          WeatherKind.Drought,
          TotalDaysAtCreation: 12.25f,
          Representative: RegionPersistedRecord.NoRepresentative));

      // Act
      var payload = SnapshotCodec.Encode(snapshot);
      var roundTrip = SnapshotCodec.Decode(payload);

      // Assert
      Assert.AreEqual(1, roundTrip.Regions.Count);
      var record = roundTrip.Regions[0];
      Assert.AreEqual(new RegionId(7), record.Id);
      Assert.AreEqual(2, record.CreatedAt.Cycle);
      Assert.AreEqual(3, record.CreatedAt.CycleDay);
      Assert.AreEqual(0.25f, record.CreatedAt.PartialCycleDay);
      Assert.AreEqual(WeatherKind.Drought, record.WeatherAtCreation);
      Assert.AreEqual(12.25f, record.TotalDaysAtCreation);
    }

    [TestMethod]
    public void Encode_Decode_RoundTripsMultipleScores() {
      // Arrange
      var snapshot = new KeystoneSnapshot();
      snapshot.RegionValues[new RegionValueKey(new RegionId(1), "keystone.region.ageDays")] = 4.5f;
      snapshot.RegionValues[new RegionValueKey(new RegionId(2), "keystone.region.ageDays")] = 1.0f;
      snapshot.RegionValues[new RegionValueKey(new RegionId(2), "folktails.regrowth")] = 0.75f;

      // Act
      var payload = SnapshotCodec.Encode(snapshot);
      var roundTrip = SnapshotCodec.Decode(payload);

      // Assert
      Assert.AreEqual(3, roundTrip.RegionValues.Count);
      Assert.AreEqual(4.5f, roundTrip.RegionValues[new RegionValueKey(new RegionId(1), "keystone.region.ageDays")]);
      Assert.AreEqual(1.0f, roundTrip.RegionValues[new RegionValueKey(new RegionId(2), "keystone.region.ageDays")]);
      Assert.AreEqual(0.75f, roundTrip.RegionValues[new RegionValueKey(new RegionId(2), "folktails.regrowth")]);
    }

    #endregion

    #region Determinism

    [TestMethod]
    public void Encode_RegionsSorted_ByRegionIdAscending() {
      // Arrange — insert in reverse id order to defeat any
      // accidental ordering coincidence.
      var snapshot = new KeystoneSnapshot();
      foreach (var id in new[] { 5, 1, 9, 3 }) {
        snapshot.Regions.Add(new RegionPersistedRecord(
            new RegionId(id), new GameTimestamp(0, 0, 0f), WeatherKind.Temperate, TotalDaysAtCreation: 0f,
            Representative: RegionPersistedRecord.NoRepresentative));
      }

      // Act
      var payload = SnapshotCodec.Encode(snapshot);

      // Assert
      var ids = payload.RegionIds;
      Assert.AreEqual(4, ids.Count);
      Assert.AreEqual(1, ids[0]);
      Assert.AreEqual(3, ids[1]);
      Assert.AreEqual(5, ids[2]);
      Assert.AreEqual(9, ids[3]);
    }

    [TestMethod]
    public void Encode_Scores_SortedByRegionIdThenKind() {
      // Arrange — mixed (id, kind) inserted in irregular order.
      var snapshot = new KeystoneSnapshot();
      snapshot.RegionValues[new RegionValueKey(new RegionId(2), "b")] = 1f;
      snapshot.RegionValues[new RegionValueKey(new RegionId(1), "a")] = 2f;
      snapshot.RegionValues[new RegionValueKey(new RegionId(1), "b")] = 3f;
      snapshot.RegionValues[new RegionValueKey(new RegionId(2), "a")] = 4f;

      // Act
      var payload = SnapshotCodec.Encode(snapshot);

      // Assert — expected order: (1,a), (1,b), (2,a), (2,b).
      Assert.AreEqual(4, payload.RegionValueIds.Count);
      Assert.AreEqual(1, payload.RegionValueIds[0]);
      Assert.AreEqual("a", payload.RegionValueKinds[0]);
      Assert.AreEqual(1, payload.RegionValueIds[1]);
      Assert.AreEqual("b", payload.RegionValueKinds[1]);
      Assert.AreEqual(2, payload.RegionValueIds[2]);
      Assert.AreEqual("a", payload.RegionValueKinds[2]);
      Assert.AreEqual(2, payload.RegionValueIds[3]);
      Assert.AreEqual("b", payload.RegionValueKinds[3]);
    }

    #endregion

    #region Validation

    [TestMethod]
    [ExpectedException(typeof(System.InvalidOperationException))]
    public void Decode_MismatchedListLengths_Throws() {
      // Arrange — region id list length 2 but cycle list length 1.
      var payload = new SnapshotPayload(
          SchemaVersion: 1,
          RegionIds: new[] { 1, 2 },
          RegionTotalDaysAtCreation: new[] { 0f, 0f },
          RegionCreatedCycle: new[] { 0 },
          RegionCreatedCycleDay: new[] { 0, 0 },
          RegionCreatedPartialCycleDay: new[] { 0f, 0f },
          RegionWeather: new[] { 0, 0 },
          RegionRepresentativeX: System.Array.Empty<int>(),
          RegionRepresentativeY: System.Array.Empty<int>(),
          RegionRepresentativeZ: System.Array.Empty<int>(),
          RegionValueIds: System.Array.Empty<int>(),
          RegionValueKinds: System.Array.Empty<string>(),
          RegionValueFloats: System.Array.Empty<float>(),
          ChunkValueRegionIds: System.Array.Empty<int>(),
          ChunkValueChunkXs: System.Array.Empty<int>(),
          ChunkValueChunkYs: System.Array.Empty<int>(),
          ChunkValueKinds: System.Array.Empty<string>(),
          ChunkValueFloats: System.Array.Empty<float>());

      // Act
      SnapshotCodec.Decode(payload);
    }

    [TestMethod]
    public void Decode_PayloadWithUnknownSchemaVersion_LoadsBestEffort() {
      // Arrange — legitimate payload but schema version is in the future.
      // Decoder is content-driven (not version-gated); loud-but-loading is
      // KeystonePersistence's responsibility (warning log). The codec just
      // copies the version through and decodes the data.
      var payload = new SnapshotPayload(
          SchemaVersion: 999,
          RegionIds: new[] { 0 },
          RegionTotalDaysAtCreation: new[] { 1.5f },
          RegionCreatedCycle: new[] { 0 },
          RegionCreatedCycleDay: new[] { 1 },
          RegionCreatedPartialCycleDay: new[] { 0.25f },
          RegionWeather: new[] { (int)WeatherKind.Badtide },
          RegionRepresentativeX: System.Array.Empty<int>(),
          RegionRepresentativeY: System.Array.Empty<int>(),
          RegionRepresentativeZ: System.Array.Empty<int>(),
          RegionValueIds: System.Array.Empty<int>(),
          RegionValueKinds: System.Array.Empty<string>(),
          RegionValueFloats: System.Array.Empty<float>(),
          ChunkValueRegionIds: System.Array.Empty<int>(),
          ChunkValueChunkXs: System.Array.Empty<int>(),
          ChunkValueChunkYs: System.Array.Empty<int>(),
          ChunkValueKinds: System.Array.Empty<string>(),
          ChunkValueFloats: System.Array.Empty<float>());

      // Act
      var snapshot = SnapshotCodec.Decode(payload);

      // Assert
      Assert.AreEqual(1, snapshot.Regions.Count);
      Assert.AreEqual(WeatherKind.Badtide, snapshot.Regions[0].WeatherAtCreation);
    }

    [TestMethod]
    [ExpectedException(typeof(System.InvalidOperationException))]
    public void Decode_MismatchedChunkScoreListLengths_Throws() {
      // Arrange — chunk-score region-id list length 2 but values list
      // length 1; codec should reject before constructing the dict.
      var payload = new SnapshotPayload(
          SchemaVersion: 1,
          RegionIds: System.Array.Empty<int>(),
          RegionTotalDaysAtCreation: System.Array.Empty<float>(),
          RegionCreatedCycle: System.Array.Empty<int>(),
          RegionCreatedCycleDay: System.Array.Empty<int>(),
          RegionCreatedPartialCycleDay: System.Array.Empty<float>(),
          RegionWeather: System.Array.Empty<int>(),
          RegionRepresentativeX: System.Array.Empty<int>(),
          RegionRepresentativeY: System.Array.Empty<int>(),
          RegionRepresentativeZ: System.Array.Empty<int>(),
          RegionValueIds: System.Array.Empty<int>(),
          RegionValueKinds: System.Array.Empty<string>(),
          RegionValueFloats: System.Array.Empty<float>(),
          ChunkValueRegionIds: new[] { 0, 1 },
          ChunkValueChunkXs: new[] { 0, 0 },
          ChunkValueChunkYs: new[] { 0, 0 },
          ChunkValueKinds: new[] { "k", "k" },
          ChunkValueFloats: new[] { 1f });

      // Act
      SnapshotCodec.Decode(payload);
    }

    #endregion

    #region Chunk-score round-trip and determinism

    [TestMethod]
    public void Encode_Decode_RoundTripsChunkScores() {
      // Arrange — three entries spanning two regions and two chunks.
      var snapshot = new KeystoneSnapshot();
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 0, 0, "keystone.chunk.test.demo")] = 4.5f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 1, 0, "keystone.chunk.test.demo")] = 1.0f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(2), 0, 0, "folktails.chunk.regrowth")] = 0.75f;

      // Act
      var payload = SnapshotCodec.Encode(snapshot);
      var roundTrip = SnapshotCodec.Decode(payload);

      // Assert
      Assert.AreEqual(3, roundTrip.ChunkValues.Count);
      Assert.AreEqual(4.5f, roundTrip.ChunkValues[
          new ChunkValueKey(new RegionId(1), 0, 0, "keystone.chunk.test.demo")]);
      Assert.AreEqual(1.0f, roundTrip.ChunkValues[
          new ChunkValueKey(new RegionId(1), 1, 0, "keystone.chunk.test.demo")]);
      Assert.AreEqual(0.75f, roundTrip.ChunkValues[
          new ChunkValueKey(new RegionId(2), 0, 0, "folktails.chunk.regrowth")]);
    }

    [TestMethod]
    public void Encode_ChunkScores_SortedByRegionThenChunkXThenChunkYThenKind() {
      // Arrange — scrambled insertion across the four sort axes.
      var snapshot = new KeystoneSnapshot();
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(2), 1, 0, "b")] = 1f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 0, 1, "a")] = 2f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(2), 0, 0, "a")] = 3f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 0, 0, "b")] = 4f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 0, 0, "a")] = 5f;
      snapshot.ChunkValues[new ChunkValueKey(new RegionId(1), 1, 0, "a")] = 6f;

      // Act
      var payload = SnapshotCodec.Encode(snapshot);

      // Assert — primary by RegionId, then ChunkX, then ChunkY, then Kind.
      Assert.AreEqual(6, payload.ChunkValueRegionIds.Count);
      AssertChunkScoreAt(payload, 0, regionId: 1, cx: 0, cy: 0, kind: "a");
      AssertChunkScoreAt(payload, 1, regionId: 1, cx: 0, cy: 0, kind: "b");
      AssertChunkScoreAt(payload, 2, regionId: 1, cx: 0, cy: 1, kind: "a");
      AssertChunkScoreAt(payload, 3, regionId: 1, cx: 1, cy: 0, kind: "a");
      AssertChunkScoreAt(payload, 4, regionId: 2, cx: 0, cy: 0, kind: "a");
      AssertChunkScoreAt(payload, 5, regionId: 2, cx: 1, cy: 0, kind: "b");
    }

    private static void AssertChunkScoreAt(
        SnapshotPayload p, int i, int regionId, int cx, int cy, string kind) {
      Assert.AreEqual(regionId, p.ChunkValueRegionIds[i], $"regionId mismatch at index {i}");
      Assert.AreEqual(cx, p.ChunkValueChunkXs[i], $"chunkX mismatch at index {i}");
      Assert.AreEqual(cy, p.ChunkValueChunkYs[i], $"chunkY mismatch at index {i}");
      Assert.AreEqual(kind, p.ChunkValueKinds[i], $"kind mismatch at index {i}");
    }

    #endregion

  }

}
