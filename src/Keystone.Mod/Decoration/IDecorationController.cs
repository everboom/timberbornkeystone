using Keystone.Core.Ports;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// Per-decoration reactivity contract. Implementations read ecology
  /// services through Core ports and update the decoration's visual
  /// state. <see cref="KeystoneDecorationRegistry"/> calls
  /// <see cref="Tick"/> on every registered controller on each
  /// throttled sweep; controllers are responsible for being cheap.
  ///
  /// <para>Decorations that don't need reactivity simply don't have
  /// an <see cref="IDecorationController"/> -- the registry skips
  /// them. Reactivity is opt-in per decoration, not a class
  /// property.</para>
  /// </summary>
  public interface IDecorationController {

    /// <summary>
    /// Re-evaluate the decoration's visuals against current ecology
    /// state.
    /// </summary>
    /// <param name="decoration">Owner decoration -- has the GameObject
    /// to mutate and the surface coord to query at.</param>
    /// <param name="moisture">Soil moisture port.</param>
    /// <param name="contamination">Soil contamination port.</param>
    /// <param name="water">Surface water port.</param>
    void Tick(
        KeystoneDecoration decoration,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IWaterQuery water);

  }

}
