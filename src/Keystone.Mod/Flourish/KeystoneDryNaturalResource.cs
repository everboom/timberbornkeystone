using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;

namespace Keystone.Mod.Flourish {

  /// <summary>
  /// Tag component attached to any blueprint carrying
  /// <see cref="KeystoneDryNaturalResourceSpec"/>. Pure marker today
  /// — exists so consumers can ask
  /// <c>entity.GetComponent&lt;KeystoneDryNaturalResource&gt;() != null</c>
  /// in O(1) without inspecting the entity's blueprint specs.
  ///
  /// <para>See <see cref="KeystoneDryNaturalResourceSpec"/> for the
  /// habitat semantics and forward-compatibility notes.</para>
  /// </summary>
  public sealed class KeystoneDryNaturalResource : BaseComponent {

    /// <summary>True if <paramref name="bo"/> is tagged with this
    /// dry-habitat marker. Companion to
    /// <see cref="KeystoneFlourish.IsDeadFlourish"/>; the two are
    /// independent (a dry flourish can be alive or dead).</summary>
    public static bool IsDry(BlockObject bo) {
      if (bo == null) return false;
      return bo.GetComponent<KeystoneDryNaturalResource>() != null;
    }

  }

}
