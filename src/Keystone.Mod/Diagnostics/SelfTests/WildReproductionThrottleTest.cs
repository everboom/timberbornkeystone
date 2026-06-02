using System;
using System.Globalization;
using Keystone.Mod.HarmonyPatches;
using Keystone.Mod.Settings;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Verifies the wild-reproduction throttle (the "Base Game" →
  /// wild-plant-reproduction multiplier) is wired end to end: the
  /// menu-only setting reaches the static the Harmony patch reads, and
  /// the patch is actually firing.
  ///
  /// <para><b>What it catches that the build-time guards can't.</b>
  /// <list type="number">
  ///   <item>The <see cref="NaturalReproductionRateAccessor"/> ctor-publish
  ///         silently not running, so
  ///         <see cref="ReproducibleReproductionChancePatch"/> reads its
  ///         <c>1f</c> (vanilla) fallback instead of the player's value —
  ///         caught by <see cref="NaturalReproductionRateAccessor.Published"/>,
  ///         which holds even when the player's multiplier is 100% (where
  ///         the fallback and the real value coincide).</item>
  ///   <item>The patch resolving its target but silently no-op'ing —
  ///         caught by
  ///         <see cref="ReproducibleReproductionChancePatch.HasRun"/>. The
  ///         startup <c>ExpectedPatchedMethodCount</c> assertion already
  ///         covers "patch failed to attach"; this covers "attached but
  ///         never ran".</item>
  /// </list></para>
  ///
  /// <para><b>What it deliberately does not do.</b> It doesn't measure
  /// actual spread rate (slow and statistical) and doesn't reach into
  /// vanilla's frozen per-id reproduction key (private to
  /// <c>NaturalResourceReproducer</c>). The behavioural confirmation is a
  /// manual check: set the slider to 0%, load, fast-forward a few
  /// game-days with the vanilla <c>PotentialSpotsToggler</c> dev overlay
  /// on, and confirm no new wild resources appear.</para>
  /// </summary>
  internal sealed class WildReproductionThrottleTest : IKeystoneSelfTest {

    private readonly KeystoneBaseGameSettings _settings;

    public WildReproductionThrottleTest(KeystoneBaseGameSettings settings) {
      _settings = settings;
    }

    /// <inheritdoc />
    public string Name => "Wild reproduction throttle";

    /// <inheritdoc />
    public string Category => "Wiring";

    /// <inheritdoc />
    public SelfTestResult Run() {
      var expected = _settings.WildReproductionMultiplier;
      var percent = (expected * 100f).ToString("0.#", CultureInfo.InvariantCulture);

      // (1) The accessor ctor published its settings reference. Checked
      // before the value comparison because, at exactly 100%, the
      // accessor's 1f fallback equals the real value and the comparison
      // below couldn't tell an unpublished accessor apart.
      if (!NaturalReproductionRateAccessor.Published) {
        return SelfTestResult.Fail(
            "NaturalReproductionRateAccessor never published its settings " +
            "reference — the reproduction patch is reading its 1f (vanilla) " +
            "fallback, so the player's throttle has no effect.",
            "The accessor is an ILoadableSingleton whose ctor publishes the " +
            "static; expected it to construct during Game-scope startup.");
      }

      // (2) The static the patch reads matches the DI-resolved settings
      // owner. Normally tautological (same AsSingleton instance); a
      // mismatch means a scope split — accessor bound in a different
      // container than the one handing this test its settings owner.
      var actual = NaturalReproductionRateAccessor.Multiplier;
      if (Math.Abs(actual - expected) > 0.0001f) {
        return SelfTestResult.Fail(
            $"Multiplier mismatch: accessor reports {actual.ToString("0.###", CultureInfo.InvariantCulture)}, " +
            $"settings owner says {expected.ToString("0.###", CultureInfo.InvariantCulture)}.",
            "The static the patch reads has drifted from the settings owner " +
            "the DI graph resolves — likely a scope mismatch or stale publish.");
      }

      // (3) The patch has actually fired. False can simply mean no wild
      // plant has loaded/marked yet this session, so this is a Skip (not
      // a Fail) with guidance — attach failure is already a startup-time
      // assertion.
      if (!ReproducibleReproductionChancePatch.HasRun) {
        return SelfTestResult.Skipped(
            $"Throttle wired at {percent}% of vanilla, but the reproduction-" +
            "chance patch hasn't fired yet — no wild plant has marked its " +
            "reproduction spots this session. Load a map with wild flora (or " +
            "fast-forward) and re-run.");
      }

      return SelfTestResult.Pass(
          $"Wild reproduction throttled to {percent}% of vanilla " +
          $"({expected.ToString("0.###", CultureInfo.InvariantCulture)}×); patch confirmed firing.");
    }

  }

}
