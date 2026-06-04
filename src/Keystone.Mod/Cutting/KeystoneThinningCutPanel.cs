using System;
using System.Collections.Generic;
using Keystone.Mod.Visualization;
using TimberUi;
using TimberUi.CommonUi;
using Timberborn.CoreUI;
using Timberborn.Localization;
using Timberborn.UILayoutSystem;
using UnityEngine.UIElements;

namespace Keystone.Mod.Cutting {

  /// <summary>
  /// Options window for the <see cref="KeystoneThinningCutTool"/>: a percentage
  /// slider ("mark this share of the eligible trees"), a "clear existing marks"
  /// toggle, and a per-species filter (one toggle per tree species, plus
  /// "Select all" / "Clear all"). Shares the dark-green right-edge nine-slice
  /// frame + header with the biome-overlay legend and the planting panel via
  /// <see cref="KeystonePanelStyle"/>; shown only while the tool is active.
  ///
  /// <para>The panel holds no policy state — every control pushes straight
  /// through to the owning tool via the callbacks passed to
  /// <see cref="Build"/>. It keeps the species <see cref="Toggle"/> elements
  /// only so the bulk buttons can flip them without firing per-toggle
  /// callbacks.</para>
  /// </summary>
  public sealed class KeystoneThinningCutPanel {

    #region Constants

    private const float PanelRight = 20f;
    private const float PanelWidth = 300f;
    private const float Padding = 8f;
    private const float SectionMarginTop = 8f;
    private const float DividerMargin = 8f;
    private const float DividerHeight = 1f;
    private const float BulkButtonGap = 6f;

    private static readonly UnityEngine.Color DividerColor = new(1f, 1f, 1f, 0.12f);

    #endregion

    #region Fields

    private readonly UILayout _uiLayout;
    private readonly ILoc _loc;

    private NineSliceVisualElement _root;

    /// <summary>Per-species toggle elements, so the bulk buttons can set them
    /// all (without notify) in one pass.</summary>
    private readonly Dictionary<string, Toggle> _speciesToggles = new();

    #endregion

    #region Construction

    /// <param name="uiLayout">Game UI layout the panel mounts into.</param>
    /// <param name="loc">Localization service for labels.</param>
    public KeystoneThinningCutPanel(UILayout uiLayout, ILoc loc) {
      _uiLayout = uiLayout;
      _loc = loc;
    }

    #endregion

    #region Build

    /// <summary>Build and mount the panel (initially hidden). Call once.</summary>
    /// <param name="titleLocKey">Loc key for the panel header.</param>
    /// <param name="percentLocKey">Loc key for the percentage slider label.</param>
    /// <param name="initialPercent">Slider starting value (0–100).</param>
    /// <param name="clearExistingLocKey">Loc key for the "clear existing" toggle.</param>
    /// <param name="initialClearExisting">"Clear existing" starting state.</param>
    /// <param name="speciesLocKey">Loc key for the species section header.</param>
    /// <param name="selectAllLocKey">Loc key for the "Select all" button.</param>
    /// <param name="clearAllLocKey">Loc key for the "Clear all" button.</param>
    /// <param name="species">Tree species as (template, localized name) pairs.</param>
    /// <param name="onPercentChanged">Pushes a new slider value to the tool.</param>
    /// <param name="onClearExistingChanged">Pushes the toggle to the tool.</param>
    /// <param name="onSpeciesToggled">Pushes one species (template, on) to the tool.</param>
    /// <param name="onSetAllSpecies">Pushes a bulk select(true)/clear(false).</param>
    public void Build(
        string titleLocKey,
        string percentLocKey,
        int initialPercent,
        string clearExistingLocKey,
        bool initialClearExisting,
        string speciesLocKey,
        string selectAllLocKey,
        string clearAllLocKey,
        IReadOnlyList<(string Template, string DisplayName)> species,
        Action<int> onPercentChanged,
        Action<bool> onClearExistingChanged,
        Action<string, bool> onSpeciesToggled,
        Action<bool> onSetAllSpecies) {
      // Absolute wrapper pinned to the right edge, content vertically centered;
      // pickingMode Ignore so the empty margin doesn't eat map clicks.
      var wrapper = new VisualElement { name = "KeystoneThinningCutPanelWrapper" };
      wrapper.style.position = Position.Absolute;
      wrapper.style.right = 0;
      wrapper.style.top = 0;
      wrapper.style.bottom = 0;
      wrapper.style.justifyContent = Justify.Center;
      wrapper.style.alignItems = Align.FlexEnd;
      wrapper.pickingMode = PickingMode.Ignore;

      _root = wrapper.AddChild<NineSliceVisualElement>("KeystoneThinningCutPanelRoot")
          .AddClass(KeystonePanelStyle.FrameBgClass)
          .SetWidth(PanelWidth)
          .SetPadding(Padding)
          .SetMarginRight(PanelRight)
          .SetDisplay(false);

      KeystonePanelStyle.AddHeader(_root, _loc.T(titleLocKey));

      // Percentage slider (0–100). The label shows the live value.
      // SliderValues is (Low, High, Default).
      var slider = _root.AddSliderInt(
          label: _loc.T(percentLocKey),
          values: new SliderValues<int>(0, 100, initialPercent));
      slider.RegisterChangeCallback(evt => onPercentChanged(evt.newValue));

      // "Clear existing marks" (default on).
      var clearToggle = _root.AddToggle(_loc.T(clearExistingLocKey));
      clearToggle.value = initialClearExisting;
      clearToggle.RegisterValueChangedCallback(evt => onClearExistingChanged(evt.newValue));

      AddDivider();

      // Species section header.
      var speciesHeader = _root.AddGameLabel(_loc.T(speciesLocKey));
      speciesHeader.AddToClassList("text--bold");
      speciesHeader.style.marginTop = SectionMarginTop;

      BuildBulkRow(selectAllLocKey, clearAllLocKey, onSetAllSpecies);

      foreach (var (template, displayName) in species) {
        var capturedTemplate = template;   // fresh per iteration; safe to close over
        var toggle = _root.AddToggle(displayName);
        toggle.value = true;               // every species starts selected
        toggle.RegisterValueChangedCallback(evt => onSpeciesToggled(capturedTemplate, evt.newValue));
        _speciesToggles[template] = toggle;
      }

      _uiLayout.AddAbsoluteItem(wrapper);
    }

    /// <summary>"Select all" / "Clear all" buttons, side by side. Each flips
    /// every species toggle (without notify, to avoid N per-toggle callbacks)
    /// and pushes a single bulk update to the tool.</summary>
    private void BuildBulkRow(string selectAllLocKey, string clearAllLocKey,
                              Action<bool> onSetAllSpecies) {
      var row = _root.AddRow();
      row.AddGameButton(_loc.T(selectAllLocKey), () => SetAll(true, onSetAllSpecies));
      var clearButton = row.AddGameButton(_loc.T(clearAllLocKey), () => SetAll(false, onSetAllSpecies));
      clearButton.style.marginLeft = BulkButtonGap;
    }

    private void SetAll(bool active, Action<bool> onSetAllSpecies) {
      foreach (var toggle in _speciesToggles.Values) {
        toggle.SetValueWithoutNotify(active);
      }
      onSetAllSpecies(active);
    }

    private void AddDivider() {
      var divider = new VisualElement { name = "KeystoneThinningCutDivider" };
      divider.style.height = DividerHeight;
      divider.style.marginTop = DividerMargin;
      divider.style.marginBottom = DividerMargin;
      divider.style.backgroundColor = DividerColor;
      _root.Add(divider);
    }

    #endregion

    #region Visibility

    /// <summary>Show or hide the panel. No-op before <see cref="Build"/>.</summary>
    public void SetVisible(bool visible) {
      _root?.SetDisplay(visible);
    }

    #endregion

  }

}
