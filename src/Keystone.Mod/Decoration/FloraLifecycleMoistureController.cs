using Keystone.Core.Ports;
using UnityEngine;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// Reactive controller for Class-B decorations cloned from vanilla
  /// flora prefabs. Walks the standard
  /// <c>#Models / &lt;stage&gt; / #&lt;state&gt;</c> hierarchy on first
  /// tick, disables the Seedling stage entirely (Class-B decorations
  /// always present as adult), and toggles the Mature stage between
  /// <c>#Alive</c> and <c>#Dying</c> based on per-voxel soil moisture.
  ///
  /// <para><b>Why not use the prefab as-is.</b> A normal entity-
  /// system spawn lets <c>Growable</c> +
  /// <c>NaturalResourceLifecycleModel</c> activate exactly one
  /// stage/state combination at a time. Decorations skip the entity
  /// system, so every sub-mesh in the cloned prefab is active
  /// simultaneously -- visually a mess of overlapping seedling +
  /// mature, alive + dried-out variants. This controller does
  /// minimal lifecycle management: pick one Mature variant to be
  /// visible, hide the rest.</para>
  ///
  /// <para><b>Hierarchy assumed.</b> Vanilla natural-resource
  /// convention: <c>#Models/Seedling/#&lt;state&gt;</c> and
  /// <c>#Models/Mature/#&lt;state&gt;</c>. Decorations whose prefabs
  /// don't follow this layout get a partial or empty effect from
  /// this controller -- if no <c>#Alive</c> child resolves, the
  /// visual stays as-is (still overlapping but at least not crashing).
  /// For non-flora decorations use <see cref="MoistureFadeController"/>
  /// or write a controller that fits the prefab.</para>
  /// </summary>
  public sealed class FloraLifecycleMoistureController : IDecorationController {

    private bool _initialized;
    private GameObject? _matureAlive;
    private GameObject? _matureDying;
    private GameObject? _matureDead;

    /// <inheritdoc />
    public void Tick(
        KeystoneDecoration decoration,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IWaterQuery water) {
      if (decoration.Root == null) return; // destroyed; registry will reap
      if (!_initialized) {
        Initialize(decoration.Root);
      }

      var moist = moisture.IsMoistAt(decoration.Surface);
      // Same activation rule as NaturalResourceLifecycleModel.Show:
      // Dead beats Dying beats Alive. We don't track "dead" yet --
      // could later if we add a "permanently dried out" timer.
      if (_matureAlive != null) _matureAlive.SetActive(moist);
      if (_matureDying != null) _matureDying.SetActive(!moist);
      if (_matureDead  != null) _matureDead.SetActive(false);
    }

    private void Initialize(GameObject root) {
      _initialized = true;

      // Hide Seedling outright -- decorations always show adult form.
      var seedling = root.transform.Find("#Models/Seedling");
      if (seedling != null) seedling.gameObject.SetActive(false);

      _matureAlive = root.transform.Find("#Models/Mature/#Alive")?.gameObject;
      _matureDying = root.transform.Find("#Models/Mature/#Dying")?.gameObject;
      _matureDead  = root.transform.Find("#Models/Mature/#Dead")?.gameObject;
    }

  }

}
