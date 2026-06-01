using Timberborn.BaseComponentSystem;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// No-op behaviour component paired with
  /// <see cref="KeystoneBiomeLevelsSpec"/>. Exists only so the
  /// <c>AddDecorator&lt;TSpec, TComponent&gt;</c> registration
  /// surfaces the spec via <c>ISpecService.GetSpecs</c>. The level
  /// data this spec carries is consumed by
  /// <see cref="BiomeLevelCatalog"/> at PostLoad and pushed into
  /// the Core <c>BiomeLevelTable</c>; per-entity runtime behaviour
  /// is none.
  /// </summary>
  public sealed class KeystoneBiomeLevels : BaseComponent {
  }

}
