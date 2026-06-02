using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Keystone.Mod.Settings {

  /// <summary>
  /// Player-tunable performance / CPU-cost controls. Renders as the
  /// "Performance" section in the Keystone mod-settings panel (fifth
  /// section, <see cref="Order"/> = 4).
  ///
  /// <para><b>MainMenu-only by design.</b> The map-update-frequency knob
  /// controls the cycle duration of <c>EcologyFieldUpdater</c>,
  /// <c>ChunkBiomeTicker</c>, and <c>ChunkClusterTicker</c>. Those tickers
  /// now consume the cadence via a dynamic <c>Func&lt;float&gt;</c>
  /// (consulted once per tick), so mid-game changes would technically
  /// propagate to the next cycle — but exposing that as a player-facing
  /// in-game slider is a separate UX decision, and rolling-sweep cadence
  /// changes only at the start of the next cycle. Until that UX is
  /// designed, keep the slider MainMenu-only.</para>
  /// </summary>
  public class KeystonePerformanceSettings : ModSettingsOwner {

    /// <summary>How often the ecology field updater, chunk biome
    /// ticker, and chunk cluster ticker refresh, in game-hours per
    /// cycle. 1 = every game-hour (current default, most responsive),
    /// 4 = once every four hours (lowest CPU cost on large maps).
    ///
    /// <para>Deliberately does not touch the fauna reconcile sweep,
    /// the daily chunk-rules pass, or the per-5-game-second decoration
    /// sweep. Fauna load is already player-controlled via the per-
    /// species abundance sliders.</para></summary>
    public ModSetting<int> MapUpdateHours { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 1, minValue: 1, maxValue: 4,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Performance.MapUpdateHours")
                .SetLocalizedTooltip("Keystone.Settings.Performance.MapUpdateHours.Tooltip"));

    /// <summary>How often the rule applier (<c>ChunkRulesApplier</c>)
    /// re-evaluates flourish / attrition / Class A-D spawn rules per
    /// chunk, in game-days per cycle. 1 = once per game-day (most
    /// responsive — content appears quickly as biomes mature),
    /// 4 = once every four game-days (lowest CPU cost on large maps).
    ///
    /// <para><b>Why a separate knob from MapUpdateHours.</b> The biome
    /// data refresh and the rules pass have different cost profiles:
    /// biome data is a per-chunk math compute, rules dispatch is many
    /// per-surface engine-side entity-creation calls. On big maps the
    /// rules pass dominates CPU even when biome data is cheap, so
    /// players need to throttle them independently.</para></summary>
    public ModSetting<int> RulesUpdateDays { get; } =
        (ModSetting<int>)new RangeIntModSetting(
            defaultValue: 1, minValue: 1, maxValue: 4,
            ModSettingDescriptor
                .CreateLocalized("Keystone.Settings.Performance.RulesUpdateDays")
                .SetLocalizedTooltip("Keystone.Settings.Performance.RulesUpdateDays.Tooltip"));

    /// <inheritdoc />
    public override int Order => 4;

    /// <inheritdoc />
    public override string HeaderLocKey => "Keystone.Settings.Performance.Header";

    /// <inheritdoc />
    public override ModSettingsContext ChangeableOn => ModSettingsContext.MainMenu;

    /// <inheritdoc />
    protected override string ModId => "SylvanGames.Keystone";

    public KeystonePerformanceSettings(
        ISettings settings,
        ModSettingsOwnerRegistry modSettingsOwnerRegistry,
        ModRepository modRepository)
        : base(settings, modSettingsOwnerRegistry, modRepository) {}

    /// <summary>Cycle duration in game-days for the map-update tickers
    /// (<c>EcologyFieldUpdater</c>, <c>ChunkBiomeTicker</c>,
    /// <c>ChunkClusterTicker</c>). Computed as
    /// <see cref="MapUpdateHours"/> / 24 — e.g. 1 hour → <c>1/24</c>,
    /// 4 hours → <c>1/6</c>. Consulted lazily by each ticker on every
    /// <c>Tick</c> (via the <c>Func&lt;float&gt;</c> RollingSweep
    /// constructor overload), so changes to the persisted store land at
    /// the start of the next cycle without requiring a reload.
    ///
    /// <para>Clamped to the setting's declared minimum (1 hour) on
    /// read. <see cref="ModSetting{T}.Value"/> can return
    /// <c>default(int) = 0</c> if the property is consulted before
    /// the persisted settings store has hydrated — which historically
    /// happened in practice when DI instantiated a ticker that
    /// depended on this setting earlier in the graph than the
    /// settings-load path. The lazy-read consumer pattern means the
    /// clamp is now defensive rather than load-bearing (the first
    /// <c>Tick</c> happens well after Bindito construction completes),
    /// but keeping it matches the slider's <c>minValue: 1</c> UI
    /// contract and guards against any future caller reading at an
    /// earlier moment.</para></summary>
    public float MapUpdateCycleDays {
      get {
        var hours = MapUpdateHours.Value;
        if (hours < 1) hours = 1;
        return hours / 24f;
      }
    }

    /// <summary>Cycle duration in game-days for the rule applier.
    /// Read lazily by <c>ChunkRulesApplier</c> on every tick via its
    /// dynamic-cadence <c>Func&lt;float&gt;</c>, so persisted
    /// changes land at the start of the next cycle without a reload.
    /// Clamped to the slider minimum for the same belt-and-suspenders
    /// reasons as <see cref="MapUpdateCycleDays"/>.</summary>
    public float RulesUpdateCycleDays {
      get {
        var days = RulesUpdateDays.Value;
        if (days < 1) days = 1;
        return days;
      }
    }

  }

}
