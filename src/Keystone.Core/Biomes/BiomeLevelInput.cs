namespace Keystone.Core.Biomes {

  /// <summary>
  /// Plain-data shape consumed by <see cref="BiomeLevelEntryValidator.TryApply"/>.
  /// Mirrors the Mod-side <c>BiomeLevelEntry</c> spec's fields without the
  /// <c>[Serialize]</c> attribute coupling (which would require a
  /// Timberborn reference). The Mod-side catalog projects each spec entry
  /// into this shape before handing it to the Core validator.
  ///
  /// <para>Field semantics match the spec docstrings on
  /// <c>Keystone.Mod.Recipes.BiomeLevelEntry</c>; see that type for the
  /// authoring-facing description. Notably: <see cref="Density"/> uses
  /// <c>-1f</c> as the "unset" sentinel, distinct from explicit
  /// <c>0f</c>.</para>
  /// </summary>
  public readonly record struct BiomeLevelInput(
      string LevelId,
      float LowerMaturity,
      float UpperMaturity,
      float Density,
      bool RunAtStartup,
      string Mode,
      int FaunaCapacityAtSaturation,
      float FaunaMinScore);

}
