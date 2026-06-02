using Keystone.Mod.Settings;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Static accessor for the player's vanilla-reproduction multiplier,
  /// readable from the Harmony patch
  /// (<see cref="ReproducibleReproductionChancePatch"/>) that can't take
  /// constructor-injected dependencies. Mirrors the
  /// <see cref="FactionIdAccessor"/> pattern: Bindito constructs and
  /// injects <see cref="KeystoneBaseGameSettings"/> normally, the
  /// ctor publishes the reference statically, and the patch reads
  /// <see cref="Multiplier"/> at invocation time.
  ///
  /// <para><b>Why the ctor publishes and why <see cref="ILoadableSingleton"/>.</b>
  /// The reproduction-chance getter the patch decorates first fires when
  /// each <c>Reproducible</c> marks its potential spots — at entity
  /// <c>PostInitializeEntity</c>, during save/map load. Bindito eagerly
  /// resolves <see cref="ILoadableSingleton"/> instances during Game
  /// scope startup (so it can call <see cref="Load"/>), and that
  /// resolution runs the ctor — which injects the settings owner,
  /// forcing it to construct and hydrate its persisted value before any
  /// entity loads. So the multiplier is correct at the first mark,
  /// before vanilla's <c>NaturalResourceReproducer</c> freezes the
  /// per-id chance for the session.</para>
  ///
  /// <para>If the settings owner is somehow not yet published (a
  /// genuinely-too-early read that shouldn't occur in-game),
  /// <see cref="Multiplier"/> returns <c>1f</c> — vanilla rate — so the
  /// fallback is "no Keystone effect" rather than a silent halt.</para>
  /// </summary>
  public sealed class NaturalReproductionRateAccessor : ILoadableSingleton {

    /// <summary>Static reference to the menu-only "Base Game" settings
    /// owner. Populated by the ctor; null only before Bindito constructs
    /// this singleton (i.e. before the Game scope is wired).</summary>
    private static KeystoneBaseGameSettings? _settings;

    /// <summary>Vanilla-reproduction multiplier (0.0–1.0) at read time,
    /// or <c>1f</c> (vanilla rate) if the settings owner has not been
    /// published yet. Read by the reproduction-chance patch.</summary>
    public static float Multiplier => _settings?.WildReproductionMultiplier ?? 1f;

    /// <summary>True once the ctor has published the settings reference.
    /// Lets <c>WildReproductionThrottleTest</c> tell "the patch is using
    /// the player's multiplier" apart from "the patch is silently on the
    /// 1f fallback because this accessor never constructed" — a
    /// distinction <see cref="Multiplier"/> alone can't make when the
    /// player's value happens to be 100%.</summary>
    public static bool Published { get; private set; }

    public NaturalReproductionRateAccessor(KeystoneBaseGameSettings settings) {
      _settings = settings;
      Published = true;
    }

    /// <inheritdoc />
    public void Load() {
      // No-op. The constructor publishes the static; ILoadableSingleton
      // is implemented only so Bindito eagerly resolves and constructs
      // this singleton (and, through injection, the settings owner)
      // during Game scope startup -- before any Reproducible marks spots.
    }

  }

}
