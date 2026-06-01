namespace Keystone.Core.Biomes {

  /// <summary>
  /// Recipe for a Class D flourish: a `BlockObject` instantiated
  /// from a <b>vanilla</b> flora blueprint (Pine, Maple, CoffeeBush,
  /// etc.) running the full vanilla lifecycle -- grows from seedling,
  /// reproduces, can be cut, planted, gathered.
  ///
  /// <para><b>Activation shape matches Class A/B/C.</b> The bucket
  /// activates when its level is active and the per-tile gate hits;
  /// on activation, the handler weighted-picks one recipe from the
  /// bucket. Class D differs only in its <b>activation source</b>:
  /// each cycle uses an RNG roll against <c>level.Density</c> instead
  /// of the deterministic per-tile hash. Class D entities persist via
  /// Timberborn's normal entity machinery, so the determinism Class
  /// A/B/C rely on (so they re-derive the same placements after a
  /// world reload) isn't required here -- and stochastic accumulation
  /// reads more naturally for "10% chance of a tree sprouting per day."
  /// See project memory entry
  /// <c>feedback_class_d_recipes_reference_vanilla.md</c>.</para>
  ///
  /// <para><b>BlueprintName references vanilla, not Keystone-authored
  /// blueprints.</b> A Class D recipe's <see cref="BlueprintName"/>
  /// is the vanilla blueprint name (e.g. <c>"Birch"</c>) -- no
  /// custom blueprint authoring. <c>CrossFactionCollectionProvider</c>
  /// loads both factions' blueprints; <c>TemplateCollectionServicePatch</c>
  /// strips Plantable/Gatherable for cross-faction donors so the
  /// active faction's UIs don't crash.</para>
  /// </summary>
  /// <param name="Biome">Which biome's Investment gates this recipe.
  /// Recipe only fires when this biome is the *dominant* biome at
  /// the tile.</param>
  /// <param name="LevelId">Level identifier the recipe attaches to.</param>
  /// <param name="BlueprintName">Vanilla flora blueprint name (e.g.
  /// <c>"Pine"</c>, <c>"Maple"</c>, <c>"Birch"</c>).</param>
  /// <param name="Category">User-facing flora category this recipe
  /// belongs to (e.g. <c>"Trees"</c>, <c>"Bushes"</c>, <c>"Crops"</c>).
  /// Drives the per-category density slider in
  /// <c>KeystoneFloraSettings</c> that scales the activation gate for
  /// this bucket. Free-form string so third-party mods can mint new
  /// categories without touching Keystone — unknown categories pass
  /// through with a neutral <c>1.0×</c> multiplier until the settings
  /// UI exposes a slider for them. Required (non-empty); the catalog
  /// warns and skips Class D recipes that omit this. Case-sensitive:
  /// <c>"Trees"</c> and <c>"trees"</c> are different categories.</param>
  /// <param name="Filter">Optional spatial-eligibility filter.</param>
  /// <param name="Weight">Relative pick weight in the bucket's
  /// weighted-random sampler. <c>1.0</c> = uniform with peers; values
  /// scale linearly. Catalog normalises non-positive inputs to the
  /// default <c>1.0</c>.</param>
  /// <param name="Height">Vertical extent of the spawned tree in
  /// voxels: the placement voxel and <c>Height</c> voxels above must
  /// be free of terrain and <c>BlockObject</c>s. Resolved by the
  /// catalog from <c>RecipeEntry.Height</c>; defaults to <c>2</c> for
  /// Class D (vanilla flora trees occupy multiple stacked voxels).</param>
  public sealed record ClassDRecipe(
      BiomeKind Biome,
      string LevelId,
      string BlueprintName,
      string Category,
      string Filter,
      float Weight,
      int Height);

}
