using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Core.Time;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// One region's persisted clock-stamp data. Captured at save time and
  /// rehydrated onto a freshly-Indexed <see cref="Region"/> at load time;
  /// region member sets, neighbours, and ecology field state are not
  /// persisted (pure derivation, rebuilt by the surveyor + region service
  /// at PostLoad).
  ///
  /// <para><see cref="TotalDaysAtCreation"/> is the absolute day-count at
  /// the moment the region was first observed -- used by the score-store
  /// derivations that need a flat real-valued time anchor (the structured
  /// <see cref="GameTimestamp"/> works fine for display but isn't a
  /// monotonic float).</para>
  ///
  /// <para><see cref="Representative"/> is a fallback for the save→load
  /// remap. The canonical-ID save path normally produces save IDs that
  /// match what <see cref="Region.Id"/> is after a fresh <c>Index()</c>,
  /// so an ID-equality lookup at load time finds each saved record's
  /// live counterpart directly. When that lookup fails (old save,
  /// surveyor difference between save and load, etc.), the load layer
  /// looks up <see cref="Representative"/> in the live surface→region
  /// map and reattaches this record's data to the region containing
  /// that surface. The sentinel <see cref="NoRepresentative"/> means
  /// the record was decoded from a v1 save that didn't carry a
  /// representative -- callers fall through to drop without trying
  /// the fallback.</para>
  /// </summary>
  /// <param name="Id">The region's stable id.</param>
  /// <param name="CreatedAt">Game timestamp at first observation.</param>
  /// <param name="WeatherAtCreation">Weather phase active at <paramref name="CreatedAt"/>.</param>
  /// <param name="TotalDaysAtCreation">Absolute day-count at first observation, for flat-time math.</param>
  /// <param name="Representative">A surface that was a member of this
  ///   region at save time. Used as a recovery key when ID equality
  ///   fails on reload. <see cref="NoRepresentative"/> sentinel = none
  ///   recorded (v1 save).</param>
  public readonly record struct RegionPersistedRecord(
      RegionId Id,
      GameTimestamp CreatedAt,
      WeatherKind WeatherAtCreation,
      float TotalDaysAtCreation,
      SurfaceCoord Representative) {

    /// <summary>Sentinel value for "no representative surface recorded
    /// in this save." Distinct from any reachable terrain coordinate
    /// because <see cref="SurfaceCoord.Z"/> never reaches
    /// <see cref="int.MinValue"/> in normal play.</summary>
    public static readonly SurfaceCoord NoRepresentative =
        new SurfaceCoord(0, 0, int.MinValue);

    /// <summary>True when no representative was recorded for this
    /// record (decoded from a v1 save, or written without picking
    /// one). Callers should skip the surface-based fallback when
    /// this is true.</summary>
    public bool HasRepresentative => Representative.Z != int.MinValue;

  }

}
