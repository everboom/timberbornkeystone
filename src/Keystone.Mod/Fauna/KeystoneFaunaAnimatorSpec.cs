using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Marker spec on fauna blueprints (e.g. <c>KeystoneDeer</c>) that
  /// want their embedded <c>IAnimator</c> driven by Keystone code.
  /// Attaches the <see cref="KeystoneFaunaAnimator"/> component at
  /// blueprint-to-prefab time via the decorator binding in
  /// <c>KeystoneTemplateModuleProvider</c>.
  ///
  /// <para>Carries per-species playback-rate multipliers so each
  /// fauna blueprint can tune its gait timing independently of its
  /// world-movement speed (which lives on
  /// <see cref="KeystoneFaunaAgentSpec"/>). Both multipliers default
  /// to <c>0.8</c> — the deer-paced baseline used before the per-spec
  /// override was introduced. Set to <c>1.0</c> for native clip
  /// speed; larger or smaller values brisken or slow the visible
  /// animation without affecting how far the fauna travels per
  /// second.</para>
  /// </summary>
  public record KeystoneFaunaAnimatorSpec : ComponentSpec {

    /// <summary>Playback-rate multiplier on the clip's native speed
    /// when the agent is in the Walking state. Independent of the
    /// agent's world-movement speed — tune visual gait without
    /// affecting travel distance per second.</summary>
    [Serialize] public float WalkAnimationMultiplier { get; init; } = 0.8f;

    /// <summary>Playback-rate multiplier on the clip's native speed
    /// when the agent is in the Idle state (eating, looking around,
    /// head-low poses). Same scale as
    /// <see cref="WalkAnimationMultiplier"/>.</summary>
    [Serialize] public float IdleAnimationMultiplier { get; init; } = 0.8f;

  }

}
