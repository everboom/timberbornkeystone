using Keystone.Mod.Diagnostics;
using Timberborn.PlantingUI;
using Timberborn.SingletonSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;

namespace Keystone.Mod.Planting {

  /// <summary>
  /// Appends Keystone's mixed-planting brush buttons into the existing
  /// vanilla planting menus, the way the "Forest Tool" mod does — but
  /// against this game version's <b>public</b> tool-button API rather than
  /// the private fields Forest Tool's older build reached into.
  ///
  /// <para>By <see cref="PostLoad"/>, <see cref="BottomBarPanel"/> (an
  /// <c>ILoadableSingleton</c>) has already built the vanilla group buttons
  /// and one <see cref="PlantingTool"/> button per plantable. We find a
  /// vanilla planting tool of the target category, ask
  /// <see cref="ToolButtonService.GetToolGroupButton"/> for the group
  /// button that owns it, and add our button to that group.</para>
  ///
  /// <para><b>Per-tool player gating.</b> Both buttons are always wired into
  /// their vanilla groups here; whether each one shows is decided live by
  /// <see cref="Keystone.Mod.Toolbar.KeystoneToolDisabler"/> from the on/off
  /// toggles in <see cref="Keystone.Mod.Settings.KeystoneUiSettings"/> (both
  /// default on; the toggle lets players who run the overlapping Forest Tool
  /// mod turn the trees/bushes variant off). The engine re-checks tool-button
  /// visibility on each tool-group open, so a setting change takes effect with
  /// no reload.</para>
  /// </summary>
  public sealed class KeystonePlantingMenuInitializer : IPostLoadableSingleton {

    #region Constants

    /// <summary>Button icon name shared by both planting brushes (trees and
    /// crops), resolved by <see cref="ToolButtonFactory"/> from the bundle's
    /// <c>Sprites/BottomBar/</c> (source-of-truth under
    /// <c>unity-assets/Keystone/AssetBundles/Resources/Sprites/BottomBar/</c>).
    /// The cutting tool will get its own icon separately.</summary>
    private const string IconName = "RandomPlantIcon";

    #endregion

    #region Fields

    private readonly ToolButtonService _toolButtonService;
    private readonly ToolButtonFactory _toolButtonFactory;
    private readonly ToolGroupService _toolGroupService;
    private readonly KeystoneCropPlantingTool _cropTool;
    private readonly KeystoneForestPlantingTool _forestTool;

    #endregion

    #region Construction

    public KeystonePlantingMenuInitializer(
        ToolButtonService toolButtonService,
        ToolButtonFactory toolButtonFactory,
        ToolGroupService toolGroupService,
        KeystoneCropPlantingTool cropTool,
        KeystoneForestPlantingTool forestTool) {
      _toolButtonService = toolButtonService;
      _toolButtonFactory = toolButtonFactory;
      _toolGroupService = toolGroupService;
      _cropTool = cropTool;
      _forestTool = forestTool;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      // Both brushes are always wired into their vanilla planting groups;
      // KeystoneToolDisabler gates each button's visibility live from the
      // KeystoneUiSettings toggles, so nothing is gated at add-time here.
      AddToolToGroup(KeystoneCropPlantingTool.GroupId, _cropTool);
      AddToolToGroup(KeystoneForestPlantingTool.GroupId, _forestTool);
    }

    /// <summary>Inject <paramref name="tool"/>'s button into the vanilla
    /// group <paramref name="groupId"/>, located via a same-category
    /// vanilla planting tool already present in that group.</summary>
    private void AddToolToGroup(string groupId, KeystonePlantingToolBase tool) {
      var groupButton = FindGroupButton(tool);
      if (groupButton == null) {
        KeystoneLog.Warn(
            $"[Keystone] KeystonePlantingMenuInitializer: could not locate the vanilla " +
            $"'{groupId}' planting menu (no built-in planting tool of this category was " +
            "found). Planting button not added; another mod may have altered the menu.");
        return;
      }

      // Icon resolves from the deployed AssetBundle. If it's missing (e.g. the
      // bundle hasn't been rebuilt after a new sprite was added on the code
      // side), Create throws -- and this runs in PostLoad, so an unguarded
      // throw aborts the whole game load. A cosmetic icon is never worth that:
      // log it loudly with the fix and skip this button instead.
      ToolButton button;
      try {
        button = _toolButtonFactory.Create(tool, IconName, groupButton.ToolButtonsElement);
      } catch (System.Exception e) {
        KeystoneLog.Error(
            $"[Keystone] KeystonePlantingMenuInitializer: failed to load icon '{IconName}' for the "
            + $"'{groupId}' planting button -- rebuild the Keystone AssetBundle in the Modding SDK "
            + $"(the sprite isn't in the deployed bundle). Button not added. {e}");
        return;
      }
      _toolGroupService.AssignToGroup(_toolGroupService.GetGroup(groupId), tool);
      groupButton.AddTool(button);
      // ToolButtonFactory already registered the button with
      // ToolButtonService, but its PostLoad pass has run by now (we're in
      // a later PostLoad), so wire this button's input/visibility here.
      button.PostLoad();
    }

    /// <summary>The group button owning a vanilla <see cref="PlantingTool"/>
    /// whose plantable matches <paramref name="tool"/>'s category, or null
    /// if none is present.</summary>
    private ToolGroupButton FindGroupButton(KeystonePlantingToolBase tool) {
      foreach (var toolButton in _toolButtonService.ToolButtons) {
        if (toolButton.Tool is PlantingTool plantingTool
            && tool.IsCategoryMember(plantingTool.PlantableSpec)) {
          return _toolButtonService.GetToolGroupButton(toolButton);
        }
      }
      return null;
    }

    #endregion

  }

}
