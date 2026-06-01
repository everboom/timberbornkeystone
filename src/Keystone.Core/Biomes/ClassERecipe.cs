namespace Keystone.Core.Biomes {

  /// <summary>
  /// Recipe for a Class E fauna agent: a wandering, non-persisted,
  /// dawn-spawned-and-dusk-despawned entity tied to a biome+level.
  /// "What animals live where" expressed as data.
  ///
  /// <para><b>Activation rule.</b> Per-cluster-per-dawn capacity
  /// reconcile, grouped by <c>(Biome, LevelId)</c>:
  /// <list type="number">
  ///   <item>For each cluster of the recipe's biome, count the
  ///         chunks whose Maturity meets the level's
  ///         <see cref="BiomeLevel.LowerMaturity"/>.</item>
  ///   <item>Cluster capacity at this level =
  ///         <c>floor(qualifyingTiles × level.FaunaDensityPerTile)</c>
  ///         if <c>qualifyingTiles ≥ level.FaunaMinTilesToSpawn</c>,
  ///         else 0. The density and gate live on the level, not on
  ///         the recipe — every Class E recipe in the bucket shares
  ///         them.</item>
  ///   <item>Per spawn slot up to that capacity, pick a recipe in
  ///         the bucket by weighted-random against
  ///         <see cref="Weight"/>; spawn it at a random in-region
  ///         tile inside a randomly-picked qualifying chunk.</item>
  /// </list></para>
  ///
  /// <para><b>Lifecycle.</b> The agent persists until dusk, when the
  /// per-day cleanup tears down every live fauna entity regardless
  /// of source. No save/load: a save at noon comes back empty until
  /// next dawn.</para>
  ///
  /// <para><b>Adding more species.</b> One Class E entry per
  /// (biome, level, species). The dev placement tool reads the same
  /// registry to pick a species by biome (ignoring level / maturity)
  /// so adding an entry makes that species placeable without code
  /// changes.</para>
  /// </summary>
  /// <param name="Biome">Which biome's chunk gates this recipe. The
  /// chunk's dominant biome at dawn-roll time must equal this.</param>
  /// <param name="LevelId">Level identifier (e.g. <c>"L2"</c>). The
  /// recipe is eligible when the chunk's Maturity for
  /// <see cref="Biome"/> meets or exceeds the level's
  /// <see cref="BiomeLevel.LowerMaturity"/>.</param>
  /// <param name="BlueprintName">Name of the agent blueprint to
  /// instantiate (e.g. <c>"KeystoneDeer"</c>). Resolved through
  /// <c>TemplateCollectionService.AllTemplates</c>; the blueprint
  /// must carry <c>KeystoneFaunaAgentSpec</c>.</param>
  /// <param name="Weight">Relative weight when the dawn spawn loop
  /// picks a recipe within the <c>(biome, levelId)</c> bucket. Used
  /// at both dawn (capacity-slot assignment) and in the dev placement
  /// tool. The bucket's total capacity comes from the
  /// <see cref="BiomeLevel.FaunaDensityPerTile"/> on the matching
  /// level — recipes share that density and divide the resulting
  /// spawn slots by weight. So a 4:1 cow:bull mix is two recipes with
  /// weights 4 and 1 on the same level. Default <c>1.0</c>: all
  /// recipes in the bucket equally likely.</param>
  /// <param name="Category">User-facing fauna category this recipe
  /// belongs to (e.g. <c>"Deer"</c>, <c>"Cattle"</c>, <c>"Fish"</c>).
  /// Drives the per-category player setting that scales per-cluster
  /// capacity for this bucket (the master fauna toggle + per-category
  /// abundance sliders in <c>KeystoneFaunaSettings</c>). Free-form
  /// string so third-party mods can mint new categories without
  /// touching Keystone — unknown categories pass through with a
  /// neutral 1.0× multiplier until the settings UI exposes a slider
  /// for them. Required (non-empty); the catalog throws at PostLoad
  /// if a Class E recipe omits this. Case-sensitive: <c>"Deer"</c>
  /// and <c>"deer"</c> are different categories.</param>
  public sealed record ClassERecipe(
      BiomeKind Biome,
      string LevelId,
      string BlueprintName,
      string Category,
      float Weight = 1.0f);

}
