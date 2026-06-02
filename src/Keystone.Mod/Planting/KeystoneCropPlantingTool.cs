using Keystone.Mod.Visualization;
using Timberborn.Fields;
using Timberborn.Localization;
using Timberborn.Planting;
using Timberborn.SelectionSystem;
using Timberborn.SelectionToolSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.TerrainQueryingSystem;
using Timberborn.UILayoutSystem;

namespace Keystone.Mod.Planting {

  /// <summary>
  /// Mixed-planting brush for <b>crops</b> — the live variant, surfaced in
  /// the vanilla farmhouse / field planting menu (tool group
  /// <see cref="GroupId"/>). Offers every faction crop
  /// (<see cref="CropSpec"/>); the player drag-selects field tiles and the
  /// brush queues a random mix.
  /// </summary>
  public sealed class KeystoneCropPlantingTool : KeystonePlantingToolBase {

    /// <summary>Vanilla tool-group id for the crop / field planting menu
    /// (the group <c>FieldsButton</c> builds).</summary>
    public const string GroupId = "Fields";

    /// <inheritdoc />
    protected override string TitleLocKey => "Tool.Keystone.PlantCrops.DisplayName";

    /// <inheritdoc />
    protected override string DescriptionLocKey => "Tool.Keystone.PlantCrops.Description";

    /// <inheritdoc />
    protected override string PanelTitleLocKey => "Tool.Keystone.PlantCrops.PanelTitle";

    public KeystoneCropPlantingTool(
        SelectionToolProcessorFactory selectionToolProcessorFactory,
        PlantingService plantingService,
        PlantingAreaValidator plantingAreaValidator,
        TerrainAreaService terrainAreaService,
        AreaHighlightingService areaHighlightingService,
        TemplateService templateService,
        EventBus eventBus,
        UILayout uiLayout,
        BiomeOverlayToggle biomeOverlayToggle,
        ILoc loc)
        : base(selectionToolProcessorFactory, plantingService, plantingAreaValidator,
            terrainAreaService, areaHighlightingService, templateService, eventBus, uiLayout,
            biomeOverlayToggle, loc) {
    }

    /// <inheritdoc />
    protected override bool IsMember(PlantableSpec plantable) {
      return plantable.HasSpec<CropSpec>();
    }

  }

}
