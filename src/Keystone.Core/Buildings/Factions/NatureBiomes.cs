using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>
  /// Canonical biome subsets used by per-faction Nature building
  /// declarations. The full biome set the Nature mechanism currently
  /// supports is <see cref="All"/>; the thematic subsets help
  /// declare building/biome affinity (a campfire on a beach reads
  /// off-theme, hence <see cref="WetlandOnly"/> for water-themed
  /// buildings vs <see cref="Dry"/> for ground-level
  /// contemplation).
  /// </summary>
  public static class NatureBiomes {

    /// <summary>All three biomes the Nature mechanism currently
    /// supports. Order is canonical — the per-faction biome union
    /// in the Mod-side modifier provider follows this ordering when
    /// emitting the NeedCollection append list.</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { "Forest", "Grassland", "Wetland" };

    /// <summary>Land-themed subset (Forest + Grassland). Use for
    /// ground-level, non-water-themed buildings — campfires, regular
    /// contemplation spots, gardens. Excluding Wetland matches the
    /// "thematic affinity" rule: a campfire by a swamp reads
    /// off-theme.</summary>
    public static readonly IReadOnlyList<string> Dry =
        new[] { "Forest", "Grassland" };

    /// <summary>Wetland-only subset, for water-themed leisure
    /// buildings (Folktails Lido, LeafCoats MudPit).</summary>
    public static readonly IReadOnlyList<string> WetlandOnly =
        new[] { "Wetland" };

  }

}
