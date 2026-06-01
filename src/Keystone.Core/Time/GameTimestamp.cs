namespace Keystone.Core.Time {

  /// <summary>
  /// Game-cycle-aware timestamp. The three components match Timberborn's
  /// <c>GameCycleService</c> exactly: which drought/badtide cycle we're
  /// in, which day within that cycle, and the fractional progress through
  /// the day.
  ///
  /// <para>Cycle counts up monotonically from map start. Within a cycle,
  /// <see cref="CycleDay"/> resets to 0 and counts up to the cycle
  /// length (which the game varies). <see cref="PartialCycleDay"/> is
  /// 0..1 within the current day.</para>
  ///
  /// <para>This is the canonical timestamp Keystone uses for region age,
  /// eco-health history, and any other "when did this happen" question.
  /// Raw <see cref="System.DateTime"/> or process ticks would not
  /// survive save/reload; the cycle/day pair does, because the game
  /// itself persists those.</para>
  /// </summary>
  /// <param name="Cycle">Cycle number from map start, monotonic.</param>
  /// <param name="CycleDay">Integer day within the current cycle, resets each cycle.</param>
  /// <param name="PartialCycleDay">Fractional progress through the current day, 0..1.</param>
  public readonly record struct GameTimestamp(int Cycle, int CycleDay, float PartialCycleDay) {

    /// <summary>The timestamp at map start: cycle 0, day 0, partial 0.</summary>
    public static readonly GameTimestamp Origin = default;

  }

}
