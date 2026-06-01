using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Flourish {

  /// <summary>
  /// Marker spec on inanimate cluster blueprints (Class C rock formations
  /// and similar) that want their child rock meshes to dynamically
  /// re-tint based on the current biome at the entity's tile.
  ///
  /// <para><b>Pairing.</b> Attaches the <see cref="KeystoneRockTint"/>
  /// component to the entity at blueprint-to-prefab time via the
  /// decorator binding in <c>KeystoneTemplateModuleProvider</c>. The
  /// component extends <c>TickableComponent</c> so Timberborn's tick
  /// system drives its per-tile material re-paint directly -- no
  /// central service or registry.</para>
  ///
  /// <para><b>When to use.</b> Apply to any Keystone-original blueprint
  /// whose child meshes reference Keystone-original materials in the
  /// <c>KeystoneRock_*</c> / <c>KeystonePathRocks_*</c> families. The
  /// service auto-detects the base material name (strips
  /// <c>_Dry</c>/<c>_Mossy</c> suffixes if present) and applies the
  /// biome-appropriate variant. Don't apply to animate blueprints --
  /// they get baked variants per biome at authoring time (the cluster
  /// blueprint references a specific tinted <c>.timbermesh</c>), not
  /// runtime swapping.</para>
  /// </summary>
  public record KeystoneRockTintSpec : ComponentSpec;

}
