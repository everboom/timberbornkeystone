using System;
using Keystone.Core.Ports;
using Keystone.Mod.Decoration;
using UnityEngine;

namespace Keystone.Mod.Overgrowth {

  /// <summary>
  /// Decoration controller for overgrowth overlays. Like
  /// <see cref="FloraLifecycleMoistureController"/> it walks the cloned
  /// <c>#Models/Mature/#&lt;state&gt;</c> hierarchy and shows exactly one
  /// variant, but it adds a <b>terminal dead</b> branch: once the owning
  /// <see cref="KeystoneOvergrowth"/> reports dead (via the
  /// <c>isDead</c> delegate), it pins <c>#Dead</c> and stops reacting to
  /// moisture. Until then it toggles <c>#Alive</c> / <c>#Dying</c> by
  /// per-tile soil moisture (the overgrowth's reversible, ecology-driven
  /// health — independent of the host tree's life state).
  /// </summary>
  public sealed class OvergrowthLifecycleController : IDecorationController {

    private readonly Func<bool> _isDead;
    private bool _initialized;
    private GameObject? _matureAlive;
    private GameObject? _matureDying;
    private GameObject? _matureDead;

    /// <param name="isDead">Reads the owning overgrowth's terminal dead
    /// state each tick. Terminal: once true it stays true until the
    /// overgrowth is cleared (and the decoration despawned).</param>
    public OvergrowthLifecycleController(Func<bool> isDead) {
      _isDead = isDead;
    }

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

      if (_isDead()) {
        if (_matureAlive != null) _matureAlive.SetActive(false);
        if (_matureDying != null) _matureDying.SetActive(false);
        if (_matureDead != null) _matureDead.SetActive(true);
        return;
      }

      var moist = moisture.IsMoistAt(decoration.Surface);
      if (_matureAlive != null) _matureAlive.SetActive(moist);
      if (_matureDying != null) _matureDying.SetActive(!moist);
      if (_matureDead != null) _matureDead.SetActive(false);
    }

    private void Initialize(GameObject root) {
      _initialized = true;
      var seedling = root.transform.Find("#Models/Seedling");
      if (seedling != null) seedling.gameObject.SetActive(false);
      _matureAlive = root.transform.Find("#Models/Mature/#Alive")?.gameObject;
      _matureDying = root.transform.Find("#Models/Mature/#Dying")?.gameObject;
      _matureDead = root.transform.Find("#Models/Mature/#Dead")?.gameObject;
    }

  }

}
