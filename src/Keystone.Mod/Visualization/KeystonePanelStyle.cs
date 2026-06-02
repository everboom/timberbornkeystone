using TimberUi;
using UnityEngine.UIElements;

namespace Keystone.Mod.Visualization {

  /// <summary>
  /// Shared visual treatment for Keystone's standalone right-edge panels
  /// (the biome-overlay legend and the mixed-planting options panel) so they
  /// read as one family: the game's dark-green, slightly-transparent
  /// nine-slice frame (<see cref="FrameBgClass"/>) and the game's own header
  /// label style (<see cref="AddHeader"/>).
  /// </summary>
  internal static class KeystonePanelStyle {

    #region Constants

    /// <summary>Nine-slice background class for a standalone info panel — the
    /// dark-green, slightly-transparent look (QuickNotificationPanel's normal
    /// state), as opposed to the brown one which reads as a button.</summary>
    public const string FrameBgClass = "square-large--green";

    private const float HeaderMarginBottom = 6f;

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

  }

}
