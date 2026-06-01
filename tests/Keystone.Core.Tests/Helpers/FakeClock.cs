using Keystone.Core.Time;

namespace Keystone.Core.Tests.Helpers {

  /// <summary>
  /// Test fake for <see cref="IClock"/>. Holds a mutable
  /// <see cref="TotalDaysElapsed"/> so tests can advance / rewind game
  /// time manually between calls. <see cref="Now"/> and
  /// <see cref="CurrentWeather"/> stay at default values — most tests
  /// that need a clock care only about <see cref="TotalDaysElapsed"/>.
  /// </summary>
  internal sealed class FakeClock : IClock {

    public float TotalDaysElapsed { get; set; }

    public GameTimestamp Now { get; set; } = GameTimestamp.Origin;

    public WeatherKind CurrentWeather { get; set; } = WeatherKind.Temperate;

  }

}
