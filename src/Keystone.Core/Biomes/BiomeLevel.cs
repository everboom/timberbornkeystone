namespace Keystone.Core.Biomes {

  /// <summary>
  /// How spawn handlers should pick tiles for this level's recipes.
  ///
  /// <list type="bullet">
  ///   <item><see cref="Deterministic"/> — per-tile activation hash
  ///         compared to <c>Density · progress</c>. The same tile
  ///         always activates at the same maturity, so the population
  ///         is reproducible across save/load. Ramps in linearly
  ///         across the level's maturity range.</item>
  ///   <item><see cref="Stochastic"/> — per-cycle independent dice
  ///         roll against <c>Density</c>. Each cycle is a fresh
  ///         chance per eligible tile; population accumulates over
  ///         real time rather than appearing all at once. No maturity
  ///         ramp (every cycle rolls at the full <c>Density</c> rate
  ///         from the moment the level activates) — the "ramp" notion
  ///         only makes sense for a hash-band that widens; a dice roll
  ///         has no such band.</item>
  /// </list>
  ///
  /// <para>The mode is a property of the level, not of any class.
  /// A level of any class can be authored as either mode; the choice
  /// is driven by player-feel (do I want "appears at a fixed maturity"
  /// or "trickles in over time?"), not by the kind of entity being
  /// spawned. Class D's vanilla-flora pipeline is the most common
  /// candidate for <see cref="Stochastic"/> because regrowth would
  /// otherwise fight with Keystone re-firing on the same tile every
  /// cycle, but that's a consequence of the entity's lifecycle, not
  /// a hard rule.</para>
  /// </summary>
  public enum LevelDispatchMode {

    /// <summary>Per-tile hash gate, ramped by maturity progress.
    /// Default.</summary>
    Deterministic = 0,

    /// <summary>Per-cycle RNG roll at full <see cref="BiomeLevel.Density"/>.
    /// No ramp.</summary>
    Stochastic = 1,

  }

  /// <summary>
  /// A single level entry on a biome's progression ladder. Pure
  /// timeline data: a level is "active and ramping" while a chunk's
  /// Maturity for the biome is between <see cref="LowerMaturity"/>
  /// and <see cref="UpperMaturity"/>; "active and saturated" once
  /// Maturity is at or above the upper bound.
  ///
  /// <para><b>Levels only matter when the biome is dominant.</b>
  /// Before a level's gate is even consulted, the chunk's tile-level
  /// dominance vote must select this biome -- which requires the
  /// biome's Suitability to be the highest among all biomes at the
  /// tile (see <see cref="ChunkBiomeSampler.SampleDominantBiome"/>).
  /// Maturity alone is not enough to fire rules: a chunk with high
  /// Forest Maturity but a currently-failing Forest Suitability
  /// (drought, inundation, contamination) drops out of Forest
  /// dominance and fires no Forest levels until conditions recover.</para>
  ///
  /// <para>What happens at the level lives elsewhere -- on actions
  /// (spawn / transform / remove / custom) registered against
  /// <c>(Biome, LevelId)</c>. The level itself is just the timeline
  /// dimension. See <see cref="BiomeLevelTable"/> for the lookup
  /// surface.</para>
  ///
  /// <para>Levels stack cumulatively: as Maturity grows past one
  /// level's range and into the next, the earlier level stays at
  /// progress=1 (its actions remain "fully fired") and the next
  /// level ramps. Multiple levels can be simultaneously active at
  /// different progress values.</para>
  /// </summary>
  /// <param name="Biome">Which biome this level belongs to.</param>
  /// <param name="LevelId">Stable identifier (e.g. <c>"L1"</c>).
  /// Recipes and code-registered actions reference this string to
  /// declare which level they fire at.</param>
  /// <param name="LowerMaturity">Maturity value (game-days) at
  /// which actions begin firing for this level (progress=0).</param>
  /// <param name="UpperMaturity">Maturity value (game-days) at
  /// which the level is fully saturated (progress=1). Must be
  /// strictly greater than <see cref="LowerMaturity"/>.</param>
  /// <param name="Density">Activation density in <c>[0, 1]</c> --
  /// the fraction of (dominant-biome, level-eligible) tiles at which
  /// a recipe will fire when the level is at full saturation
  /// (<c>progress=1</c>, i.e. maturity at or above
  /// <see cref="UpperMaturity"/>). Coverage ramps in linearly:
  /// the effective threshold at runtime is <c>Density · progress</c>
  /// where progress is
  /// <c>clamp01((maturity - LowerMaturity) / (UpperMaturity - LowerMaturity))</c>,
  /// so a level with Density 0.10 spawns on ~0% of tiles at
  /// <see cref="LowerMaturity"/> and ~10% at
  /// <see cref="UpperMaturity"/>. This is the *level-wide* density:
  /// when multiple recipes share a <c>(biome, level)</c> bucket,
  /// exactly one of them is chosen per activated tile (weighted-random
  /// by each recipe's <c>Weight</c>). So density 0.33 with 15 recipes
  /// means 33% of eligible tiles end up with one of the 15 -- not
  /// 99.7% from cumulative per-recipe hashing. Default 0.10.
  ///
  /// <para><b>Levels stack additively across tiles.</b> The activation
  /// hash in <c>FlourishThreshold.ComputeActivation</c> is keyed on
  /// <c>(tile, biome, levelId)</c>, so each level draws an independent
  /// per-tile threshold. If L1 (Density 0.10) and L2 (Density 0.10)
  /// are both fully saturated and both have recipes, total coverage
  /// across them lands at ~19% (1 − (1−0.10)·(1−0.10)) once you
  /// account for the overlap, not 10%. Designers picking per-level
  /// densities should account for that — if you want "L2 brings
  /// total to 20%", set L1=0.10 and L2≈0.11, not L2=0.20.</para>
  ///
  /// <para><b>Stochastic levels don't ramp.</b> The per-cycle dice
  /// roll fires at the full <c>Density</c> rate as soon as the level
  /// activates — there's no widening "band of activated tiles," so
  /// the maturity ramp has no meaning for stochastic gates. For
  /// stochastic levels, <c>Density</c> reads directly as "per-day
  /// spawn chance per eligible tile" and the <see cref="UpperMaturity"/>
  /// value is informational only. See <see cref="LevelDispatchMode"/>
  /// for the per-level mode selector.</para>
  ///
  /// <para><b>RunAtStartup levels don't ramp either.</b> Worldgen
  /// content (rock clusters etc.) fires once during the startup pass
  /// at full <c>Density</c>, regardless of whatever Maturity happens
  /// to be at that moment. The ramp has no meaning for one-shot
  /// snapshots. <see cref="UpperMaturity"/> is informational only on
  /// RunAtStartup levels too.</para></param>
  /// <param name="Mode">Tile-selection strategy for this level. See
  /// <see cref="LevelDispatchMode"/>. Default
  /// <see cref="LevelDispatchMode.Deterministic"/>.</param>
  /// <param name="FaunaCapacityAtSaturation">Class E only: maximum
  /// number of agents this <c>(biome, levelId)</c> bucket spawns into
  /// a cluster as the cluster's
  /// <see cref="Keystone.Core.Ecology.Clusters.ChunkClusterIndex.Score"/>
  /// approaches 1. Realised capacity is
  /// <c>floor(cluster.Score · FaunaCapacityAtSaturation)</c>, so a
  /// half-saturated cluster (score ≈ 0.5) gets half the cap, a
  /// near-saturated one nearly all of it. Each spawn slot then picks
  /// a recipe from the bucket by <c>Weight</c> (preserves the 4:1
  /// cow:bull mix without per-recipe density caps). Default <c>0</c>:
  /// no natural fauna spawn at this level.
  ///
  /// <para><b>Quality vs. quantity trade-off.</b> Because <c>Score</c>
  /// is hyperbolic in <c>RawScore = Σ_t weights · TileCountsAbove[t]</c>,
  /// a small fully-mature cluster and a large half-mature cluster can
  /// reach the same Score and therefore the same capacity — the
  /// "small nice area or large less-nice area" tradeoff. The capacity
  /// curve has no cliff: Score asymptotically approaches 1 but never
  /// reaches it, so capacity asymptotically approaches
  /// <c>FaunaCapacityAtSaturation - 1</c> (floored) and is reached
  /// only by an arbitrarily mature/large cluster.</para></param>
  /// <param name="FaunaMinScore">Class E only: cluster
  /// <see cref="Keystone.Core.Ecology.Clusters.ChunkClusterIndex.Score"/>
  /// must reach or exceed this floor before this bucket spawns any
  /// fauna. Below the floor, capacity is forced to zero (and any
  /// live fauna of this bucket are culled the next dawn). Default
  /// <c>0</c>: no floor, capacity scales linearly with Score from
  /// zero up. Used for species that should only appear once the
  /// biome is genuinely well-established (e.g. cattle requiring a
  /// substantial Grassland), independent of the spawn-placement
  /// filter that <see cref="LowerMaturity"/> already provides on
  /// individual chunks.</param>
  public sealed record BiomeLevel(
      BiomeKind Biome,
      string LevelId,
      float LowerMaturity,
      float UpperMaturity,
      float Density,
      LevelDispatchMode Mode = LevelDispatchMode.Deterministic,
      bool RunAtStartup = false,
      int FaunaCapacityAtSaturation = 0,
      float FaunaMinScore = 0f);

}
