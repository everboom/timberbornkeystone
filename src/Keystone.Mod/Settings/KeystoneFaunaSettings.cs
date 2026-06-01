using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Keystone.Mod.Settings {

  /// <summary>
  /// Player-tunable fauna controls. Renders as the "Fauna" section in the
  /// Keystone mod-settings panel (second section, <see cref="Order"/> = 1).
  ///
  /// <para>Includes a master enable toggle plus per-category abundance
  /// sliders for the three currently-shipping fauna families (deer, cattle
  /// = cow + bull, fish). The abundance sliders grey out when fauna is
  /// disabled (see <see cref="OnAfterLoad"/>).</para>
  ///
  /// <para><b>UI only.</b> The settings are loaded and persisted but no
  /// consumer reads from them yet — fauna ticker / drainer wiring lands in
  /// a follow-up.</para>
  /// </summary>
  public class KeystoneFaunaSettings : ModSettingsOwner {

    /// <summary>Master switch. When off, the fauna pipeline short-
    /// circuits and any live Keystone-spawned fauna is culled on the next
    /// reconcile cycle.</summary>
    public ModSetting<bool> EnableFauna { get; } =
        new(defaultValue: true,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Fauna.EnableFauna")
                .SetLocalizedTooltip("Keystone.Settings.Fauna.EnableFauna.Tooltip"));

    /// <summary>Multiplier on deer per-cluster capacity. Percent.</summary>
    public ModSetting<int> DeerAbundancePercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Fauna.DeerAbundance")
                .SetLocalizedTooltip("Keystone.Settings.Fauna.DeerAbundance.Tooltip"));

    /// <summary>Multiplier on cattle (cow + bull, treated as one
    /// category) per-cluster capacity. Percent.</summary>
    public ModSetting<int> CattleAbundancePercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Fauna.CattleAbundance")
                .SetLocalizedTooltip("Keystone.Settings.Fauna.CattleAbundance.Tooltip"));

    /// <summary>Multiplier on fish per-cluster capacity. Percent.</summary>
    public ModSetting<int> FishAbundancePercent { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 100, minValue: 0, maxValue: 200,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Fauna.FishAbundance")
                .SetLocalizedTooltip("Keystone.Settings.Fauna.FishAbundance.Tooltip"));

    /// <inheritdoc />
    public override int Order => 1;

    /// <inheritdoc />
    public override string HeaderLocKey => "Keystone.Settings.Fauna.Header";

    /// <inheritdoc />
    public override ModSettingsContext ChangeableOn =>
        ModSettingsContext.MainMenu | ModSettingsContext.Game;

    /// <inheritdoc />
    protected override string ModId => "SylvanGames.Keystone";

    public KeystoneFaunaSettings(
        ISettings settings,
        ModSettingsOwnerRegistry modSettingsOwnerRegistry,
        ModRepository modRepository)
        : base(settings, modSettingsOwnerRegistry, modRepository) {}

    /// <summary>Wire the per-category sliders to grey out when the
    /// master toggle is off. <see cref="ModSettingsBox.UpdateSingleton"/>
    /// polls <c>Descriptor.IsEnabled()</c> every frame so the sliders
    /// react live to <c>EnableFauna</c> toggling.</summary>
    protected override void OnAfterLoad() {
      base.OnAfterLoad();
      DeerAbundancePercent.Descriptor.SetEnableCondition(() => EnableFauna.Value);
      CattleAbundancePercent.Descriptor.SetEnableCondition(() => EnableFauna.Value);
      FishAbundancePercent.Descriptor.SetEnableCondition(() => EnableFauna.Value);
    }

    /// <summary>True when wildlife is enabled. Equivalent to
    /// <c>EnableFauna.Value</c> but exposed as a property so consumers
    /// read it the same way they read the multiplier.</summary>
    public bool IsEnabled => EnableFauna.Value;

    /// <summary>Per-cluster capacity multiplier for a Class E recipe
    /// bucket, by user-facing category name. Returns 0 if the master
    /// toggle is off; otherwise looks up the relevant slider's value
    /// (0-200%) and returns it as a fraction (0.0-2.0).
    ///
    /// <para>Unknown categories pass through at <c>1.0f</c> (no
    /// effect). This is deliberate: third-party mods can mint new
    /// categories without touching Keystone, and their fauna spawns
    /// at recipe-defined density until Keystone exposes a slider for
    /// them. Match is case-sensitive — pass the canonical category
    /// string from <see cref="Categories"/>.</para></summary>
    /// <param name="category">User-facing category name from the
    /// recipe's <c>Category</c> field.</param>
    public float MultiplierFor(string category) {
      if (!EnableFauna.Value) return 0f;
      return category switch {
        Categories.Deer   => DeerAbundancePercent.Value   / 100f,
        Categories.Cattle => CattleAbundancePercent.Value / 100f,
        Categories.Fish   => FishAbundancePercent.Value   / 100f,
        _                 => 1f,
      };
    }

    /// <summary>Canonical fauna-category strings recognised by
    /// <see cref="MultiplierFor"/>. Use these constants from C# code
    /// when looking up a category programmatically; recipe-book JSON
    /// uses the same strings as plain field values.</summary>
    public static class Categories {
      /// <summary>Deer family (currently <c>KeystoneDeer</c>).</summary>
      public const string Deer = "Deer";
      /// <summary>Cattle family (currently <c>KeystoneCow</c> +
      /// <c>KeystoneBull</c>, treated as one slider).</summary>
      public const string Cattle = "Cattle";
      /// <summary>Fish family (currently <c>KeystoneFish1</c> +
      /// <c>KeystoneFish2</c>, treated as one slider).</summary>
      public const string Fish = "Fish";
    }

  }

}
