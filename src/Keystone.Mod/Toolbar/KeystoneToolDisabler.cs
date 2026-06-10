using Keystone.Mod.Cutting;
using Keystone.Mod.Planting;
using Keystone.Mod.Settings;
using Timberborn.ToolSystem;

namespace Keystone.Mod.Toolbar {

  /// <summary>
  /// Per-tool visibility gate for Keystone's three injected toolbar brushes —
  /// mixed-crop planting, mixed tree/bush planting, and the cutting planner —
  /// driven by the on/off toggles in <see cref="KeystoneUiSettings"/>.
  ///
  /// <para><b>How it gates.</b> Plugged into the vanilla tool-button
  /// visibility system as an <see cref="IToolDisabler"/> (MultiBind in
  /// <c>KeystoneConfigurator</c>). Each <c>ToolButton</c> hides itself when
  /// any disabler returns false for its tool, and re-evaluates its visibility
  /// on every <c>ToolGroupEnteredEvent</c>. So flipping a setting takes effect
  /// the next time the player opens that tool group — no reload, no
  /// add/remove of buttons, no polling. The brushes are always created and
  /// assigned to their vanilla groups (see the menu initializers); this only
  /// decides whether each button is shown.</para>
  ///
  /// <para>Returns true for every non-Keystone tool, so vanilla and other
  /// mods' tools are never affected.</para>
  /// </summary>
  public sealed class KeystoneToolDisabler : IToolDisabler {

    private readonly KeystoneUiSettings _uiSettings;

    public KeystoneToolDisabler(KeystoneUiSettings uiSettings) {
      _uiSettings = uiSettings;
    }

    /// <inheritdoc />
    public bool IsEnabled(ITool tool) => tool switch {
        KeystoneCropPlantingTool => _uiSettings.MixedCropPlantingTool.Value,
        KeystoneForestPlantingTool => _uiSettings.MixedForestPlantingTool.Value,
        KeystoneLoggingTool => _uiSettings.CuttingPlannerTool.Value,
        _ => true,
    };

  }

}
