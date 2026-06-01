using System.Collections.Generic;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Plain-data shape consumed by <see cref="AttritionRecipeParser.TryParse"/>.
  /// Mirrors the Mod-side <c>AttritionEntry</c> spec's fields without the
  /// <c>[Serialize]</c> attribute coupling (which would require a
  /// Timberborn reference). The Mod-side catalog projects each spec entry
  /// into this shape before handing it to the Core parser. Collection
  /// fields use <see cref="IReadOnlyList{T}"/> so the spec's
  /// <c>ImmutableArray&lt;string&gt;</c> passes through directly without
  /// dragging <c>System.Collections.Immutable</c> into Core.
  ///
  /// <para>Field semantics match the spec docstrings on
  /// <c>Keystone.Mod.Recipes.AttritionEntry</c>; see that type for the
  /// authoring-facing description.</para>
  /// </summary>
  public readonly record struct AttritionEntryInput(
      string Biome,
      string Level,
      string Action,
      IReadOnlyList<string> Classes,
      IReadOnlyList<string> VanillaSpecies,
      float Probability,
      string Filter,
      string ScaleBy,
      float ScaleMin,
      float ScaleMax,
      float ProbabilityAtMin,
      IReadOnlyList<string> ExcludeHabitats,
      IReadOnlyList<string> IncludeHabitats);

}
