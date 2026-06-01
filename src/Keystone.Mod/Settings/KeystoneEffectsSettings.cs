using System.Collections.Generic;
using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Keystone.Mod.Settings {

  /// <summary>
  /// Player-tunable atmospheric / ambient-effect controls. Renders as the
  /// "Effects" section in the Keystone mod-settings panel (third section,
  /// <see cref="Order"/> = 2).
  ///
  /// <para>Currently exposes one knob — foggy-morning frequency, expressed
  /// as "once every N days". Maps to <c>probability = 1 / N</c> when
  /// rolling the per-day "is today foggy" gate, so N=1 = every day, N=4 =
  /// matches the current 0.25 default, N=7 = roughly once a week. More
  /// atmospheric content (firefly density, contamination haze) will layer
  /// onto this section as it ships.</para>
  ///
  /// <para><b>UI only.</b> The setting is loaded and persisted but no
  /// consumer reads it yet — <c>WetlandMistDirector</c> still uses its
  /// hard-coded constant. Wiring lands in a follow-up.</para>
  /// </summary>
  public class KeystoneEffectsSettings : ModSettingsOwner {

    /// <summary>How often foggy mornings happen. Stored as one of
    /// <see cref="FrequencyValues"/>; mapped to a per-day probability
    /// by <see cref="FoggyMorningProbability"/>. Renders as a dropdown
    /// with localized labels (Off / Rare / Sometimes / Often / Always).
    /// "Sometimes" matches the previous hard-coded 25% default and is
    /// the new default.</summary>
    public LimitedStringModSetting FoggyMorningFrequency { get; } =
        new(defaultOptionIndex: 2,    // "Sometimes"
            new List<LimitedStringModSettingValue> {
              new(FrequencyValues.Off,       "Keystone.Settings.Effects.FoggyMorningFrequency.Off"),
              new(FrequencyValues.Rare,      "Keystone.Settings.Effects.FoggyMorningFrequency.Rare"),
              new(FrequencyValues.Sometimes, "Keystone.Settings.Effects.FoggyMorningFrequency.Sometimes"),
              new(FrequencyValues.Often,     "Keystone.Settings.Effects.FoggyMorningFrequency.Often"),
              new(FrequencyValues.Always,    "Keystone.Settings.Effects.FoggyMorningFrequency.Always"),
            },
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Effects.FoggyMorningFrequency")
                .SetLocalizedTooltip("Keystone.Settings.Effects.FoggyMorningFrequency.Tooltip"));

    /// <inheritdoc />
    public override int Order => 2;

    /// <inheritdoc />
    public override string HeaderLocKey => "Keystone.Settings.Effects.Header";

    /// <inheritdoc />
    public override ModSettingsContext ChangeableOn =>
        ModSettingsContext.MainMenu | ModSettingsContext.Game;

    /// <inheritdoc />
    protected override string ModId => "SylvanGames.Keystone";

    public KeystoneEffectsSettings(
        ISettings settings,
        ModSettingsOwnerRegistry modSettingsOwnerRegistry,
        ModRepository modRepository)
        : base(settings, modSettingsOwnerRegistry, modRepository) {}

    /// <summary>Probability the per-day "is today foggy?" gate fires
    /// in <see cref="Keystone.Mod.Atmosphere.WetlandMistDirector"/>.
    /// Maps the discrete <see cref="FoggyMorningFrequency"/> dropdown
    /// to a per-day probability:
    /// <list type="bullet">
    ///   <item><c>Off</c> → 0 (no mist ever)</item>
    ///   <item><c>Rare</c> → 1/7 (roughly once a week)</item>
    ///   <item><c>Sometimes</c> → 0.25 (the previous default — 25%)</item>
    ///   <item><c>Often</c> → 0.5 (half the mornings)</item>
    ///   <item><c>Always</c> → 1 (every morning)</item>
    /// </list>
    /// Read each daily roll, so mid-game dropdown changes take effect
    /// on the next pre-dawn cycle. Unknown values fall back to
    /// <c>Sometimes</c>.</summary>
    public float FoggyMorningProbability => FoggyMorningFrequency.Value switch {
      FrequencyValues.Off       => 0f,
      FrequencyValues.Rare      => 1f / 7f,
      FrequencyValues.Sometimes => 0.25f,
      FrequencyValues.Often     => 0.5f,
      FrequencyValues.Always    => 1f,
      _                         => 0.25f,
    };

    /// <summary>Canonical persisted values for
    /// <see cref="FoggyMorningFrequency"/>. The dropdown stores one of
    /// these strings; new entries here must also be added to the
    /// dropdown's option list and the mapping in
    /// <see cref="FoggyMorningProbability"/>.</summary>
    public static class FrequencyValues {
      public const string Off       = "Off";
      public const string Rare      = "Rare";
      public const string Sometimes = "Sometimes";
      public const string Often     = "Often";
      public const string Always    = "Always";
    }

  }

}
