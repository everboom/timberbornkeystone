using System;
using System.Collections.Generic;
using Keystone.Core.Ecology.Fields;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Validates an <see cref="AttritionEntryInput"/> and, on success,
  /// produces an <see cref="AttritionRecipe"/>. Splits the parse /
  /// validation / clamp logic from the Mod-side FlourishCatalog's
  /// I/O (<c>ISpecService</c> walk + warning sink) so the rules
  /// — biome name parse, action vocabulary, classes/vanilla
  /// targeting, probability clamps, ScaleBy ramp validation, habitat
  /// filter — are testable without a Timberborn host.
  ///
  /// <para>Pure: all rejected inputs go through the
  /// <paramref name="warn"/> callback (the Mod-side caller routes that
  /// to <c>UDebug.LogWarning</c>; tests route it to a list). The
  /// parser does not throw.</para>
  /// </summary>
  public static class AttritionRecipeParser {

    /// <summary>Recognised habitat tags today. Land/aquatic detection
    /// is parked until we wire <c>FloodableNaturalResourceSpec.MinWaterHeight</c>
    /// inspection into the handler.</summary>
    public static readonly IReadOnlyList<string> KnownHabitats = new[] { "Dry" };

    /// <summary>Class strings recognised by the attrition system today.
    /// Class A passes parsing but is skipped at handler execution time
    /// pending its own design pass; see the spec docstring on
    /// <c>AttritionEntry.Classes</c>.</summary>
    public static readonly IReadOnlyList<string> KnownClasses = new[] { "A", "B", "C" };

    /// <summary>Parse and validate <paramref name="entry"/>. Returns
    /// <c>true</c> with <paramref name="recipe"/> filled on success.
    /// On any validation failure that aborts the parse, returns
    /// <c>false</c> with one <paramref name="warn"/> call describing
    /// the failure. Non-fatal issues (probability clamps, unknown
    /// habitats / classes dropped from a list) still produce a warning
    /// but the parse continues.
    ///
    /// <para><paramref name="source"/> labels which book the entry
    /// came from in warning messages.</para></summary>
    public static bool TryParse(
        AttritionEntryInput entry,
        string source,
        Action<string> warn,
        out AttritionRecipe recipe) {
      recipe = null!;

      if (string.IsNullOrEmpty(entry.Level)) {
        warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' has " +
             "empty Level. Skipped.");
        return false;
      }
      if (!Enum.TryParse<BiomeKind>(entry.Biome, ignoreCase: true, out var biome)) {
        warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' has " +
             $"Biome='{entry.Biome}' which is not a known BiomeKind. Skipped.");
        return false;
      }
      if (!TryParseAction(entry.Action, out var action)) {
        warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
             $"(Biome={biome}, Level={entry.Level}) has Action='{entry.Action}' " +
             "which is not 'Kill' or 'Destroy'. Skipped.");
        return false;
      }

      var classesEmpty = IsNullOrEmpty(entry.Classes);
      var vanillaEmpty = IsNullOrEmpty(entry.VanillaSpecies);
      if (classesEmpty && vanillaEmpty) {
        warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
             $"(Biome={biome}, Level={entry.Level}) has empty Classes AND empty " +
             "VanillaSpecies — nothing to target. Skipped.");
        return false;
      }
      var classes = classesEmpty
          ? (IReadOnlyList<string>)Array.Empty<string>()
          : NormaliseClasses(entry.Classes, source, biome, entry.Level, warn);
      if (!classesEmpty && classes.Count == 0 && vanillaEmpty) {
        warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
             $"(Biome={biome}, Level={entry.Level}) had no recognised Classes after " +
             "validation and no VanillaSpecies fallback. Skipped.");
        return false;
      }
      var vanillaSpecies = vanillaEmpty
          ? (IReadOnlyList<string>)Array.Empty<string>()
          : entry.VanillaSpecies;

      var probability = entry.Probability;
      if (probability < 0f || probability > 1f) {
        var clamped = probability < 0f ? 0f : 1f;
        warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
             $"(Biome={biome}, Level={entry.Level}) has Probability={probability} " +
             $"outside [0, 1]. Clamped to {clamped}.");
        probability = clamped;
      }

      EcologyChannel? scaleBy = null;
      var scaleMin = 0f;
      var scaleMax = 0f;
      var probabilityAtMin = 0f;
      if (!string.IsNullOrEmpty(entry.ScaleBy)) {
        if (!Enum.TryParse<EcologyChannel>(entry.ScaleBy, ignoreCase: true, out var channel)) {
          warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
               $"(Biome={biome}, Level={entry.Level}) has ScaleBy='{entry.ScaleBy}' " +
               "which is not a known EcologyChannel. Skipped.");
          return false;
        }
        if (!(entry.ScaleMax > entry.ScaleMin)) {
          warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
               $"(Biome={biome}, Level={entry.Level}) has ScaleMax={entry.ScaleMax} " +
               $"<= ScaleMin={entry.ScaleMin}. Skipped.");
          return false;
        }
        probabilityAtMin = entry.ProbabilityAtMin;
        if (probabilityAtMin < 0f || probabilityAtMin > 1f) {
          var clamped = probabilityAtMin < 0f ? 0f : 1f;
          warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
               $"(Biome={biome}, Level={entry.Level}) has ProbabilityAtMin=" +
               $"{probabilityAtMin} outside [0, 1]. Clamped to {clamped}.");
          probabilityAtMin = clamped;
        }
        scaleBy = channel;
        scaleMin = entry.ScaleMin;
        scaleMax = entry.ScaleMax;
      }

      var excludeHabitats = NormaliseHabitats(
          entry.ExcludeHabitats, "ExcludeHabitats", source, biome, entry.Level, warn);
      var includeHabitats = NormaliseHabitats(
          entry.IncludeHabitats, "IncludeHabitats", source, biome, entry.Level, warn);

      recipe = new AttritionRecipe(
          Biome: biome,
          LevelId: entry.Level,
          Action: action,
          TargetClasses: classes,
          Probability: probability,
          Filter: entry.Filter ?? "",
          ScaleBy: scaleBy,
          ScaleMin: scaleMin,
          ScaleMax: scaleMax,
          ProbabilityAtMin: probabilityAtMin,
          ExcludeHabitats: excludeHabitats,
          IncludeHabitats: includeHabitats,
          VanillaSpecies: vanillaSpecies);
      return true;
    }

    /// <summary>Filter a habitat-tag list (Include or Exclude) against
    /// the known habitat vocabulary; warn-and-drop unknowns. Empty
    /// input (default for entries that don't author the field) passes
    /// through unchanged. <paramref name="listLabel"/> appears in the
    /// warning text so authors can tell which list a stray tag came
    /// from ("ExcludeHabitats" vs "IncludeHabitats").</summary>
    public static IReadOnlyList<string> NormaliseHabitats(
        IReadOnlyList<string> raw, string listLabel,
        string source, BiomeKind biome, string levelId,
        Action<string> warn) {
      if (IsNullOrEmpty(raw)) return Array.Empty<string>();
      var result = new List<string>(raw.Count);
      for (var i = 0; i < raw.Count; i++) {
        var h = raw[i];
        var known = false;
        for (var j = 0; j < KnownHabitats.Count; j++) {
          if (KnownHabitats[j] == h) { known = true; break; }
        }
        if (known) {
          if (!result.Contains(h)) result.Add(h);
        } else {
          warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
               $"(Biome={biome}, Level={levelId}) references {listLabel}=" +
               $"'{h}' which is not currently recognised. Known: " +
               $"[{string.Join(", ", KnownHabitats)}]. Entry dropped from the list.");
        }
      }
      return result;
    }

    /// <summary>Filter an attrition entry's <c>Classes</c> to the
    /// recognised set in <see cref="KnownClasses"/>. Unknown values
    /// get a single warning and are dropped. Class A passes parsing
    /// but is skipped at handler execution time pending its own design
    /// pass.</summary>
    public static IReadOnlyList<string> NormaliseClasses(
        IReadOnlyList<string> raw, string source, BiomeKind biome, string levelId,
        Action<string> warn) {
      var result = new List<string>(raw.Count);
      for (var i = 0; i < raw.Count; i++) {
        var c = raw[i];
        var known = false;
        for (var j = 0; j < KnownClasses.Count; j++) {
          if (KnownClasses[j] == c) { known = true; break; }
        }
        if (known) {
          if (!result.Contains(c)) result.Add(c);
        } else {
          warn($"[Keystone] AttritionRecipeParser: attrition in book '{source}' " +
               $"(Biome={biome}, Level={levelId}) references Class='{c}' which is not " +
               $"one of [{string.Join(", ", KnownClasses)}]. Skipped.");
        }
      }
      return result;
    }

    /// <summary>Parse the <c>Action</c> field of an attrition entry.
    /// <c>"Kill"</c> and <c>"Destroy"</c> are recognised; anything else
    /// returns <c>false</c>.</summary>
    public static bool TryParseAction(string raw, out AttritionAction action) {
      switch (raw) {
        case "Kill":    action = AttritionAction.Kill;    return true;
        case "Destroy": action = AttritionAction.Destroy; return true;
        default:        action = default;                 return false;
      }
    }

    private static bool IsNullOrEmpty(IReadOnlyList<string> list)
        => list == null || list.Count == 0;

  }

}
