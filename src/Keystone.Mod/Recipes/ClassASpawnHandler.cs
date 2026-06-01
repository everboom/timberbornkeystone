using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Decoration;
using UnityEngine;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Rule handler for Class A recipes — non-<c>BlockObject</c>
  /// decorations spawned via <see cref="KeystoneDecorationRegistry"/>.
  /// The only handler with true reconcile-and-despawn semantics: each
  /// cycle, every tile that *should* host a decoration gets one (and
  /// any decoration whose tile *shouldn't* anymore gets despawned).
  ///
  /// <para><b>Smart investment, dumb decoration.</b> The handler owns
  /// a memo from <c>(SurfaceCoord, levelId)</c> to the spawned
  /// decoration plus a per-cycle "seen" scratch.
  /// <see cref="OnRecipeChosen"/> adds keys to the scratch as it
  /// spawns; <see cref="OnCycleComplete"/> despawns anything not seen.</para>
  ///
  /// <para><b>Save/load.</b> Decorations are not persisted (they're
  /// pure visuals). On load, the first cycle re-spawns them as a pure
  /// function of persisted Investment values plus the deterministic
  /// per-tile activation hash.</para>
  /// </summary>
  public sealed class ClassASpawnHandler : SpawnHandlerBase<ClassARecipe> {

    private readonly FlourishCatalog _catalog;
    private readonly KeystoneDecorationRegistry _registry;

    /// <summary>Live decorations keyed by <c>(surface, levelId)</c>.</summary>
    private readonly Dictionary<(SurfaceCoord Surface, string LevelId), KeystoneDecoration>
        _spawned = new();

    /// <summary>"Still wanted" keys for the in-progress cycle. Cleared
    /// at cycle start; populated as <see cref="OnRecipeChosen"/> fires.
    /// Anything in <see cref="_spawned"/> not present here at
    /// cycle end gets despawned.</summary>
    private readonly HashSet<(SurfaceCoord Surface, string LevelId)> _seenScratch = new();

    public ClassASpawnHandler(
        FlourishCatalog catalog,
        RecipeFilterRegistry filters,
        IPlantingMarkQuery marks,
        KeystoneDecorationRegistry registry)
        : base(filters, marks) {
      _catalog = catalog;
      _registry = registry;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<ClassARecipe> GetAllRecipes() => _catalog.AllClassA;

    /// <inheritdoc />
    protected override IReadOnlyList<ClassARecipe> GetRecipes(BiomeKind biome, string levelId)
        => _catalog.ClassAFor(biome, levelId);

    /// <inheritdoc />
    protected override string GetFilter(ClassARecipe recipe) => recipe.Filter;

    /// <inheritdoc />
    protected override float GetWeight(ClassARecipe recipe) => recipe.Weight;

    /// <inheritdoc />
    protected override (BiomeKind Biome, string LevelId) GetBucketKey(ClassARecipe recipe) =>
        (recipe.Biome, recipe.LevelId);

    /// <inheritdoc />
    public override void OnCycleStart() {
      _seenScratch.Clear();
    }

    /// <inheritdoc />
    protected override void OnRecipeChosen(
        SurfaceCoord surface, BiomeKind biome, BiomeLevel level, ClassARecipe recipe) {
      var key = (surface, level.LevelId);
      _seenScratch.Add(key);
      if (_spawned.ContainsKey(key)) return;

      var spawned = _registry.Spawn(
          recipe.DonorBlueprintName,
          new Vector3Int(surface.X, surface.Y, surface.Z),
          controller: null);
      if (spawned != null) {
        _spawned[key] = spawned;
      }
    }

    /// <inheritdoc />
    public override void OnCycleComplete() {
      List<(SurfaceCoord Surface, string LevelId)>? toRemove = null;
      foreach (var key in _spawned.Keys) {
        if (_seenScratch.Contains(key)) continue;
        (toRemove ??= new List<(SurfaceCoord Surface, string LevelId)>()).Add(key);
      }
      if (toRemove == null) return;
      foreach (var key in toRemove) {
        _registry.Despawn(_spawned[key]);
        _spawned.Remove(key);
      }
    }

  }

}
