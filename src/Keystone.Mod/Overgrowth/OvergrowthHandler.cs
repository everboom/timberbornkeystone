using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Recipes;
using Keystone.Mod.Settings;
using Timberborn.BlockSystem;
using Timberborn.NaturalResourcesLifecycle;
using UnityEngine;

namespace Keystone.Mod.Overgrowth {

  /// <summary>
  /// Rule handler that drapes existing trees in overgrowth flourishes
  /// per <see cref="OvergrowthRecipe"/> (GitHub issue #33). Plugged into
  /// <c>ChunkRulesApplier</c> via <see cref="Keystone.Mod.Recipes.IRuleHandler"/>
  /// alongside the spawn handlers and attrition.
  ///
  /// <para><b>Why it extends <see cref="SpawnHandlerBase{TRecipe}"/>.</b>
  /// Not to spawn — to reuse the base's per-level dispatch by
  /// <c>BiomeLevel.Mode</c>. <see cref="OvergrowthTarget.Live"/> recipes
  /// sit on <c>Deterministic</c> levels (hash-gated, coverage-capped at
  /// <c>Density × progress</c> — decoration that never grows past a fixed
  /// fraction); <see cref="OvergrowthTarget.Dead"/> recipes on
  /// <c>Stochastic</c> levels (per-cycle roll that accumulates — every
  /// dead tree eventually overgrows). The only override is
  /// <see cref="OnRecipeChosen"/>: instead of placing a blueprint, find a
  /// tree of the recipe's target state at the surface and
  /// <see cref="KeystoneOvergrowth.Apply(string)"/> the composition.</para>
  /// </summary>
  public sealed class OvergrowthHandler : SpawnHandlerBase<OvergrowthRecipe> {

    #region Fields

    private readonly FlourishCatalog _catalog;
    private readonly IBlockService _blockService;
    private readonly OvergrowthReseeder _reseeder;
    private readonly KeystoneOvergrowthSettings _settings;

    #endregion

    #region Construction

    public OvergrowthHandler(
        FlourishCatalog catalog,
        RecipeFilterRegistry filters,
        IPlantingMarkQuery marks,
        IBlockService blockService,
        OvergrowthReseeder reseeder,
        KeystoneOvergrowthSettings settings)
        : base(filters, marks) {
      _catalog = catalog;
      _blockService = blockService;
      _reseeder = reseeder;
      _settings = settings;
    }

    #endregion

    #region SpawnHandlerBase

    /// <summary>Player-tunable rate gating (see
    /// <see cref="KeystoneOvergrowthSettings"/>) — two independent sliders:
    /// the Dead/Live overgrow levels (graphics) scale by the overgrowth-rate
    /// slider; the Reseed level (gameplay) scales by the replacement-rate
    /// slider (0% → multiplier 0 → the level is skipped before any roll).
    /// Each overgrowth level is single-target by design, so
    /// <c>recipes[0].Target</c> classifies the whole bucket — mirrors how
    /// <c>ClassDSpawnHandler</c> reads <c>recipes[0].Category</c>.</summary>
    protected override float GetDensityMultiplier(IReadOnlyList<OvergrowthRecipe> recipes) {
      return recipes[0].Target == OvergrowthTarget.Reseed
          ? _settings.ReplacementRateMultiplier
          : _settings.OvergrowthRateMultiplier;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<OvergrowthRecipe> GetAllRecipes() => _catalog.AllOvergrowth;

    /// <inheritdoc />
    protected override IReadOnlyList<OvergrowthRecipe> GetRecipes(BiomeKind biome, string levelId)
        => _catalog.OvergrowthFor(biome, levelId);

    /// <inheritdoc />
    protected override string GetFilter(OvergrowthRecipe recipe) => recipe.Filter;

    /// <inheritdoc />
    protected override float GetWeight(OvergrowthRecipe recipe) => recipe.Weight;

    /// <inheritdoc />
    protected override (BiomeKind Biome, string LevelId) GetBucketKey(OvergrowthRecipe recipe)
        => (recipe.Biome, recipe.LevelId);

    /// <inheritdoc />
    protected override void OnRecipeChosen(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, OvergrowthRecipe recipe) {
      // Defense in depth on the dispatcher's marked-tile skip.
      if (IsMarked(surface)) return;

      var tile = new Vector3Int(surface.X, surface.Y, surface.Z);
      var overgrowth = _blockService.GetFirstObjectWithComponentAt<KeystoneOvergrowth>(tile);
      if (overgrowth == null) return;

      // Trees carry LivingNaturalResource; a missing one (non-standard
      // tree) is treated as not-dead.
      var living = overgrowth.GetComponent<LivingNaturalResource>();
      var isDead = living != null && living.IsDead;

      if (recipe.Target == OvergrowthTarget.Reseed) {
        ReseedIfReady(overgrowth, isDead, biome, recipe);
        return;
      }

      // Overgrow (Live / Dead): drape a fresh composition on a barren tree
      // of the matching state.
      if (overgrowth.IsOvergrown || !overgrowth.CanOvergrow) return;
      if (recipe.Target == OvergrowthTarget.Dead && !isDead) return;
      if (recipe.Target == OvergrowthTarget.Live && isDead) return;

      overgrowth.Apply(recipe.Composition);
    }

    /// <summary>Reseed gate (the terminal dead-tree stage): the host tree
    /// must be <b>dead</b> and its reclamation maturity past the recipe's
    /// threshold. (Biome maturity is already gated by the recipe's level
    /// band, so reaching here means the biome is recovering.)
    /// <para><b>The overgrowth visual is irrelevant here</b> — the
    /// reclamation clock (<see cref="KeystoneOvergrowth.Maturity"/>) accrues
    /// on every dead tree whether or not it's overgrown, so a barren dead
    /// tree reseeds just like an overgrown one. Bad conditions (drought /
    /// badwater) erode that maturity instead, naturally stalling reseed —
    /// no separate "overgrowth alive" check needed.</para></summary>
    private void ReseedIfReady(
        KeystoneOvergrowth overgrowth, bool hostTreeDead, BiomeKind biome, OvergrowthRecipe recipe) {
      if (!hostTreeDead) return;
      if (overgrowth.Maturity < recipe.MaturityThreshold) return;
      var sourceLevel = string.IsNullOrEmpty(recipe.SourceLevel) ? recipe.LevelId : recipe.SourceLevel;
      _reseeder.TryReseed(overgrowth, biome, sourceLevel, recipe.Composition);
    }

    #endregion

  }

}
