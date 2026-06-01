namespace Keystone.Core.Biomes {

  /// <summary>
  /// The biome categories Keystone classifies chunks into. Each chunk
  /// scores against every biome; the chunk's "label" at any moment is
  /// the biome with the highest current score (subject to the
  /// classifier's tie-breaking rules).
  ///
  /// <para>Naming follows the design taxonomy. Adding a new biome
  /// requires a target function in <see cref="BiomeTargets"/> at
  /// minimum.</para>
  /// </summary>
  public enum BiomeKind {

    #region Land -- irrigated

    /// <summary>Irrigated land with multiple tree species. Slow to
    /// accumulate, slow to lose under normal conditions, instantly
    /// devastated by contamination.</summary>
    Forest,

    /// <summary>Irrigated land with no foliage or mixed crops (i.e.,
    /// not a forest and not a monoculture).</summary>
    Grassland,

    /// <summary>Irrigated land dominated by a single species. Currently
    /// fires on crop monocultures (one crop species at min density);
    /// the predicate may grow to cover tree monocultures too -- a
    /// pine-only forest is the same kind of "deliberate, low-diversity
    /// productive state" as a wheat field, distinct from a real
    /// Forest's species-diversity ceiling.</summary>
    Monoculture,

    #endregion

    #region Water -- clean

    /// <summary>High-flow water of any depth -- the main channel where
    /// current is strong enough to sweep away aquatic plant life.
    /// Distinguishes from <see cref="Wetland"/> (which is the slow
    /// side-channels with shallow water) by the flow-speed threshold,
    /// not by depth.</summary>
    River,

    /// <summary>Deep water with low flow -- typically a dam reservoir
    /// or natural pond. "Deep" means strictly greater than depth 1
    /// (hard threshold, no grace range -- water at exactly depth 1
    /// counts as Wetland). "Low flow" means below the high-flow
    /// threshold, which includes both stagnant and slow-flowing
    /// water.</summary>
    Lake,

    /// <summary>Shallow low-flow water -- the slow side-channels and
    /// flood-plain edges where aquatic plant life can establish. The
    /// "low flow" qualifier excludes high-flow main-channel rivers
    /// (those are <see cref="River"/>); the "shallow" qualifier
    /// excludes deep stagnant pools (<see cref="Lake"/>).</summary>
    Wetland,

    #endregion

    #region Structural

    /// <summary>Surface beneath an overhang -- different ecology
    /// (no rain, no sun, sheltered air). Classified at the structural
    /// region level too, but kept per-chunk for consistency.</summary>
    Cave,

    #endregion

    #region Negative -- moisture deficit

    /// <summary>Sustained dry land (no contamination). Recoverable
    /// once water returns; rises quickly under sustained drought
    /// conditions per the temporal model.</summary>
    Dry,

    #endregion

    #region Negative -- contamination

    /// <summary>Contaminated dry land (badwater dust on cracked
    /// earth). Rises rapidly to its target, decays slowly after
    /// cleanup -- the "lingering scar" mechanic.</summary>
    Contaminated,

    /// <summary>Contaminated water of any flow / depth. The
    /// stagnant-vs-flowing distinction we make for clean water
    /// doesn't carry over -- contaminated water is uniformly
    /// "yuck."</summary>
    Badwater,

    #endregion

    #region Land -- riparian (per-tile)

    /// <summary>The vegetated land margin alongside sustained clean
    /// water. Unlike every other member, Riparian is a <b>per-tile</b>
    /// biome with no per-chunk Suitability/Maturity slots: its dominance
    /// and content levels are driven by per-tile riparian maturity
    /// (<see cref="RiparianMaturityParameters"/> / the per-surface store),
    /// resolved in the per-tile dominance sampler -- not by the per-chunk
    /// scoring machinery. It is therefore deliberately excluded from
    /// <see cref="BiomeValueKinds.AllBiomes"/> (kept last so the existing
    /// members' ordinals don't shift).</summary>
    Riparian,

    #endregion

  }

}
