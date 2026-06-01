namespace Keystone.Core.Biomes {

  /// <summary>
  /// Recipe for a Class C flourish: a `BlockObject`-claiming entity
  /// that the player can select and demolish but doesn't have a
  /// vanilla harvest/plant lifecycle. The "weeds" tier between
  /// inert Class B flourishes and full Class D vanilla flora.
  ///
  /// <para><b>Activation rule.</b> Same shape as
  /// <see cref="ClassBRecipe"/> -- density on the level, weighted
  /// pick from the bucket.</para>
  ///
  /// <para><b>Spawn lifecycle.</b> Persistent like Class B (uses
  /// <c>BlockObjectFactory.CreateFinished</c>), but the Class C
  /// handler keeps no <c>_attempted</c> memo. After the player
  /// demolishes a Class C entity, the next cycle re-evaluates and
  /// re-spawns if conditions still warrant -- the player removes,
  /// the recipe grows back. Balance is the tuning lever; if respawn
  /// cadence feels antagonistic, lower the level's <c>Density</c>
  /// (sparser tiles eligible) or lift the level's
  /// <c>LowerMaturity</c> (delay before any spawn).</para>
  /// </summary>
  /// <param name="Biome">Which biome's Investment gates this recipe.
  /// Recipe only fires when this biome is the *dominant* biome at
  /// the tile.</param>
  /// <param name="LevelId">Level identifier the recipe attaches to.
  /// Range and density come from the biome's entry for this level
  /// in <see cref="BiomeLevelTable"/>.</param>
  /// <param name="BlueprintName">Name of the Keystone-authored
  /// blueprint to instantiate at activated tiles.</param>
  /// <param name="Filter">Optional spatial-eligibility filter.</param>
  /// <param name="Weight">Relative pick weight in the bucket.</param>
  /// <param name="Height">Vertical extent of the spawned flourish in
  /// voxels: the placement voxel and <c>Height</c> voxels above must
  /// be free of terrain and <c>BlockObject</c>s. Resolved by the
  /// catalog from <c>RecipeEntry.Height</c>; defaults to <c>1</c> for
  /// Class C (single-voxel flourishes).</param>
  public sealed record ClassCRecipe(
      BiomeKind Biome,
      string LevelId,
      string BlueprintName,
      string Filter,
      float Weight,
      int Height);

}
