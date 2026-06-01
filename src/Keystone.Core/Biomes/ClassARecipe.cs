namespace Keystone.Core.Biomes {

  /// <summary>
  /// Recipe for a Class A flourish in the eco-sensitive ambient
  /// tier: a non-`BlockObject` decoration whose presence is driven
  /// by per-tile biome Score + Investment. Lives on a single tile,
  /// coexists with other content on that tile, is deterministically
  /// re-derived from biome state each reconciliation cycle, and
  /// carries no save state per instance.
  ///
  /// <para><b>Activation rule.</b> A tile activates the
  /// <c>(Biome, LevelId)</c> bucket when:</para>
  /// <list type="number">
  ///   <item><see cref="Biome"/> is the dominant biome at the tile
  ///         under the Score-pass + max-Score rule in
  ///         <see cref="ChunkBiomeSampler.SampleDominantBiome"/>
  ///         (Score clears the pass threshold and is the highest
  ///         among passers there);</item>
  ///   <item>the chunk's Investment for <see cref="Biome"/> places
  ///         that biome at or past <see cref="LevelId"/>'s lower
  ///         bound in <see cref="BiomeLevelTable"/>;</item>
  ///   <item>the tile passes <see cref="Filter"/> (if set);</item>
  ///   <item>the per-tile activation hash is below
  ///         <c>level.Density</c>, the level's activation fraction.</item>
  /// </list>
  ///
  /// <para>When activation passes, exactly one recipe is selected
  /// from the bucket via weighted-random pick (using
  /// <see cref="Weight"/>) seeded by a separate per-tile pick hash.
  /// Density is owned by the level, not the recipe, so adding more
  /// recipes broadens variety without changing the fraction of tiles
  /// that activate.</para>
  ///
  /// <para><b>Class B / C / D recipes</b> share this same activation
  /// shape -- filter, gate, weighted-pick from the bucket -- but
  /// differ in spawn lifecycle. Class D additionally swaps the
  /// per-tile activation hash for an RNG roll, since Class D entities
  /// persist via vanilla and don't need deterministic per-tile placement.</para>
  /// </summary>
  /// <param name="Biome">Which biome's Investment gates this recipe.
  /// Recipe only fires when this biome is the *dominant* biome at
  /// the tile.</param>
  /// <param name="LevelId">Level identifier (e.g. <c>"L1"</c>) the
  /// recipe attaches to. Range and density come from the biome's
  /// entry for this level in <see cref="BiomeLevelTable"/>.</param>
  /// <param name="DonorBlueprintName">Name of the blueprint whose
  /// prefab the spawn driver clones at activated tiles. Typically a
  /// vanilla flora blueprint cloned at runtime.</param>
  /// <param name="Filter">Optional spatial-eligibility filter applied
  /// before the activation gate. Empty string = no filter. See
  /// <c>RecipeEntry.Filter</c> for recognised values.</param>
  /// <param name="Weight">Relative weight in the per-bucket
  /// weighted-random pick. <c>1.0</c> = uniform with peers; values
  /// scale linearly. Catalog normalises non-positive inputs to the
  /// default <c>1.0</c>; remove the recipe entry to disable.</param>
  public sealed record ClassARecipe(
      BiomeKind Biome,
      string LevelId,
      string DonorBlueprintName,
      string Filter,
      float Weight);

}
