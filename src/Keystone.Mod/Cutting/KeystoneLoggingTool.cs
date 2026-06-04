using System.Collections.Generic;
using Keystone.Core.Cutting;
using Keystone.Core.Ports;
using Keystone.Mod.Visualization;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.Localization;
using Timberborn.NaturalResources;
using Timberborn.Planting;
using Timberborn.SelectionSystem;
using Timberborn.SelectionToolSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.TerrainQueryingSystem;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using Timberborn.UILayoutSystem;
using UnityEngine;

namespace Keystone.Mod.Cutting {

  /// <summary>
  /// Player-facing <b>logging brush</b>: drag-select an area and mark a
  /// player-set <em>fraction</em> of the trees in it for cutting ("thin 30% of
  /// the pines here"). The cut-side mirror of the Keystone planting brush, and
  /// a leaner take on Cordial's Cutter Tool (<c>docs/private/cuttertool.md</c>):
  /// a percentage knob instead of fixed checkered/line patterns.
  ///
  /// <para><b>How cutting happens.</b> The tool only writes the host's
  /// tree-cutting <em>area</em> (via <see cref="ICuttingAreaWriter"/> over
  /// <see cref="TreeCuttingArea"/>) — the same designation path the vanilla
  /// forester area tool uses — so the existing forester pipeline fells the
  /// trees. Nothing is force-removed. Per-tile selection is delegated to the
  /// Core <see cref="LoggingSelector"/>.</para>
  ///
  /// <para><b>Per-tile + seed.</b> Each tile's include/exclude is a pure
  /// function of its coordinate and a per-drag <see cref="_seed"/>, so the
  /// preview highlight doesn't flicker as the rectangle is sized and what the
  /// preview shows is exactly what the commit marks. The seed is bumped after
  /// each commit, so re-dragging the same area rerolls the subset.</para>
  ///
  /// <para><b>Species filter is Mod-side.</b> Resolving a tile's species (the
  /// tree on it, or its planting mark as fallback) needs game state, so it
  /// lives here, not in Core. Tiles whose species the player deselected are
  /// dropped before <see cref="LoggingSelector"/> is consulted.</para>
  ///
  /// <para><b>Dev-mode only.</b> Surfaced only when
  /// <see cref="KeystoneLoggingMenuInitializer"/>'s dev-mode check passes,
  /// for the same reason as the planting brush — it overlaps Cordial's Cutter
  /// Tool, and the design is still settling (issue #30).</para>
  /// </summary>
  public sealed class KeystoneLoggingTool : ITool, ILoadableSingleton, IToolDescriptor {

    #region Constants

    /// <summary>Vanilla tool-group id for the forester tree-cutting menu (the
    /// group <c>TreeCuttingAreaButton</c> builds).</summary>
    public const string GroupId = "TreeCutting";

    /// <summary>Vanilla tree-cutting cursor (same one the base-game area tool
    /// and Cordial's Cutter Tool use).</summary>
    private const string CursorKey = "CutTreeCursor";

    private const string TitleLocKey = "Tool.Keystone.Logging.DisplayName";
    private const string DescriptionLocKey = "Tool.Keystone.Logging.Description";
    private const string PanelTitleLocKey = "Tool.Keystone.Logging.PanelTitle";
    private const string PercentLocKey = "Tool.Keystone.Logging.Percent";
    private const string OverrideExistingLocKey = "Tool.Keystone.Logging.OverrideExisting";
    private const string SpeciesLocKey = "Tool.Keystone.Logging.Species";
    private const string SelectAllLocKey = "Tool.Keystone.Logging.SelectAll";
    private const string ClearAllLocKey = "Tool.Keystone.Logging.ClearAll";

    /// <summary>Slider starting value (percent of eligible trees to mark).
    /// Defaults to 100 — a full clear-cut of the selected species — which the
    /// player dials down to thin.</summary>
    private const int DefaultPercent = 100;

    /// <summary>Tile tint for trees the current seed/percentage will mark.</summary>
    private static readonly Color WillCutColor = new(0.95f, 0.2f, 0.15f, 1f);

    /// <summary>Tint for eligible-but-spared trees, so the player sees both
    /// what dies and what stays.</summary>
    private static readonly Color SparedColor = new(0.45f, 0.7f, 0.35f, 0.55f);

    #endregion

    #region Fields

    private readonly ICuttingAreaWriter _cuttingAreaWriter;
    private readonly IBlockService _blockService;
    private readonly PlantingService _plantingService;
    private readonly TerrainAreaService _terrainAreaService;
    private readonly AreaHighlightingService _areaHighlightingService;
    private readonly TemplateService _templateService;
    private readonly UILayout _uiLayout;
    private readonly BiomeOverlayToggle _biomeOverlayToggle;
    private readonly ILoc _loc;

    private readonly SelectionToolProcessor _selectionToolProcessor;

    /// <summary>(template, localized display name) pairs in display order;
    /// built at <see cref="Load"/>, drives the panel's species list.</summary>
    private readonly List<(string Template, string DisplayName)> _species = new();

    /// <summary>Tree species currently eligible to be cut (the panel's
    /// per-species toggles). Starts with every species selected.</summary>
    private readonly HashSet<string> _activeSpecies = new();

    /// <summary>Fraction of eligible trees to mark, in <c>[0, 1]</c>. Driven by
    /// the panel's percentage slider.</summary>
    private double _fraction = DefaultPercent / 100d;

    /// <summary>When true (default), each drag first clears the active-species
    /// cutting marks in the area before re-marking, so the drag <em>overrides</em>
    /// what's there (sets the area to ~X%) rather than accumulating across drags
    /// — and lowering the slider removes marks. Driven by a panel toggle.</summary>
    private bool _overrideExisting = true;

    /// <summary>Per-drag selection seed; fixed across one drag's preview and
    /// commit, bumped afterward so re-dragging the same area rerolls.</summary>
    private int _seed;

    private KeystoneLoggingPanel _panel;

    #endregion

    #region Construction

    public KeystoneLoggingTool(
        SelectionToolProcessorFactory selectionToolProcessorFactory,
        ICuttingAreaWriter cuttingAreaWriter,
        IBlockService blockService,
        PlantingService plantingService,
        TerrainAreaService terrainAreaService,
        AreaHighlightingService areaHighlightingService,
        TemplateService templateService,
        UILayout uiLayout,
        BiomeOverlayToggle biomeOverlayToggle,
        ILoc loc) {
      _cuttingAreaWriter = cuttingAreaWriter;
      _blockService = blockService;
      _plantingService = plantingService;
      _terrainAreaService = terrainAreaService;
      _areaHighlightingService = areaHighlightingService;
      _templateService = templateService;
      _uiLayout = uiLayout;
      _biomeOverlayToggle = biomeOverlayToggle;
      _loc = loc;
      _selectionToolProcessor = selectionToolProcessorFactory.Create(
          PreviewCallback, ActionCallback, ShowNoneCallback, CursorKey);
    }

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      BuildSpeciesList();
    }

    /// <summary>Enumerate the faction's tree plantables (every
    /// <see cref="PlantableSpec"/> carrying a <see cref="NaturalResourceSpec"/>
    /// and a <see cref="TreeComponentSpec"/>, usable with the current feature
    /// toggles, ordered by <see cref="NaturalResourceSpec.Order"/>) and start
    /// with all of them selected. Mirrors the planting tool's species build and
    /// Cordial Cutter Tool's spec service.</summary>
    private void BuildSpeciesList() {
      var rows = new List<(string Template, int Order, string DisplayName)>();
      foreach (var plantable in _templateService.GetAll<PlantableSpec>()) {
        if (!plantable.HasSpec<NaturalResourceSpec>()) continue;
        if (!plantable.HasSpec<TreeComponentSpec>()) continue;
        var naturalResource = plantable.GetSpec<NaturalResourceSpec>();
        if (!naturalResource.UsableWithCurrentFeatureToggles) continue;
        rows.Add((plantable.TemplateName, naturalResource.Order, ResolveDisplayName(plantable)));
      }
      rows.Sort((a, b) => a.Order.CompareTo(b.Order));
      foreach (var row in rows) {
        _species.Add((row.Template, row.DisplayName));
        _activeSpecies.Add(row.Template);
      }
    }

    /// <summary>Localized display name from the plantable's
    /// <see cref="LabeledEntitySpec"/>, falling back to the raw template name
    /// when the label is absent or unlocalized.</summary>
    private string ResolveDisplayName(PlantableSpec plantable) {
      if (plantable.HasSpec<LabeledEntitySpec>()) {
        var key = plantable.GetSpec<LabeledEntitySpec>().DisplayNameLocKey;
        if (!string.IsNullOrEmpty(key)) {
          var text = _loc.T(key);
          if (!string.IsNullOrEmpty(text)) return text;
        }
      }
      return plantable.TemplateName;
    }

    #endregion

    #region ITool / IToolDescriptor

    /// <inheritdoc />
    public ToolDescription DescribeTool() {
      return new ToolDescription.Builder(_loc.T(TitleLocKey))
          .AddSection(_loc.T(DescriptionLocKey))
          .Build();
    }

    /// <inheritdoc />
    public void Enter() {
      // Shares the right-edge slot with the biome legend, so force the overlay
      // off rather than have the two stack (same as the planting tool).
      _biomeOverlayToggle.Disable();
      EnsurePanel();
      _selectionToolProcessor.Enter();
      _panel.SetVisible(true);
    }

    /// <inheritdoc />
    public void Exit() {
      _selectionToolProcessor.Exit();
      _areaHighlightingService.UnhighlightAll();
      _panel?.SetVisible(false);
    }

    /// <summary>Lazily build + mount the options panel on first entry.</summary>
    private void EnsurePanel() {
      if (_panel != null) return;
      _panel = new KeystoneLoggingPanel(_uiLayout, _loc);
      _panel.Build(
          PanelTitleLocKey,
          PercentLocKey, DefaultPercent,
          OverrideExistingLocKey, _overrideExisting,
          SpeciesLocKey, SelectAllLocKey, ClearAllLocKey,
          _species,
          onPercentChanged: percent => _fraction = percent / 100d,
          onOverrideChanged: v => _overrideExisting = v,
          onSpeciesToggled: SetSpeciesActive,
          onSetAllSpecies: SetAllSpeciesActive);
    }

    #endregion

    #region Selection callbacks

    /// <summary>Hover preview: tint each eligible tree red if the current
    /// seed/percentage will mark it, dim green if it's spared. Tiles that
    /// aren't eligible trees (wrong species, empty, buildings) are left
    /// untinted.</summary>
    private void PreviewCallback(IEnumerable<Vector3Int> inputBlocks, Ray ray) {
      foreach (var tile in _terrainAreaService.InMapLeveledCoordinates(inputBlocks, ray)) {
        if (!IsEligible(tile)) continue;
        var color = LoggingSelector.ShouldMark(tile.x, tile.y, tile.z, _fraction, _seed)
            ? WillCutColor
            : SparedColor;
        _areaHighlightingService.DrawTile(tile, color);
      }
      _areaHighlightingService.Highlight();
    }

    /// <summary>Commit: collect the eligible tiles, clear their existing marks
    /// (when "clear existing" is on, so the drag sets the area to ~X% rather
    /// than accumulating), mark the seed/percentage-selected subset, then bump
    /// the seed so the next drag rerolls.</summary>
    private void ActionCallback(IEnumerable<Vector3Int> inputBlocks, Ray ray) {
      var eligible = new List<Vector3Int>();
      foreach (var tile in _terrainAreaService.InMapLeveledCoordinates(inputBlocks, ray)) {
        if (IsEligible(tile)) eligible.Add(tile);
      }

      // Override: clear the active-species marks in the area first (since
      // `eligible` already is active-species), so re-dragging — or lowering the
      // slider — removes marks rather than only adding them.
      if (_overrideExisting) {
        _cuttingAreaWriter.UnmarkForCutting(ToTuples(eligible));
      }

      var toMark = new List<Vector3Int>();
      foreach (var tile in eligible) {
        if (LoggingSelector.ShouldMark(tile.x, tile.y, tile.z, _fraction, _seed)) {
          toMark.Add(tile);
        }
      }
      _cuttingAreaWriter.MarkForCutting(ToTuples(toMark));

      _areaHighlightingService.UnhighlightAll();
      _seed++;   // reroll for the next drag (Murmur finalizer decorrelates adjacent seeds)
    }

    private void ShowNoneCallback() {
      _areaHighlightingService.UnhighlightAll();
    }

    #endregion

    #region Eligibility

    /// <summary>True if the tile holds a tree, or a planting mark, of a species
    /// the player has left selected. Tree takes precedence over a mark when
    /// both are present.</summary>
    private bool IsEligible(Vector3Int tile) {
      var species = ResolveSpecies(tile);
      return species != null && _activeSpecies.Contains(species);
    }

    /// <summary>The species at a tile: the living tree's (precedence), else the
    /// planting mark's resource (fallback), else <c>null</c>. The tree's name is
    /// the instantiated GameObject name with Unity's <c>(Clone)</c> suffix and
    /// spaces stripped — it then equals the <see cref="PlantableSpec.TemplateName"/>
    /// the species filter is keyed on. (Same string match Cordial's Cutter Tool
    /// uses; brittle if a faction's tree prefab name diverges from its template
    /// name, but it is the only handle a placed tree exposes.)</summary>
    private string ResolveSpecies(Vector3Int tile) {
      var tree = _blockService.GetBottomObjectComponentAt<TreeComponent>(tile);
      if (tree != null) return CleanName(tree.Name);
      var resource = _plantingService.GetResourceAt(tile);
      return string.IsNullOrEmpty(resource) ? null : resource;
    }

    private static string CleanName(string name) =>
        name.Replace("(Clone)", "").Replace(" ", "");

    #endregion

    #region Species selection (panel callbacks)

    /// <summary>Add/remove a species from the active set (a per-species toggle
    /// changed).</summary>
    private void SetSpeciesActive(string template, bool active) {
      if (active) _activeSpecies.Add(template);
      else _activeSpecies.Remove(template);
    }

    /// <summary>Select-all / clear-all: drive every species into or out of the
    /// active set in one shot.</summary>
    private void SetAllSpeciesActive(bool active) {
      _activeSpecies.Clear();
      if (!active) return;
      foreach (var (template, _) in _species) {
        _activeSpecies.Add(template);
      }
    }

    #endregion

    #region Helpers

    private static IEnumerable<(int X, int Y, int Z)> ToTuples(IEnumerable<Vector3Int> tiles) {
      foreach (var tile in tiles) {
        yield return (tile.x, tile.y, tile.z);
      }
    }

    #endregion

  }

}
