using Keystone.Core.Biomes;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Timberborn.BlockSystem;
using Timberborn.Cutting;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.GoodStackSystem;
using Timberborn.Goods;
using UnityEngine;

namespace Keystone.Mod.Overgrowth {

  /// <summary>
  /// Performs the overgrowth <b>reseed</b> step (GitHub issue #33): an
  /// overgrown, matured, <i>dead</i> tree is replaced by a fresh living
  /// seedling, mimicking how a felled tree leaves its wood behind for a
  /// lumberjack to fetch. The eligibility gates (host tree dead, overgrowth
  /// alive + mature, biome maturity) live in <see cref="OvergrowthHandler"/>;
  /// this service is the pure mechanism it calls once a tile qualifies.
  ///
  /// <para><b>Three steps, mirroring a real cut</b>
  /// (<c>Timberborn.Cutting.Cuttable.Cut</c>):</para>
  /// <list type="number">
  ///   <item><b>Remove</b> the dead tree via <see cref="EntityService.Delete"/>
  ///         — which removes it from the entity + block registries
  ///         synchronously (only the Unity <c>GameObject</c> destroy is
  ///         deferred) and fires the standard removal events other systems
  ///         and mods observe.</item>
  ///   <item><b>Plant</b> a weighted pick from the biome's Class D table
  ///         through <see cref="ClassDSpawnHandler.TrySpawnClassD"/>, so the
  ///         seedling shares the same planting-mark / cutting-mark /
  ///         vertical-clearance checks as any Class D spawn, then carry the
  ///         overgrowth straight onto it.</item>
  ///   <item><b>Drop</b> the felled tree's wood onto the new seedling's own
  ///         <see cref="GoodStack"/> (every tree template carries one) and
  ///         register it with the lumberjack stack service. The retrieval
  ///         path (<c>GoodStackRetrieverBehavior</c>) has no alive/dead host
  ///         check, so the wood is haulable off the living seedling exactly
  ///         like a stump's.</item>
  /// </list>
  ///
  /// <para><b>Why the seedling's own GoodStack.</b> Vanilla has no
  /// free-standing "log pile" entity — felled wood always sits on a host
  /// (the stump). Reusing the new seedling as that host collapses the
  /// tile conflict: wood and seedling share the tile because they share the
  /// entity. No new asset, no adjacent-tile search.</para>
  /// </summary>
  public sealed class OvergrowthReseeder {

    #region Fields

    private readonly FlourishCatalog _catalog;
    private readonly ClassDSpawnHandler _classD;
    private readonly EntityService _entityService;
    private readonly GoodStackService<LumberjackFlagSpec> _lumberjackStacks;

    /// <summary>Session-local RNG for the weighted species pick. Like the
    /// stochastic spawn dispatch, reseed is deliberately non-deterministic
    /// across reloads — "deadwood is reclaimed over real time," not "the
    /// same species regrows from the same seed."</summary>
    private readonly System.Random _rng = new();

    #endregion

    #region Construction

    public OvergrowthReseeder(
        FlourishCatalog catalog,
        ClassDSpawnHandler classD,
        EntityService entityService,
        GoodStackService<LumberjackFlagSpec> lumberjackStacks) {
      _catalog = catalog;
      _classD = classD;
      _entityService = entityService;
      _lumberjackStacks = lumberjackStacks;
    }

    #endregion

    #region Public API

    /// <summary>Reseed the dead tree carrying <paramref name="overgrowth"/>:
    /// remove it, plant a weighted Class D seedling from
    /// <paramref name="biome"/>/<paramref name="sourceLevelId"/> at the same
    /// tile, carry <paramref name="composition"/> overgrowth onto the new
    /// tree, and drop the felled wood. Returns <c>true</c> on success.
    /// Returns <c>false</c> (leaving the tile barren if the dead tree was
    /// already removed) when there's no host, no Class D species for the
    /// source level, or the tile rejects the seedling (water, clearance, a
    /// player mark).</summary>
    public bool TryReseed(
        KeystoneOvergrowth overgrowth, BiomeKind biome, string sourceLevelId, string composition) {
      var host = overgrowth.GetComponent<BlockObject>();
      if (host == null) return false;
      var tile = host.Coordinates;

      // Decide the replacement species first — if the source level has no
      // Class D table, abort before deleting anything.
      var pick = PickClassD(biome, sourceLevelId);
      if (pick == null) {
        KeystoneLog.Verbose(
            $"[Keystone] OvergrowthReseeder: no Class D species for " +
            $"{biome}/{sourceLevelId}; reseed at {tile} skipped.");
        return false;
      }

      // Capture the felled-wood value BEFORE deleting. A grown tree that
      // died of drought still carries its full yield (death doesn't strip
      // it); a dead sapling that never grew yields 0 (Yielder disabled).
      var yield = ReadYield(host);

      // Step 1 — remove the dead tree (frees the tile synchronously for the
      // spawn below; fires the standard removal events).
      _entityService.Delete(host);

      // Step 2 — plant the seedling via the Class D path. The tile is now
      // clear, so its replacement scan finds nothing to displace.
      var seedling = _classD.TrySpawnClassD(pick.BlueprintName, tile, pick.Height);
      if (seedling == null) {
        KeystoneLog.Verbose(
            $"[Keystone] OvergrowthReseeder: seedling '{pick.BlueprintName}' " +
            $"could not be placed at {tile} after clearing the dead tree; " +
            "tile reverts to barren.");
        return false;
      }

      // Carry the overgrowth straight onto the new tree ("overgrown from
      // the start").
      seedling.GetComponent<KeystoneOvergrowth>()?.Apply(composition);

      // Step 3 — drop the felled wood for hauling.
      DropWood(seedling, yield);

      KeystoneLog.Verbose(
          $"[Keystone] OvergrowthReseeder: reseeded {tile} with " +
          $"'{pick.BlueprintName}'" +
          (yield.Amount > 0 ? $" (+{yield.Amount} {yield.GoodId})." : " (no wood)."));
      return true;
    }

    #endregion

    #region Helpers

    /// <summary>The dead tree's felled-wood value. <c>Yielder.Yield</c>
    /// reports amount 0 when the yielder is disabled (the tree never grew),
    /// so dead saplings naturally yield nothing.</summary>
    private static GoodAmount ReadYield(BlockObject host) {
      var cuttable = host.GetComponent<Cuttable>();
      return cuttable != null ? cuttable.Yielder.Yield : default;
    }

    /// <summary>Enable the new seedling's own <see cref="GoodStack"/> with
    /// the felled wood and register it for lumberjack hauling. The tree's
    /// <c>LumberjackGoodStackAdder</c> wired removal-on-empty during its
    /// (already-run) <c>Start</c>, but its initial add saw an empty stack —
    /// so register explicitly here, guarded against a double-add.</summary>
    private void DropWood(BlockObject seedling, GoodAmount yield) {
      if (yield.Amount <= 0) return;
      var stack = seedling.GetComponent<GoodStack>();
      if (stack == null) return;
      stack.EnableGoodStack(yield);
      if (!AlreadyRegistered(stack)) {
        _lumberjackStacks.Add(stack);
      }
    }

    private bool AlreadyRegistered(GoodStack stack) {
      var stacks = _lumberjackStacks.GoodStacks;
      for (var i = 0; i < stacks.Count; i++) {
        if (stacks[i] == stack) return true;
      }
      return false;
    }

    /// <summary>Weighted-random pick over the biome's Class D table at the
    /// source level, using the recipes' own weights (so Grassland stays
    /// birch-heavy). Non-positive weights count as 1. Returns <c>null</c>
    /// when the table is empty.</summary>
    private ClassDRecipe? PickClassD(BiomeKind biome, string levelId) {
      var recipes = _catalog.ClassDFor(biome, levelId);
      if (recipes.Count == 0) return null;
      var total = 0f;
      for (var i = 0; i < recipes.Count; i++) {
        total += Weight(recipes[i]);
      }
      if (total <= 0f) return null;
      var roll = (float)(_rng.NextDouble() * total);
      for (var i = 0; i < recipes.Count; i++) {
        roll -= Weight(recipes[i]);
        if (roll <= 0f) return recipes[i];
      }
      return recipes[recipes.Count - 1];
    }

    private static float Weight(ClassDRecipe recipe) => recipe.Weight > 0f ? recipe.Weight : 1f;

    #endregion

  }

}
