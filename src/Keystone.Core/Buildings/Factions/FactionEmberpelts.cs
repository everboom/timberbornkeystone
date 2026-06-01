using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>
  /// Emberpelts-specific building classifications. See
  /// <see cref="FactionFolktails"/> for the per-faction structure
  /// rationale.
  ///
  /// <para>Emberpelts (workshop mod, "phoenix" theme) ships its
  /// canonical building catalog through
  /// <c>TemplateCollection.Buildings.Emberpelts.blueprint.json</c>
  /// with most blueprints baked into the mod's Unity AssetBundle, so
  /// the entries below were derived from the catalog file (the
  /// authoritative naming source) rather than from scanning loose
  /// <c>.blueprint.json</c> files.</para>
  ///
  /// <para><b>Notable absences</b> compared to Folktails:
  /// <list type="bullet">
  ///   <item>No <c>Beehive</c> or <c>Scarecrow</c> — Emberpelts is
  ///         phoenix/fire themed, ships neither.</item>
  ///   <item>No <c>BulletinPole</c> or <c>Hammock</c>.</item>
  ///   <item><c>DecorativeClock</c> (IronTeeth) is just
  ///         <c>Clock.Emberpelts</c> in this faction's naming.</item>
  /// </list></para>
  /// </summary>
  internal static class FactionEmberpelts {

    public const string Id = "Emberpelts";

    public static readonly IReadOnlyList<string> TransparentBuildings = new[] {
      // Decoration props the player places in the surrounding wild.
      "Weathervane.Emberpelts",
      // Transient detonation charges.
      "Dynamite.Emberpelts",
      "DoubleDynamite.Emberpelts",
      "TripleDynamite.Emberpelts",
    };

    public static readonly IReadOnlyList<string> NoAuraBuildings = new[] {
      // Designation flags.
      "GathererFlag.Emberpelts",
      "LumberjackFlag.Emberpelts",
      "ScavengerFlag.Emberpelts",
      // Wild-resource production.
      "Forester.Emberpelts",
      "TappersShack.Emberpelts",
      // Farmhouse. NB the runtime Blueprint.Name is "Farmhouse" (lowercase
      // h), NOT "FarmHouse" as the catalog *path*
      // (Buildings/Food/FarmHouse/FarmHouse.Emberpelts.blueprint) would
      // suggest -- the bundled blueprint's internal Name field disagrees
      // with its file path's casing (verified against Player.log). The
      // classifier is now exact-first with a case-insensitive fallback, so
      // either casing would match; we list the exact runtime spelling so
      // the exact path hits and FindCasingDrift stays quiet.
      "Farmhouse.Emberpelts",
      // Decoration — point-sized ornaments and props. Notably the
      // Emberpelts clock blueprint is named "Clock" (IronTeeth calls
      // it "DecorativeClock") so it goes in this list explicitly.
      "Shrub.Emberpelts",
      "Lantern.Emberpelts",
      "Hedge.Emberpelts",
      "Bench.Emberpelts",
      "BeaverStatue.Emberpelts",
      "BeaverBust.Emberpelts",
      "Brazier.Emberpelts",
      "Clock.Emberpelts",
      "PoleBanner.Emberpelts",
      "SquareBanner.Emberpelts",
      "WoodFence.Emberpelts",
      "MetalFence.Emberpelts",
      // Automation sensors / signal devices.
      "StreamGauge.Emberpelts",
      "ContaminationSensor.Emberpelts",
      "DepthSensor.Emberpelts",
      "FlowSensor.Emberpelts",
      "WeatherStation.Emberpelts",
      "Chronometer.Emberpelts",
      "PopulationCounter.Emberpelts",
      "PowerMeter.Emberpelts",
      "ResourceCounter.Emberpelts",
      "ScienceCounter.Emberpelts",
      "Indicator.Emberpelts",
      "Lever.Emberpelts",
      "Memory.Emberpelts",
      "Relay.Emberpelts",
      "Speaker.Emberpelts",
      "Timer.Emberpelts",
      "HttpAdapter.Emberpelts",
      "HttpLever.Emberpelts",
    };

    /// <summary>Emberpelts Nature-source buildings. Faction-level
    /// Wetland aversion: all biome subsets are <see cref="NatureBiomes.Dry"/>,
    /// including the otherwise-sky RooftopTerrace. The
    /// faction's NeedCollection therefore never instantiates the
    /// Wetland need.</summary>
    public static readonly IReadOnlyList<NatureContribution> NatureContributions = new[] {
      new NatureContribution(Id, new NatureBuilding[] {
        new NatureBuilding("ContemplationSpot.Emberpelts", NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
        new NatureBuilding("Campfire.Emberpelts",          NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
        new NatureBuilding("RooftopTerrace.Emberpelts",    NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
      }),
    };

  }

}
