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
  /// Options window for the <see cref="KeystoneLoggingTool"/>. Top to
  /// bottom: the per-species filter (one toggle per tree species, plus
  /// "Select all" / "Clear all"), then — below a divider — the other options:
  /// a percentage slider with a live <c>X%</c> readout to its right, and an
  /// "Override existing marks" toggle. Shares the right-edge nine-slice frame,
  /// header, geometry, and bulk-button look with the planting panel and biome
  /// legend via <see cref="KeystonePanelStyle"/>; shown only while the tool is
  /// active.
  ///
  /// <para>The panel holds no policy state — every control pushes straight
  /// through to the owning tool via the callbacks passed to
  /// <see cref="Build"/>. It keeps the species <see cref="Toggle"/> elements
  /// only so the bulk buttons can flip them without firing per-toggle
  /// callbacks.</para>
  /// </summary>
  public sealed class KeystoneLoggingPanel {

    #region Constants

    private const float DividerMargin = 8f;
    private const float DividerHeight = 1f;
    private const float BulkRowMarginTop = 2f;
    private const float BulkRowMarginBottom = 8f;

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
    public KeystoneLoggingPanel(UILayout uiLayout, ILoc loc) {
      _uiLayout = uiLayout;
      _loc = loc;
    }

    #endregion

    #region Build

    /// <summary>Build and mount the panel (initially hidden). Call once.</summary>
    /// <param name="titleLocKey">Loc key for the panel header.</param>
    /// <param name="percentLocKey">Loc key for the percentage slider label.</param>
    /// <param name="initialPercent">Slider starting value (0–100).</param>
    /// <param name="overrideLocKey">Loc key for the "Override existing" toggle.</param>
    /// <param name="initialOverride">"Override existing" starting state.</param>
    /// <param name="speciesLocKey">Loc key for the species section header.</param>
    /// <param name="selectAllLocKey">Loc key for the "Select all" button.</param>
    /// <param name="clearAllLocKey">Loc key for the "Clear all" button.</param>
    /// <param name="species">Tree species as (template, localized name) pairs.</param>
    /// <param name="onPercentChanged">Pushes a new slider value to the tool.</param>
    /// <param name="onOverrideChanged">Pushes the toggle to the tool.</param>
    /// <param name="onSpeciesToggled">Pushes one species (template, on) to the tool.</param>
    /// <param name="onSetAllSpecies">Pushes a bulk select(true)/clear(false).</param>
    public void Build(
        string titleLocKey,
        string percentLocKey,
        int initialPercent,
        string overrideLocKey,
        bool initialOverride,
        string speciesLocKey,
        string selectAllLocKey,
        string clearAllLocKey,
        IReadOnlyList<(string Template, string DisplayName)> species,
        Action<int> onPercentChanged,
        Action<bool> onOverrideChanged,
        Action<string, bool> onSpeciesToggled,
        Action<bool> onSetAllSpecies) {
      _root = KeystonePanelStyle.BuildRightEdgePanel("KeystoneLoggingPanelRoot", out var wrapper);

      KeystonePanelStyle.AddHeader(_root, _loc.T(titleLocKey));

      // --- Tree-type filter first ---
      var speciesHeader = _root.AddGameLabel(_loc.T(speciesLocKey));
      speciesHeader.AddToClassList("text--bold");

      BuildBulkRow(selectAllLocKey, clearAllLocKey, onSetAllSpecies);

      foreach (var (template, displayName) in species) {
        var capturedTemplate = template;   // fresh per iteration; safe to close over
        var toggle = _root.AddToggle(displayName);
        toggle.value = true;               // every species starts selected
        toggle.RegisterValueChangedCallback(evt => onSpeciesToggled(capturedTemplate, evt.newValue));
        _speciesToggles[template] = toggle;
      }

      AddDivider();

      // --- Then the other options ---
      // Percentage slider (0–100) with a live "X%" readout to its right.
      // SliderValues is (Low, High, Default).
      var slider = _root.AddSliderInt(
          label: _loc.T(percentLocKey),
          values: new SliderValues<int>(0, 100, initialPercent));
      slider.AddEndLabel(value => value + "%");
      slider.RegisterChangeCallback(evt => onPercentChanged(evt.newValue));

      var overrideToggle = _root.AddToggle(_loc.T(overrideLocKey));
      overrideToggle.value = initialOverride;
      overrideToggle.RegisterValueChangedCallback(evt => onOverrideChanged(evt.newValue));

      _uiLayout.AddAbsoluteItem(wrapper);
    }

    /// <summary>"Select all" / "Clear all" buttons, side by side, using the
    /// shared bulk-button look. Each flips every species toggle (without
    /// notify, to avoid N per-toggle callbacks) and pushes a single bulk update
    /// to the tool.</summary>
    private void BuildBulkRow(string selectAllLocKey, string clearAllLocKey,
                              Action<bool> onSetAllSpecies) {
      var row = _root.AddRow();
      row.style.marginTop = BulkRowMarginTop;
      row.style.marginBottom = BulkRowMarginBottom;

      row.Add(KeystonePanelStyle.MakeBulkButton(
          _loc.T(selectAllLocKey), () => SetAll(true, onSetAllSpecies)));

      var clearButton = KeystonePanelStyle.MakeBulkButton(
          _loc.T(clearAllLocKey), () => SetAll(false, onSetAllSpecies));
      clearButton.style.marginLeft = KeystonePanelStyle.BulkButtonGap;
      row.Add(clearButton);
    }

    private void SetAll(bool active, Action<bool> onSetAllSpecies) {
      foreach (var toggle in _speciesToggles.Values) {
        toggle.SetValueWithoutNotify(active);
      }
      onSetAllSpecies(active);
    }

    private void AddDivider() {
      var divider = new VisualElement { name = "KeystoneLoggingDivider" };
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
