using System.Collections.Generic;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Per-entity targeting predicates for
  /// <see cref="AttritionRecipe"/>: does this (classId, vanillaName)
  /// pair match the recipe's <see cref="AttritionRecipe.TargetClasses"/>
  /// or <see cref="AttritionRecipe.VanillaSpecies"/>?
  ///
  /// <para>Class IDs in the recipe's list are
  /// Keystone-stamped content (Class A/B/C). Class D entities don't
  /// carry a stamp — they're identified by their vanilla blueprint
  /// name and matched against the recipe's separate
  /// <c>VanillaSpecies</c> list. This helper keeps that two-track
  /// matching logic on one testable seam.</para>
  /// </summary>
  public static class AttritionTargeting {

    /// <summary>True iff the entity described by
    /// <paramref name="classId"/> +
    /// <paramref name="vanillaBlueprintName"/> is targeted by
    /// <paramref name="recipe"/>. A Class-D entity (<paramref name="classId"/>
    /// = <c>"D"</c>) matches via the recipe's
    /// <see cref="AttritionRecipe.VanillaSpecies"/> list; any other
    /// class id matches via <see cref="AttritionRecipe.TargetClasses"/>.
    /// The two paths are independent — a recipe with
    /// <c>TargetClasses=["B"]</c> doesn't target Class-D entries even
    /// if they're present in the entity scratch.</summary>
    public static bool MatchesTarget(
        AttritionRecipe recipe, string classId, string vanillaBlueprintName) {
      if (recipe == null) return false;
      if (classId == "D") {
        return !string.IsNullOrEmpty(vanillaBlueprintName)
            && Contains(recipe.VanillaSpecies, vanillaBlueprintName);
      }
      if (string.IsNullOrEmpty(classId)) return false;
      return Contains(recipe.TargetClasses, classId);
    }

    private static bool Contains(IReadOnlyList<string> list, string value) {
      for (var i = 0; i < list.Count; i++) {
        if (list[i] == value) return true;
      }
      return false;
    }

  }

}
