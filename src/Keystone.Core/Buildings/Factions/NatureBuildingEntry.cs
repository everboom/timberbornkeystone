using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>One Nature-source building on a faction.</summary>
  /// <param name="BlueprintName">Blueprint filename without the
  /// <c>.blueprint</c> extension (e.g. <c>"ContemplationSpot.Folktails"</c>).
  /// Matched against the last path segment of each loaded blueprint's
  /// Path (lowercased), so directory layout in the source mod is
  /// irrelevant.</param>
  /// <param name="Biomes">Biome subset the building offers. Use one of
  /// the canonical lists in <see cref="NatureBiomes"/> for general
  /// contemplation buildings and the smaller subsets for thematic
  /// exceptions.</param>
  /// <param name="Transparent">Surveyor-invisibility flag. When
  /// <c>true</c>, the building's tile does not count as settled at
  /// all. Defaults to <c>false</c>; every entry in the production
  /// table today uses NoAura instead. Mutually exclusive with
  /// <paramref name="NoAura"/>; both true is a configuration error
  /// that the Mod-side modifier provider asserts on.</param>
  /// <param name="NoAura">No-aura flag. When <c>true</c>, the
  /// building's tile counts as settled but does NOT propagate the
  /// 1-tile settled aura to its 8 lateral neighbors. Mutually
  /// exclusive with <paramref name="Transparent"/>.</param>
  /// <remarks>If both flags are <c>false</c> the building emits a
  /// Nature source with default settlement semantics (settles its
  /// own tile AND propagates a 1-tile aura).</remarks>
  public sealed record NatureBuilding(
      string BlueprintName,
      IReadOnlyList<string> Biomes,
      bool Transparent = false,
      bool NoAura = false);

  /// <summary>One source's (faction or third-party mod) declaration
  /// of buildings that contribute Nature-source semantics to a
  /// particular faction's NeedCollection. A single source file may
  /// declare multiple contributions targeting different factions
  /// (e.g. a mod that adds contemplation buildings to both Folktails
  /// and IronTeeth would yield two contributions).</summary>
  /// <param name="FactionId">Matches the <c>CollectionId</c> on the
  /// target faction's <c>NeedCollection</c> blueprint (e.g.
  /// <c>"Folktails"</c>). For per-faction source files this is just
  /// the file's own <c>Id</c>; for per-mod source files it names the
  /// faction whose beavers will use these buildings.</param>
  /// <param name="Buildings">Buildings that should carry
  /// <c>KeystoneNatureSourceSpec</c>. Aggregated across all
  /// contributions sharing the same <paramref name="FactionId"/> by
  /// <see cref="FactionRegistry"/>.</param>
  public sealed record NatureContribution(
      string FactionId,
      IReadOnlyList<NatureBuilding> Buildings);

  /// <summary>One faction's aggregated Nature-source buildings,
  /// merged from every <see cref="NatureContribution"/> in
  /// <see cref="FactionRegistry"/> that targets this
  /// <paramref name="FactionId"/>. This is the consumer-facing
  /// shape used by the Mod-side modifier provider and self-tests.</summary>
  public sealed record NatureFactionEntry(
      string FactionId,
      IReadOnlyList<NatureBuilding> Buildings);

}
