using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>
  /// Building classifications for the "Tree of Life" workshop mod
  /// (manifest id <c>grauschweif.treeoflife</c>). Sits alongside the
  /// per-faction files because it follows the same registry pattern,
  /// but covers a third-party mod whose blueprints span Folktails and
  /// IronTeeth.
  ///
  /// <para><b>Mod content (v1.5.2):</b> the monumental tree itself
  /// in two faction variants (15×15 BOs with mixed
  /// <c>All</c> / <c>Floor,Bottom,Corners,Middle</c> / <c>None</c>
  /// occupations), two planting-ground variants (also 15×15),
  /// and a pair of unrelated utility buildings (an
  /// effectively-uncraftable <c>UnForester.Common</c> at 1,000,000
  /// science cost and a small <c>UngroupedStorage</c>).
  ///
  /// <para><b>What we classify:</b>
  /// <list type="bullet">
  ///   <item>Both TreeOfLife faction variants → settled + no halo
  ///         (a 15×15 BO with the default 1-tile aura would sterilize
  ///         a 17×17 ecology block around it, which is far too much
  ///         for a single attraction).</item>
  ///   <item>TreeOfLife.Folktails additionally → Keystone Nature
  ///         source (visiting beavers fulfill the biome-keyed
  ///         Folktails Nature need on top of the mod's own
  ///         <c>TreeBlessing*</c> / <c>TreeCurse*</c> /
  ///         <c>FruitOfLife</c> needs). TreeOfLife.IronTeeth is
  ///         deliberately not a Nature source — IronTeeth is opted
  ///         out of the Keystone Nature need set entirely
  ///         (see <see cref="FactionIronTeeth"/>) — but still
  ///         needs the NoAura treatment.</item>
  /// </list></para>
  ///
  /// <para><b>What we don't classify:</b>
  /// <list type="bullet">
  ///   <item>Planting grounds — left to default treatment by design;
  ///         not Keystone's concern.</item>
  ///   <item><c>UnForester.Common</c> — 1,000,000 science cost makes
  ///         it effectively inaccessible to players; default settle+
  ///         aura is fine for the rare unlock.</item>
  ///   <item><c>UngroupedStorage</c> — 1×1×1 enterable storage;
  ///         default settle+aura is correct.</item>
  /// </list></para></para>
  /// </summary>
  internal static class ModTreeOfLife {

    /// <summary>Source-mod id for diagnostics. Matches the second
    /// half of the mod's manifest id (<c>grauschweif.treeoflife</c>);
    /// the author prefix is dropped to match the short-name
    /// convention the per-faction files use.</summary>
    public const string Id = "TreeOfLife";

    public static readonly IReadOnlyList<string> TransparentBuildings =
        new string[0];

    /// <summary>TreeOfLife.IronTeeth — settled + no halo via the
    /// name-based path (the simpler of the two no-aura mechanisms),
    /// because IronTeeth doesn't participate in the Nature need set
    /// and so doesn't need a <see cref="NatureContribution"/> entry.
    ///
    /// <para>The Folktails variant gets the same NoAura footprint
    /// via its <see cref="NatureContribution"/> entry below
    /// (<c>NoAura: true</c> on the <see cref="NatureBuilding"/>
    /// causes the modifier provider to attach
    /// <c>KeystoneEcologyNoAuraSpec</c> alongside the Nature source
    /// spec — single mechanism, two effects).</para></summary>
    public static readonly IReadOnlyList<string> NoAuraBuildings = new[] {
      "TreeOfLife.IronTeeth",
    };

    /// <summary>Tree of Life on the Folktails and Emberpelts
    /// factions. Both entries set <c>NoAura: true</c> — the modifier
    /// provider attaches <c>KeystoneEcologyNoAuraSpec</c> alongside
    /// <c>KeystoneNatureSourceSpec</c>, so the tree settles its own
    /// 15×15 footprint without propagating the aura.
    ///
    /// <para><b>Emberpelts variant is pre-wired ahead of the
    /// modder shipping it</b> — at time of writing the mod ships
    /// only Folktails / IronTeeth variants, but the author is
    /// planning an Emberpelts variant. Listing it here means the
    /// integration lights up the moment that blueprint appears in
    /// the templates, no Keystone update needed. While absent, the
    /// wiring is harmless: the suffix-match in
    /// <c>KeystoneNatureModifierProvider</c> fires only when a
    /// matching blueprint loads, and <c>NatureBuildingWiringTest</c>
    /// already treats "name targets a faction not currently active"
    /// as an expected absence.</para>
    ///
    /// <para><b>Biomes:</b>
    /// <list type="bullet">
    ///   <item><b>Folktails:</b> <see cref="NatureBiomes.All"/> —
    ///         visiting the tree fulfills Forest, Grassland, and
    ///         Wetland flavors of the Keystone Nature need (same
    ///         shape as the vanilla <c>RooftopTerrace.Folktails</c>
    ///         entry).</item>
    ///   <item><b>Emberpelts:</b> <see cref="NatureBiomes.Dry"/> —
    ///         Forest and Grassland only. Matches the faction-level
    ///         Wetland aversion (every other Emberpelts Nature
    ///         building uses Dry, and the faction's NeedCollection
    ///         deliberately doesn't instantiate the Wetland need).
    ///         Using All here would drag Wetland into the Emberpelts
    ///         biome union as the sole source — breaking the
    ///         faction's design.</item>
    /// </list></para></summary>
    public static readonly IReadOnlyList<NatureContribution> NatureContributions = new[] {
      new NatureContribution(FactionFolktails.Id, new NatureBuilding[] {
        new NatureBuilding("TreeOfLife.Folktails", NatureBiomes.All,
                           Transparent: false, NoAura: true),
      }),
      new NatureContribution(FactionEmberpelts.Id, new NatureBuilding[] {
        new NatureBuilding("TreeOfLife.Emberpelts", NatureBiomes.Dry,
                           Transparent: false, NoAura: true),
      }),
    };

  }

}
