namespace Keystone.Core.Biomes {

  /// <summary>
  /// Recipe for a Class B flourish: a `BlockObject`-claiming
  /// non-interactive entity, spawned once when the bucket activates
  /// at a tile and persisted by Timberborn's vanilla machinery
  /// thereafter. The "your stewardship is paying off" mid-tier
  /// marker.
  ///
  /// <para><b>Activation rule.</b> Same shape as
  /// <see cref="ClassARecipe"/>. Density and the per-tile hash
  /// gate are owned by the level (<see cref="BiomeLevel.Density"/>)
  /// rather than the recipe -- multiple recipes share the bucket
  /// and exactly one is picked per activated tile via weighted
  /// random sampling on <see cref="Weight"/>.</para>
  ///
  /// <para><b>Spawn lifecycle.</b> One-shot persistent: the Class B
  /// handler instantiates the blueprint via
  /// <c>BlockObjectFactory.CreateFinished</c> at the activated
  /// tile, then the entity is in Timberborn's hands. There's no
  /// continuous reconcile/despawn cycle.</para>
  /// </summary>
  /// <param name="Biome">Which biome's Investment gates this recipe.
  /// Recipe only fires when this biome is the *dominant* biome at
  /// the tile.</param>
  /// <param name="LevelId">Level identifier the recipe attaches to.
  /// Range and density come from the biome's entry for this level
  /// in <see cref="BiomeLevelTable"/>.</param>
  /// <param name="BlueprintName">Name of the Keystone-authored
  /// blueprint to instantiate at activated tiles.</param>
  /// <param name="Filter">Optional spatial-eligibility filter.
  /// See <c>RecipeEntry.Filter</c>.</param>
  /// <param name="Weight">Relative pick weight in the bucket.</param>
  /// <param name="Height">Vertical extent of the spawned flourish in
  /// voxels: the placement voxel and <c>Height</c> voxels above must
  /// be free of terrain and <c>BlockObject</c>s. Resolved by the
  /// catalog from <c>RecipeEntry.Height</c>; defaults to <c>1</c> for
  /// Class B (single-voxel flourishes).</param>
  public sealed record ClassBRecipe(
      BiomeKind Biome,
      string LevelId,
      string BlueprintName,
      string Filter,
      float Weight,
      int Height);

}
