using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Keystone.Mod.Settings {

  /// <summary>
  /// Settings that change how the <em>base game</em> behaves — as opposed
  /// to tuning Keystone's own ecology content. Renders as the "Base Game"
  /// section, first in the panel (<see cref="Order"/> = 0).
  ///
  /// <para>A deliberately general bucket: today it holds a single knob
  /// (the wild-reproduction throttle), but it's the home for any future
  /// "Keystone alters a stock Timberborn mechanic" toggle so those don't
  /// each spawn a one-row section.</para>
  ///
  /// <para><b>MainMenu-only by design — and section-wide.</b> Because
  /// <see cref="ModSettingsContext"/> changeability is an owner-level
  /// property (it governs the whole section, not individual rows), every
  /// setting added here is editable from the main menu only. That fits
  /// the current member — the wild-reproduction multiplier is read into
  /// the engine's reproduction bookkeeping at resource mark-time and
  /// frozen for the session, so a mid-game edit couldn't take effect
  /// until the next load anyway (see
  /// <c>ReproducibleReproductionChancePatch</c>). If a future base-game
  /// tweak needs to be changeable in-game, it can't live in this section
  /// as-is — it'd need its own owner with a wider
  /// <see cref="ChangeableOn"/>.</para>
  /// </summary>
  public class KeystoneBaseGameSettings : ModSettingsOwner {

    /// <summary>Multiplier on vanilla natural-resource reproduction
    /// chance, as a percent of the stock rate. 100 = vanilla, 5 =
    /// default (1/20th of vanilla), 0 = wild spread disabled. Read via
    /// <see cref="WildReproductionMultiplier"/> by the reproduction-chance
    /// Harmony patch at resource mark-time.</summary>
    public ModSetting<int> WildReproductionPercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 5, minValue: 0, maxValue: 100,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.BaseGame.WildReproduction")
                .SetLocalizedTooltip("Keystone.Settings.BaseGame.WildReproduction.Tooltip"));

    /// <inheritdoc />
    public override int Order => 0;

    /// <inheritdoc />
    public override string HeaderLocKey => "Keystone.Settings.BaseGame.Header";

    /// <inheritdoc />
    public override ModSettingsContext ChangeableOn => ModSettingsContext.MainMenu;

    /// <inheritdoc />
    protected override string ModId => "SylvanGames.Keystone";

    public KeystoneBaseGameSettings(
        ISettings settings,
        ModSettingsOwnerRegistry modSettingsOwnerRegistry,
        ModRepository modRepository)
        : base(settings, modSettingsOwnerRegistry, modRepository) {}

    /// <summary>Vanilla-reproduction multiplier as a fraction
    /// (0.0–1.0): <see cref="WildReproductionPercent"/> / 100.
    /// 5% → <c>0.05f</c>, 100% → <c>1.0f</c>, 0% → <c>0f</c> (halt).
    ///
    /// <para>The percent is clamped to its declared <c>[0, 100]</c>
    /// range on read — defensive against a stray out-of-range value in
    /// a hand-edited settings file. 0 is preserved as a valid "off"
    /// value, not treated as an unhydrated default: by the time this is
    /// read (resource mark-time, after <c>NaturalReproductionRateAccessor</c>
    /// has been eagerly constructed with this owner injected) the
    /// persisted value has loaded.</para></summary>
    public float WildReproductionMultiplier {
      get {
        var percent = WildReproductionPercent.Value;
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        return percent / 100f;
      }
    }

  }

}
