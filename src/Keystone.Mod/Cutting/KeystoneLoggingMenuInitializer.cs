using Keystone.Mod.Diagnostics;
using Timberborn.ForestryUI;
using Timberborn.SingletonSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;

namespace Keystone.Mod.Cutting {

  /// <summary>
  /// Appends the Keystone logging brush button into the vanilla
  /// <b>TreeCutting</b> tool group, the way the planting brush injects into the
  /// planting menus. By <see cref="PostLoad"/> the bottom bar has built the
  /// vanilla group buttons; we locate the group that owns the base-game
  /// <see cref="TreeCuttingAreaSelectionTool"/> and add our button to it.
  ///
  /// <para><b>Player gating.</b> The button is always wired into the vanilla
  /// TreeCutting group here; whether it shows is decided live by
  /// <see cref="Keystone.Mod.Toolbar.KeystoneToolDisabler"/> from the
  /// <see cref="Keystone.Mod.Settings.KeystoneUiSettings"/> cutting-planner
  /// toggle (default on; the toggle lets players who run Cordial's Cutter Tool
  /// turn it off — <c>docs/private/cuttertool.md</c>). The engine re-checks
  /// tool-button visibility on each tool-group open, so a setting change needs
  /// no reload.</para>
  /// </summary>
  public sealed class KeystoneLoggingMenuInitializer : IPostLoadableSingleton {

    #region Constants

    /// <summary>Button icon name, resolved by <see cref="ToolButtonFactory"/>
    /// from the bundle's <c>Sprites/BottomBar/</c> (source-of-truth under
    /// <c>unity-assets/Keystone/AssetBundles/Resources/Sprites/BottomBar/</c>).</summary>
    private const string IconName = "CuttingToolIcon";

    #endregion

    #region Fields

    private readonly ToolButtonService _toolButtonService;
    private readonly ToolButtonFactory _toolButtonFactory;
    private readonly ToolGroupService _toolGroupService;
    private readonly KeystoneLoggingTool _tool;

    #endregion

    #region Construction

    public KeystoneLoggingMenuInitializer(
        ToolButtonService toolButtonService,
        ToolButtonFactory toolButtonFactory,
        ToolGroupService toolGroupService,
        KeystoneLoggingTool tool) {
      _toolButtonService = toolButtonService;
      _toolButtonFactory = toolButtonFactory;
      _toolGroupService = toolGroupService;
      _tool = tool;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      // Always wired into the vanilla TreeCutting group; KeystoneToolDisabler
      // gates the button's visibility live from the KeystoneUiSettings toggle.
      var groupButton = FindGroupButton();
      if (groupButton == null) {
        KeystoneLog.Warn(
            "[Keystone] KeystoneLoggingMenuInitializer: could not locate the vanilla " +
            "'TreeCutting' menu (no built-in tree-cutting tool was found). Logging " +
            "button not added; another mod may have altered the menu.");
        return;
      }

      // Icon resolves from the deployed AssetBundle. If it's missing (e.g. the
      // bundle hasn't been rebuilt after a new sprite was added on the code
      // side), Create throws -- and this runs in PostLoad, so an unguarded
      // throw aborts the whole game load. A cosmetic icon is never worth that:
      // log it loudly with the fix and skip this button instead.
      ToolButton button;
      try {
        button = _toolButtonFactory.Create(_tool, IconName, groupButton.ToolButtonsElement);
      } catch (System.Exception e) {
        KeystoneLog.Error(
            $"[Keystone] KeystoneLoggingMenuInitializer: failed to load icon '{IconName}' for the "
            + "cutting-planner button -- rebuild the Keystone AssetBundle in the Modding SDK "
            + $"(the sprite isn't in the deployed bundle). Button not added. {e}");
        return;
      }
      _toolGroupService.AssignToGroup(_toolGroupService.GetGroup(KeystoneLoggingTool.GroupId), _tool);
      groupButton.AddTool(button);
      // ToolButtonFactory registered the button, but the service's PostLoad pass
      // has already run (we're in a later PostLoad), so wire it up here.
      button.PostLoad();
    }

    /// <summary>The group button owning the vanilla
    /// <see cref="TreeCuttingAreaSelectionTool"/>, or null if none is present.
    /// Iterates rather than calling <c>GetToolButton&lt;T&gt;()</c> (which
    /// throws when absent) so an altered menu degrades to a warning.</summary>
    private ToolGroupButton FindGroupButton() {
      foreach (var toolButton in _toolButtonService.ToolButtons) {
        if (toolButton.Tool is TreeCuttingAreaSelectionTool) {
          return _toolButtonService.GetToolGroupButton(toolButton);
        }
      }
      return null;
    }

    #endregion

  }

}
