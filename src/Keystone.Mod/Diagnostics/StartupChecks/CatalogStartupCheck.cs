using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Buildings;
using Keystone.Core.Flora;
using Keystone.Mod.Buildings;
using Keystone.Mod.Flora;
using Keystone.Mod.Recipes;

namespace Keystone.Mod.Diagnostics.StartupChecks {

  /// <summary>
  /// Verifies that the catalogs Keystone populates from JSON blueprints
  /// (and from <c>BlockObjectSpec</c> scans of vanilla content) came
  /// back non-empty. The motivating failure mode was a single
  /// JSON-property rename on <see cref="KeystoneBiomeLevelsSpec"/>
  /// that produced an empty <see cref="BiomeLevelTable"/> -- no
  /// errors logged, no biome content spawning, days to diagnose.
  ///
  /// <para>Each catalog is forced through <c>EnsurePostLoaded</c>
  /// where it exposes one, so the check is robust to Bindito's
  /// non-deterministic <c>PostLoad</c> order.</para>
  /// </summary>
  public sealed class CatalogStartupCheck : IStartupCheck {

    private readonly BiomeLevelTable _biomeLevels;
    private readonly BiomeLevelCatalog _biomeLevelCatalog;
    private readonly FlourishCatalog _flourishCatalog;
    private readonly FloraCatalog _floraCatalog;
    private readonly FloraCatalogLoader _floraCatalogLoader;
    private readonly BuildingCatalog _buildingCatalog;
    private readonly BuildingCatalogLoader _buildingCatalogLoader;

    public CatalogStartupCheck(
        BiomeLevelTable biomeLevels,
        BiomeLevelCatalog biomeLevelCatalog,
        FlourishCatalog flourishCatalog,
        FloraCatalog floraCatalog,
        FloraCatalogLoader floraCatalogLoader,
        BuildingCatalog buildingCatalog,
        BuildingCatalogLoader buildingCatalogLoader) {
      _biomeLevels = biomeLevels;
      _biomeLevelCatalog = biomeLevelCatalog;
      _flourishCatalog = flourishCatalog;
      _floraCatalog = floraCatalog;
      _floraCatalogLoader = floraCatalogLoader;
      _buildingCatalog = buildingCatalog;
      _buildingCatalogLoader = buildingCatalogLoader;
    }

    /// <inheritdoc />
    public string Category => "Catalogs";

    /// <inheritdoc />
    public bool IsReady =>
        _biomeLevelCatalog.IsLoaded
        && _flourishCatalog.IsLoaded
        && _floraCatalogLoader.IsLoaded
        && _buildingCatalogLoader.IsLoaded;

    /// <inheritdoc />
    public IEnumerable<StartupFinding> Run() {
      if (_biomeLevels.Count == 0) {
        yield return new StartupFinding(
            StartupFindingSeverity.Error,
            "Keystone couldn't load its biome rules. No ecology content " +
            "will appear on the map.",
            DetailedMessage:
                "BiomeLevelTable is empty; likely a JSON property rename " +
                "on KeystoneBiomeLevelsSpec entries.");
      }

      if (_flourishCatalog.AllClassA.Count == 0
          && _flourishCatalog.AllClassB.Count == 0
          && _flourishCatalog.AllClassC.Count == 0
          && _flourishCatalog.AllClassD.Count == 0) {
        yield return new StartupFinding(
            StartupFindingSeverity.Error,
            "Keystone couldn't load its content recipes. No ecology " +
            "content will spawn.",
            DetailedMessage:
                "FlourishCatalog has zero recipes across all classes; " +
                "KeystoneRecipeBookSpec blueprints likely failed to deserialise.");
      }

      if (_floraCatalog.Count == 0) {
        yield return new StartupFinding(
            StartupFindingSeverity.Warning,
            "Keystone couldn't catalog the vanilla flora. Some debug and " +
            "placement features may not work.",
            DetailedMessage:
                "FloraCatalog is empty (cursor debug panel + Class D " +
                "placement tool depend on it).");
      }

      if (_buildingCatalog.Count == 0) {
        yield return new StartupFinding(
            StartupFindingSeverity.Warning,
            "Keystone couldn't catalog the buildings. Cross-faction " +
            "building visibility may not work.",
            DetailedMessage:
                "BuildingCatalog is empty (cross-faction building visibility " +
                "+ cursor debug panel classification depend on it).");
      }
    }

  }

}
