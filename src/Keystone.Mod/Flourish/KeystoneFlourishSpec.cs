using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Flourish {

  /// <summary>
  /// Marker spec on every Keystone-authored ambient flourish blueprint.
  /// Empty for now; future fields could include category, biome
  /// affinity, recipe id, etc. once we have a real flourish-recipe API.
  ///
  /// <para><b>Used by</b> the Harmony patches in
  /// <c>HarmonyPatches/</c>: rather than match a blueprint-name prefix
  /// to identify "ambient" entities, we'll switch to checking
  /// <c>entity.HasComponent&lt;KeystoneFlourish&gt;()</c>. Type-safe
  /// contract instead of string-based one. (Migration pending; the
  /// existing prefix check still applies for the
  /// <c>StrippedEntityProbe</c>'s code-built blueprints.)</para>
  /// </summary>
  public record KeystoneFlourishSpec : ComponentSpec;

}
