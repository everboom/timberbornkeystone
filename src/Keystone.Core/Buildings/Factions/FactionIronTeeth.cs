using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>
  /// Iron Teeth-specific building classifications. See
  /// <see cref="FactionFolktails"/> for the per-faction structure
  /// rationale.
  ///
  /// <para><b>Nature mechanism: opted out.</b> The contemplation
  /// mechanic doesn't fit Iron Teeth's industrial theme, so this
  /// faction contributes nothing to the Nature path. Decoration /
  /// utility-building classifications still apply normally.</para>
  /// </summary>
  internal static class FactionIronTeeth {

    public const string Id = "IronTeeth";

    public static readonly IReadOnlyList<string> TransparentBuildings = new[] {
      // Transient detonation charges placed in the wild.
      "Dynamite.IronTeeth",
      "DoubleDynamite.IronTeeth",
      "TripleDynamite.IronTeeth",
    };

    public static readonly IReadOnlyList<string> NoAuraBuildings = new[] {
      // Designation flags.
      "GathererFlag.IronTeeth",
      "LumberjackFlag.IronTeeth",
      "ScavengerFlag.IronTeeth",
      // Wild-resource production.
      "Forester.IronTeeth",
      "TappersShack.IronTeeth",
      // Farmhouse.
      "FarmHouse.IronTeeth",
      // Decoration — point-sized ornaments and props.
      "Shrub.IronTeeth",
      "Lantern.IronTeeth",
      "Bench.IronTeeth",
      "BeaverStatue.IronTeeth",
      "BeaverBust.IronTeeth",
      "Brazier.IronTeeth",
      "Bell.IronTeeth",
      "DecorativeClock.IronTeeth",
      "PoleBanner.IronTeeth",
      "SquareBanner.IronTeeth",
      "WoodFence.IronTeeth",
      "MetalFence.IronTeeth",
      // Automation sensors / signal devices.
      "StreamGauge.IronTeeth",
      "ContaminationSensor.IronTeeth",
      "DepthSensor.IronTeeth",
      "FlowSensor.IronTeeth",
      "WeatherStation.IronTeeth",
      "Chronometer.IronTeeth",
      "PopulationCounter.IronTeeth",
      "PowerMeter.IronTeeth",
      "ResourceCounter.IronTeeth",
      "ScienceCounter.IronTeeth",
      "Indicator.IronTeeth",
      "Lever.IronTeeth",
      "Memory.IronTeeth",
      "Relay.IronTeeth",
      "Speaker.IronTeeth",
      "Timer.IronTeeth",
      "HttpAdapter.IronTeeth",
      "HttpLever.IronTeeth",
    };

    /// <summary>Iron Teeth has no Nature-source buildings. The
    /// contemplation mechanic doesn't fit IT's industrial theme;
    /// the faction is deliberately opted out of the Nature need
    /// set. The empty contribution list is here for symmetry —
    /// adding entries later would require no other plumbing.</summary>
    public static readonly IReadOnlyList<NatureContribution> NatureContributions =
        new NatureContribution[0];

  }

}
