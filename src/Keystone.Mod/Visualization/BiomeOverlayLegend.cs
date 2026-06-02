using Keystone.Core.Biomes;
using TimberUi;
using Timberborn.CoreUI;
using Timberborn.SingletonSystem;
using Timberborn.UILayoutSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace Keystone.Mod.Visualization {

  /// <summary>
  /// Legend panel for the biome overlay. Shows a colored swatch and
  /// name for each biome. Visible only while the overlay toggle is
  /// active. Vertically centered on the right edge of the screen.
  ///
  /// <para>Shares the standalone-panel look (dark-green nine-slice frame +
  /// title header) with the mixed-planting options panel via
  /// <see cref="KeystonePanelStyle"/>.</para>
  ///
  /// <para><b>Mounting.</b> Inserted via
  /// <see cref="UILayout.AddAbsoluteItem"/> rather than a fresh
  /// <c>RootVisualElementProvider.CreateEmpty</c> UIDocument. The
  /// distinction matters because Timberborn's global panel
  /// stylesheets (which define <c>square-large--green</c>,
  /// <c>game-text-normal</c>, etc.) are attached to the
  /// <c>Common/GameUI</c> visual tree backing <c>UILayout</c>. A
  /// detached UIDocument inherits none of those classes, so a
  /// <see cref="NineSliceVisualElement"/> with the background class
  /// renders as nothing. Beaver Chronicles uses the same mount path
  /// (<c>UILayout.AddBottomRight</c>) for the same reason.</para>
  /// </summary>
  public sealed class BiomeOverlayLegend : ILoadableSingleton, IUpdatableSingleton {

    #region Constants

    private const float PanelRight = 20f;
    private const float PanelWidth = 170f;
    // Per-side panel padding. Left and bottom are roomier; the top stays
    // modest because the header already carries its own bottom margin.
    private const float PaddingTop = 6f;
    private const float PaddingRight = 8f;
    private const float PaddingBottom = 12f;
    private const float PaddingLeft = 12f;
    private const float SwatchSize = 14f;
    private const float SwatchTextGap = 8f;
    private const float RowSpacing = 3f;
    private const float FooterMarginTop = 6f;
    private const float FooterOpacity = 0.65f;

    private static readonly Color SwatchBorderColor = new(0f, 0f, 0f, 0.45f);

    #endregion

    #region Fields

    private readonly BiomeOverlayToggle _toggle;
    private readonly UILayout _uiLayout;
    private NineSliceVisualElement _root;
    private bool _wasVisible;

    #endregion

    #region Construction

    public BiomeOverlayLegend(BiomeOverlayToggle toggle, UILayout uiLayout) {
      _toggle = toggle;
      _uiLayout = uiLayout;
    }

    #endregion

    #region ILoadableSingleton

    public void Load() {
      // Wrapper fills the absolute-items overlay so flex can centre
      // the legend vertically against the right edge. No TimberUi
      // helper for position/anchor, so styled raw.
      var wrapper = new VisualElement { name = "BiomeLegendWrapper" }
          .JustifyContent()
          .AlignItems(Align.FlexEnd);
      wrapper.style.position = Position.Absolute;
      wrapper.style.right = 0;
      wrapper.style.top = 0;
      wrapper.style.bottom = 0;
      wrapper.pickingMode = PickingMode.Ignore;

      _root = wrapper.AddChild<NineSliceVisualElement>("BiomeLegendRoot")
          .AddClass(KeystonePanelStyle.FrameBgClass)
          .SetWidth(PanelWidth)
          .SetMarginRight(PanelRight)
          .SetDisplay(false);
      _root.style.paddingTop = PaddingTop;
      _root.style.paddingRight = PaddingRight;
      _root.style.paddingBottom = PaddingBottom;
      _root.style.paddingLeft = PaddingLeft;

      KeystonePanelStyle.AddHeader(_root, "Biomes");

      AddEntry("Forest", BiomeOverlayRenderer.ColorFor(BiomeKind.Forest));
      AddEntry("Grassland", BiomeOverlayRenderer.ColorFor(BiomeKind.Grassland));
      AddEntry("Riparian", BiomeOverlayRenderer.ColorFor(BiomeKind.Riparian));
      AddEntry("Wetland", BiomeOverlayRenderer.ColorFor(BiomeKind.Wetland));
      AddEntry("River / Lake", BiomeOverlayRenderer.ColorFor(BiomeKind.River));
      AddEntry("Monoculture", BiomeOverlayRenderer.ColorFor(BiomeKind.Monoculture));
      AddEntry("Dry", BiomeOverlayRenderer.ColorFor(BiomeKind.Dry));
      AddEntry("Contaminated", BiomeOverlayRenderer.ColorFor(BiomeKind.Contaminated));
      AddEntry("Cave", BiomeOverlayRenderer.ColorFor(BiomeKind.Cave));

      var footer = _root.AddGameLabel("Size = maturity");
      footer.style.marginTop = FooterMarginTop;
      footer.style.opacity = FooterOpacity;

      _uiLayout.AddAbsoluteItem(wrapper);
    }

    private void AddEntry(string label, Color color) {
      var row = _root.AddRow()
          .AlignItems()
          .SetMarginBottom(RowSpacing);

      var swatch = row.AddChild<VisualElement>()
          .SetSize(SwatchSize)
          .SetBorder(SwatchBorderColor, 1f)
          .SetMarginRight(SwatchTextGap);
      swatch.style.backgroundColor = color;
      swatch.style.borderTopLeftRadius = 2;
      swatch.style.borderTopRightRadius = 2;
      swatch.style.borderBottomLeftRadius = 2;
      swatch.style.borderBottomRightRadius = 2;

      row.AddGameLabel(label);
    }

    #endregion

    #region IUpdatableSingleton

    public void UpdateSingleton() {
      var visible = _toggle.Enabled;
      if (visible == _wasVisible) return;
      _root.SetDisplay(visible);
      _wasVisible = visible;
    }

    #endregion

  }

}
