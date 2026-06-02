using System;

namespace Keystone.Core.Flourish {

  /// <summary>
  /// Pure decay rule for dead flourishes. A flourish reaches
  /// <see cref="FlourishLifeStatus.Dead"/> when its habitat turned
  /// against it (attrition kill, badwater self-kill). Dead remains
  /// otherwise persist in the world indefinitely until a spawn handler
  /// reclaims the tile; this rule lets them rot away on their own at a
  /// steady per-day chance so a region that dies and stays marginal
  /// clears itself rather than accumulating dead growth forever.
  ///
  /// <para>The Mod-side sweep visits every dead flourish once per
  /// decay cycle (one game-day) and rolls
  /// <see cref="PerCycleProbability"/> to decide deletion. Keeping the
  /// probability math here — separate from the entity-enumeration and
  /// deletion plumbing — makes the cadence-correctness rule unit-testable
  /// without a Timberborn host.</para>
  /// </summary>
  public static class DeadFlourishDecay {

    #region Constants

    /// <summary>Default probability that a given dead flourish is
    /// removed over one game-day. 0.10 = ~10% per day, i.e. a median
    /// lifetime of roughly a week of in-game time before it rots away.
    /// Gradual by design: a per-day roll, not a synchronized cull, so
    /// dead remains thin out smoothly rather than vanishing in lockstep.</summary>
    public const float DefaultDailyDeleteChance = 0.10f;

    #endregion

    #region Decay probability

    /// <summary>
    /// Probability that a dead flourish should be deleted during a
    /// decay cycle that advanced <paramref name="cycleDtDays"/> game-days,
    /// given a per-day removal chance of <paramref name="dailyChance"/>.
    ///
    /// <para>Compounded so the per-day rate is preserved regardless of
    /// cycle length: <c>p = 1 − (1 − dailyChance)^cycleDtDays</c>. A
    /// normal one-day cycle returns <paramref name="dailyChance"/>
    /// exactly; a cycle that happened to span two game-days (e.g. a
    /// starved sweep, or fast-forward) returns two days' worth of
    /// chance (<c>0.10 → 0.19</c>), matching "10% chance each day."</para>
    ///
    /// <para>Returns 0 when <paramref name="cycleDtDays"/> is 0 — the
    /// rolling sweep reports zero elapsed time on its first cycle after
    /// a load, so no dead flourishes are culled at load; deletion only
    /// begins once real game-time has elapsed.</para>
    /// </summary>
    /// <param name="dailyChance">Per-day deletion probability, in
    /// <c>[0, 1]</c>.</param>
    /// <param name="cycleDtDays">Game-days elapsed during the cycle
    /// being processed; must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">If
    /// <paramref name="dailyChance"/> is outside <c>[0, 1]</c> or
    /// <paramref name="cycleDtDays"/> is negative — these are caller
    /// bugs, so they surface loudly rather than clamping silently.</exception>
    public static float PerCycleProbability(float dailyChance, float cycleDtDays) {
      if (dailyChance < 0f || dailyChance > 1f) {
        throw new ArgumentOutOfRangeException(
            nameof(dailyChance),
            $"Daily delete chance must be in [0, 1]; got {dailyChance}.");
      }
      if (cycleDtDays < 0f) {
        throw new ArgumentOutOfRangeException(
            nameof(cycleDtDays),
            $"Cycle dt must be non-negative; got {cycleDtDays}.");
      }
      if (cycleDtDays == 0f) return 0f;
      // Short-circuit the certain cases so float pow can't drift them
      // off 0 / 1 at the boundaries.
      if (dailyChance == 0f) return 0f;
      if (dailyChance == 1f) return 1f;
      return 1f - (float)Math.Pow(1f - dailyChance, cycleDtDays);
    }

    #endregion

  }

}
