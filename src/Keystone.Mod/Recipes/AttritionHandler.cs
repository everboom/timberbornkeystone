using System;
using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Flourish;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Flourish;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.EntitySystem;
using UnityEngine;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Rule handler that applies <see cref="AttritionRecipe"/> rules to
  /// Keystone Class B / Class C entities. Plugged into
  /// <see cref="ChunkRulesApplier"/> via the <see cref="IRuleHandler"/>
  /// interface alongside the four spawn handlers.
  ///
  /// <para><b>Per-tile semantics.</b> For each <see cref="OnUnit"/>
  /// call (one per (active level) per surface in a dominant-biome
  /// chunk), the handler:
  /// <list type="number">
  ///   <item>Looks up attrition recipes for <c>(biome, level)</c>.</item>
  ///   <item>Walks the surface's entities (via
  ///         <see cref="IBlockService.GetObjectsAt"/>) and collects
  ///         the ones carrying <see cref="KeystoneVariant"/> with
  ///         <c>Class</c> in <c>{"B", "C"}</c>.</item>
  ///   <item>For each recipe, for each matching entity: applies the
  ///         spatial filter, rolls Bernoulli on
  ///         <see cref="AttritionRecipe.Probability"/>, and on hit
  ///         applies <see cref="AttritionAction.Kill"/> (set
  ///         <c>KeystoneFlourish.LifeStatus = Dead</c>) or
  ///         <see cref="AttritionAction.Destroy"/>
  ///         (<see cref="EntityService.Delete"/>).</item>
  /// </list>
  /// Multiple recipes in the same bucket roll independently — they
  /// stack. An entity targeted by both "L1: 5% destroy" and "L2: 10%
  /// destroy" rolls both per cycle.</para>
  ///
  /// <para><b>Class A is skipped.</b> The schema accepts <c>"A"</c>
  /// in <see cref="AttritionEntry.Classes"/> but this handler doesn't
  /// act on Class A entities. <see cref="ClassASpawnHandler"/>
  /// reconciles every cycle, so a deleted Class A would just respawn
  /// the next pass. Class A attrition needs to coordinate with the
  /// spawn handler's "seen" set; that's a separate design round.</para>
  ///
  /// <para><b>Class D (vanilla flora) is targetable by blueprint
  /// name.</b> Vanilla entities don't carry a
  /// <see cref="KeystoneVariant"/> stamp, so the class-string mechanism
  /// (<see cref="AttritionEntry.Classes"/>) doesn't address them. A
  /// recipe declares its vanilla targets explicitly via
  /// <see cref="AttritionEntry.VanillaSpecies"/> (e.g.
  /// <c>["Cattail", "Spadderdock"]</c> on River's high-flow rule);
  /// the handler matches each vanilla entity at the tile against the
  /// bucket's combined species list. Player-marked tiles remain
  /// exempt because the per-surface marked-tile skip in
  /// <c>ChunkRulesApplier</c> fires before this handler runs.</para>
  ///
  /// <para><b>RNG.</b> Bernoulli rolls use a non-deterministic
  /// <see cref="System.Random"/>. Each cycle gives an entity a fresh
  /// chance; reload reproducibility isn't required here (attrition is
  /// stochastic by design).</para>
  /// </summary>
  public sealed class AttritionHandler : IRuleHandler {

    #region Fields

    private readonly FlourishCatalog _catalog;
    private readonly RecipeFilterRegistry _filters;
    private readonly IBlockService _blockService;
    private readonly EntityService _entityService;
    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly System.Random _rng = new();

    /// <summary>Scratch buffer for the per-surface entity collection.
    /// Reused across <see cref="OnUnit"/> calls. <c>ClassId</c> is
    /// <c>"B"</c>/<c>"C"</c> for Keystone-stamped entities or
    /// <c>"D"</c> for vanilla flora; <c>VanillaBlueprintName</c> is the
    /// vanilla blueprint name for Class D entries and empty for B/C.</summary>
    private readonly List<(Timberborn.BlockSystem.BlockObject Entity, string ClassId, string VanillaBlueprintName)> _tileScratch = new();

    #endregion

    #region Construction

    public AttritionHandler(
        FlourishCatalog catalog,
        RecipeFilterRegistry filters,
        IBlockService blockService,
        EntityService entityService,
        RegionService regions,
        IEcologyFieldQuery fieldQuery) {
      _catalog = catalog;
      _filters = filters;
      _blockService = blockService;
      _entityService = entityService;
      _regions = regions;
      _fieldQuery = fieldQuery;
    }

    #endregion

    #region IRuleHandler

    /// <inheritdoc />
    public void OnCycleStart() {}

    /// <inheritdoc />
    public void OnCycleComplete() {}

    /// <inheritdoc />
    public bool ShouldRun() => _catalog.AllAttrition.Count > 0;

    /// <inheritdoc />
    public System.Collections.Generic.IEnumerable<(BiomeKind Biome, string LevelId)> ActiveBuckets {
      get {
        var recipes = _catalog.AllAttrition;
        for (var i = 0; i < recipes.Count; i++) {
          yield return (recipes[i].Biome, recipes[i].LevelId);
        }
      }
    }

    /// <inheritdoc />
    public void OnUnit(SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress) {
      // Attrition deliberately ignores the level-progress ramp that
      // spawn handlers honour. Each cycle is an independent Bernoulli
      // roll on recipe.Probability (or its ScaleBy-channel-scaled
      // variant); ramping it would slow early-level kill rates in a
      // way that doesn't match the design intent — attrition is "this
      // habitat doesn't sustain that flora," not "this habitat is
      // gradually becoming hostile."
      _ = progress;
      var recipes = _catalog.AttritionFor(biome, level.LevelId);
      if (recipes.Count == 0) return;

      _tileScratch.Clear();
      CollectAttritionTargets(surface, recipes, _tileScratch);
      if (_tileScratch.Count == 0) return;

      // Resolve the chunk's ecology field once per surface. Used only
      // by recipes with channel-based probability scaling; recipes
      // with a constant probability skip the sample entirely.
      var region = _regions.Containing(surface);
      var field = region != null ? _fieldQuery.FieldFor(region.Id) : null;

      for (var ri = 0; ri < recipes.Count; ri++) {
        var recipe = recipes[ri];
        if (!_filters.IsEligible(recipe.Filter, surface)) continue;

        var probability = EffectiveProbability(recipe, surface, field);
        if (probability <= 0f) continue;  // below ScaleMin → rule skips

        for (var ei = 0; ei < _tileScratch.Count; ei++) {
          var (entity, classId, vanillaBlueprintName) = _tileScratch[ei];
          if (entity == null) continue;  // destroyed by an earlier recipe this tile
          // Match either by class string (B/C Keystone entities) or by
          // vanilla blueprint name (Class D entities — they have no
          // KeystoneVariant stamp). Core helper owns the two-track
          // matching logic.
          if (!AttritionTargeting.MatchesTarget(recipe, classId, vanillaBlueprintName)) continue;
          if (!IsHabitatIncluded(entity, recipe.IncludeHabitats)) continue;
          if (IsHabitatExcluded(entity, recipe.ExcludeHabitats)) continue;
          if (_rng.NextDouble() >= probability) continue;
          ApplyAction(entity, recipe.Action);
          if (recipe.Action == AttritionAction.Destroy) {
            // Clear the scratch slot so a later recipe in this bucket
            // can't re-touch the destroyed entity.
            _tileScratch[ei] = (null!, classId, vanillaBlueprintName);
          }
        }
      }
    }

    /// <summary>True if <paramref name="entity"/>'s habitat is on the
    /// recipe's exclude list. Today only the <c>"Dry"</c> tag is
    /// wired — entities with <see cref="KeystoneDryNaturalResource"/>
    /// match it. Other habitat names in the exclude list (e.g.
    /// <c>"Land"</c>, <c>"Aquatic"</c>) are accepted by the parser
    /// for forward compatibility but don't currently match any
    /// entity, so they're silently inert here.</summary>
    private static bool IsHabitatExcluded(
        Timberborn.BlockSystem.BlockObject entity, IReadOnlyList<string> excludeHabitats) {
      if (excludeHabitats.Count == 0) return false;
      for (var i = 0; i < excludeHabitats.Count; i++) {
        if (excludeHabitats[i] == "Dry" && KeystoneDryNaturalResource.IsDry(entity)) {
          return true;
        }
      }
      return false;
    }

    /// <summary>True if the recipe has no include-habitat gate, or if
    /// <paramref name="entity"/> carries at least one of the listed
    /// habitat tags. Empty list means "no gate" (the common case), so
    /// recipes without <c>IncludeHabitats</c> behave identically to the
    /// pre-gate semantics. Same habitat vocabulary as
    /// <see cref="IsHabitatExcluded"/>; today only <c>"Dry"</c> is
    /// wired.</summary>
    private static bool IsHabitatIncluded(
        Timberborn.BlockSystem.BlockObject entity, IReadOnlyList<string> includeHabitats) {
      if (includeHabitats.Count == 0) return true;
      for (var i = 0; i < includeHabitats.Count; i++) {
        if (includeHabitats[i] == "Dry" && KeystoneDryNaturalResource.IsDry(entity)) {
          return true;
        }
      }
      return false;
    }

    /// <summary>Resolve the per-tile probability for
    /// <paramref name="recipe"/> at <paramref name="surface"/>.
    /// Constant-probability recipes return
    /// <see cref="AttritionRecipe.Probability"/> directly; recipes
    /// with channel-based scaling sample <paramref name="field"/> and
    /// delegate the interp math to
    /// <see cref="AttritionRecipe.EffectiveProbability"/>. If the
    /// recipe wants a channel sample but no field is available, the
    /// rule is skipped (probability 0) — we don't fall back to the
    /// unscaled <c>Probability</c> in that case because the recipe's
    /// intent is "rule depends on this channel."</summary>
    private static float EffectiveProbability(
        AttritionRecipe recipe, SurfaceCoord surface, RegionEcologyField? field) {
      if (recipe.ScaleBy == null) return recipe.Probability;
      if (field == null) return 0f;
      var sample = field.Sample(recipe.ScaleBy.Value, surface.X, surface.Y);
      return recipe.EffectiveProbability(sample);
    }

    #endregion

    #region Per-tile evaluation

    /// <summary>Walk the block service for <paramref name="surface"/>'s
    /// tile and collect targetable entities: Keystone-stamped Class B/C
    /// (via <see cref="KeystoneVariant"/>) and vanilla flora whose
    /// blueprint name appears in any of the bucket's recipes'
    /// <see cref="AttritionRecipe.VanillaSpecies"/> lists. Class A is
    /// registry-tracked (not BlockObject-tracked) and intentionally
    /// not enumerated here — see the type-level doc.
    ///
    /// <para><b>Rock clusters (geology) are categorically exempt.</b>
    /// Attrition models ecological pressure on flora/fauna; rocks
    /// aren't biotic, so no kill rule should ever destroy them
    /// regardless of biome or recipe. Detected by the presence of
    /// <see cref="KeystoneRockTint"/>, which every rock-cluster
    /// blueprint carries via <c>KeystoneRockTintSpec</c>. Skipping
    /// them here (rather than per-recipe via <c>ExcludeHabitats</c>)
    /// means new attrition recipes can't accidentally forget the
    /// exemption.</para>
    ///
    /// <para><b>Vanilla-species early filter.</b> Vanilla entities
    /// are only added to the scratch when their blueprint name
    /// matches at least one recipe in the bucket. The lookup walks the
    /// recipes' <c>VanillaSpecies</c> lists; if no recipe in the
    /// bucket targets any vanilla species, the vanilla-entity branch
    /// is skipped entirely (no <c>BlockObjectSpec.Blueprint</c>
    /// component read).</para></summary>
    private void CollectAttritionTargets(
        SurfaceCoord surface,
        IReadOnlyList<AttritionRecipe> bucket,
        List<(Timberborn.BlockSystem.BlockObject Entity, string ClassId, string VanillaBlueprintName)> scratch) {
      var tile = new Vector3Int(surface.X, surface.Y, surface.Z);
      var anyVanillaTarget = false;
      for (var ri = 0; ri < bucket.Count; ri++) {
        if (bucket[ri].VanillaSpecies.Count > 0) {
          anyVanillaTarget = true;
          break;
        }
      }
      // Per-BO isolation: one malformed third-party BO at this tile
      // (component access throws, blueprint ref divergent) shouldn't
      // skip the whole attrition pass. Treat per-BO failures as
      // "non-targetable, move on."
      foreach (var bo in _blockService.GetObjectsAt(tile)) {
        if (bo == null) continue;
        try {
          if (bo.GetComponent<KeystoneRockTint>() != null) continue;  // geology, not biota
          var variant = bo.GetComponent<KeystoneVariant>();
          if (variant != null) {
            var classId = variant.Class;
            if (classId == "B" || classId == "C") {
              scratch.Add((bo, classId, ""));
            }
            // KeystoneVariant present but not B/C (e.g. future Class A
            // stamp): not targetable by attrition, skip.
            continue;
          }
          // Unstamped entity: candidate for Class D vanilla targeting,
          // but only if any recipe in the bucket actually lists vanilla
          // species (otherwise the blueprint-name read is wasted).
          if (!anyVanillaTarget) continue;
          var blueprintName = bo.GetComponent<BlockObjectSpec>()?.Blueprint?.Name;
          if (string.IsNullOrEmpty(blueprintName)) continue;
          if (!AnyRecipeTargetsVanilla(bucket, blueprintName)) continue;
          scratch.Add((bo, "D", blueprintName));
        } catch (Exception ex) {
          KeystoneLog.Warn(
              $"[Keystone] AttritionHandler.CollectAttritionTargets: BO at " +
              $"{tile} threw {ex.GetType().Name}: {ex.Message}. Treating as non-targetable.");
          Diagnostics.KeystoneIntegrationHealth.TryRecord(
              "Per-tile errors",
              $"AttritionHandler target collection: {ex.GetType().Name}");
        }
      }
    }

    /// <summary>True if at least one recipe in <paramref name="bucket"/>
    /// names <paramref name="blueprintName"/> in its
    /// <see cref="AttritionRecipe.VanillaSpecies"/> list. Used to gate
    /// vanilla-entity admission into the scratch buffer so non-targeted
    /// vanilla flora (Birch, Maple, etc. on a River chunk that only
    /// targets Cattail/Spadderdock) doesn't bloat the scratch.</summary>
    private static bool AnyRecipeTargetsVanilla(
        IReadOnlyList<AttritionRecipe> bucket, string blueprintName) {
      for (var ri = 0; ri < bucket.Count; ri++) {
        if (ContainsString(bucket[ri].VanillaSpecies, blueprintName)) return true;
      }
      return false;
    }

    private static bool ContainsString(IReadOnlyList<string> list, string value) {
      for (var i = 0; i < list.Count; i++) {
        if (list[i] == value) return true;
      }
      return false;
    }

    private void ApplyAction(Timberborn.BlockSystem.BlockObject entity, AttritionAction action) {
      try {
        switch (action) {
          case AttritionAction.Kill:
            var flourish = entity.GetComponent<KeystoneFlourish>();
            if (flourish != null) {
              flourish.SetLifeStatus(FlourishLifeStatus.Dead);
            }
            break;
          case AttritionAction.Destroy:
            _entityService.Delete(entity);
            break;
        }
      } catch (Exception ex) {
        // Per-entity loop runs every cycle; a single broken entity
        // shouldn't take the whole handler down.
        KeystoneLog.Error(
            $"[Keystone] AttritionHandler.ApplyAction({action}) threw: {ex}");
      }
    }

    #endregion

  }

}
