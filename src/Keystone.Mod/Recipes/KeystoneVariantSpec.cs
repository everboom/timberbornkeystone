using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Marker spec on every Keystone-spawn-eligible blueprint. Triggers
  /// attachment of <see cref="KeystoneVariant"/> at instantiation.
  /// The variant component carries the per-entity content class
  /// designation (<c>"A"</c> / <c>"B"</c> / <c>"C"</c>) set at spawn
  /// time by the handler that placed the entity.
  ///
  /// <para><b>Why a per-entity persistent class.</b> Recipes are
  /// decoupled from blueprints: the same blueprint can be referenced
  /// by recipes with different classes. Selection / demolish
  /// suppression must follow the recipe's class, not the blueprint's
  /// asset, and it must survive save/load. A spec-driven persistent
  /// component is the only mechanism Timberborn's entity persistence
  /// reattaches on load. Runtime-added components (via
  /// <c>GameObject.AddComponent</c>) are lost on reload.</para>
  /// </summary>
  public record KeystoneVariantSpec : ComponentSpec;

}
