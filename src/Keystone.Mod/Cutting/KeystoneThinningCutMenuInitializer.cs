using Keystone.Mod.Diagnostics;
using Timberborn.ForestryUI;
using Timberborn.SingletonSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;

namespace Keystone.Mod.Cutting {

  /// <summary>
  /// Appends the Keystone thinning-cut brush button into the vanilla
  /// <b>TreeCutting</b> tool group, the way the planting brush injects into the
  /// planting menus. By <see cref="PostLoad"/> the bottom bar has built the
  /// vanilla group buttons; we locate the group that owns the base-game
  /// <see cref="TreeCuttingAreaSelectionTool"/> and add our button to it.
  ///
  /// <para><b>Dev-mode only (for now).</b> Gated behind
  /// <see cref="KeystoneDevMode"/>, like the planting brushes, so it never
  /// reaches a clean release build — the design is still in flux (issue #30)
  /// and the tool overlaps Cordial's Cutter Tool
  /// (<c>docs/private/cuttertool.md</c>).</para>
  /// </summary>
  public sealed class KeystoneThinningCutMenuInitializer : IPostLoadableSingleton {

    #region Constants

    /// <summary>Placeholder button icon (shared with the dev tool group); a
    /// dedicated icon needs the Unity asset pipeline.</summary>
    private const string IconName = "KeystoneFlourishPlacement";

    #endregion

    #region Fields

    private readonly ToolButtonService _toolButtonService;
    private readonly ToolButtonFactory _toolButtonFactory;
    private readonly ToolGroupService _toolGroupService;
    private readonly KeystoneThinningCutTool _tool;

    #endregion

    #region Construction

    public KeystoneThinningCutMenuInitializer(
        ToolButtonService toolButtonService,
        ToolButtonFactory toolButtonFactory,
        ToolGroupService toolGroupService,
        KeystoneThinningCutTool tool) {
      _toolButtonService = toolButtonService;
      _toolButtonFactory = toolButtonFactory;
      _toolGroupService = toolGroupService;
      _tool = tool;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      // Dev-mode only: not surfaced in a clean release build.
      if (!KeystoneDevMode.IsEnabled) return;

      var groupButton = FindGroupButton();
      if (groupButton == null) {
        KeystoneLog.Warn(
            "[Keystone] KeystoneThinningCutMenuInitializer: could not locate the vanilla " +
            "'TreeCutting' menu (no built-in tree-cutting tool was found). Thinning-cut " +
            "button not added; another mod may have altered the menu.");
        return;
      }

      var button = _toolButtonFactory.Create(_tool, IconName, groupButton.ToolButtonsElement);
      _toolGroupService.AssignToGroup(_toolGroupService.GetGroup(KeystoneThinningCutTool.GroupId), _tool);
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
