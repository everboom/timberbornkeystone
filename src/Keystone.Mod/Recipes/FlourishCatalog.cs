using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Mod.Diagnostics;
using Timberborn.BlueprintSystem;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// At-PostLoad enumeration of every flourish recipe registered
  /// across all loaded mods. Walks every <see cref="KeystoneRecipeBookSpec"/>
  /// instance via <c>ISpecService.GetSpecs</c>, dispatches each
  /// entry to <see cref="ClassARecipe"/> / <see cref="ClassBRecipe"/>
  /// / <see cref="ClassCRecipe"/> based on the entry's
  /// <c>Class</c> field, and indexes the result by
  /// <c>(BiomeKind, LevelId)</c> for fast handler lookup.
  ///
  /// <para><b>Recipes are decoupled from blueprints.</b> A recipe
  /// names a blueprint by string and declares its own class. The
  /// same blueprint can be referenced by multiple recipes with
  /// different classes (Cattail-as-Class-A and Cattail-as-Class-C
  /// sharing the asset). Per-entity behavior (visual lifecycle via
  /// <c>KeystoneFlourishSpec</c> -&gt; <c>KeystoneFlourish</c>;
  /// per-entity class designation via <see cref="KeystoneVariant"/>)
  /// is layered independently and may differ between recipes
  /// referencing the same blueprint.</para>
  ///
  /// <para><b>Modular by construction.</b> Two registration paths,
  /// both automatically merged at PostLoad:
  /// <list type="bullet">
  ///   <item>Blueprint-based (preferred): faction-expansion mods
  ///         author recipe-book blueprints carrying
  ///         <see cref="KeystoneRecipeBookSpec"/> and drop them into
  ///         their mod folder. The catalog reads
  ///         <c>ISpecService.GetSpecs</c> across all loaded mods.
  ///         No C# from the modder.</item>
  ///   <item>Code-based fallback: register
  ///         <see cref="ClassARecipe"/> / <see cref="ClassBRecipe"/>
  ///         / <see cref="ClassCRecipe"/> via
  ///         <c>MultiBind&lt;ClassXRecipe&gt;().ToInstance(...)</c>.
  ///         Useful for prototype recipes that don't warrant a
  ///         JSON blueprint.</item>
  /// </list></para>
  /// </summary>
  public sealed class FlourishCatalog : IPostLoadableSingleton {

    private readonly ISpecService _specs;
    private readonly IEnumerable<ClassARecipe> _codeRecipesA;
    private readonly IEnumerable<ClassBRecipe> _codeRecipesB;
    private readonly IEnumerable<ClassCRecipe> _codeRecipesC;
    private readonly IEnumerable<ClassDRecipe> _codeRecipesD;
    private readonly IEnumerable<ClassERecipe> _codeRecipesE;
    private readonly List<ClassARecipe> _classA = new();
    private readonly Dictionary<(BiomeKind Biome, string LevelId), List<ClassARecipe>> _classAByBiomeLevel = new();
    private readonly List<ClassBRecipe> _classB = new();
    private readonly Dictionary<(BiomeKind Biome, string LevelId), List<ClassBRecipe>> _classBByBiomeLevel = new();
    private readonly List<ClassCRecipe> _classC = new();
    private readonly Dictionary<(BiomeKind Biome, string LevelId), List<ClassCRecipe>> _classCByBiomeLevel = new();
    private readonly List<ClassDRecipe> _classD = new();
    private readonly Dictionary<(BiomeKind Biome, string LevelId), List<ClassDRecipe>> _classDByBiomeLevel = new();
    private readonly List<ClassERecipe> _classE = new();
    private readonly Dictionary<(BiomeKind Biome, string LevelId), List<ClassERecipe>> _classEByBiomeLevel = new();
    private readonly List<AttritionRecipe> _attrition = new();
    private readonly Dictionary<(BiomeKind Biome, string LevelId), List<AttritionRecipe>> _attritionByBiomeLevel = new();
    private readonly List<OvergrowthRecipe> _overgrowth = new();
    private readonly Dictionary<(BiomeKind Biome, string LevelId), List<OvergrowthRecipe>> _overgrowthByBiomeLevel = new();
    private bool _postLoadCompleted;

    /// <summary>True once <see cref="PostLoad"/> has run. Read by the
    /// startup self-check to defer until the catalog is populated.</summary>
    public bool IsLoaded => _postLoadCompleted;

    public FlourishCatalog(
        ISpecService specs,
        IEnumerable<ClassARecipe> codeRecipesA,
        IEnumerable<ClassBRecipe> codeRecipesB,
        IEnumerable<ClassCRecipe> codeRecipesC,
        IEnumerable<ClassDRecipe> codeRecipesD,
        IEnumerable<ClassERecipe> codeRecipesE) {
      _specs = specs;
      _codeRecipesA = codeRecipesA;
      _codeRecipesB = codeRecipesB;
      _codeRecipesC = codeRecipesC;
      _codeRecipesD = codeRecipesD;
      _codeRecipesE = codeRecipesE;
    }

    /// <summary>Run <see cref="PostLoad"/> if it hasn't run this
    /// session. Idempotent: a second call no-ops. Used by
    /// <c>KeystoneStartupWarmup</c> to enforce ordering relative to
    /// Bindito's non-deterministic <c>PostLoad</c> sequence without
    /// depending on <c>OrderingAttribute</c>.</summary>
    public void EnsurePostLoaded() {
      if (_postLoadCompleted) return;
      PostLoad();
    }

    /// <inheritdoc />
    public void PostLoad() {
      if (_postLoadCompleted) return;
      // Outermost try/catch wraps the entire PostLoad body. A failure
      // anywhere in the recipe-book walk leaves the catalogs partially
      // populated (whatever was parsed before the throw); _postLoadCompleted
      // stays false so a downstream EnsurePostLoaded retries. Without
      // the catch, the spawn handlers fire against empty buckets and
      // no flora ever spawns.
      try {
      _classA.Clear();
      _classAByBiomeLevel.Clear();
      _classB.Clear();
      _classBByBiomeLevel.Clear();
      _classC.Clear();
      _classCByBiomeLevel.Clear();
      _classD.Clear();
      _classDByBiomeLevel.Clear();
      _classE.Clear();
      _classEByBiomeLevel.Clear();
      _attrition.Clear();
      _attritionByBiomeLevel.Clear();
      _overgrowth.Clear();
      _overgrowthByBiomeLevel.Clear();

      var bookCount = 0;
      var entryCount = 0;
      var classACount = 0;
      var classBCount = 0;
      var classCCount = 0;
      var classDCount = 0;
      var classECount = 0;
      var skipped = 0;
      var attritionEntryCount = 0;
      var attritionCount = 0;
      var attritionSkipped = 0;

      foreach (var book in _specs.GetSpecs<KeystoneRecipeBookSpec>()) {
        bookCount++;
        var bookBlueprintName = book.Blueprint?.Name ?? "<unbound>";

        foreach (var attrition in book.Attritions) {
          attritionEntryCount++;
          if (TryParseAttrition(attrition, bookBlueprintName, out var recipe)) {
            AddAttrition(recipe);
            attritionCount++;
          } else {
            attritionSkipped++;
          }
        }

        foreach (var overgrowth in book.Overgrowths) {
          var parsed = new List<OvergrowthRecipe>();
          if (TryParseOvergrowth(overgrowth, bookBlueprintName, parsed)) {
            foreach (var recipe in parsed) {
              AddOvergrowth(recipe);
            }
          }
        }

        foreach (var entry in book.Recipes) {
          entryCount++;
          if (!TryParseEntry(entry, bookBlueprintName, out var biomes)) {
            skipped++;
            continue;
          }
          var filter = entry.Filter ?? "";
          var weight = NormaliseWeight(entry.Weight);
          // Height resolution is class-specific: A/B/C default to 1
          // voxel (single-voxel flourishes); D defaults to 2 voxels
          // (vanilla flora trees stack multiple blocks). Authored
          // Height takes precedence; 0 means "use the class default".
          var heightDefault = entry.Class == "D" ? 2 : 1;
          var height = entry.Height > 0 ? entry.Height : heightDefault;

          // Expand the entry to one ClassXRecipe per blueprint name per
          // biome. BlueprintNames takes precedence; BlueprintName (if
          // non-empty) is appended as the last name. Biomes is a single-
          // entry list for biome-keyed recipes, or every BiomeKind when
          // the entry's Biome is empty (= "any biome").
          foreach (var blueprintName in EnumerateBlueprintNames(entry, bookBlueprintName)) {
            foreach (var biome in biomes) {
              switch (entry.Class) {
                case "A":
                  AddRecipeA(new ClassARecipe(
                      Biome: biome,
                      LevelId: entry.Level,
                      DonorBlueprintName: blueprintName,
                      Filter: filter,
                      Weight: weight));
                  classACount++;
                  break;
                case "B":
                  AddRecipeB(new ClassBRecipe(
                      Biome: biome,
                      LevelId: entry.Level,
                      BlueprintName: blueprintName,
                      Filter: filter,
                      Weight: weight,
                      Height: height));
                  classBCount++;
                  break;
                case "C":
                  AddRecipeC(new ClassCRecipe(
                      Biome: biome,
                      LevelId: entry.Level,
                      BlueprintName: blueprintName,
                      Filter: filter,
                      Weight: weight,
                      Height: height));
                  classCCount++;
                  break;
                case "D":
                  // Category is required for Class D (it drives the
                  // per-category multiplier from KeystoneFloraSettings).
                  // Skip-with-warning rather than throw so a single bad
                  // recipe doesn't take down the whole catalog at load.
                  if (string.IsNullOrEmpty(entry.Category)) {
                    KeystoneLog.Warn(
                        $"[Keystone] FlourishCatalog: Class D recipe in book '{bookBlueprintName}' " +
                        $"for blueprint '{blueprintName}' is missing required field 'Category'. " +
                        "Add Category (e.g. 'Trees', 'Bushes', 'Crops') in the recipe-book JSON. Recipe skipped.");
                    skipped++;
                    break;
                  }
                  AddRecipeD(new ClassDRecipe(
                      Biome: biome,
                      LevelId: entry.Level,
                      BlueprintName: blueprintName,
                      Category: entry.Category,
                      Filter: filter,
                      Weight: weight,
                      Height: height));
                  classDCount++;
                  break;
                case "E":
                  // Class E = fauna agent. No Filter (the agent's own
                  // walkability filter handles per-tile eligibility) and
                  // no Height (fauna are non-BlockObject, no vertical
                  // clearance reservation). Capacity-driven spawning at
                  // dawn: the (biome, levelId) bucket's combined capacity
                  // comes from the level's FaunaDensityPerTile +
                  // FaunaMinTilesToSpawn; Weight decides per-slot pick
                  // among recipes sharing the bucket.
                  //
                  // Category is required for Class E (it drives the
                  // per-category multiplier from KeystoneFaunaSettings).
                  // Skip-with-warning rather than throw so a single bad
                  // recipe doesn't take down the whole catalog at load.
                  if (string.IsNullOrEmpty(entry.Category)) {
                    KeystoneLog.Warn(
                        $"[Keystone] FlourishCatalog: Class E recipe in book '{bookBlueprintName}' " +
                        $"for blueprint '{blueprintName}' is missing required field 'Category'. " +
                        "Add Category (e.g. 'Deer', 'Cattle', 'Fish') in the recipe-book JSON. Recipe skipped.");
                    skipped++;
                    break;
                  }
                  AddRecipeE(new ClassERecipe(
                      Biome: biome,
                      LevelId: entry.Level,
                      BlueprintName: blueprintName,
                      Category: entry.Category,
                      Weight: weight));
                  classECount++;
                  break;
                default:
                  KeystoneLog.Warn(
                      $"[Keystone] FlourishCatalog: recipe in book '{bookBlueprintName}' " +
                      $"references unknown Class='{entry.Class}' (expected A, B, C, D, or E). " +
                      "Recipe skipped.");
                  skipped++;
                  break;
              }
            }
          }
        }
      }

      // Code-registered fallback recipes (prototypes / no-blueprint).
      var codeACount = 0;
      foreach (var recipe in _codeRecipesA) {
        if (recipe == null) continue;
        AddRecipeA(recipe);
        codeACount++;
      }
      var codeBCount = 0;
      foreach (var recipe in _codeRecipesB) {
        if (recipe == null) continue;
        AddRecipeB(recipe);
        codeBCount++;
      }
      var codeCCount = 0;
      foreach (var recipe in _codeRecipesC) {
        if (recipe == null) continue;
        AddRecipeC(recipe);
        codeCCount++;
      }
      var codeDCount = 0;
      foreach (var recipe in _codeRecipesD) {
        if (recipe == null) continue;
        AddRecipeD(recipe);
        codeDCount++;
      }
      var codeECount = 0;
      foreach (var recipe in _codeRecipesE) {
        if (recipe == null) continue;
        AddRecipeE(recipe);
        codeECount++;
      }

      // Invariant: every Class D / Class E recipe in a (biome, levelId)
      // bucket must share the same Category. The bucket's per-cycle
      // multiplier comes from looking up the category of any one recipe
      // in the bucket — that lookup is only well-defined if the bucket
      // is single-category. A violation almost certainly means a content
      // author dropped a recipe into the wrong bucket; warn loudly so
      // it's caught at load time instead of producing surprising
      // multipliers at gameplay time. Warn rather than throw so a
      // single broken recipe book doesn't block the whole mod from
      // loading; the rest of the pipeline tolerates mixed-category
      // buckets (it just picks one category's multiplier and applies it
      // to all recipes in the bucket).
      foreach (var (key, bucket) in _classDByBiomeLevel) {
        if (bucket.Count <= 1) continue;
        var firstCategory = bucket[0].Category;
        for (var i = 1; i < bucket.Count; i++) {
          if (bucket[i].Category != firstCategory) {
            KeystoneLog.Warn(
                $"[Keystone] FlourishCatalog: Class D bucket {key} contains recipes with " +
                $"mixed Categories ('{firstCategory}' vs '{bucket[i].Category}'). " +
                "Per-category density sliders will use the first recipe's category for the whole " +
                "bucket. Either unify the Category across all recipes in this bucket or split them " +
                "into different levels.");
            break;
          }
        }
      }
      foreach (var (key, bucket) in _classEByBiomeLevel) {
        if (bucket.Count <= 1) continue;
        var firstCategory = bucket[0].Category;
        for (var i = 1; i < bucket.Count; i++) {
          if (bucket[i].Category != firstCategory) {
            KeystoneLog.Warn(
                $"[Keystone] FlourishCatalog: Class E bucket {key} contains recipes with " +
                $"mixed Categories ('{firstCategory}' vs '{bucket[i].Category}'). " +
                "Per-category abundance sliders will use the first recipe's category for the whole " +
                "bucket. Either unify the Category across all recipes in this bucket or split them " +
                "into different levels.");
            break;
          }
        }
      }

      KeystoneLog.Verbose(
          $"[Keystone] FlourishCatalog: walked {bookCount} recipe book(s), " +
          $"{entryCount} spawn entries ({skipped} skipped), {attritionEntryCount} attritions ({attritionSkipped} skipped). " +
          $"Class A {_classA.Count} ({classACount} blueprint, {codeACount} code) across {_classAByBiomeLevel.Count} (biome,level) bucket(s); " +
          $"Class B {_classB.Count} ({classBCount} blueprint, {codeBCount} code) across {_classBByBiomeLevel.Count} (biome,level) bucket(s); " +
          $"Class C {_classC.Count} ({classCCount} blueprint, {codeCCount} code) across {_classCByBiomeLevel.Count} (biome,level) bucket(s); " +
          $"Class D {_classD.Count} ({classDCount} blueprint, {codeDCount} code) across {_classDByBiomeLevel.Count} (biome,level) bucket(s); " +
          $"Class E {_classE.Count} ({classECount} blueprint, {codeECount} code) across {_classEByBiomeLevel.Count} (biome,level) bucket(s); " +
          $"Attrition {_attrition.Count} ({attritionCount} parsed) across {_attritionByBiomeLevel.Count} (biome,level) bucket(s).");

      _postLoadCompleted = true;
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleError(
            "FlourishCatalog.PostLoad", "Subsystem failed", ex);
      }
    }

    /// <summary>Project the Mod-side <see cref="AttritionEntry"/> spec
    /// into the Core <see cref="AttritionEntryInput"/> shape and
    /// delegate parsing/validation to
    /// <see cref="AttritionRecipeParser.TryParse"/>. The parser owns
    /// the actual rules — biome/action vocabulary, probability clamps,
    /// ScaleBy ramp validation, habitat/class filters — so they're
    /// testable without standing up a Timberborn host.</summary>
    private static bool TryParseAttrition(
        AttritionEntry entry, string sourceBookName, out AttritionRecipe recipe) {
      var input = new AttritionEntryInput(
          Biome: entry.Biome,
          Level: entry.Level,
          Action: entry.Action,
          Classes: entry.Classes,
          VanillaSpecies: entry.VanillaSpecies,
          Probability: entry.Probability,
          Filter: entry.Filter ?? "",
          ScaleBy: entry.ScaleBy,
          ScaleMin: entry.ScaleMin,
          ScaleMax: entry.ScaleMax,
          ProbabilityAtMin: entry.ProbabilityAtMin,
          ExcludeHabitats: entry.ExcludeHabitats,
          IncludeHabitats: entry.IncludeHabitats);
      return AttritionRecipeParser.TryParse(input, sourceBookName, KeystoneLog.Warn, out recipe);
    }

    private void AddAttrition(AttritionRecipe recipe) {
      _attrition.Add(recipe);
      var key = (recipe.Biome, recipe.LevelId);
      if (!_attritionByBiomeLevel.TryGetValue(key, out var list)) {
        list = new List<AttritionRecipe>();
        _attritionByBiomeLevel[key] = list;
      }
      list.Add(recipe);
    }

    /// <summary>All registered attrition rules, in registration order.</summary>
    public IReadOnlyList<AttritionRecipe> AllAttrition => _attrition;

    /// <summary>Attrition rules registered against the given
    /// <paramref name="biome"/> at the given <paramref name="levelId"/>.
    /// Returns an empty list when no rules target the bucket.</summary>
    public IReadOnlyList<AttritionRecipe> AttritionFor(BiomeKind biome, string levelId) {
      if (_attritionByBiomeLevel.TryGetValue((biome, levelId), out var list)) return list;
      return Array.Empty<AttritionRecipe>();
    }

    /// <summary>Parse a Mod-side <see cref="OvergrowthEntry"/> into one or
    /// more Core <see cref="OvergrowthRecipe"/> (one per composition, via
    /// <see cref="EnumerateCompositions"/>), appended to
    /// <paramref name="outRecipes"/>. Requires a known biome, a level, a
    /// known <c>Target</c> (Live/Dead/Reseed), and at least one composition;
    /// warns and skips otherwise. Returns true if any recipe was produced.
    /// Overgrowth is biome-specific (the recovery biomes), so there's no
    /// "any biome" expansion.</summary>
    private static bool TryParseOvergrowth(
        OvergrowthEntry entry, string sourceBookName, List<OvergrowthRecipe> outRecipes) {
      if (string.IsNullOrEmpty(entry.Level)) {
        KeystoneLog.Warn(
            $"[Keystone] FlourishCatalog: overgrowth entry in book '{sourceBookName}' " +
            "has empty Level. Entry skipped.");
        return false;
      }
      if (!Enum.TryParse<BiomeKind>(entry.Biome, ignoreCase: true, out var biome)) {
        KeystoneLog.Warn(
            $"[Keystone] FlourishCatalog: overgrowth entry (Level='{entry.Level}') in book " +
            $"'{sourceBookName}' has Biome='{entry.Biome}' which is not a known BiomeKind. Entry skipped.");
        return false;
      }
      if (!Enum.TryParse<OvergrowthTarget>(entry.Target, ignoreCase: true, out var target)) {
        KeystoneLog.Warn(
            $"[Keystone] FlourishCatalog: overgrowth entry (Biome='{entry.Biome}', Level='{entry.Level}') " +
            $"in book '{sourceBookName}' has Target='{entry.Target}' (expected Live, Dead, or Reseed). " +
            "Entry skipped.");
        return false;
      }
      var weight = NormaliseWeight(entry.Weight);
      var filter = entry.Filter ?? "";
      var sourceLevel = entry.SourceLevel ?? "";
      var added = 0;
      foreach (var composition in EnumerateCompositions(entry, sourceBookName)) {
        outRecipes.Add(new OvergrowthRecipe(
            biome, entry.Level, target, composition, filter, weight,
            entry.MaturityThreshold, sourceLevel));
        added++;
      }
      if (added == 0) {
        KeystoneLog.Warn(
            $"[Keystone] FlourishCatalog: overgrowth entry (Biome='{entry.Biome}', Level='{entry.Level}', " +
            $"Target='{entry.Target}') in book '{sourceBookName}' has no Composition/Compositions. " +
            "Entry skipped.");
        return false;
      }
      return true;
    }

    /// <summary>Enumerate an overgrowth entry's compositions: every
    /// non-blank name in <see cref="OvergrowthEntry.Compositions"/>, then
    /// <see cref="OvergrowthEntry.Composition"/> if non-blank. Mirrors
    /// <see cref="EnumerateBlueprintNames"/>.</summary>
    private static IEnumerable<string> EnumerateCompositions(
        OvergrowthEntry entry, string sourceBookName) {
      foreach (var name in entry.Compositions) {
        if (string.IsNullOrWhiteSpace(name)) {
          KeystoneLog.Warn(
              $"[Keystone] FlourishCatalog: empty entry in Compositions in book " +
              $"'{sourceBookName}' (Biome={entry.Biome}, Level={entry.Level}, " +
              $"Target={entry.Target}). Skipped.");
          continue;
        }
        yield return name;
      }
      if (!string.IsNullOrWhiteSpace(entry.Composition)) {
        yield return entry.Composition;
      }
    }

    private void AddOvergrowth(OvergrowthRecipe recipe) {
      _overgrowth.Add(recipe);
      var key = (recipe.Biome, recipe.LevelId);
      if (!_overgrowthByBiomeLevel.TryGetValue(key, out var list)) {
        list = new List<OvergrowthRecipe>();
        _overgrowthByBiomeLevel[key] = list;
      }
      list.Add(recipe);
    }

    /// <summary>All registered overgrowth rules, in registration order.</summary>
    public IReadOnlyList<OvergrowthRecipe> AllOvergrowth => _overgrowth;

    /// <summary>Overgrowth rules registered against the given
    /// <paramref name="biome"/> at the given <paramref name="levelId"/>.
    /// Returns an empty list when no rules target the bucket.</summary>
    public IReadOnlyList<OvergrowthRecipe> OvergrowthFor(BiomeKind biome, string levelId) {
      if (_overgrowthByBiomeLevel.TryGetValue((biome, levelId), out var list)) return list;
      return Array.Empty<OvergrowthRecipe>();
    }

    /// <summary>Every <see cref="BiomeKind"/> in enum-declaration order,
    /// cached. Used to expand "any biome" recipe entries (those that
    /// leave <c>Biome</c> empty) into per-biome buckets.</summary>
    private static readonly IReadOnlyList<BiomeKind> AllBiomes =
        ((BiomeKind[])Enum.GetValues(typeof(BiomeKind)));

    private bool TryParseEntry(
        RecipeEntry entry, string sourceBookName, out IReadOnlyList<BiomeKind> biomes) {
      biomes = Array.Empty<BiomeKind>();

      if (string.IsNullOrEmpty(entry.BlueprintName) && entry.BlueprintNames.Length == 0) {
        KeystoneLog.Warn(
            $"[Keystone] FlourishCatalog: recipe in book '{sourceBookName}' has " +
            "empty BlueprintName and empty BlueprintNames. Recipe skipped.");
        return false;
      }
      if (string.IsNullOrEmpty(entry.Level)) {
        KeystoneLog.Warn(
            $"[Keystone] FlourishCatalog: recipe '{entry.BlueprintName}' in book " +
            $"'{sourceBookName}' has empty Level. Recipe skipped.");
        return false;
      }
      // Empty Biome = "any biome": expand into one recipe per BiomeKind
      // internally, so a single authoring entry covers every chunk
      // regardless of dominant biome. Used for geological / worldgen
      // content (rock clusters at the Worldgen level) where biome is
      // not load-bearing.
      if (string.IsNullOrEmpty(entry.Biome)) {
        biomes = AllBiomes;
        return true;
      }
      if (!Enum.TryParse<BiomeKind>(entry.Biome, ignoreCase: true, out var biome)) {
        KeystoneLog.Warn(
            $"[Keystone] FlourishCatalog: recipe '{entry.BlueprintName}' in book " +
            $"'{sourceBookName}' has Biome='{entry.Biome}' which is not a known " +
            "BiomeKind. Recipe skipped.");
        return false;
      }
      biomes = new[] { biome };
      return true;
    }

    /// <summary>All registered Class A recipes, in registration
    /// order. Useful for diagnostics; the handler hot path
    /// uses <see cref="ClassAFor"/> instead.</summary>
    public IReadOnlyList<ClassARecipe> AllClassA => _classA;

    /// <summary>Class A recipes registered against the given
    /// <paramref name="biome"/> at the given <paramref name="levelId"/>.
    /// Returns an empty list when no recipes target the bucket.</summary>
    public IReadOnlyList<ClassARecipe> ClassAFor(BiomeKind biome, string levelId) {
      if (_classAByBiomeLevel.TryGetValue((biome, levelId), out var list)) return list;
      return Array.Empty<ClassARecipe>();
    }

    /// <summary>All Class A recipes registered against
    /// <paramref name="biome"/>, across every level. Linear scan;
    /// for the handler hot path use <see cref="ClassAFor"/>
    /// keyed by (biome, level) instead.</summary>
    public IEnumerable<ClassARecipe> ClassAForBiome(BiomeKind biome) {
      foreach (var recipe in _classA) {
        if (recipe.Biome == biome) yield return recipe;
      }
    }

    /// <summary>All registered Class B recipes, in registration
    /// order.</summary>
    public IReadOnlyList<ClassBRecipe> AllClassB => _classB;

    /// <summary>Class B recipes registered against the given
    /// <paramref name="biome"/> at the given <paramref name="levelId"/>.
    /// Returns an empty list when no recipes target the bucket.</summary>
    public IReadOnlyList<ClassBRecipe> ClassBFor(BiomeKind biome, string levelId) {
      if (_classBByBiomeLevel.TryGetValue((biome, levelId), out var list)) return list;
      return Array.Empty<ClassBRecipe>();
    }

    /// <summary>All Class B recipes registered against
    /// <paramref name="biome"/>, across every level. Linear scan
    /// suitable for dev tools.</summary>
    public IEnumerable<ClassBRecipe> ClassBForBiome(BiomeKind biome) {
      foreach (var recipe in _classB) {
        if (recipe.Biome == biome) yield return recipe;
      }
    }

    /// <summary>All registered Class C recipes, in registration
    /// order.</summary>
    public IReadOnlyList<ClassCRecipe> AllClassC => _classC;

    /// <summary>Class C recipes registered against the given
    /// <paramref name="biome"/> at the given <paramref name="levelId"/>.
    /// Returns an empty list when no recipes target the bucket.</summary>
    public IReadOnlyList<ClassCRecipe> ClassCFor(BiomeKind biome, string levelId) {
      if (_classCByBiomeLevel.TryGetValue((biome, levelId), out var list)) return list;
      return Array.Empty<ClassCRecipe>();
    }

    /// <summary>All Class C recipes registered against
    /// <paramref name="biome"/>, across every level. Linear scan
    /// suitable for dev tools.</summary>
    public IEnumerable<ClassCRecipe> ClassCForBiome(BiomeKind biome) {
      foreach (var recipe in _classC) {
        if (recipe.Biome == biome) yield return recipe;
      }
    }

    /// <summary>All registered Class D recipes, in registration
    /// order.</summary>
    public IReadOnlyList<ClassDRecipe> AllClassD => _classD;

    /// <summary>Class D recipes registered against the given
    /// <paramref name="biome"/> at the given <paramref name="levelId"/>.
    /// Returns an empty list when no recipes target the bucket.</summary>
    public IReadOnlyList<ClassDRecipe> ClassDFor(BiomeKind biome, string levelId) {
      if (_classDByBiomeLevel.TryGetValue((biome, levelId), out var list)) return list;
      return Array.Empty<ClassDRecipe>();
    }

    /// <summary>All Class D recipes registered against
    /// <paramref name="biome"/>, across every level. Linear scan
    /// suitable for dev tools.</summary>
    public IEnumerable<ClassDRecipe> ClassDForBiome(BiomeKind biome) {
      foreach (var recipe in _classD) {
        if (recipe.Biome == biome) yield return recipe;
      }
    }

    /// <summary>All registered Class E recipes (fauna), in
    /// registration order.</summary>
    public IReadOnlyList<ClassERecipe> AllClassE => _classE;

    /// <summary>Class E recipes registered against the given
    /// <paramref name="biome"/> at the given <paramref name="levelId"/>.
    /// Returns an empty list when no recipes target the bucket.</summary>
    public IReadOnlyList<ClassERecipe> ClassEFor(BiomeKind biome, string levelId) {
      if (_classEByBiomeLevel.TryGetValue((biome, levelId), out var list)) return list;
      return Array.Empty<ClassERecipe>();
    }

    /// <summary>All Class E recipes registered against
    /// <paramref name="biome"/>, across every level. Used by the
    /// fauna dev placement tool to pick a species by chunk biome
    /// without regard to the chunk's current maturity / level.</summary>
    public IEnumerable<ClassERecipe> ClassEForBiome(BiomeKind biome) {
      foreach (var recipe in _classE) {
        if (recipe.Biome == biome) yield return recipe;
      }
    }

    /// <summary>Yield each blueprint name an entry expands to:
    /// every name in <c>BlueprintNames</c>, then <c>BlueprintName</c>
    /// if non-empty. Empty / whitespace-only names are skipped with a
    /// warning so a stray entry doesn't silently register a no-op
    /// recipe.</summary>
    private static IEnumerable<string> EnumerateBlueprintNames(RecipeEntry entry, string sourceBookName) {
      foreach (var name in entry.BlueprintNames) {
        if (string.IsNullOrWhiteSpace(name)) {
          KeystoneLog.Warn(
              $"[Keystone] FlourishCatalog: empty entry in BlueprintNames in book " +
              $"'{sourceBookName}' (Class={entry.Class}, Biome={entry.Biome}, " +
              $"Level={entry.Level}). Skipped.");
          continue;
        }
        yield return name;
      }
      if (!string.IsNullOrWhiteSpace(entry.BlueprintName)) {
        yield return entry.BlueprintName;
      }
    }

    /// <summary>Default pick weight when the entry omits <c>Weight</c>
    /// (or the deserialiser leaves it at 0). 1.0 = uniform with peers.</summary>
    private const float DefaultWeight = 1.0f;

    private static float NormaliseWeight(float value) {
      if (value <= 0f) return DefaultWeight;
      return value;
    }

    private void AddRecipeA(ClassARecipe recipe) {
      _classA.Add(recipe);
      var key = (recipe.Biome, recipe.LevelId);
      if (!_classAByBiomeLevel.TryGetValue(key, out var list)) {
        list = new List<ClassARecipe>();
        _classAByBiomeLevel[key] = list;
      }
      list.Add(recipe);
    }

    private void AddRecipeB(ClassBRecipe recipe) {
      _classB.Add(recipe);
      var key = (recipe.Biome, recipe.LevelId);
      if (!_classBByBiomeLevel.TryGetValue(key, out var list)) {
        list = new List<ClassBRecipe>();
        _classBByBiomeLevel[key] = list;
      }
      list.Add(recipe);
    }

    private void AddRecipeC(ClassCRecipe recipe) {
      _classC.Add(recipe);
      var key = (recipe.Biome, recipe.LevelId);
      if (!_classCByBiomeLevel.TryGetValue(key, out var list)) {
        list = new List<ClassCRecipe>();
        _classCByBiomeLevel[key] = list;
      }
      list.Add(recipe);
    }

    private void AddRecipeD(ClassDRecipe recipe) {
      _classD.Add(recipe);
      var key = (recipe.Biome, recipe.LevelId);
      if (!_classDByBiomeLevel.TryGetValue(key, out var list)) {
        list = new List<ClassDRecipe>();
        _classDByBiomeLevel[key] = list;
      }
      list.Add(recipe);
    }

    private void AddRecipeE(ClassERecipe recipe) {
      _classE.Add(recipe);
      var key = (recipe.Biome, recipe.LevelId);
      if (!_classEByBiomeLevel.TryGetValue(key, out var list)) {
        list = new List<ClassERecipe>();
        _classEByBiomeLevel[key] = list;
      }
      list.Add(recipe);
    }

  }

}
