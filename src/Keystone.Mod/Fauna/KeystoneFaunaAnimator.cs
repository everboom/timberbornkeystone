using Keystone.Mod.Diagnostics;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.TimbermeshAnimations;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Per-entity wrapper around the prefab's embedded
  /// <see cref="IAnimator"/> for a fauna asset. Exposes a single
  /// imperative <see cref="PlayClip"/> entry point; the agent
  /// component drives clip selection from state transitions.
  ///
  /// <para><b>Lazy IAnimator lookup.</b> The handle is resolved on
  /// first <see cref="PlayClip"/> call (and on
  /// <see cref="InitializeEntity"/>, whichever fires first). Two
  /// reasons: the order of component <c>InitializeEntity</c> on a
  /// shared GameObject isn't guaranteed by the decorator framework,
  /// so an early caller (e.g. the agent's
  /// <see cref="IInitializableEntity.InitializeEntity"/>) might
  /// arrive before this component has set up; and lazy init means
  /// PlayClip is safe at any time without ordering constraints.</para>
  ///
  /// <para><b>Per-species playback speeds.</b> Walk and Idle each
  /// have their own playback-rate multiplier on
  /// <see cref="KeystoneFaunaAnimatorSpec"/>; the agent reads
  /// <see cref="WalkAnimationMultiplier"/> /
  /// <see cref="IdleAnimationMultiplier"/> and passes the right one
  /// to <see cref="PlayClip"/> on each state transition. The
  /// animator itself stays clip-agnostic — callers choose the
  /// speed.</para>
  /// </summary>
  public sealed class KeystoneFaunaAnimator : BaseComponent,
                                              IAwakableComponent,
                                              IInitializableEntity {

    private KeystoneFaunaAnimatorSpec _spec = null!;
    private IAnimator? _animator;
    private bool _resolveAttempted;

    public void Awake() {
      _spec = GetComponent<KeystoneFaunaAnimatorSpec>();
    }

    public void InitializeEntity() {
      EnsureAnimator();
    }

    /// <summary>Per-species playback-rate multiplier for Walk clips,
    /// read from the spec at Awake.</summary>
    public float WalkAnimationMultiplier => _spec.WalkAnimationMultiplier;

    /// <summary>Per-species playback-rate multiplier for Idle clips,
    /// read from the spec at Awake.</summary>
    public float IdleAnimationMultiplier => _spec.IdleAnimationMultiplier;

    /// <summary>Play <paramref name="clipName"/>, looped or one-shot
    /// per <paramref name="loop"/>, at the given playback-rate
    /// <paramref name="speed"/> (1.0 = clip's native speed). The
    /// caller chooses the speed per clip — typically
    /// <see cref="WalkAnimationMultiplier"/> for Walk and
    /// <see cref="IdleAnimationMultiplier"/> for an Idle variant.
    /// Silently noops if the underlying <see cref="IAnimator"/> isn't
    /// found (no VAT data in the .timbermesh) or if the clip name
    /// isn't in the animator's catalog (logged but not thrown).</summary>
    public void PlayClip(string clipName, bool loop, float speed) {
      EnsureAnimator();
      if (_animator == null) return;
      try {
        _animator.Speed = speed;
        _animator.Play(clipName, loop);
        KeystoneLog.Verbose(
            $"[Keystone] KeystoneFaunaAnimator on '{Name}': PlayClip('{clipName}', loop={loop}, speed={speed:F2}) " +
            $"(length={_animator.AnimationLength:F2}s).");
      } catch (System.Exception ex) {
        UDebug.LogWarning(
            $"[Keystone] KeystoneFaunaAnimator on '{Name}': PlayClip('{clipName}') threw " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
    }

    /// <summary>Length of the currently-playing clip in seconds. Used
    /// by the agent to time state transitions in real time without
    /// coupling to the game-tick rate. Returns 0 if no clip is set
    /// (no animator, or PlayClip never called).</summary>
    public float CurrentClipLength => _animator?.AnimationLength ?? 0f;

    /// <summary>True iff <see cref="EnsureAnimator"/> ran and found an
    /// <see cref="IAnimator"/> in the prefab's children. Surfaced for
    /// the self-test battery so it can verify that every fauna
    /// blueprint with this spec has VAT data — the "no IAnimator
    /// found" branch in <see cref="EnsureAnimator"/> is a silent
    /// runtime failure (warning log only, fauna T-pose) and a self-
    /// test can spot it on demand.</summary>
    public bool AnimatorResolved => _animator != null;

    private void EnsureAnimator() {
      if (_resolveAttempted) return;
      _resolveAttempted = true;
      _animator = GameObject.GetComponentInChildren<IAnimator>(includeInactive: true);
      if (_animator == null) {
        UDebug.LogWarning(
            $"[Keystone] KeystoneFaunaAnimator on '{Name}': no IAnimator found in " +
            "children. Did the .timbermesh export with 'Use vertex animations' enabled?");
        return;
      }
      _animator.Enabled = true;
    }

  }

}
