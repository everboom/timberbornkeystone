using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Keystone.Mod.Settings {

  /// <summary>
  /// Player-tunable UI controls. Renders as the "UI" section in the
  /// Keystone mod-settings panel.
  /// </summary>
  public class KeystoneUiSettings : ModSettingsOwner {

    /// <summary>Biome overlay box transparency, 0–100. Higher = more
    /// opaque. Read at draw time, so changes take effect immediately
    /// without requiring a reload.</summary>
    public ModSetting<int> BiomeOverlayOpacity { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 75, minValue: 25, maxValue: 100,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.UI.BiomeOverlayOpacity")
                .SetLocalizedTooltip("Keystone.Settings.UI.BiomeOverlayOpacity.Tooltip"));

    /// <inheritdoc />
    public override int Order => 4;

    /// <inheritdoc />
    public override string HeaderLocKey => "Keystone.Settings.UI.Header";

    /// <inheritdoc />
    public override ModSettingsContext ChangeableOn => ModSettingsContext.All;

    /// <inheritdoc />
    protected override string ModId => "SylvanGames.Keystone";

    public KeystoneUiSettings(
        ISettings settings,
        ModSettingsOwnerRegistry modSettingsOwnerRegistry,
        ModRepository modRepository)
        : base(settings, modSettingsOwnerRegistry, modRepository) {}

    /// <summary>Biome overlay alpha as a float in [0, 1]. Clamps to
    /// 0 at minimum so the overlay can be effectively disabled even
    /// while the toggle is on.</summary>
    public float BiomeOverlayAlpha => BiomeOverlayOpacity.Value / 100f;

  }

}
