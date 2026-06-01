using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Marker spec attached to fauna blueprints (e.g. <c>KeystoneDeer</c>)
  /// to wire the wander-and-idle agent. The decorator binding in
  /// <c>KeystoneTemplateModuleProvider</c> attaches the
  /// <see cref="KeystoneFaunaAgent"/> component to the prefab at
  /// blueprint-to-prefab time, with Bindito constructing the agent
  /// (services injected through its constructor). The instance carries
  /// those service refs forward when the prefab is cloned by
  /// <c>KeystoneDecorationRegistry.Spawn</c>; the placement tool then
  /// calls <see cref="KeystoneFaunaAgent.Configure"/> on the spawned
  /// instance to set its runtime state (region + initial tile).
  ///
  /// <para>Carries the per-species world-movement speed in tiles per
  /// real-time second. Independent of game speed (1× / 3× game-time
  /// multiplier doesn't change how fast the fauna moves on screen)
  /// and independent of the animation playback rate, which lives on
  /// <see cref="KeystoneFaunaAnimatorSpec"/>.</para>
  /// </summary>
  public record KeystoneFaunaAgentSpec : ComponentSpec {

    /// <summary>World-movement speed in tiles per real-time second.
    /// Default <c>1.0</c> — a deliberately clean baseline so per-fauna
    /// overrides read as straight multipliers (e.g. <c>0.65</c> = "65%
    /// of baseline"). Independent of game speed and independent of
    /// the animation multipliers on
    /// <see cref="KeystoneFaunaAnimatorSpec"/>; the two should be
    /// tuned together so foot-slide doesn't read wrong.</summary>
    [Serialize] public float WorldSpeedTilesPerSec { get; init; } = 1.0f;

  }

}
