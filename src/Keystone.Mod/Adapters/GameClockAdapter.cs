using Keystone.Core.Time;
using Timberborn.GameCycleSystem;
using Timberborn.HazardousWeatherSystem;
using Timberborn.TimeSystem;
using Timberborn.WeatherSystem;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="IClock"/> implementation backed by Timberborn's
  /// <see cref="GameCycleService"/> + <see cref="WeatherService"/> +
  /// <see cref="HazardousWeatherService"/>. Translates the game's
  /// (Cycle, CycleDay, PartialCycleDay) directly into a
  /// <see cref="GameTimestamp"/> and resolves the active weather kind
  /// via type checks on the current hazardous-weather instance.
  /// </summary>
  public sealed class GameClockAdapter : IClock {

    #region Fields

    private readonly GameCycleService _cycle;
    private readonly WeatherService _weather;
    private readonly HazardousWeatherService _hazardous;
    private readonly IDayNightCycle _dayNight;

    #endregion

    #region Construction

    public GameClockAdapter(
        GameCycleService cycle,
        WeatherService weather,
        HazardousWeatherService hazardous,
        IDayNightCycle dayNight) {
      _cycle = cycle;
      _weather = weather;
      _hazardous = hazardous;
      _dayNight = dayNight;
    }

    #endregion

    #region IClock

    /// <inheritdoc />
    public GameTimestamp Now =>
        new(_cycle.Cycle, _cycle.CycleDay, _cycle.PartialCycleDay);

    /// <inheritdoc />
    public float TotalDaysElapsed => _dayNight.PartialDayNumber;

    /// <inheritdoc />
    public WeatherKind CurrentWeather {
      get {
        if (!_weather.IsHazardousWeather) {
          return WeatherKind.Temperate;
        }
        var hw = _hazardous.CurrentCycleHazardousWeather;
        return hw switch {
            BadtideWeather => WeatherKind.Badtide,
            DroughtWeather => WeatherKind.Drought,
            _ => WeatherKind.Temperate,
        };
      }
    }

    #endregion

  }

}
