using System;

namespace Keystone.Core.Atmosphere {

  /// <summary>
  /// Absolute timestamps (in continuous game-days, matching
  /// <c>IClock.TotalDaysElapsed</c>) at which a scheduled mist spawns
  /// and despawns. Despawn-day may be the next day after spawn-day
  /// when the despawn window wraps past midnight; the returned
  /// <see cref="DespawnTime"/> already includes that wrap.
  /// </summary>
  public readonly record struct ScheduledMistTime(float SpawnTime, float DespawnTime);

  /// <summary>
  /// Pure deterministic-RNG roll layer that decides whether a single
  /// (day, x, y) tile gets a scheduled mist this morning, and when.
  /// Extracted from <c>WetlandMistDirector</c> so the determinism
  /// contract (same day + same tile + same parameters → same outcome
  /// across runs / reloads) is testable without the surrounding
  /// orchestration (Wetland-chunk detection, water-depth gate,
  /// neighbour-4 check, Unity GameObject instantiation).
  ///
  /// <para><b>Deterministic seeds.</b> The night-gate seed is
  /// <c>day × 2654435761</c> (Knuth's multiplier). The per-tile seed
  /// is <c>day × 1000003 XOR (x × 73856093) XOR (y × 19349663)</c>
  /// (three coprime large primes — standard Bevilacqua-style 2D
  /// hash). The two seeds are independent so the night-gate RNG can't
  /// correlate with per-tile rolls.</para>
  ///
  /// <para><b>What the caller does.</b> The Mod-side director runs
  /// the higher-level orchestration: walks Wetland-dominant chunks,
  /// applies the neighbour-4 chunk-edge filter, finds a surface in
  /// the column, applies the water-depth band gate, instantiates the
  /// GameObject. The per-tile roll is the innermost decision.</para>
  /// </summary>
  public static class MistScheduleRoll {

    /// <summary>True iff the day-level binary "is today foggy" gate
    /// passes. Seeded by <paramref name="day"/> alone so reloads
    /// produce the same outcome regardless of time-of-day at load.
    /// <paramref name="foggyMorningProbability"/> is clamped to
    /// <c>[0, 1]</c> implicitly by the RNG comparison
    /// (probability ≤ 0 → never; probability ≥ 1 → always).</summary>
    public static bool ShouldRollToday(int day, float foggyMorningProbability) {
      var nightSeed = unchecked((int)(day * 2654435761u));
      var nightRng = new Random(nightSeed);
      return nightRng.NextDouble() < foggyMorningProbability;
    }

    /// <summary>Roll for a single tile. Returns the absolute spawn /
    /// despawn timestamps if the tile is scheduled, or <c>null</c> if
    /// any of three gates rejects:
    /// <list type="number">
    ///   <item>Density gate: <c>rng.NextDouble() ≥ density</c>.</item>
    ///   <item>Mid-window-load skip: the rolled spawn hour-fraction
    ///         is already in the past on this day (caller's
    ///         <paramref name="currentHourFraction"/>). Spawning
    ///         immediately would compress the mist's visible lifetime.</item>
    ///   <item>(No third gate at this layer — the water-depth + neighbour
    ///         gates are the caller's responsibility.)</item>
    /// </list>
    ///
    /// <para><b>Day-wrap.</b> When the despawn window comes earlier
    /// in the day than the spawn window (e.g. spawn at hour 20,
    /// despawn at hour 1), the despawn falls on the <i>next</i>
    /// in-game day. The returned <see cref="ScheduledMistTime.DespawnTime"/>
    /// already includes the +1 day so the timeline stays
    /// monotonic.</para>
    ///
    /// <para><b>Determinism.</b> Same <paramref name="day"/>,
    /// <paramref name="x"/>, <paramref name="y"/> + same window /
    /// density parameters always produce the same outcome — same
    /// pass/fail, same spawn/despawn times.</para>
    /// </summary>
    /// <param name="spawnWindowStart">Start of the spawn-time window
    /// as a day-fraction (e.g. 18.5/24 for "in-game hour 18.5").</param>
    /// <param name="spawnWindowEnd">End of the spawn-time window
    /// (exclusive) as a day-fraction. Must be greater than
    /// <paramref name="spawnWindowStart"/>.</param>
    /// <param name="despawnWindowStart">Start of the despawn-time
    /// window as a day-fraction. May be less than
    /// <paramref name="spawnWindowStart"/> — that triggers the
    /// day-wrap.</param>
    /// <param name="despawnWindowEnd">End of the despawn-time window
    /// (exclusive) as a day-fraction. Must be greater than
    /// <paramref name="despawnWindowStart"/>.</param>
    /// <param name="density">Per-tile spawn probability in <c>[0, 1]</c>.</param>
    /// <param name="currentHourFraction">The caller's current
    /// hour-fraction within <paramref name="day"/>; the roll skips
    /// when the spawn moment is already past.</param>
    public static ScheduledMistTime? TryRollTile(
        int day, int x, int y,
        float spawnWindowStart, float spawnWindowEnd,
        float despawnWindowStart, float despawnWindowEnd,
        float density,
        float currentHourFraction) {
      var seed = unchecked(day * 1000003 ^ (x * 73856093) ^ (y * 19349663));
      var rng = new Random(seed);

      if (rng.NextDouble() >= density) return null;

      var spawnHourFrac = (float)(spawnWindowStart
          + rng.NextDouble() * (spawnWindowEnd - spawnWindowStart));
      var despawnHourFrac = (float)(despawnWindowStart
          + rng.NextDouble() * (despawnWindowEnd - despawnWindowStart));

      if (spawnHourFrac < currentHourFraction) return null;

      // Day-wrap: despawn earlier in the day than spawn → next day.
      var despawnDay = despawnHourFrac > spawnHourFrac ? day : day + 1;

      return new ScheduledMistTime(
          SpawnTime: day + spawnHourFrac,
          DespawnTime: despawnDay + despawnHourFrac);
    }

  }

}
