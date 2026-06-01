using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Keystone.Mod.Settings {

  /// <summary>
  /// Player-tunable flora-density sliders. Renders as the "Flora" section
  /// in the Keystone mod-settings panel (first section, <see cref="Order"/>
  /// = 0).
  ///
  /// <para>One slider for decorative Class B flourishes plus per-category
  /// sliders for Class D vanilla flora (trees, bushes, crops). The Class D
  /// sliders use the same category-keyed pattern as
  /// <see cref="KeystoneFaunaSettings"/>: each recipe declares its
  /// category in the recipe-book JSON, and the spawn handler reads
  /// <see cref="MultiplierFor"/> at dispatch time. Splitting an existing
  /// category later (or adding a new one) is a slider + switch arm +
  /// recipe-stamp change — no spawn-handler structural change needed.</para>
  /// </summary>
  public class KeystoneFloraSettings : ModSettingsOwner {

    /// <summary>Multiplier on Class B spawn density. Scales the activation
    /// gate that decides which tiles in a (biome, level) bucket get a
    /// flourish placed on them. Percent: 0 = no decorative plants, 100 =
    /// current behaviour, 200 = saturate every eligible tile once the
    /// product exceeds the hash range.</summary>
    public ModSetting<int> ClassBDensityPercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Flora.ClassBDensity")
                .SetLocalizedTooltip("Keystone.Settings.Flora.ClassBDensity.Tooltip"));

    /// <summary>Multiplier on Class D <see cref="Categories.Trees"/>
    /// spawn density (Birch, Maple, Oak, Mangrove, ...). Percent.</summary>
    public ModSetting<int> TreeDensityPercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Flora.TreeDensity")
                .SetLocalizedTooltip("Keystone.Settings.Flora.TreeDensity.Tooltip"));

    /// <summary>Multiplier on Class D <see cref="Categories.Bushes"/>
    /// spawn density (BlueberryBush, Dandelion, ...). Percent.</summary>
    public ModSetting<int> BushDensityPercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Flora.BushDensity")
                .SetLocalizedTooltip("Keystone.Settings.Flora.BushDensity.Tooltip"));

    /// <summary>Multiplier on Class D <see cref="Categories.Crops"/>
    /// spawn density (Cattail, Spadderdock, ...). Percent.</summary>
    public ModSetting<int> CropDensityPercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Flora.CropDensity")
                .SetLocalizedTooltip("Keystone.Settings.Flora.CropDensity.Tooltip"));

    /// <summary>Days of maturity to pre-seed on a new game. 0 = ecology
    /// builds from nothing. 3 = L1 biomes established at game start
    /// (default). 7 = well into L2, mature ecosystem from day one.
    /// Only affects new games — loaded saves use their saved maturity.
    /// Read at startup by <c>KeystoneStartupWarmup</c>.</summary>
    public ModSetting<int> NewGameWarmupDays { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 3, minValue: 0, maxValue: 7,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Flora.NewGameWarmup")
                .SetLocalizedTooltip("Keystone.Settings.Flora.NewGameWarmup.Tooltip"));

    /// <summary>Maximum growth-speed bonus for plants in a qualifying
    /// biome. Percent: 0 = no bonus, 20 = default (20% faster growth
    /// at full suitability + maturity), 100 = up to 100% faster.</summary>
    public ModSetting<int> GrowthBonusPercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 30, minValue: 0, maxValue: 100,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Flora.GrowthBonus")
                .SetLocalizedTooltip("Keystone.Settings.Flora.GrowthBonus.Tooltip"));

    /// <inheritdoc />
    public override int Order => 0;

    /// <inheritdoc />
    public override string HeaderLocKey => "Keystone.Settings.Flora.Header";

    /// <inheritdoc />
    public override ModSettingsContext ChangeableOn =>
        ModSettingsContext.MainMenu | ModSettingsContext.Game;

    /// <inheritdoc />
    protected override string ModId => "SylvanGames.Keystone";

    public KeystoneFloraSettings(
        ISettings settings,
        ModSettingsOwnerRegistry modSettingsOwnerRegistry,
        ModRepository modRepository)
        : base(settings, modSettingsOwnerRegistry, modRepository) {}

    /// <summary>Per-bucket density multiplier for a Class D recipe
    /// bucket, by user-facing category name. Looks up the relevant
    /// slider's value (0-200%) and returns it as a fraction (0.0-2.0).
    ///
    /// <para>Unknown categories pass through at <c>1.0f</c> (no effect).
    /// This is deliberate: third-party mods can mint new categories
    /// without touching Keystone, and their flora spawns at recipe-
    /// defined density until Keystone exposes a slider for them. Match
    /// is case-sensitive — pass the canonical category string from
    /// <see cref="Categories"/>.</para></summary>
    /// <param name="category">User-facing category name from the
    /// recipe's <c>Category</c> field.</param>
    public float MultiplierFor(string category) {
      return category switch {
        Categories.Trees  => TreeDensityPercent.Value  / 100f,
        Categories.Bushes => BushDensityPercent.Value  / 100f,
        Categories.Crops  => CropDensityPercent.Value  / 100f,
        _                 => 1f,
      };
    }

    /// <summary>Canonical Class D category strings recognised by
    /// <see cref="MultiplierFor"/>. Use these constants from C# code
    /// when looking up a category programmatically; recipe-book JSON
    /// uses the same strings as plain field values.</summary>
    public static class Categories {
      /// <summary>Trees (Birch, Maple, ChestnutTree, Oak, Mangrove, ...).</summary>
      public const string Trees = "Trees";
      /// <summary>Bushes — planted by forester, persist when harvested
      /// (BlueberryBush, Dandelion, ...).</summary>
      public const string Bushes = "Bushes";
      /// <summary>Crops — planted by farmer, harvested as the whole
      /// plant (Cattail, Spadderdock, ...).</summary>
      public const string Crops = "Crops";
    }

  }

}
