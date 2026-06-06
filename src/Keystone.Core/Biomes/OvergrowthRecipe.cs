namespace Keystone.Core.Biomes {

  /// <summary>Which tree state an <see cref="OvergrowthRecipe"/> acts on.</summary>
  public enum OvergrowthTarget {

    /// <summary>Living trees. Paired with <c>Deterministic</c> levels so
    /// coverage is hash-gated and capped (decoration for an otherwise
    /// boring area, never beyond a fixed fraction).</summary>
    Live,

    /// <summary>Dead trees. Paired with <c>Stochastic</c> levels so the
    /// per-cycle roll accumulates over time — every dead tree eventually
    /// overgrows, on the way to being reseeded into a new living tree.</summary>
    Dead,

    /// <summary>Reseed: replace an <i>already-overgrown, dead</i> tree
    /// whose overgrowth has matured (<see cref="OvergrowthRecipe.MaturityThreshold"/>)
    /// with a fresh living seedling drawn from the biome's Class D table
    /// (<see cref="OvergrowthRecipe.SourceLevel"/>), carrying the
    /// overgrowth straight onto the new tree and dropping the felled
    /// tree's wood for hauling. The terminal stage of the dead-tree arc
    /// (GitHub issue #33). Paired with <c>Stochastic</c> levels so mature
    /// overgrown deadwood is reclaimed gradually.</summary>
    Reseed,

  }

  /// <summary>
  /// Recipe for the <b>overgrowth augmentation</b> (GitHub issue #33): a
  /// rule that drapes an <i>existing</i> tree in a flourish composition,
  /// rather than spawning a new entity. The additive counterpart of an
  /// attrition rule — a new rule family in the recipe book, not a spawn
  /// class (A–E are taken).
  ///
  /// <para>Dispatched by <c>OvergrowthHandler</c>, which extends the
  /// shared spawn dispatch (<c>SpawnHandlerBase</c>) purely to reuse its
  /// per-level <c>Mode</c> handling: <see cref="OvergrowthTarget.Live"/>
  /// recipes sit on <c>Deterministic</c> levels (capped coverage),
  /// <see cref="OvergrowthTarget.Dead"/> recipes on <c>Stochastic</c>
  /// levels (accumulate over time). The recipe carries no probability of
  /// its own — the level owns the rate.</para>
  /// </summary>
  /// <param name="Biome">Dominant biome that gates this recipe (the
  /// recovery biomes — Grassland / Forest).</param>
  /// <param name="LevelId">Level identifier; its maturity band + Mode
  /// drive when and how the rule fires.</param>
  /// <param name="Target">Which tree state to act on (Live / Dead / Reseed).</param>
  /// <param name="Composition">Keystone flourish composition blueprint to
  /// drape on the tree (the overgrowth overlay). For <see cref="OvergrowthTarget.Reseed"/>
  /// this is the composition carried onto the new seedling.</param>
  /// <param name="Filter">Optional spatial-eligibility filter.</param>
  /// <param name="Weight">Relative pick weight within the bucket's
  /// weighted-random sampler. Catalog normalises non-positive to 1.0.</param>
  /// <param name="MaturityThreshold">Reseed only: the overgrowth's accrued
  /// <c>Maturity</c> must reach this before the dead tree is reseeded.
  /// Ignored by Live/Dead targets.</param>
  /// <param name="SourceLevel">Reseed only: the level whose Class D table
  /// the replacement species is drawn from (weighted). Empty falls back to
  /// <paramref name="LevelId"/>. Ignored by Live/Dead targets.</param>
  public sealed record OvergrowthRecipe(
      BiomeKind Biome,
      string LevelId,
      OvergrowthTarget Target,
      string Composition,
      string Filter,
      float Weight,
      float MaturityThreshold = 0f,
      string SourceLevel = "");

}
