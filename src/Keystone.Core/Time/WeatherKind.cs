namespace Keystone.Core.Time {

  /// <summary>
  /// The weather phase the game is currently in. Matches Timberborn's
  /// three-state weather model: temperate (the default rainy/sunny
  /// phase), drought (no rain, evaporation), badtide (rain produces
  /// contaminated water).
  ///
  /// Useful as ecology context: "this region was first observed during
  /// a drought" or "this contamination spread happened during a badtide".
  /// </summary>
  public enum WeatherKind {

    /// <summary>The default phase between hazardous events.</summary>
    Temperate,

    /// <summary>Hazardous phase: no rain, water sources dry up.</summary>
    Drought,

    /// <summary>Hazardous phase: precipitation produces contaminated water.</summary>
    Badtide,

  }

}
