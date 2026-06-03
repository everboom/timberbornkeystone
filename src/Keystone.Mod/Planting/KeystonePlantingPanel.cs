using System;
using System.Collections.Generic;
using Keystone.Core.Planting;
using Keystone.Mod.Visualization;
using TimberUi;
using Timberborn.CoreUI;
using Timberborn.Localization;
using Timberborn.UILayoutSystem;
using UnityEngine.UIElements;

namespace Keystone.Mod.Planting {

  /// <summary>
  /// Options window for a Keystone mixed-planting tool: a per-species weight
  /// (set with native &#8722;/+ steppers and shown as a live proportion bar +
  /// percent), "Select all" / "Clear all" bulk buttons, and — below a divider —
  /// the tool-behavior toggles ("Overwrite existing plants" and its opt-in
  /// "Destroy existing plants" sub-toggle).
  /// A right-edge <c>square-large--green</c> nine-slice panel (the game's
  /// standalone-info-panel look) mounted via
  /// <see cref="UILayout.AddAbsoluteItem"/> so the global panel stylesheet
  /// classes resolve; shown only while its tool is active. The buttons are
  /// real <see cref="NineSliceButton"/>s carrying the game's own button USS
  /// classes (orange "game" button + green hover, square +/- glyphs);
  /// <c>NineSliceButton</c> is internal to Timberborn but bindable because the
  /// mod compiles against the publicized <c>Timberborn.CoreUI</c> (see
  /// <c>Keystone.Mod.csproj</c>).
  ///
  /// <para>Weight changes write straight through to the tool's
  /// <see cref="PlantingPalette"/> (Core); the panel holds no selection
  /// state of its own beyond the per-row labels it refreshes. Anchored
  /// exactly where the biome-overlay legend sits (vertically centered on
  /// the right edge) — the two never show at once because entering a
  /// planting tool forces the overlay off (see
  /// <see cref="KeystonePlantingToolBase.Enter"/>).</para>
  ///
  /// <para>Each row is <c>[name] [&#8722;] [weight] [+]  [proportion bar]
  /// [NN%]</c>. The weight number, bar, and percent show that entry's
  /// <em>share of the total weight</em>, so nudging one visibly rescales every
  /// bar — the weighted-blend principle made legible. An entry at weight 0 has
  /// an empty bar and grays out; steppers are always visible. A final
  /// "clearings" row weights how much bare ground the brush leaves, competing
  /// in the same blend (it replaced the old "Allow gaps" toggle and is not
  /// swept by the Select all / Clear all buttons).</para>
  /// </summary>
  public sealed class KeystonePlantingPanel {

    #region Option toggle

    /// <summary>One tool-behavior checkbox in the panel's options section: its
    /// label loc key, initial on/off state, and the setter that pushes changes
    /// back to the owning tool. (Kept tool-side rather than in the Core
    /// <see cref="PlantingPalette"/> because it's planting <em>behavior</em>,
    /// not species selection policy.)</summary>
    public readonly struct OptionToggle {

      /// <summary>Loc key for the checkbox label.</summary>
      public readonly string LocKey;

      /// <summary>Initial checked state.</summary>
      public readonly bool Initial;

      /// <summary>Invoked with the new value whenever the player toggles it.</summary>
      public readonly Action<bool> OnChanged;

      public OptionToggle(string locKey, bool initial, Action<bool> onChanged) {
        LocKey = locKey;
        Initial = initial;
        OnChanged = onChanged;
      }
    }

    #endregion

    #region Constants

    // Native button USS classes (Timberborn.CoreUI / global panel stylesheet,
    // reachable now that Properties/IgnoresAccessChecks.cs opens
    // Timberborn.CoreUI). The bulk buttons get the standard orange "game"
    // button (green hover for free via the stylesheet's :hover rule); the
    // steppers get the square +/- glyph buttons.
    private const string ButtonGameClass = "button-game";
    private const string ButtonGameTextClass = "game-text-normal";
    private const string ButtonGameSizeClass = "button-game--medium";
    private const string ButtonSquareClass = "button-square";
    private const string ButtonSquareSmallClass = "button-square--small";
    private const string ButtonPlusClass = "button-plus";
    private const string ButtonMinusClass = "button-minus";

    private const float PanelRight = 20f;
    private const float PanelWidth = 340f;
    private const float Padding = 8f;
    private const float RowSpacing = 4f;
    private const float BulkRowMarginBottom = 8f;
    private const float BulkRowMarginTop = 2f;
    private const float BulkButtonGap = 6f;
    private const float BulkButtonPaddingX = 10f;
    private const float BulkButtonPaddingY = 3f;

    // Row layout: [ name ] [ − ] [ weight ] [ + ]   [ proportion bar ] [ NN% ]
    private const float NameWidth = 84f;
    private const float NameGap = 6f;
    private const float WeightColWidth = 16f;
    private const float WeightColSideMargin = 3f;
    private const float BarLeftGap = 12f;     // the "bit of space" before the bar
    private const float BarRightGap = 6f;
    private const float BarHeight = 12f;
    private const float BarRadius = 3f;
    private const float PercentWidth = 38f;
    private const float PercentGap = 6f;

    /// <summary>Opacity applied to a species' name + share readout while its
    /// weight is 0 (excluded from the draw), so disabled rows read as grayed.
    /// Also reused to gray the "Cut existing" toggle while overwrite is off.</summary>
    private const float DisabledRowOpacity = 0.4f;

    // Options section (tool-behavior toggles below the weight rows): a thin
    // rule separates them from the mix, and the "Destroy existing" sub-toggle is
    // indented under "Overwrite" to read as dependent on it.
    private const float OptionsDividerMargin = 8f;
    private const float OptionsDividerHeight = 1f;
    private const float DestroyToggleIndent = 18f;
    private static readonly UnityEngine.Color OptionsDividerColor = new(1f, 1f, 1f, 0.12f);

    // Proportion bar: a recessed dark track with a bright-green fill whose
    // width is the species' share of the total weight.
    private static readonly UnityEngine.Color BarTrackColor = new(0f, 0f, 0f, 0.28f);
    private static readonly UnityEngine.Color BarFillColor = new(0.56f, 0.85f, 0.5f, 1f);

    #endregion

    #region Fields

    private readonly UILayout _uiLayout;
    private readonly ILoc _loc;
    private readonly PlantingPalette _palette;

    private NineSliceVisualElement _root;

    /// <summary>Every weight row (species, then the clearings row), in display
    /// order, so the steppers and bulk buttons can recompute every row's bar
    /// fill, weight number, percent, and grayed-out state after a palette
    /// mutation.</summary>
    private readonly List<RowControls> _rows = new();

    /// <summary>One weight row's mutable elements. <see cref="GetWeight"/> reads
    /// this row's current weight from the palette (a species' weight, or the
    /// clearings weight); the rest track its share of the total: the
    /// <see cref="Weight"/> number, the bar <see cref="Fill"/> (width = share),
    /// and the <see cref="Percent"/> readout — all grayed at weight 0.</summary>
    private sealed class RowControls {
      public Func<int> GetWeight;
      public Label Weight;
      public Label Name;
      public Label Percent;
      public VisualElement Fill;
    }

    #endregion

    #region Construction

    /// <param name="uiLayout">Game UI layout the panel mounts into.</param>
    /// <param name="loc">Localization service for labels.</param>
    /// <param name="palette">The tool's selection policy; the panel mutates it.</param>
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
    /// <param name="selectAllLocKey">Loc key for the "Select all" button.</param>
    /// <param name="clearAllLocKey">Loc key for the "Clear all" button.</param>
    /// <param name="clearingsLocKey">Loc key for the clearings (open-ground) row.</param>
    /// <param name="species">Species to list, as (template name, already-
    /// localized display name) pairs, in display order.</param>
    /// <param name="overwrite">The "Overwrite existing plants" toggle.</param>
    /// <param name="destroyExisting">The "Destroy existing plants" sub-toggle;
    /// the panel keeps it disabled + grayed while <paramref name="overwrite"/>
    /// is off (it has no effect there).</param>
    public void Build(
        string titleLocKey,
        string selectAllLocKey,
        string clearAllLocKey,
        string clearingsLocKey,
        IReadOnlyList<(string Template, string DisplayName)> species,
        OptionToggle overwrite,
        OptionToggle destroyExisting) {
      // Absolute wrapper pinned to the right edge, content anchored top.
      // pickingMode Ignore on the wrapper so the empty margin doesn't eat
      // map clicks; the box itself (and its controls) still pick normally.
      var wrapper = new VisualElement { name = "KeystonePlantingPanelWrapper" };
      wrapper.style.position = Position.Absolute;
      wrapper.style.right = 0;
      wrapper.style.top = 0;
      wrapper.style.bottom = 0;
      wrapper.style.justifyContent = Justify.Center;
      wrapper.style.alignItems = Align.FlexEnd;
      wrapper.pickingMode = PickingMode.Ignore;

      _root = wrapper.AddChild<NineSliceVisualElement>("KeystonePlantingPanelRoot")
          .AddClass(KeystonePanelStyle.FrameBgClass)
          .SetWidth(PanelWidth)
          .SetPadding(Padding)
          .SetMarginRight(PanelRight)
          .SetDisplay(false);

      KeystonePanelStyle.AddHeader(_root, _loc.T(titleLocKey));

      BuildBulkRow(selectAllLocKey, clearAllLocKey);

      foreach (var (template, displayName) in species) {
        var capturedTemplate = template;   // fresh per iteration; safe to close over
        BuildWeightRow(
            displayName,
            getWeight: () => _palette.GetWeight(capturedTemplate),
            onMinus: () => { _palette.DecrementWeight(capturedTemplate); RefreshAllRows(); },
            onPlus: () => { _palette.IncrementWeight(capturedTemplate); RefreshAllRows(); });
      }

      // Clearings: how much bare ground the brush leaves, as a peer row in the
      // blend (replaced the old "Allow gaps" toggle). The bulk buttons don't
      // touch it; the player dials it independently.
      BuildWeightRow(
          _loc.T(clearingsLocKey),
          getWeight: () => _palette.GapWeight,
          onMinus: () => { _palette.DecrementGapWeight(); RefreshAllRows(); },
          onPlus: () => { _palette.IncrementGapWeight(); RefreshAllRows(); });

      RefreshAllRows();   // initial bars / weights / percents / grayed state (needs the full total)

      BuildOptionsSection(overwrite, destroyExisting);

      _uiLayout.AddAbsoluteItem(wrapper);
    }

    /// <summary>"Select all" (weight 1) / "Clear all" (weight 0) buttons,
    /// side by side. Each re-reads every row label afterward.</summary>
    private void BuildBulkRow(string selectAllLocKey, string clearAllLocKey) {
      var row = _root.AddRow().SetMarginBottom(BulkRowMarginBottom);
      row.style.marginTop = BulkRowMarginTop;

      row.Add(MakeBulkButton(
          _loc.T(selectAllLocKey),
          () => SetAllWeightsAndRefresh(PlantingPalette.DefaultWeight)));

      var clearButton = MakeBulkButton(
          _loc.T(clearAllLocKey),
          () => SetAllWeightsAndRefresh(PlantingPalette.MinWeight));
      clearButton.style.marginLeft = BulkButtonGap;
      row.Add(clearButton);
    }

    /// <summary>Tool-behavior toggles below the weight rows, separated by a
    /// thin rule: "Overwrite existing plants" and its indented, opt-in
    /// "Destroy existing plants" sub-toggle. The sub-toggle is disabled + grayed
    /// while overwrite is off (it does nothing there) but keeps its own value
    /// so the player's choice survives toggling overwrite off and back on.
    /// Each toggle's change writes straight through to the owning tool via the
    /// <see cref="OptionToggle.OnChanged"/> setter.</summary>
    private void BuildOptionsSection(OptionToggle overwrite, OptionToggle destroyExisting) {
      var divider = new VisualElement { name = "KeystonePlantingOptionsDivider" };
      divider.style.height = OptionsDividerHeight;
      divider.style.marginTop = OptionsDividerMargin;
      divider.style.marginBottom = OptionsDividerMargin;
      divider.style.backgroundColor = OptionsDividerColor;
      _root.Add(divider);

      var overwriteToggle = _root.AddToggle(_loc.T(overwrite.LocKey));
      overwriteToggle.value = overwrite.Initial;

      var destroyToggle = _root.AddToggle(_loc.T(destroyExisting.LocKey));
      destroyToggle.value = destroyExisting.Initial;
      destroyToggle.style.marginLeft = DestroyToggleIndent;

      // "Destroy existing" only means anything when overwrite is on; mirror that
      // in the UI (interaction + opacity) without throwing away its value.
      void SetDestroyInteractable(bool overwriteOn) {
        destroyToggle.SetEnabled(overwriteOn);
        destroyToggle.style.opacity = overwriteOn ? 1f : DisabledRowOpacity;
      }

      overwriteToggle.RegisterValueChangedCallback(evt => {
        overwrite.OnChanged(evt.newValue);
        SetDestroyInteractable(evt.newValue);
      });
      destroyToggle.RegisterValueChangedCallback(evt => destroyExisting.OnChanged(evt.newValue));

      SetDestroyInteractable(overwrite.Initial);
    }

    /// <summary>One weight row: <c>[name] [&#8722;] [weight] [+]  [proportion
    /// bar] [NN%]</c>. The weight number, bar fill, and percent track this
    /// entry's share of the total weight; all gray out at weight 0. Steppers
    /// are always visible. <paramref name="getWeight"/> reads the row's weight
    /// (a species' or the clearings weight); <paramref name="onMinus"/> /
    /// <paramref name="onPlus"/> mutate it then refresh. Bar / number sizing is
    /// deferred to <see cref="RefreshAllRows"/>, which runs once every row (and
    /// thus the total) exists.</summary>
    private void BuildWeightRow(string displayName, Func<int> getWeight,
                               Action onMinus, Action onPlus) {
      var row = _root.AddRow().AlignItems();
      row.style.marginBottom = RowSpacing;

      var nameLabel = row.AddGameLabel(displayName);
      nameLabel.style.width = NameWidth;
      nameLabel.style.flexShrink = 0f;
      nameLabel.style.marginRight = NameGap;
      nameLabel.style.whiteSpace = WhiteSpace.NoWrap;
      nameLabel.style.overflow = Overflow.Hidden;
      nameLabel.style.textOverflow = TextOverflow.Ellipsis;

      var minus = MakeStepperButton(ButtonMinusClass, onMinus);
      row.Add(minus);

      var weightLabel = row.AddGameLabel(string.Empty);
      weightLabel.style.width = WeightColWidth;
      weightLabel.style.flexShrink = 0f;
      weightLabel.style.marginLeft = WeightColSideMargin;
      weightLabel.style.marginRight = WeightColSideMargin;
      weightLabel.style.unityTextAlign = UnityEngine.TextAnchor.MiddleCenter;

      var plus = MakeStepperButton(ButtonPlusClass, onPlus);
      row.Add(plus);

      var fill = AddBar(row);   // the bar's left margin is the "bit of space"

      var percentLabel = row.AddGameLabel(string.Empty);
      percentLabel.style.width = PercentWidth;
      percentLabel.style.flexShrink = 0f;
      percentLabel.style.marginLeft = PercentGap;
      percentLabel.style.unityTextAlign = UnityEngine.TextAnchor.MiddleRight;

      _rows.Add(new RowControls {
        GetWeight = getWeight, Weight = weightLabel,
        Name = nameLabel, Percent = percentLabel, Fill = fill,
      });
    }

    /// <summary>Append a proportion bar (recessed track + colored fill) to the
    /// row and return the fill element, whose width <see cref="RefreshAllRows"/>
    /// sets to the species' share. Starts empty.</summary>
    private static VisualElement AddBar(VisualElement row) {
      var track = new VisualElement { name = "Bar" };
      track.style.flexGrow = 1f;
      track.style.flexShrink = 1f;
      track.style.height = BarHeight;
      track.style.marginLeft = BarLeftGap;
      track.style.marginRight = BarRightGap;
      track.style.backgroundColor = BarTrackColor;
      SetRounded(track, BarRadius);

      var fill = new VisualElement { name = "BarFill" };
      fill.style.height = Length.Percent(100f);
      fill.style.width = Length.Percent(0f);
      fill.style.backgroundColor = BarFillColor;
      SetRounded(fill, BarRadius);

      track.Add(fill);
      row.Add(track);
      return fill;
    }

    private static void SetRounded(VisualElement element, float radius) {
      element.style.borderTopLeftRadius = radius;
      element.style.borderTopRightRadius = radius;
      element.style.borderBottomLeftRadius = radius;
      element.style.borderBottomRightRadius = radius;
    }

    /// <summary>The native orange "game" button (with the stylesheet's green
    /// hover) used for the bulk Select all / Clear all actions. A real
    /// <see cref="NineSliceButton"/> — reachable now that
    /// <c>Properties/IgnoresAccessChecks.cs</c> grants access to
    /// Timberborn.CoreUI internals — so its nine-slice frame renders.</summary>
    private static NineSliceButton MakeBulkButton(string text, Action onClick) {
      var button = new NineSliceButton { text = text };
      button.AddToClassList(ButtonGameClass);
      button.AddToClassList(ButtonGameTextClass);
      button.AddToClassList(ButtonGameSizeClass);
      button.clicked += onClick;
      // The two buttons split the row evenly (flexGrow + zero basis), each with
      // its own internal padding and centered label, so they read as a tidy
      // pair instead of two cramped content-sized buttons. The gap between them
      // is set by the caller (BulkButtonGap on the second).
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
      button.style.unityTextAlign = UnityEngine.TextAnchor.MiddleCenter;
      return button;
    }

    /// <summary>A native square &#8722;/+ stepper (<see cref="NineSliceButton"/>
    /// with the game's <c>button-square</c> + glyph classes). The glyph comes
    /// from the USS class, so no text is set.</summary>
    private static NineSliceButton MakeStepperButton(string glyphClass, Action onClick) {
      var button = new NineSliceButton();
      button.AddToClassList(ButtonSquareClass);
      button.AddToClassList(ButtonSquareSmallClass);
      button.AddToClassList(glyphClass);
      button.clicked += onClick;
      ZeroMargins(button);
      button.style.flexShrink = 0f;   // keep the square stepper from squishing
      return button;
    }

    /// <summary>Strip the native classes' default outer margins so the row
    /// stays tight; per-element spacing is added explicitly by the caller.</summary>
    private static void ZeroMargins(VisualElement button) {
      button.style.marginLeft = 0;
      button.style.marginRight = 0;
      button.style.marginTop = 0;
      button.style.marginBottom = 0;
    }

    #endregion

    #region Visibility

    /// <summary>Show or hide the panel. No-op before <see cref="Build"/>.</summary>
    public void SetVisible(bool visible) {
      _root?.SetDisplay(visible);
    }

    #endregion

    #region Weight wiring

    /// <summary>Drive every species to <paramref name="weight"/> in the palette
    /// and refresh the whole panel. Leaves the clearings weight alone.</summary>
    private void SetAllWeightsAndRefresh(int weight) {
      _palette.SetAllWeights(weight);
      RefreshAllRows();
    }

    /// <summary>Resync every row to the palette: each row's weight number, bar
    /// fill, and percent show its share of the total weight (so changing one
    /// rescales them all), and the name + number + percent dim to
    /// <see cref="DisabledRowOpacity"/> at weight 0. When every weight is 0 the
    /// bars are empty and read 0%.</summary>
    private void RefreshAllRows() {
      var total = 0;
      foreach (var row in _rows) {
        total += row.GetWeight();
      }
      foreach (var row in _rows) {
        var weight = row.GetWeight();
        var share = total > 0 ? (float)weight / total : 0f;
        row.Weight.text = weight.ToString();
        row.Fill.style.width = Length.Percent(share * 100f);
        row.Percent.text = UnityEngine.Mathf.RoundToInt(share * 100f) + "%";
        var opacity = weight == 0 ? DisabledRowOpacity : 1f;
        row.Name.style.opacity = opacity;
        row.Weight.style.opacity = opacity;
        row.Percent.style.opacity = opacity;
      }
    }

    #endregion

  }

}
