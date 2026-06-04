using System;
using TimberUi;
using Timberborn.CoreUI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Keystone.Mod.Visualization {

  /// <summary>
  /// Shared visual treatment for Keystone's standalone right-edge tool panels
  /// (the biome-overlay legend, the mixed-planting options panel, the
  /// logging options panel) so they read as one family: the game's
  /// dark-green nine-slice frame (<see cref="FrameBgClass"/>), the game's own
  /// header style (<see cref="AddHeader"/>), one shared panel geometry
  /// (width / padding / right-margin / row spacing), and one bulk-button look
  /// (<see cref="MakeBulkButton"/>).
  /// </summary>
  internal static class KeystonePanelStyle {

    #region Frame + geometry

    /// <summary>Nine-slice background class for a standalone info panel — the
    /// dark-green, slightly-transparent look (QuickNotificationPanel's normal
    /// state), as opposed to the brown one which reads as a button.</summary>
    public const string FrameBgClass = "square-large--green";

    /// <summary>Standard right-edge panel width, padding, right margin, and
    /// inter-row spacing. Shared so every Keystone tool panel lines up.</summary>
    public const float StandardWidth = 340f;
    public const float StandardPadding = 8f;
    public const float StandardRightMargin = 20f;
    public const float RowSpacing = 4f;

    private const float HeaderMarginBottom = 6f;

    #endregion

    #region Bulk buttons

    // Native button USS classes (Timberborn.CoreUI / global panel stylesheet,
    // reachable because the mod compiles against the publicized Timberborn.CoreUI).
    private const string ButtonGameClass = "button-game";
    private const string ButtonGameTextClass = "game-text-normal";
    private const string ButtonGameSizeClass = "button-game--medium";

    /// <summary>Gap between two side-by-side bulk buttons.</summary>
    public const float BulkButtonGap = 6f;
    private const float BulkButtonPaddingX = 10f;
    private const float BulkButtonPaddingY = 3f;

    #endregion

    #region Header

    /// <summary>Append a title header to <paramref name="root"/> using the
    /// game's own header style — TimberUi's <c>AddLabelHeader</c>, i.e. the
    /// <c>text--header</c> USS class from the global panel stylesheet — rather
    /// than a hand-rolled look, so it matches vanilla section headers. Only a
    /// bottom margin is added, for spacing from the panel body.</summary>
    public static Label AddHeader(VisualElement root, string text) {
      var header = root.AddLabelHeader(text);
      header.style.marginBottom = HeaderMarginBottom;
      return header;
    }

    #endregion

    #region Right-edge panel frame

    /// <summary>Build the standard right-edge panel: an absolute, vertically-
    /// centered wrapper (<see cref="PickingMode.Ignore"/> so its empty margin
    /// doesn't eat map clicks) holding the dark-green nine-slice frame, sized +
    /// padded to the shared geometry and initially hidden. Returns the frame;
    /// the caller adds content to it and mounts <paramref name="wrapper"/> via
    /// <c>UILayout.AddAbsoluteItem</c>.</summary>
    public static NineSliceVisualElement BuildRightEdgePanel(string name, out VisualElement wrapper) {
      wrapper = new VisualElement { name = name + "Wrapper" };
      wrapper.style.position = Position.Absolute;
      wrapper.style.right = 0;
      wrapper.style.top = 0;
      wrapper.style.bottom = 0;
      wrapper.style.justifyContent = Justify.Center;
      wrapper.style.alignItems = Align.FlexEnd;
      wrapper.pickingMode = PickingMode.Ignore;
      return wrapper.AddChild<NineSliceVisualElement>(name)
          .AddClass(FrameBgClass)
          .SetWidth(StandardWidth)
          .SetPadding(StandardPadding)
          .SetMarginRight(StandardRightMargin)
          .SetDisplay(false);
    }

    #endregion

    #region Bulk button factory

    /// <summary>The native orange "game" button (with the stylesheet's green
    /// hover) used for bulk Select all / Clear all actions in tool panels. Two
    /// in a row split it evenly (flexGrow + zero basis), each with its own
    /// padding and centered label, so they read as a tidy pair. A real
    /// <see cref="NineSliceButton"/> — bindable because the mod compiles against
    /// the publicized <c>Timberborn.CoreUI</c> — so its nine-slice frame
    /// renders.</summary>
    public static NineSliceButton MakeBulkButton(string text, Action onClick) {
      var button = new NineSliceButton { text = text };
      button.AddToClassList(ButtonGameClass);
      button.AddToClassList(ButtonGameTextClass);
      button.AddToClassList(ButtonGameSizeClass);
      button.clicked += onClick;
      button.style.flexGrow = 1f;
      button.style.flexBasis = 0f;
      button.style.marginTop = 0;
      button.style.marginBottom = 0;
      button.style.marginLeft = 0;
      button.style.marginRight = 0;
      button.style.paddingLeft = BulkButtonPaddingX;
      button.style.paddingRight = BulkButtonPaddingX;
      button.style.paddingTop = BulkButtonPaddingY;
      button.style.paddingBottom = BulkButtonPaddingY;
      button.style.unityTextAlign = TextAnchor.MiddleCenter;
      return button;
    }

    #endregion

  }

}
