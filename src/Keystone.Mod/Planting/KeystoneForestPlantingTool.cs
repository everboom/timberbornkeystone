using Keystone.Mod.Visualization;
using Timberborn.Forestry;
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
  /// Mixed-planting brush for <b>trees and bushes</b> — surfaced in the
  /// vanilla forester planting menu (tool group <see cref="GroupId"/>),
  /// where the base game groups bushes together with trees. Offers every
  /// faction tree (<see cref="TreeComponentSpec"/>) and bush
  /// (<see cref="BushSpec"/>).
  ///
  /// <para><b>Held back.</b> This variant directly overlaps the existing
  /// third-party "Forest Tool" mod. It is fully built and wired into DI,
  /// but its toolbar button is intentionally not registered yet — see
  /// <see cref="KeystonePlantingMenuInitializer"/> (the
  /// <c>EnableForestVariant</c> gate) — pending a conversation with Forest
  /// Tool's author. The crop variant, which is net-new territory, ships
  /// live in the meantime.</para>
  /// </summary>
  public sealed class KeystoneForestPlantingTool : KeystonePlantingToolBase {

    /// <summary>Vanilla tool-group id for the forester tree/bush planting
    /// menu (the group <c>ForestryButton</c> builds).</summary>
    public const string GroupId = "Forestry";

    /// <inheritdoc />
    protected override string TitleLocKey => "Tool.Keystone.PlantForest.DisplayName";

    /// <inheritdoc />
    protected override string DescriptionLocKey => "Tool.Keystone.PlantForest.Description";

    /// <inheritdoc />
    protected override string PanelTitleLocKey => "Tool.Keystone.PlantForest.PanelTitle";

    public KeystoneForestPlantingTool(
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
      return plantable.HasSpec<TreeComponentSpec>() || plantable.HasSpec<BushSpec>();
    }

  }

}
