using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>
  /// Folktails-specific building classifications: which Folktails
  /// blueprints Keystone tags as ecology-transparent (surveyor pretends
  /// the BO isn't there) or no-aura (settles its own voxel, no halo).
  /// Aggregated into the global sets by <see cref="FactionRegistry"/>;
  /// the adapter reads only through the registry / through
  /// <see cref="BlueprintNamePolicy"/>.
  ///
  /// <para><b>One file per faction</b> so that "what does Keystone do
  /// with Folktails buildings?" is one file open, not three greps.
  /// Generic / heuristic policy (substring tokens, blocking-natural
  /// whitelist) lives in <see cref="BlueprintNamePolicy"/>.</para>
  /// </summary>
  internal static class FactionFolktails {

    /// <summary>Canonical faction id. Matches the suffix on this
    /// faction's blueprint names and the <c>CollectionId</c> on its
    /// <c>NeedCollection</c> blueprint.</summary>
    public const string Id = "Folktails";

    /// <summary>Blueprints the surveyor should pretend aren't there
    /// at all (no settle, no halo). Things that conceptually exist
    /// <i>in</i> the wild rather than overlaying built land.</summary>
    public static readonly IReadOnlyList<string> TransparentBuildings = new[] {
      // Coexist-with-nature production / prop.
      "Beehive.Folktails",
      "Scarecrow.Folktails",
      "Weathervane.Folktails",
      // Transient detonation charges placed in the wild.
      "Dynamite.Folktails",
      "DoubleDynamite.Folktails",
      "TripleDynamite.Folktails",
    };

    /// <summary>Blueprints that settle their own voxel but suppress
    /// the 1-tile halo. Real built infrastructure that shouldn't
    /// sterilize a 3×3 ecology block around itself.</summary>
    public static readonly IReadOnlyList<string> NoAuraBuildings = new[] {
      // Designation flags — markers for wild-area harvesting.
      "GathererFlag.Folktails",
      "LumberjackFlag.Folktails",
      "ScavengerFlag.Folktails",
      // Wild-resource production.
      "Forester.Folktails",
      "TappersShack.Folktails",
      // Farmhouses — work outward into surrounding fields.
      "AquaticFarmhouse.Folktails",
      "EfficientFarmHouse.Folktails",
      // Decoration — point-sized ornaments and props.
      "Shrub.Folktails",
      "Lantern.Folktails",
      "Hedge.Folktails",
      "Bench.Folktails",
      "Hammock.Folktails",
      "BeaverStatue.Folktails",
      "PoleBanner.Folktails",
      "SquareBanner.Folktails",
      "BulletinPole.Folktails",
      "WoodFence.Folktails",
      // Automation sensors / signal devices — single-tile
      // instruments deliberately scatterable in any context.
      "StreamGauge.Folktails",
      "ContaminationSensor.Folktails",
      "DepthSensor.Folktails",
      "FlowSensor.Folktails",
      "WeatherStation.Folktails",
      "Chronometer.Folktails",
      "PopulationCounter.Folktails",
      "PowerMeter.Folktails",
      "ResourceCounter.Folktails",
      "ScienceCounter.Folktails",
      "Indicator.Folktails",
      "Lever.Folktails",
      "Memory.Folktails",
      "Relay.Folktails",
      "Speaker.Folktails",
      "Timer.Folktails",
      "HttpAdapter.Folktails",
      "HttpLever.Folktails",
    };

    /// <summary>Folktails Nature-source buildings (contemplation
    /// affordances). All currently use the NoAura footprint:
    /// ground-level structures sitting in preserved nature, settling
    /// their own voxel without sterilizing the surrounding 3×3.
    /// RooftopTerrace also NoAura — sits atop a lodge but Keystone
    /// classifies the lodge separately.
    ///
    /// <para>Wrapped in a <see cref="NatureContribution"/> targeting
    /// this faction's own <see cref="Id"/>. Other source files
    /// (e.g. third-party mod files) may also contribute to Folktails
    /// — <see cref="FactionRegistry"/> aggregates by FactionId.</para></summary>
    public static readonly IReadOnlyList<NatureContribution> NatureContributions = new[] {
      new NatureContribution(Id, new NatureBuilding[] {
        new NatureBuilding("ContemplationSpot.Folktails", NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
        new NatureBuilding("Campfire.Folktails",          NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
        new NatureBuilding("Lido.Folktails",              NatureBiomes.WetlandOnly,
                           Transparent: false, NoAura: true),
        new NatureBuilding("RooftopTerrace.Folktails",    NatureBiomes.All,
                           Transparent: false, NoAura: true),
      }),
    };

  }

}
