using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ports;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Shared base for the four per-class spawn handlers (A / B / C / D).
  /// Provides the per-recipe dispatch primitives — filter eligibility,
  /// activation-hash gate, weighted-random pick — so subclasses only
  /// implement what's class-specific.
  ///
  /// <para>Replaced the prior <c>FlourishReconcilerBase</c> in the
  /// Refactor: the per-tile rolling-sweep dispatch moved up to
  /// <see cref="ChunkRulesApplier"/>, leaving the dispatch primitives
  /// down here where each handler can reach them.</para>
  ///
  /// <para><b>Subclass extension points:</b>
  /// <list type="bullet">
  ///   <item><see cref="GetRecipes"/> / <see cref="GetAllRecipes"/> —
  ///         pull from the catalog's per-class index.</item>
  ///   <item><see cref="GetFilter"/> / <see cref="GetWeight"/> —
  ///         project the per-recipe filter and pick weight.</item>
  ///   <item><see cref="EvaluateLevel"/> — default impl runs the
  ///         deterministic dispatch and forwards to
  ///         <see cref="OnRecipeChosen"/>. Class D overrides to swap
  ///         the activation hash for an RNG roll and drive its
  ///         per-tile persistence memo.</item>
  ///   <item><see cref="OnRecipeChosen"/> — the actual spawn /
  ///         decoration / variant-stamp work for the picked recipe.</item>
  /// </list></para>
  /// </summary>
  /// <typeparam name="TRecipe">The Class A/B/C/D recipe type. Constrained
  /// to <c>class</c> so the deterministic-pick helper can return
  /// <c>null</c> on no-pick.</typeparam>
  public abstract class SpawnHandlerBase<TRecipe> : IRuleHandler
      where TRecipe : class {

    #region Fields

    private readonly RecipeFilterRegistry _filters;
    private readonly IPlantingMarkQuery _marks;

    /// <summary>Reusable scratch buffers for the eligibility loop. One
    /// pair per handler instance so we don't allocate per-tile.</summary>
    private readonly List<int> _eligibleIndices = new();
    private readonly List<float> _eligibleWeights = new();

    /// <summary>Per-handler RNG used by the stochastic dispatch path.
    /// Session-local (not persisted); stochastic levels are deliberately
    /// non-deterministic across reloads — the design is "population
    /// accumulates over real time," not "same flora regenerates from
    /// the same seed."</summary>
    private readonly System.Random _rng = new();

    #endregion

    #region Construction

    protected SpawnHandlerBase(RecipeFilterRegistry filters, IPlantingMarkQuery marks) {
      _filters = filters;
      _marks = marks;
    }

    #endregion

    #region Mark gate

    /// <summary>True if the tile at <paramref name="surface"/> carries
    /// a player planting mark. Spawn handlers should consult this at
    /// the top of <see cref="OnRecipeChosen"/> and bail when true:
    /// Keystone never places a <c>BlockObject</c> over a tile the
    /// player has explicitly designated for planting.
    /// <para><b>Defense in depth.</b> The dispatcher
    /// (<c>ChunkRulesApplier.ProcessUnit</c>) already short-circuits
    /// marked tiles before any handler is called, so this guard
    /// should never fire under normal flow. It exists so the
    /// invariant survives future refactors of the dispatcher loop or
    /// new call sites that route directly into a handler's
    /// <see cref="OnRecipeChosen"/>.</para></summary>
    protected bool IsMarked(SurfaceCoord surface) {
      return _marks.IsMarked(surface.X, surface.Y, surface.Z);
    }

    /// <summary><see cref="IsMarked(SurfaceCoord)"/> variant for call
    /// sites that already carry a <c>Vector3Int</c> tile coordinate
    /// (e.g. <c>ClassDSpawnHandler.TrySpawnClassD</c> which is invoked
    /// from dev-tool placement paths). Same semantics as the
    /// <see cref="SurfaceCoord"/> overload.</summary>
    protected bool IsMarked(UnityEngine.Vector3Int tile) {
      return _marks.IsMarked(tile.x, tile.y, tile.z);
    }

    #endregion

    #region IRuleHandler

    /// <inheritdoc />
    public virtual void OnCycleStart() {}

    /// <inheritdoc />
    public virtual void OnCycleComplete() {}

    /// <inheritdoc />
    public virtual void OnTickEnd() {}

    /// <inheritdoc />
    public virtual bool ShouldRun() => GetAllRecipes().Count > 0;

    /// <inheritdoc />
    /// <remarks>Spawn handlers place <c>BlockObject</c>s, so by default
    /// they honour the applier's marked-tile skip. Overridden by
    /// <see cref="Keystone.Mod.Overgrowth.OvergrowthHandler"/>, whose
    /// draping is non-destructive.</remarks>
    public virtual bool RunsOnMarkedTiles => false;

    /// <inheritdoc />
    public System.Collections.Generic.IEnumerable<(BiomeKind Biome, string LevelId)> ActiveBuckets {
      get {
        var recipes = GetAllRecipes();
        for (var i = 0; i < recipes.Count; i++) {
          yield return GetBucketKey(recipes[i]);
        }
      }
    }

    /// <inheritdoc />
    public void OnUnit(SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress) {
      var recipes = GetRecipes(biome, level.LevelId);
      if (recipes.Count == 0) return;
      EvaluateLevel(surface, biome, level, progress, recipes);
    }

    #endregion

    #region Per-class extension points

    /// <summary>The per-class catalog accessor for the full recipe
    /// list. Used by <see cref="ShouldRun"/> to skip cycles when
    /// nothing's registered for this class.</summary>
    protected abstract IReadOnlyList<TRecipe> GetAllRecipes();

    /// <summary>Pull the recipes for a <c>(biome, levelId)</c> bucket
    /// from the catalog. Returns an empty list when nothing's
    /// registered.</summary>
    protected abstract IReadOnlyList<TRecipe> GetRecipes(BiomeKind biome, string levelId);

    /// <summary>Pull the <c>(biome, levelId)</c> bucket key for a
    /// recipe. Used by <see cref="ActiveBuckets"/> to enumerate the
    /// buckets this handler has any recipes for, which feeds the
    /// <see cref="ChunkRulesApplier"/>'s precomputed
    /// "interested handlers per bucket" map. The map skips handlers
    /// that would no-op on this surface's <c>(biome, level)</c>.</summary>
    protected abstract (BiomeKind Biome, string LevelId) GetBucketKey(TRecipe recipe);

    /// <summary>Per-recipe spatial filter name (matches a
    /// registered <see cref="IRecipeFilter.Name"/>); empty for
    /// "no filter".</summary>
    protected abstract string GetFilter(TRecipe recipe);

    /// <summary>Per-recipe pick weight in the bucket's
    /// weighted-random sampler.</summary>
    protected abstract float GetWeight(TRecipe recipe);

    /// <summary>Density multiplier applied to the activation gate on
    /// top of <c>level.Density × progress</c> (deterministic) or
    /// <c>level.Density</c> (stochastic). Computed once per
    /// <c>(biome, level)</c> bucket in <see cref="EvaluateLevel"/> and
    /// passed down to the pick functions. Base returns <c>1f</c>;
    /// subclasses override to plug in player-tunable density from
    /// <see cref="Keystone.Mod.Settings"/>. <c>0f</c> disables all
    /// spawns for this bucket; values &gt; 1f saturate every tile once
    /// the product clears the hash range.
    ///
    /// <para><paramref name="recipes"/> is the bucket the dispatcher
    /// just looked up — non-empty (the dispatcher skips empty buckets
    /// before this is called) and homogeneous on any classification
    /// the subclass cares about (e.g. <c>recipes[0].Category</c> is
    /// well-defined because <c>FlourishCatalog</c> enforces single-
    /// category buckets at load). Subclasses that want a global
    /// multiplier just ignore the argument.</para></summary>
    protected virtual float GetDensityMultiplier(IReadOnlyList<TRecipe> recipes) => 1f;

    /// <summary>Subclass hook: handle the bucket of recipes for a
    /// <c>(tile, biome, level)</c> tuple where the level is active
    /// and the bucket has at least one recipe.
    ///
    /// <para>Default implementation: dispatch on
    /// <see cref="BiomeLevel.Mode"/> to either
    /// <see cref="TryDeterministicPick"/> (hash gate, ramped) or
    /// <see cref="TryStochasticPick"/> (RNG roll, no ramp), then
    /// forward to <see cref="OnRecipeChosen"/> on a hit. Subclasses
    /// override only if they need to wrap the call — e.g. Class D
    /// adds a per-tile memo gate before delegating to base.</para>
    ///
    /// <para><paramref name="progress"/> is the level's saturation
    /// fraction in <c>[0, 1]</c>. The deterministic gate scales with
    /// it so coverage ramps in linearly across the level's maturity
    /// range; the stochastic gate ignores it (every cycle is an
    /// independent roll at full <c>Density</c>).</para></summary>
    protected virtual void EvaluateLevel(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress,
        IReadOnlyList<TRecipe> recipes) {
      var multiplier = GetDensityMultiplier(recipes);
      if (multiplier <= 0f) return;
      var recipe = level.Mode == LevelDispatchMode.Stochastic
          ? TryStochasticPick(surface, biome, level, progress, recipes, _rng, multiplier)
          : TryDeterministicPick(surface, biome, level, progress, recipes, multiplier);
      if (recipe != null) {
        OnRecipeChosen(surface, biome, level, recipe);
      }
    }

    /// <summary>Subclass hook: a recipe has been chosen at this
    /// <c>(tile, biome, level)</c>. Subclass spawns, stamps, memos,
    /// etc.</summary>
    protected abstract void OnRecipeChosen(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, TRecipe recipe);

    #endregion

    #region Dispatch primitives

    /// <summary>Filter recipes by per-recipe Filter, check the
    /// activation hash against the level's *current* density target
    /// (<c>Density × progress × densityMultiplier</c>), and pick one
    /// recipe via weighted-random sampling using a separate per-tile
    /// pick hash. Returns the chosen recipe or <c>null</c> if no
    /// recipe was selected (no eligible candidates, activation gate
    /// failed, all weights zero).
    ///
    /// <para><b>Maturity ramp.</b> The effective threshold is
    /// <c>level.Density · progress · densityMultiplier</c>, so coverage
    /// grows linearly from 0% at <see cref="BiomeLevel.LowerMaturity"/>
    /// to <c>level.Density · densityMultiplier</c> at
    /// <see cref="BiomeLevel.UpperMaturity"/> rather than snapping to
    /// full strength the moment maturity crosses the lower bound.
    /// <paramref name="densityMultiplier"/> comes from
    /// <see cref="GetDensityMultiplier"/> and is constant across the
    /// bucket; <c>1f</c> is the no-op. See <see cref="BiomeLevel.Density"/>
    /// for the cross-level (additive) interaction.</para></summary>
    protected virtual TRecipe? TryDeterministicPick(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress,
        IReadOnlyList<TRecipe> recipes, float densityMultiplier) {
      if (!BuildEligiblePool(surface, recipes)) return null;
      var activationHash = FlourishThreshold.ComputeActivation(
          surface.X, surface.Y, biome, level.LevelId);
      if (activationHash >= level.Density * progress * densityMultiplier) return null;
      return PickFromPool(surface, biome, level, recipes);
    }

    /// <summary>Same as <see cref="TryDeterministicPick"/> but the
    /// activation gate is an RNG roll instead of the deterministic
    /// hash. Recipe pick remains hash-based so spatial patterns stay
    /// reproducible — only the activation timing is random. Used by
    /// Class D.
    ///
    /// <para><b>No maturity ramp.</b> Unlike the deterministic gate,
    /// the stochastic gate doesn't scale with <paramref name="progress"/>:
    /// every cycle is an independent per-tile dice roll at
    /// <c>level.Density · densityMultiplier</c>. There's no "band of
    /// activated tiles" to widen with maturity, so the ramp shape
    /// has no analogue here — the per-day chance is the steady-state
    /// rate from the moment the level activates
    /// (<c>maturity ≥ LowerMaturity</c>). <paramref name="progress"/>
    /// is kept on the signature for symmetry with the deterministic
    /// variant.</para></summary>
    protected virtual TRecipe? TryStochasticPick(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress,
        IReadOnlyList<TRecipe> recipes, System.Random rng, float densityMultiplier) {
      _ = progress;
      if (!BuildEligiblePool(surface, recipes)) return null;
      if (rng.NextDouble() >= level.Density * densityMultiplier) return null;
      return PickFromPool(surface, biome, level, recipes);
    }

    /// <summary>Fill <see cref="_eligibleIndices"/> and
    /// <see cref="_eligibleWeights"/> from <paramref name="recipes"/>,
    /// dropping anything blocked by its filter.</summary>
    private bool BuildEligiblePool(SurfaceCoord surface, IReadOnlyList<TRecipe> recipes) {
      _eligibleIndices.Clear();
      _eligibleWeights.Clear();
      for (var i = 0; i < recipes.Count; i++) {
        var recipe = recipes[i];
        if (!_filters.IsEligible(GetFilter(recipe), surface)) continue;
        _eligibleIndices.Add(i);
        _eligibleWeights.Add(GetWeight(recipe));
      }
      return _eligibleIndices.Count > 0;
    }

    /// <summary>Weighted-random pick over the previously built
    /// eligible pool.</summary>
    private TRecipe? PickFromPool(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, IReadOnlyList<TRecipe> recipes) {
      var pickHash = FlourishThreshold.ComputePick(
          surface.X, surface.Y, biome, level.LevelId);
      var pickIdx = WeightedPick.Pick(_eligibleWeights, pickHash);
      if (pickIdx < 0) return null;
      return recipes[_eligibleIndices[pickIdx]];
    }

    #endregion

  }

}
