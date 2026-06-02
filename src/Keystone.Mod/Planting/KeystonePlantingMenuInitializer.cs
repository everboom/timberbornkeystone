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
  /// button that owns it, and add our button to that group. Player-facing,
  /// so — unlike <see cref="Keystone.Mod.Toolbar.KeystoneToolGroup"/> — it
  /// is not gated behind dev mode.</para>
  ///
  /// <para><b>Held-back trees variant.</b> Only the crop button is wired
  /// today. The trees/bushes button (which overlaps the upstream Forest
  /// Tool mod) is one flag away — set <see cref="EnableForestVariant"/> to
  /// <c>true</c> once we've squared it with Forest Tool's author.</para>
  /// </summary>
  public sealed class KeystonePlantingMenuInitializer : IPostLoadableSingleton {

    #region Constants

    /// <summary>When false, the trees/bushes brush is built and DI-wired
    /// but its toolbar button is not registered. See
    /// <see cref="KeystoneForestPlantingTool"/> for why it's held back.
    /// <c>static readonly</c> (not <c>const</c>) so flipping it doesn't
    /// trip an unreachable-code warning on the guarded call.</summary>
    private static readonly bool EnableForestVariant = false;

    /// <summary>Placeholder button icon name, resolved by
    /// <see cref="ToolButtonFactory"/> from <c>Sprites/BottomBar/</c>.
    /// Shared with the dev tool group for now; per-tool icons need the
    /// Unity asset pipeline.</summary>
    private const string IconName = "KeystoneFlourishPlacement";

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
      AddToolToGroup(KeystoneCropPlantingTool.GroupId, _cropTool);
      if (EnableForestVariant) {
        AddToolToGroup(KeystoneForestPlantingTool.GroupId, _forestTool);
      }
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

      var button = _toolButtonFactory.Create(tool, IconName, groupButton.ToolButtonsElement);
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
