using System;
using System.Collections.Generic;
using Keystone.Core.Planting;
using Keystone.Mod.Visualization;
using Timberborn.EntitySystem;
using Timberborn.Localization;
using Timberborn.NaturalResources;
using Timberborn.Planting;
using Timberborn.PlantingUI;
using Timberborn.SelectionSystem;
using Timberborn.SelectionToolSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.TerrainQueryingSystem;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using Timberborn.UILayoutSystem;
using UnityEngine;

namespace Keystone.Mod.Planting {

  /// <summary>
  /// Player-facing mixed-planting brush: drag-select an area and each tile
  /// is queued for planting with a random pick from the enabled species
  /// (or left empty, when "Allow gaps" is on). A Keystone reimplementation
  /// of the third-party "Forest Tool" concept (see
  /// <c>docs/private/foresttool.md</c>), extended to crops and built on
  /// the clean vanilla planting services rather than Forest Tool's static
  /// global state. Concrete variants (crops; trees and bushes) fix the
  /// species category via <see cref="IsMember"/>.
  ///
  /// <para><b>How planting happens.</b> The tool only writes planting
  /// <em>marks</em> through <see cref="PlantingService"/> — the same path
  /// the vanilla planting tools use — so beavers fulfil them and every
  /// downstream system (growth, yields, science gates) behaves normally.
  /// Nothing is force-spawned. Per-tile species choice comes from the
  /// Core <see cref="PlantingPalette"/>; the draw uses a plain
  /// <see cref="Random"/> because it fires on a player click, not inside a
  /// simulation tick.</para>
  /// </summary>
  public abstract class KeystonePlantingToolBase : ITool, ILoadableSingleton, IToolDescriptor {

    #region Constants

    /// <summary>Vanilla planting cursor asset (same one Forest Tool and
    /// the built-in planting tools use).</summary>
    private const string CursorKey = "PlantingCursor";

    private const string SelectAllLocKey = "Tool.Keystone.Planting.SelectAll";
    private const string ClearAllLocKey = "Tool.Keystone.Planting.ClearAll";
    private const string ClearingsLocKey = "Tool.Keystone.Planting.Clearings";

    private static readonly Color PreviewColor = new(0f, 0.8f, 0f, 1f);

    #endregion

    #region Fields

    private readonly PlantingService _plantingService;
    private readonly PlantingAreaValidator _plantingAreaValidator;
    private readonly TerrainAreaService _terrainAreaService;
    private readonly AreaHighlightingService _areaHighlightingService;
    private readonly TemplateService _templateService;
    private readonly EventBus _eventBus;
    private readonly UILayout _uiLayout;
    private readonly BiomeOverlayToggle _biomeOverlayToggle;

    /// <summary>Localization service. Exposed to subclasses for their
    /// loc-key-backed tool description.</summary>
    protected readonly ILoc Loc;

    private readonly SelectionToolProcessor _selectionToolProcessor;
    private readonly PlantingPalette _palette = new();
    private readonly System.Random _rng = new();

    /// <summary>(template, localized display name) pairs, in display
    /// order. Built at <see cref="Load"/>; drives the options panel.</summary>
    private readonly List<(string Template, string DisplayName)> _species = new();

    private KeystonePlantingPanel _panel;

    #endregion

    #region Construction

    protected KeystonePlantingToolBase(
        SelectionToolProcessorFactory selectionToolProcessorFactory,
        PlantingService plantingService,
        PlantingAreaValidator plantingAreaValidator,
        TerrainAreaService terrainAreaService,
        AreaHighlightingService areaHighlightingService,
        TemplateService templateService,
        EventBus eventBus,
        UILayout uiLayout,
        BiomeOverlayToggle biomeOverlayToggle,
        ILoc loc) {
      _plantingService = plantingService;
      _plantingAreaValidator = plantingAreaValidator;
      _terrainAreaService = terrainAreaService;
      _areaHighlightingService = areaHighlightingService;
      _templateService = templateService;
      _eventBus = eventBus;
      _uiLayout = uiLayout;
      _biomeOverlayToggle = biomeOverlayToggle;
      Loc = loc;
      _selectionToolProcessor = selectionToolProcessorFactory.Create(
          PreviewCallback, ActionCallback, ShowNoneCallback, CursorKey);
    }

    #endregion

    #region Subclass contract

    /// <summary>Loc key for the tool's display name (button tooltip title).</summary>
    protected abstract string TitleLocKey { get; }

    /// <summary>Loc key for the tool's description.</summary>
    protected abstract string DescriptionLocKey { get; }

    /// <summary>Loc key for the options-panel header.</summary>
    protected abstract string PanelTitleLocKey { get; }

    /// <summary>True if <paramref name="plantable"/> belongs to this tool's
    /// category (e.g. crops, or trees and bushes). Decides which species
    /// the brush offers.</summary>
    protected abstract bool IsMember(PlantableSpec plantable);

    /// <summary>Public view of <see cref="IsMember"/>, so the menu
    /// initializer can locate a vanilla planting tool of this tool's
    /// category (and thereby the group button to inject into) without
    /// duplicating the category predicate.</summary>
    public bool IsCategoryMember(PlantableSpec plantable) => IsMember(plantable);

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      BuildSpeciesList();
    }

    /// <summary>Enumerate the active faction's plantables of this tool's
    /// category and seed the palette. Mirrors the vanilla
    /// <c>ForestryButton</c>/<c>FieldsButton</c> filter (feature-toggle
    /// usable, ordered by <see cref="NaturalResourceSpec.Order"/>).</summary>
    private void BuildSpeciesList() {
      var rows = new List<(string Template, int Order, string DisplayName)>();
      foreach (var plantable in _templateService.GetAll<PlantableSpec>()) {
        if (!plantable.HasSpec<NaturalResourceSpec>()) continue;
        var naturalResource = plantable.GetSpec<NaturalResourceSpec>();
        if (!naturalResource.UsableWithCurrentFeatureToggles) continue;
        if (!IsMember(plantable)) continue;
        rows.Add((plantable.TemplateName, naturalResource.Order, ResolveDisplayName(plantable)));
      }
      rows.Sort((a, b) => a.Order.CompareTo(b.Order));
      foreach (var row in rows) {
        _palette.Add(row.Template);
        _species.Add((row.Template, row.DisplayName));
      }
    }

    /// <summary>Localized display name from the plantable's
    /// <see cref="LabeledEntitySpec"/>, falling back to the raw template
    /// name when the label is absent or unlocalized.</summary>
    private string ResolveDisplayName(PlantableSpec plantable) {
      if (plantable.HasSpec<LabeledEntitySpec>()) {
        var key = plantable.GetSpec<LabeledEntitySpec>().DisplayNameLocKey;
        if (!string.IsNullOrEmpty(key)) {
          var text = Loc.T(key);
          if (!string.IsNullOrEmpty(text)) return text;
        }
      }
      return plantable.TemplateName;
    }

    #endregion

    #region ITool / IToolDescriptor

    /// <inheritdoc />
    public ToolDescription DescribeTool() {
      return new ToolDescription.Builder(Loc.T(TitleLocKey))
          .AddSection(Loc.T(DescriptionLocKey))
          .Build();
    }

    /// <inheritdoc />
    public void Enter() {
      // The options panel shares the right-edge slot the biome legend
      // uses, so force the overlay off rather than have the two stack.
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

    /// <summary>Lazily build + mount the options panel on first entry, so
    /// a tool whose button is never wired (the trees/bushes variant, while
    /// it is held back) builds no UI at all.</summary>
    private void EnsurePanel() {
      if (_panel != null) return;
      _panel = new KeystonePlantingPanel(_uiLayout, Loc, _palette);
      _panel.Build(PanelTitleLocKey, SelectAllLocKey, ClearAllLocKey, ClearingsLocKey, _species);
    }

    #endregion

    #region Selection callbacks

    /// <summary>Hover preview: tint every in-area tile with the planting
    /// color.</summary>
    private void PreviewCallback(IEnumerable<Vector3Int> inputBlocks, Ray ray) {
      foreach (var tile in _terrainAreaService.InMapLeveledCoordinates(inputBlocks, ray)) {
        _areaHighlightingService.DrawTile(tile, PreviewColor);
      }
      _areaHighlightingService.Highlight();
    }

    /// <summary>Commit: per tile, draw a species (or a gap) from the
    /// palette and write the corresponding planting mark.</summary>
    private void ActionCallback(IEnumerable<Vector3Int> inputBlocks, Ray ray) {
      foreach (var tile in _terrainAreaService.InMapLeveledCoordinates(inputBlocks, ray)) {
        var species = _palette.Choose((float)_rng.NextDouble());
        if (species == null) {
          // Gap (or nothing enabled): clear any existing mark so a
          // re-drag with gaps on can carve holes in a planted area.
          _plantingService.UnsetPlantingCoordinates(tile);
        } else if (_plantingAreaValidator.CanPlant(tile, species)) {
          _plantingService.SetPlantingCoordinates(tile, species);
        }
      }
      // Aggregate refresh event the planting UI listens for (matches the
      // vanilla MarkArea path and Forest Tool).
      _eventBus.Post(new PlantingAreaMarkedEvent());
    }

    private void ShowNoneCallback() {
      _areaHighlightingService.UnhighlightAll();
    }

    #endregion

  }

}
