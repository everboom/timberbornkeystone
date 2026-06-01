namespace Keystone.Core.Time {

  /// <summary>
  /// Read-side port over the host's game clock. Engine-agnostic surface
  /// over Timberborn's <c>GameCycleService</c> and weather services. The
  /// Mod layer provides a <c>GameClockAdapter</c> that wraps these.
  ///
  /// Both <see cref="Now"/> and <see cref="CurrentWeather"/> are
  /// snapshots; consumers reading them at the same moment get
  /// consistent values, but a clock read at frame N+1 may differ from
  /// frame N.
  /// </summary>
  public interface IClock {

    /// <summary>The current game timestamp.</summary>
    GameTimestamp Now { get; }

    /// <summary>The current weather phase.</summary>
    WeatherKind CurrentWeather { get; }

    /// <summary>
    /// Continuous monotonic day-count from map start. Equivalent to
    /// Timberborn's <c>IDayNightCycle.PartialDayNumber</c>: the integer
    /// part is the elapsed-days count, the fractional part is progress
    /// through the current day.
    ///
    /// <para>Used by Keystone for flat real-valued time math (age
    /// accumulators, dt computation across ticks) where the structured
    /// <see cref="GameTimestamp"/>'s cycle/day pair is awkward. The
    /// adapter forwards directly to the game's value; this is not a
    /// composite estimate.</para>
    /// </summary>
    float TotalDaysElapsed { get; }

  }

}
