using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>
  /// Building classifications for the "New Building Styles" workshop
  /// mod (manifest id <c>Redic.NewStyles</c>). Sits alongside the
  /// per-faction files because it follows the same registry pattern,
  /// but covers a third-party mod rather than a faction — its
  /// blueprints carry both <c>.Folktails</c> and <c>.IronTeeth</c>
  /// suffixes (it adds style variants into both vanilla factions), so
  /// splitting it across <see cref="FactionFolktails"/> /
  /// <see cref="FactionIronTeeth"/> would be the wrong organizing
  /// axis. One file per source mod, in line with the same "two greps
  /// down to one file open" rationale that motivated the per-faction
  /// split.
  ///
  /// <para><b>Mod content (v0.1):</b> 29 block-object blueprints
  /// (the inspector script misses Baker/FoodPlant/TubewayStop because
  /// of a folder traversal quirk — full catalog covered here).
  /// Breakdown:
  /// <list type="bullet">
  ///   <item>15 enterable buildings (cabins, flats, mill, steam
  ///         plant, thinker, baker, foodplant, district center,
  ///         tubeway stop, ...) — default settle+aura, no entries
  ///         needed. <c>TubewayStop.IronTeeth</c> is also
  ///         auto-promoted to NoAura by the "Tube" structural-path
  ///         substring.</item>
  ///   <item>6 decoration items with point-sized footprints (Cyprus
  ///         tree / Lamp / Stringlights × two faction variants) —
  ///         listed in <see cref="NoAuraBuildings"/>; scattered in
  ///         path / wild areas and shouldn't sterilize a 3×3 ecology
  ///         halo.</item>
  ///   <item>6 roof tiles (<c>CabinRoof*</c>, <c>FlatRoof*</c>) —
  ///         Path-occupying, inherit vanilla Path classification
  ///         which is already aura-friendly. No entries needed.</item>
  ///   <item>2 contemplation affordances on Folktails
  ///         (<c>Shrine.Folktails</c> = ContemplationSpot variant,
  ///         <c>Rooflounge.Folktails</c> = RooftopTerrace variant) —
  ///         listed in <see cref="NatureContributions"/> targeting
  ///         the <c>Folktails</c> NeedCollection.</item>
  ///   <item><c>ZiplineTower.Folktails</c> — caught by the "Zipline"
  ///         token in <see cref="BlueprintNamePolicy.StructuralPathTokens"/>;
  ///         no entry needed.</item>
  /// </list></para>
  ///
  /// <para>No transparent props (no <c>Beehive</c> / <c>Scarecrow</c> /
  /// dynamite equivalents in this mod).</para>
  /// </summary>
  internal static class ModNewBuildingStyles {

    /// <summary>Source-mod id for diagnostics. Matches the second
    /// half of the mod's manifest id (<c>Redic.NewStyles</c>); the
    /// "Redic" author prefix is dropped to match the short-name
    /// convention the per-faction files use.</summary>
    public const string Id = "NewBuildingStyles";

    public static readonly IReadOnlyList<string> TransparentBuildings =
        new string[0];

    public static readonly IReadOnlyList<string> NoAuraBuildings = new[] {
      // Decorative tree — declares the vanilla Shrub need and reuses
      // the Shrub flavor-description loc key. Behaves like Shrub.
      "Cyprus.Folktails",
      "Cyprus.IronTeeth",
      // Decorative lantern — declares the vanilla Lantern need and
      // reuses the Lantern description/flavor loc keys. Behaves like
      // Lantern.
      "Lamp.Folktails",
      "Lamp.IronTeeth",
      // Decorative string lights — merge-along-line decor; scatters
      // through path tiles.
      "Stringlights.Folktails",
      "Stringlights.IronTeeth",
    };

    /// <summary>Contemplation buildings the mod adds to the Folktails
    /// faction. Both target Folktails' NeedCollection (where the
    /// vanilla ContemplationSpot / RooftopTerrace needs already live);
    /// <see cref="FactionRegistry"/> aggregates these into the same
    /// <see cref="NatureFactionEntry"/> as
    /// <see cref="FactionFolktails.NatureContributions"/>.
    ///
    /// <para><b>Shrine</b> declares <c>AttractionSpec.NeedId =
    /// "ContemplationSpot"</c> and reuses the
    /// <c>Building.ContemplationSpot.*</c> loc keys — it's a
    /// reskinned ContemplationSpot. Mirrors the vanilla
    /// <c>ContemplationSpot.Folktails</c> entry: Dry biomes, NoAura.</para>
    ///
    /// <para><b>Rooflounge</b> declares <c>AttractionSpec.NeedId =
    /// "RooftopTerrace"</c> and reuses the
    /// <c>Building.RooftopTerrace.*</c> loc keys — a reskinned
    /// RooftopTerrace. Mirrors the vanilla
    /// <c>RooftopTerrace.Folktails</c> entry: All biomes, NoAura.</para></summary>
    public static readonly IReadOnlyList<NatureContribution> NatureContributions = new[] {
      new NatureContribution(FactionFolktails.Id, new NatureBuilding[] {
        new NatureBuilding("Shrine.Folktails",     NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
        new NatureBuilding("Rooflounge.Folktails", NatureBiomes.All,
                           Transparent: false, NoAura: true),
      }),
    };

  }

}
