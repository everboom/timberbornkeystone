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

    /// <summary>When on, replaces the vanilla dark tilled-soil (wet-field)
    /// albedo on tiles marked for planting with Keystone's lighter texture.
    /// Off by default while the look is being finalised. Read live by
    /// <see cref="Keystone.Mod.FieldTint.KeystoneFieldTextureOverride"/>.</summary>
    public ModSetting<bool> CustomTilledSoilTexture { get; } =
        new(defaultValue: false,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.UI.TilledSoilTexture")
                .SetLocalizedTooltip("Keystone.Settings.UI.TilledSoilTexture.Tooltip"));

    /// <summary>On/off for the mixed-crop planting brush (injected into the
    /// vanilla field/crop planting menu). On by default; turn it off here to
    /// hide the button. Read live by
    /// <see cref="Keystone.Mod.Toolbar.KeystoneToolDisabler"/>: toggling it
    /// shows/hides the tool button the next time its tool group is opened, no
    /// reload.</summary>
    public ModSetting<bool> MixedCropPlantingTool { get; } =
        new(defaultValue: true,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.UI.MixedCropPlantingTool")
                .SetLocalizedTooltip("Keystone.Settings.UI.MixedCropPlantingTool.Tooltip"));

    /// <summary>On/off for the mixed tree/bush planting brush (injected into
    /// the vanilla forester planting menu). On by default; same live gating
    /// as <see cref="MixedCropPlantingTool"/>.</summary>
    public ModSetting<bool> MixedForestPlantingTool { get; } =
        new(defaultValue: true,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.UI.MixedForestPlantingTool")
                .SetLocalizedTooltip("Keystone.Settings.UI.MixedForestPlantingTool.Tooltip"));

    /// <summary>On/off for the cutting-planner brush (injected into the
    /// vanilla forester tree-cutting menu). On by default; same live gating
    /// as <see cref="MixedCropPlantingTool"/>.</summary>
    public ModSetting<bool> CuttingPlannerTool { get; } =
        new(defaultValue: true,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.UI.CuttingPlannerTool")
                .SetLocalizedTooltip("Keystone.Settings.UI.CuttingPlannerTool.Tooltip"));

    /// <inheritdoc />
    public override int Order => 6;

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
