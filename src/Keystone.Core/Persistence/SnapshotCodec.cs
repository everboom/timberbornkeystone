using System;
using System.Collections.Generic;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Core.Time;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Pure-Core translation between <see cref="KeystoneSnapshot"/>
  /// (mutable, in-memory) and <see cref="SnapshotPayload"/>
  /// (immutable, parallel-lists, the over-the-wire shape).
  ///
  /// <para><b>Determinism contract.</b> <see cref="Encode"/> sorts
  /// regions by id ascending and entries by (id, kind ordinal) so any
  /// two equivalent snapshots produce byte-identical payloads. This is
  /// the property that makes save diffs meaningful and lets the player
  /// reproduce a save exactly.</para>
  /// </summary>
  public static class SnapshotCodec {

    #region Constants

    /// <summary>Current schema version. Increment when the payload
    /// shape changes incompatibly.
    /// <para><b>v2:</b> added per-region representative surface lists
    /// (<c>RegionRepresentativeX/Y/Z</c>) used as a recovery fallback
    /// when the saved <c>RegionId</c> doesn't match a freshly-Indexed
    /// live region. v1 saves still load: <see cref="Decode"/> populates
    /// each record with <see cref="RegionPersistedRecord.NoRepresentative"/>
    /// so the fallback is skipped and the load layer falls through to
    /// the original drop behaviour.</para></summary>
    public const int CurrentSchemaVersion = 3;

    #endregion

    #region Encode

    /// <summary>
    /// Encode <paramref name="snapshot"/> as the immutable, sorted
    /// parallel-list payload the Timberborn loader writes to disk.
    /// </summary>
    public static SnapshotPayload Encode(KeystoneSnapshot snapshot) {
      if (snapshot is null) {
        throw new ArgumentNullException(nameof(snapshot));
      }

      var sortedRegions = new List<RegionPersistedRecord>(snapshot.Regions);
      sortedRegions.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));

      var n = sortedRegions.Count;
      var regionIds = new int[n];
      var regionTotalDays = new float[n];
      var regionCycle = new int[n];
      var regionCycleDay = new int[n];
      var regionPartialCycleDay = new float[n];
      var regionWeather = new int[n];
      var representativeX = new int[n];
      var representativeY = new int[n];
      var representativeZ = new int[n];
      for (var i = 0; i < n; i++) {
        var r = sortedRegions[i];
        regionIds[i] = r.Id.Value;
        regionTotalDays[i] = r.TotalDaysAtCreation;
        regionCycle[i] = r.CreatedAt.Cycle;
        regionCycleDay[i] = r.CreatedAt.CycleDay;
        regionPartialCycleDay[i] = r.CreatedAt.PartialCycleDay;
        regionWeather[i] = (int)r.WeatherAtCreation;
        representativeX[i] = r.Representative.X;
        representativeY[i] = r.Representative.Y;
        representativeZ[i] = r.Representative.Z;
      }

      var sortedRegionValues = new List<KeyValuePair<RegionValueKey, float>>(snapshot.RegionValues);
      sortedRegionValues.Sort((a, b) => {
        var byId = a.Key.RegionId.Value.CompareTo(b.Key.RegionId.Value);
        if (byId != 0) return byId;
        return string.CompareOrdinal(a.Key.Kind, b.Key.Kind);
      });

      var m = sortedRegionValues.Count;
      var regionValueIds = new int[m];
      var regionValueKinds = new string[m];
      var regionValueFloats = new float[m];
      for (var i = 0; i < m; i++) {
        var s = sortedRegionValues[i];
        regionValueIds[i] = s.Key.RegionId.Value;
        regionValueKinds[i] = s.Key.Kind;
        regionValueFloats[i] = s.Value;
      }

      var sortedChunkValues = new List<KeyValuePair<ChunkValueKey, float>>(snapshot.ChunkValues);
      sortedChunkValues.Sort((a, b) => {
        var byId = a.Key.RegionId.Value.CompareTo(b.Key.RegionId.Value);
        if (byId != 0) return byId;
        var byCx = a.Key.ChunkX.CompareTo(b.Key.ChunkX);
        if (byCx != 0) return byCx;
        var byCy = a.Key.ChunkY.CompareTo(b.Key.ChunkY);
        if (byCy != 0) return byCy;
        return string.CompareOrdinal(a.Key.Kind, b.Key.Kind);
      });

      var k = sortedChunkValues.Count;
      var chunkValueRegionIds = new int[k];
      var chunkValueChunkXs = new int[k];
      var chunkValueChunkYs = new int[k];
      var chunkValueKinds = new string[k];
      var chunkValueFloats = new float[k];
      for (var i = 0; i < k; i++) {
        var s = sortedChunkValues[i];
        chunkValueRegionIds[i] = s.Key.RegionId.Value;
        chunkValueChunkXs[i] = s.Key.ChunkX;
        chunkValueChunkYs[i] = s.Key.ChunkY;
        chunkValueKinds[i] = s.Key.Kind;
        chunkValueFloats[i] = s.Value;
      }

      return new SnapshotPayload(
          SchemaVersion: CurrentSchemaVersion,
          RegionIds: regionIds,
          RegionTotalDaysAtCreation: regionTotalDays,
          RegionCreatedCycle: regionCycle,
          RegionCreatedCycleDay: regionCycleDay,
          RegionCreatedPartialCycleDay: regionPartialCycleDay,
          RegionWeather: regionWeather,
          RegionRepresentativeX: representativeX,
          RegionRepresentativeY: representativeY,
          RegionRepresentativeZ: representativeZ,
          RegionValueIds: regionValueIds,
          RegionValueKinds: regionValueKinds,
          RegionValueFloats: regionValueFloats,
          ChunkValueRegionIds: chunkValueRegionIds,
          ChunkValueChunkXs: chunkValueChunkXs,
          ChunkValueChunkYs: chunkValueChunkYs,
          ChunkValueKinds: chunkValueKinds,
          ChunkValueFloats: chunkValueFloats);
    }

    #endregion

    #region Decode

    /// <summary>
    /// Decode <paramref name="payload"/> back into a mutable
    /// <see cref="KeystoneSnapshot"/>. Validates list-length parity
    /// across the region-, region-value-, and chunk-value- sub-blocks;
    /// mismatch indicates a corrupted or partially-written save and
    /// throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when parallel-list lengths disagree.</exception>
    public static KeystoneSnapshot Decode(SnapshotPayload payload) {
      if (payload is null) {
        throw new ArgumentNullException(nameof(payload));
      }

      var regionCount = payload.RegionIds.Count;
      if (payload.RegionTotalDaysAtCreation.Count != regionCount
          || payload.RegionCreatedCycle.Count != regionCount
          || payload.RegionCreatedCycleDay.Count != regionCount
          || payload.RegionCreatedPartialCycleDay.Count != regionCount
          || payload.RegionWeather.Count != regionCount) {
        throw new InvalidOperationException(
            $"SnapshotPayload region lists are not all length {regionCount} -- save is malformed.");
      }

      // Representative-surface lists are v2; allow them to be empty
      // when decoding a v1 save (KeystonePersistence's Load() doesn't
      // populate the lists when the saved blob doesn't carry them).
      // Either all three present at the right length, or all empty.
      var hasRepresentatives =
          payload.RegionRepresentativeX.Count == regionCount
          && payload.RegionRepresentativeY.Count == regionCount
          && payload.RegionRepresentativeZ.Count == regionCount;
      var representativesEmpty =
          payload.RegionRepresentativeX.Count == 0
          && payload.RegionRepresentativeY.Count == 0
          && payload.RegionRepresentativeZ.Count == 0;
      if (!hasRepresentatives && !representativesEmpty) {
        throw new InvalidOperationException(
            "SnapshotPayload region-representative lists must either be all length " +
            $"{regionCount} (v2+) or all empty (v1 forward-compat) -- save is malformed.");
      }

      var regionValueCount = payload.RegionValueIds.Count;
      if (payload.RegionValueKinds.Count != regionValueCount || payload.RegionValueFloats.Count != regionValueCount) {
        throw new InvalidOperationException(
            $"SnapshotPayload region-value lists are not all length {regionValueCount} -- save is malformed.");
      }

      var chunkValueCount = payload.ChunkValueRegionIds.Count;
      if (payload.ChunkValueChunkXs.Count != chunkValueCount
          || payload.ChunkValueChunkYs.Count != chunkValueCount
          || payload.ChunkValueKinds.Count != chunkValueCount
          || payload.ChunkValueFloats.Count != chunkValueCount) {
        throw new InvalidOperationException(
            $"SnapshotPayload chunk-value lists are not all length {chunkValueCount} -- save is malformed.");
      }

      var snapshot = new KeystoneSnapshot();
      for (var i = 0; i < regionCount; i++) {
        var weather = (WeatherKind)payload.RegionWeather[i];
        var representative = hasRepresentatives
            ? new SurfaceCoord(
                payload.RegionRepresentativeX[i],
                payload.RegionRepresentativeY[i],
                payload.RegionRepresentativeZ[i])
            : RegionPersistedRecord.NoRepresentative;
        var record = new RegionPersistedRecord(
            new RegionId(payload.RegionIds[i]),
            new GameTimestamp(
                payload.RegionCreatedCycle[i],
                payload.RegionCreatedCycleDay[i],
                payload.RegionCreatedPartialCycleDay[i]),
            weather,
            payload.RegionTotalDaysAtCreation[i],
            representative);
        snapshot.Regions.Add(record);
      }
      for (var i = 0; i < regionValueCount; i++) {
        var key = new RegionValueKey(new RegionId(payload.RegionValueIds[i]), payload.RegionValueKinds[i]);
        snapshot.RegionValues[key] = payload.RegionValueFloats[i];
      }
      for (var i = 0; i < chunkValueCount; i++) {
        var key = new ChunkValueKey(
            new RegionId(payload.ChunkValueRegionIds[i]),
            payload.ChunkValueChunkXs[i],
            payload.ChunkValueChunkYs[i],
            payload.ChunkValueKinds[i]);
        snapshot.ChunkValues[key] = payload.ChunkValueFloats[i];
      }
      return snapshot;
    }

    #endregion

  }

}
