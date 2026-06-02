using System.Collections.Generic;
using Keystone.Core.Planting;
using TimberUi;
using Timberborn.CoreUI;
using Timberborn.Localization;
using Timberborn.UILayoutSystem;
using UnityEngine.UIElements;

namespace Keystone.Mod.Planting {

  /// <summary>
  /// Options window for a Keystone mixed-planting tool: one on/off toggle
  /// per plantable species, an "All" master toggle, and an "Allow gaps"
  /// toggle. Mirrors the visual treatment of
  /// <see cref="Keystone.Mod.Visualization.BiomeOverlayLegend"/> — a
  /// right-edge <c>square-large--brown</c> nine-slice box mounted via
  /// <see cref="UILayout.AddAbsoluteItem"/> (so the global panel
  /// stylesheet classes resolve), shown only while its tool is active.
  ///
  /// <para>Toggle changes write straight through to the tool's
  /// <see cref="PlantingPalette"/> (Core); the panel holds no selection
  /// state of its own. Anchored exactly where the biome-overlay legend
  /// sits (vertically centered on the right edge) — the two never show at
  /// once because entering a planting tool forces the overlay off (see
  /// <see cref="KeystonePlantingToolBase.Enter"/>).</para>
  /// </summary>
  public sealed class KeystonePlantingPanel {

    #region Constants

    private const string FrameBgClass = "square-large--brown";
    private const float PanelRight = 20f;
    private const float PanelWidth = 210f;
    private const float Padding = 6f;
    private const float HeaderMarginBottom = 4f;
    private const float RowSpacing = 2f;
    private const float GapsMarginTop = 6f;

    #endregion

    #region Fields

    private readonly UILayout _uiLayout;
    private readonly ILoc _loc;
    private readonly PlantingPalette _palette;

    private NineSliceVisualElement _root;
    private Toggle _allToggle;
    private readonly List<(Toggle Toggle, string Template)> _speciesToggles = new();

    #endregion

    #region Construction

    /// <param name="uiLayout">Game UI layout the panel mounts into.</param>
    /// <param name="loc">Localization service for labels.</param>
    /// <param name="palette">The tool's selection policy; toggles mutate it.</param>
    public KeystonePlantingPanel(UILayout uiLayout, ILoc loc, PlantingPalette palette) {
      _uiLayout = uiLayout;
      _loc = loc;
      _palette = palette;
    }

    #endregion

    #region Build

    /// <summary>
    /// Build and mount the panel (initially hidden). Call once.
    /// </summary>
    /// <param name="titleLocKey">Loc key for the panel header.</param>
    /// <param name="allLabelLocKey">Loc key for the "All" master toggle.</param>
    /// <param name="gapsLabelLocKey">Loc key for the "Allow gaps" toggle.</param>
    /// <param name="species">Species to list, as (template name, already-
    /// localized display name) pairs, in display order.</param>
    public void Build(
        string titleLocKey,
        string allLabelLocKey,
        string gapsLabelLocKey,
        IReadOnlyList<(string Template, string DisplayName)> species) {
      // Absolute wrapper pinned to the right edge, content anchored top.
      // pickingMode Ignore on the wrapper so the empty margin doesn't eat
      // map clicks; the box itself (and its toggles) still pick normally.
      var wrapper = new VisualElement { name = "KeystonePlantingPanelWrapper" };
      wrapper.style.position = Position.Absolute;
      wrapper.style.right = 0;
      wrapper.style.top = 0;
      wrapper.style.bottom = 0;
      wrapper.style.justifyContent = Justify.Center;
      wrapper.style.alignItems = Align.FlexEnd;
      wrapper.pickingMode = PickingMode.Ignore;

      _root = wrapper.AddChild<NineSliceVisualElement>("KeystonePlantingPanelRoot")
          .AddClass(FrameBgClass)
          .SetWidth(PanelWidth)
          .SetPadding(Padding)
          .SetMarginRight(PanelRight)
          .SetDisplay(false);

      _root.AddGameLabel(_loc.T(titleLocKey), bold: true)
          .SetMarginBottom(HeaderMarginBottom);

      _allToggle = _root.AddToggle(_loc.T(allLabelLocKey));
      _allToggle.value = true;
      _allToggle.RegisterValueChangedCallback(evt => OnAllToggled(evt.newValue));

      foreach (var (template, displayName) in species) {
        var toggle = _root.AddToggle(displayName);
        toggle.value = _palette.IsEnabled(template);
        toggle.style.marginBottom = RowSpacing;
        var capturedTemplate = template;
        toggle.RegisterValueChangedCallback(evt => {
          _palette.SetEnabled(capturedTemplate, evt.newValue);
          SyncAllToggle();
        });
        _speciesToggles.Add((toggle, template));
      }

      var gapsToggle = _root.AddToggle(_loc.T(gapsLabelLocKey));
      gapsToggle.value = _palette.AllowGaps;
      gapsToggle.style.marginTop = GapsMarginTop;
      gapsToggle.RegisterValueChangedCallback(evt => _palette.AllowGaps = evt.newValue);

      _uiLayout.AddAbsoluteItem(wrapper);
    }

    #endregion

    #region Visibility

    /// <summary>Show or hide the panel. No-op before <see cref="Build"/>.</summary>
    public void SetVisible(bool visible) {
      _root?.SetDisplay(visible);
    }

    #endregion

    #region Toggle wiring

    /// <summary>Master toggle flipped: drive every species toggle and the
    /// palette to the new state. Uses <c>SetValueWithoutNotify</c> on the
    /// children so their individual callbacks don't re-fire.</summary>
    private void OnAllToggled(bool value) {
      _palette.SetAllEnabled(value);
      foreach (var (toggle, _) in _speciesToggles) {
        toggle.SetValueWithoutNotify(value);
      }
    }

    /// <summary>An individual species toggle changed: reflect whether
    /// every species is now enabled in the master toggle, without firing
    /// its callback.</summary>
    private void SyncAllToggle() {
      var allEnabled = true;
      foreach (var (_, template) in _speciesToggles) {
        if (!_palette.IsEnabled(template)) {
          allEnabled = false;
          break;
        }
      }
      _allToggle.SetValueWithoutNotify(allEnabled);
    }

    #endregion

  }

}
