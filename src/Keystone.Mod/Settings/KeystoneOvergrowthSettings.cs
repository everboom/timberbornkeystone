using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Keystone.Mod.Settings {

  /// <summary>
  /// Player-tunable controls for the dead-tree overgrowth system (GitHub
  /// issue #33). Renders as the "Overgrowth" section in the Keystone
  /// mod-settings panel (<see cref="Order"/> = 3, right after Fauna —
  /// grouping the ecology-content sections together).
  ///
  /// <para>Two <b>independent</b> rate sliders, mirroring the system's split
  /// between graphics and gameplay:
  /// <list type="bullet">
  ///   <item><see cref="OvergrowthDensityPercent"/> — the rate at which
  ///         trees get draped in overgrowth flourishes (the graphical
  ///         layer). Read by <c>OvergrowthHandler.GetDensityMultiplier</c>
  ///         for the overgrow levels. 0% = no overgrowth visuals.</item>
  ///   <item><see cref="ReplacementRatePercent"/> — the rate at which
  ///         mature dead trees are replaced by new seedlings (the gameplay
  ///         layer). Scales the Reseed level's density. 0% = trees are
  ///         never replaced; 100% = default speed; 200% = double.</item>
  /// </list>
  /// They're independent because the reclamation clock that gates
  /// replacement accrues on every dead tree, overgrown or not — so you can
  /// turn overgrowth visuals down (graphical cost) without slowing
  /// replacement (gameplay), or vice versa.</para>
  /// </summary>
  public class KeystoneOvergrowthSettings : ModSettingsOwner {

    /// <summary>Multiplier on the overgrowth spawn rate — scales the
    /// activation gate for the Dead/Live overgrow levels (per-cycle
    /// stochastic roll for dead trees, hash-gated coverage for live).
    /// Percent: 0 = no overgrowth visuals, 100 = blueprint-defined rate,
    /// 200 = twice as fast / twice the coverage. Graphical only — does not
    /// affect whether or how fast trees are replaced.</summary>
    public ModSetting<int> OvergrowthDensityPercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Overgrowth.Density")
                .SetLocalizedTooltip("Keystone.Settings.Overgrowth.Density.Tooltip"));

    /// <summary>Multiplier on the tree-replacement rate — scales the Reseed
    /// level's density (how quickly an eligible mature dead tree is swapped
    /// for a new seedling). Percent: 0 = trees are never replaced (deadwood
    /// persists), 100 = default speed, 200 = double. Independent of the
    /// overgrowth visual: a dead tree accrues replacement-eligibility
    /// whether or not it's overgrown.</summary>
    public ModSetting<int> ReplacementRatePercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Overgrowth.ReplacementRate")
                .SetLocalizedTooltip("Keystone.Settings.Overgrowth.ReplacementRate.Tooltip"));

    /// <inheritdoc />
    public override int Order => 3;

    /// <inheritdoc />
    public override string HeaderLocKey => "Keystone.Settings.Overgrowth.Header";

    /// <inheritdoc />
    public override ModSettingsContext ChangeableOn =>
        ModSettingsContext.MainMenu | ModSettingsContext.Game;

    /// <inheritdoc />
    protected override string ModId => "SylvanGames.Keystone";

    public KeystoneOvergrowthSettings(
        ISettings settings,
        ModSettingsOwnerRegistry modSettingsOwnerRegistry,
        ModRepository modRepository)
        : base(settings, modSettingsOwnerRegistry, modRepository) {}

    /// <summary>Overgrowth spawn-rate multiplier as a fraction (0.0–2.0).
    /// Applied to the Dead/Live overgrow levels' activation gate.</summary>
    public float OvergrowthRateMultiplier => OvergrowthDensityPercent.Value / 100f;

    /// <summary>Tree-replacement-rate multiplier as a fraction (0.0–2.0).
    /// Applied to the Reseed level's density; 0 disables replacement.</summary>
    public float ReplacementRateMultiplier => ReplacementRatePercent.Value / 100f;

  }

}
