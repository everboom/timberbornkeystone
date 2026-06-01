using System.Collections.Generic;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Immutable over-the-wire shape of a persisted Keystone snapshot.
  /// Native parallel lists only -- the persistence subsystem maps
  /// every list directly onto <c>ListKey&lt;int|string|float&gt;</c>
  /// entries on the Timberborn save object, so we can avoid
  /// <c>IValueSerializer&lt;T&gt;</c> implementations entirely.
  ///
  /// <para><see cref="SnapshotCodec"/> is the only producer/consumer:
  /// it sorts (RegionId ascending; entries by RegionId then Kind
  /// ordinal) at encode time so a save written from logically-
  /// equivalent state is byte-stable across runs.</para>
  ///
  /// <para>List-length parity is invariant across the region-,
  /// region-value-, and chunk-value- sub-blocks: all six region
  /// lists share the same length, all three region-value lists
  /// share the same length, and all five chunk-value lists share
  /// the same length. Decode validates this and throws on
  /// mismatch.</para>
  /// </summary>
  public sealed record SnapshotPayload(
      int SchemaVersion,
      IReadOnlyList<int> RegionIds,
      IReadOnlyList<float> RegionTotalDaysAtCreation,
      IReadOnlyList<int> RegionCreatedCycle,
      IReadOnlyList<int> RegionCreatedCycleDay,
      IReadOnlyList<float> RegionCreatedPartialCycleDay,
      IReadOnlyList<int> RegionWeather,
      IReadOnlyList<int> RegionRepresentativeX,
      IReadOnlyList<int> RegionRepresentativeY,
      IReadOnlyList<int> RegionRepresentativeZ,
      IReadOnlyList<int> RegionValueIds,
      IReadOnlyList<string> RegionValueKinds,
      IReadOnlyList<float> RegionValueFloats,
      IReadOnlyList<int> ChunkValueRegionIds,
      IReadOnlyList<int> ChunkValueChunkXs,
      IReadOnlyList<int> ChunkValueChunkYs,
      IReadOnlyList<string> ChunkValueKinds,
      IReadOnlyList<float> ChunkValueFloats) {

    /// <summary>Sentinel Z value written into
    /// <see cref="RegionRepresentativeZ"/> when no representative was
    /// recorded (v1 saves decoded forward, or save-side code chose not
    /// to pick one). Matches
    /// <see cref="RegionPersistedRecord.NoRepresentative"/>'s Z.</summary>
    public const int NoRepresentativeZ = int.MinValue;

  }

}
