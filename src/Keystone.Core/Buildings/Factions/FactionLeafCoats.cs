using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>
  /// LeafCoats-specific building classifications. See
  /// <see cref="FactionFolktails"/> for the per-faction structure
  /// rationale.
  ///
  /// <para>LeafCoats (workshop "early-access" mod) is the tree
  /// faction — settlements built in / on tree trunks, with branch
  /// bridges connecting elevated structures. Most building
  /// categorizations align with vanilla Folktails / IronTeeth
  /// patterns; the tree-themed additions (PruningFlag, GathererHut,
  /// metal-gate / hedge-gate variants, arch banner, branch bridges)
  /// are listed explicitly here.</para>
  ///
  /// <para><b>Notable absences</b> compared to Folktails / IronTeeth /
  /// Emberpelts:
  /// <list type="bullet">
  ///   <item>No dynamite / detonator / firework launcher — LeafCoats
  ///         doesn't ship demolition or pyrotechnics.</item>
  ///   <item>No FarmHouse — the faction handles food differently
  ///         (FoodProcessor, Fermenter, GathererHut).</item>
  /// </list></para>
  /// </summary>
  internal static class FactionLeafCoats {

    public const string Id = "LeafCoats";

    public static readonly IReadOnlyList<string> TransparentBuildings = new[] {
      // Coexist-with-nature props.
      "Scarecrow.LeafCoats",
      "Weathervane.LeafCoats",
      "Beehive.LeafCoats",
    };

    public static readonly IReadOnlyList<string> NoAuraBuildings = new[] {
      // Designation flags — including PruningFlag, LeafCoats' tree-
      // care equivalent of the lumberjack flag.
      "GathererFlag.LeafCoats",
      "LumberjackFlag.LeafCoats",
      "ScavengerFlag.LeafCoats",
      "PruningFlag.LeafCoats",
      // Wild-resource production.
      "Forester.LeafCoats",
      "TappersShack.LeafCoats",
      // Gatherer station — sits in the foraging area, the
      // surrounding wild is what the gatherers work in. Distinct
      // from vanilla where GathererFlag alone designates and workers
      // commute from elsewhere; LeafCoats splits this into flag
      // (designation) + hut (worker housing for foragers).
      "GathererHut.LeafCoats",
      // Decoration — point-sized ornaments and props. Includes the
      // gate variants of decorative fences (HedgeGate, MetalGate),
      // the new ArchBanner, and LeafCoats' single-name Clock
      // (vanilla IronTeeth has "DecorativeClock").
      "Shrub1.LeafCoats",
      "Shrub2.LeafCoats",
      "Lantern.LeafCoats",
      "MetalLantern.LeafCoats",
      "Hedge.LeafCoats",
      "HedgeGate.LeafCoats",
      "Bench.LeafCoats",
      "BeaverStatue.LeafCoats",
      "BeaverBust.LeafCoats",
      "Clock.LeafCoats",
      "PoleBanner.LeafCoats",
      "SquareBanner.LeafCoats",
      "ArchBanner.LeafCoats",
      "MetalFence.LeafCoats",
      "MetalGate.LeafCoats",
      // Automation sensors / signal devices.
      "StreamGauge.LeafCoats",
      "ContaminationSensor.LeafCoats",
      "DepthSensor.LeafCoats",
      "FlowSensor.LeafCoats",
      "WeatherStation.LeafCoats",
      "Chronometer.LeafCoats",
      "PopulationCounter.LeafCoats",
      "PowerMeter.LeafCoats",
      "ResourceCounter.LeafCoats",
      "ScienceCounter.LeafCoats",
      "Indicator.LeafCoats",
      "Lever.LeafCoats",
      "Memory.LeafCoats",
      "Relay.LeafCoats",
      "Speaker.LeafCoats",
      "Timer.LeafCoats",
      "HttpAdapter.LeafCoats",
      "HttpLever.LeafCoats",
    };

    /// <summary>LeafCoats Nature-source buildings. The tree faction
    /// ships extra contemplation affordances tied to the
    /// tree-building theme: Garden (ground-level botanic), MudPit
    /// (wetland leisure), ObservationTerrace, and a branch-mounted
    /// ContemplationSpot variant.</summary>
    public static readonly IReadOnlyList<NatureContribution> NatureContributions = new[] {
      new NatureContribution(Id, new NatureBuilding[] {
        new NatureBuilding("ContemplationSpot.LeafCoats",        NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
        new NatureBuilding("ContemplationSpot.Branch.LeafCoats", NatureBiomes.All,
                           Transparent: false, NoAura: true),
        new NatureBuilding("ObservationTerrace.LeafCoats",       NatureBiomes.All,
                           Transparent: false, NoAura: true),
        new NatureBuilding("Garden.LeafCoats",                   NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
        new NatureBuilding("MudPit.LeafCoats",                   NatureBiomes.WetlandOnly,
                           Transparent: false, NoAura: true),
      }),
    };

  }

}
