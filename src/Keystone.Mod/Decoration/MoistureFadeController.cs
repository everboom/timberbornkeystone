using Keystone.Core.Ports;
using UnityEngine;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// Sample reactive controller: scales the decoration based on per-voxel
  /// soil moisture. Visible at full size when the surface is moist,
  /// shrunk to <see cref="DryScale"/> when it's dry. Crude but obvious
  /// for prototype validation; richer responses (mesh swap, material
  /// tint, fade) come later when we know the API supports them.
  ///
  /// <para>Reads only <c>IMoistureQuery.IsMoistAt</c> -- contamination
  /// and water are ignored. Real Class-B controllers will compose
  /// multiple inputs.</para>
  /// </summary>
  public sealed class MoistureFadeController : IDecorationController {

    /// <summary>Scale applied when the surface is dry. Pick something
    /// visibly different from 1 so the reactivity is unambiguous.</summary>
    private static readonly Vector3 DryScale = new(0.4f, 0.4f, 0.4f);

    private static readonly Vector3 MoistScale = Vector3.one;

    /// <inheritdoc />
    public void Tick(
        KeystoneDecoration decoration,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IWaterQuery water) {
      if (decoration.Root == null) return; // destroyed; registry will reap
      var moist = moisture.IsMoistAt(decoration.Surface);
      decoration.Root.transform.localScale = moist ? MoistScale : DryScale;
    }

  }

}
