using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Keystone.Core.Flora;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Diagnostics.SelfTests;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Cutting;
using Timberborn.Fields;
using Timberborn.Forestry;
using Timberborn.Gathering;
using Timberborn.Growing;
using Timberborn.NaturalResources;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.NaturalResourcesMoisture;
using Timberborn.Planting;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.TerrainSystem;
using Timberborn.Yielding;
using UnityEngine;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Flora {

  /// <summary>
  /// One-shot loader that fills the Core <see cref="FloraCatalog"/>
  /// from <see cref="TemplateService.GetAll{T}"/> at
  /// <see cref="PostLoad"/>, then dumps the catalog to <c>player.log</c>
  /// for review.
  ///
  /// <para><b>Why <see cref="NaturalResourceSpec"/> as the membership
  /// criterion.</b> Broadest "map-spawned biotic element" signal --
  /// trees, bushes, mushrooms, cattails, ground cover, regardless of
  /// which mod ships them. Crops and planter-managed agriculture are
  /// included (they carry NaturalResourceSpec) but typically show
  /// alive=0 because they spawn into farmhouses, not onto the map.</para>
  ///
  /// <para><b>Two yield channels.</b> A blueprint may be both
  /// cuttable and gatherable (Pine, Maple, ChestnutTree, Mangrove).
  /// We capture both <see cref="CuttableSpec.Yielder"/> and
  /// <see cref="GatherableSpec.Yielder"/> separately so that, for
  /// example, Maple's logs (cut) and syrup (tap) both surface. The
  /// gather yield's <c>ResourceGroup</c> distinguishes
  /// <c>Tappable</c> from <c>Gatherable</c> -- the dump labels them
  /// as <c>taps</c> / <c>fruits</c> respectively.</para>
  ///
  /// <para><b>Live counts run at PostLoad.</b> Spec data is ready at
  /// Load, but per-instance counts need entities to be spawned. The
  /// census walks every column on the map (via
  /// <see cref="ITerrainService.GetAllHeightsInCell"/>) and probes
  /// nearby voxels with <see cref="IBlockService.GetObjectsAt"/>,
  /// deduping by entity reference. Alive vs dead is determined by
  /// <see cref="LivingNaturalResource.IsDead"/>.</para>
  ///
  /// <para><b>Why a map walk instead of <c>EntityComponentRegistry</c>.</b>
  /// The natural-resources runtime classes
  /// (<c>NaturalResource</c>, <c>LivingNaturalResource</c>,
  /// <c>DyingNaturalResource</c>) don't implement
  /// <c>IRegisteredComponent</c>, so <c>GetEnabled&lt;T&gt;()</c>
  /// can't reach them. The map walk is the same scan pattern
  /// <see cref="Survey.KeystoneSurveyor"/> already uses for its
  /// block-object dump; cost is one-shot at load.</para>
  /// </summary>
  public sealed class FloraCatalogLoader : IPostLoadableSingleton, IKeystoneLoadStatus {

    #region Constants

    /// <summary>Resource-group string that flags a gatherable as a tap (sap), not fruit-picking.</summary>
    private const string TappableResourceGroup = "Tappable";

    /// <summary>Resource-group string for fruit-style gatherable yield.</summary>
    private const string GatherableResourceGroup = "Gatherable";

    /// <summary>
    /// Vertical span (in voxels) we probe above each terrain surface
    /// when looking for flora. Trees are several voxels tall but their
    /// <see cref="BlockObject"/> is registered at every footprint cell;
    /// the dedupe-by-entity HashSet ensures we still count each entity
    /// once.
    /// </summary>
    private const int VerticalProbeRange = 8;

    #endregion

    #region Fields

    private readonly TemplateService _templateService;
    private readonly ITerrainService _terrain;
    private readonly IBlockService _blockService;
    private readonly FloraCatalog _catalog;

    /// <summary>True once <see cref="PostLoad"/> has finished
    /// populating <see cref="FloraCatalog"/>. Read by the startup
    /// self-check to defer until the catalog is ready, and by
    /// <see cref="LoaderSurvivalTest"/> to flag silent load
    /// failures.</summary>
    public bool IsLoaded { get; private set; }

    /// <inheritdoc />
    public string LoaderName => nameof(FloraCatalogLoader);

    #endregion

    #region Construction

    public FloraCatalogLoader(
        TemplateService templateService,
        ITerrainService terrain,
        IBlockService blockService,
        FloraCatalog catalog) {
      _templateService = templateService;
      _terrain = terrain;
      _blockService = blockService;
      _catalog = catalog;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      // Outermost try/catch around the whole PostLoad body. BuildEntries
      // has per-blueprint isolation, but a throw in _catalog.Populate
      // or anywhere else outside the inner loop would otherwise escape
      // to Bindito with IsLoaded never set, and CatalogStartupCheck
      // would silently defer forever rather than fire the dialog.
      try {
        var sw = Stopwatch.StartNew();
        var entries = BuildEntries();
        _catalog.Populate(entries);
        var catalogMs = sw.ElapsedMilliseconds;

        // Census is diagnostic-only: walks the map to count alive/dead
        // entities per blueprint for the log dump. The catalog is
        // already populated by the time we get here, so a census
        // failure (e.g. an unexpected per-entity shape from a third-
        // party flora mod's spawned instance) shouldn't take out the
        // critical path or leave IsLoaded false. Isolate it.
        var alive = new Dictionary<string, int>();
        var dead = new Dictionary<string, int>();
        long censusMs = 0;
        try {
          sw.Restart();
          (alive, dead) = Census();
          censusMs = sw.ElapsedMilliseconds;
        } catch (System.Exception ex) {
          KeystoneLog.Error(
              $"[Keystone] FloraCatalogLoader: Census threw: {ex}. " +
              "Catalog is populated; live alive/dead counts unavailable for this session.");
          KeystoneIntegrationHealth.TryRecord(
              "Diagnostic failures",
              $"FloraCatalogLoader.Census: {ex.GetType().Name}");
        }

        LogSummary(catalogMs, censusMs, alive, dead);
        LogEntries(alive, dead);
        IsLoaded = true;
      } catch (System.Exception ex) {
        LifecycleGuard.HandleError("FloraCatalogLoader.PostLoad", "Subsystem failed", ex);
      }
    }

    #endregion

    #region Catalog build

    private List<FloraEntry> BuildEntries() {
      // Iterate via TemplateService rather than
      // ISpecService.GetSpecs<NaturalResourceSpec>: the spec-service walk
      // fires every loaded bundle's deserialize lazy that contains a
      // NaturalResourceSpec at root -- i.e. every natural-resource
      // blueprint across every installed mod -- and turns us into the
      // trigger point for any third-party mod that ships a broken
      // nested-blueprint reference. TemplateService.GetAll<T>() walks
      // only TemplateCollectionService.AllTemplates (active faction's
      // collections + our cross-faction NaturalResources loads), so
      // foreign-mod content we don't subscribe to is never re-attempted
      // by us. See BuildingCatalogLoader.ScanAll for the same reasoning.
      var result = new List<FloraEntry>();
      var skipped = 0;
      foreach (var natural in _templateService.GetAll<NaturalResourceSpec>()) {
        // Per-blueprint isolation: a single malformed third-party
        // blueprint shouldn't take out the whole catalog. Mirrors
        // BuildingCatalogLoader.ScanAll's per-blueprint try/catch.
        try {
          var bp = natural.Blueprint;
          if (bp == null) {
            continue;
          }

          var name = bp.Name;
          var template = TryGet<TemplateSpec>(natural)?.TemplateName;
          var faction = ExtractFaction(name);
          var kind = ClassifyKind(natural);

          var growable = TryGet<GrowableSpec>(natural);
          var watered = TryGet<WateredNaturalResourceSpec>(natural);
          var floodable = TryGet<FloodableNaturalResourceSpec>(natural);
          var cuttable = TryGet<CuttableSpec>(natural);
          var gatherable = TryGet<GatherableSpec>(natural);
          var plantable = TryGet<PlantableSpec>(natural);
          var plantableGroups = plantable != null && !string.IsNullOrEmpty(plantable.ResourceGroup)
              ? new[] { plantable.ResourceGroup }
              : System.Array.Empty<string>();

          result.Add(new FloraEntry(
              blueprintName: name,
              templateName: template,
              faction: faction,
              kind: kind,
              plantableGroups: plantableGroups,
              growthTimeInDays: growable?.GrowthTimeInDays,
              daysToDieDry: watered?.DaysToDieDry,
              minWaterHeight: floodable?.MinWaterHeight,
              maxWaterHeight: floodable?.MaxWaterHeight,
              daysToDieFlooded: floodable?.DaysToDie,
              isCuttable: cuttable != null,
              removeOnCut: cuttable?.RemoveOnCut,
              isGatherable: gatherable != null,
              yieldGrowthTimeInDays: gatherable?.YieldGrowthTimeInDays,
              cutYield: ToYieldInfo(cuttable?.Yielder),
              gatherYield: ToYieldInfo(gatherable?.Yielder)));
        } catch (System.Exception ex) {
          skipped++;
          var name = natural.Blueprint?.Name ?? "(no blueprint)";
          KeystoneLog.Warn(
              $"[Keystone] FloraCatalogLoader: skipping natural-resource blueprint '{name}' " +
              $"-- threw {ex.GetType().Name}: {ex.Message}. " +
              "Catalog will be missing this entry; downstream consumers won't see it.");
          KeystoneIntegrationHealth.TryRecord("Skipped blueprints (flora)", name);
        }
      }
      if (skipped > 0) {
        KeystoneLog.Warn(
            $"[Keystone] FloraCatalogLoader: skipped {skipped} malformed blueprint(s); " +
            $"catalog contains {result.Count} entries.");
      }
      return result;
    }

    /// <summary>
    /// Coarse classification by marker spec. <c>CropSpec</c> wins over
    /// <c>BushSpec</c> when both are present (a crop that's also a bush
    /// would still be game-managed by farmhouses, not foresters); in
    /// practice these specs are mutually exclusive in vanilla.
    /// </summary>
    private static FloraKind ClassifyKind(ComponentSpec sibling) {
      if (sibling.HasSpec<CropSpec>()) return FloraKind.Crop;
      if (sibling.HasSpec<TreeComponentSpec>()) return FloraKind.Tree;
      if (sibling.HasSpec<BushSpec>()) return FloraKind.Bush;
      return FloraKind.GroundCover;
    }

    /// <summary>
    /// Pull the faction tag from the last <c>.</c>-segment of the
    /// blueprint name. Null when the name has no <c>.</c>-segment we
    /// could split. The convention isn't perfect for all naturals (a
    /// <c>Pine.WhitePine</c> blueprint yields <c>"WhitePine"</c>, which
    /// is a variant not a faction) but consumers can refine on lookup
    /// and the same heuristic mirrors what the building loader does so
    /// any future cross-references stay consistent.
    /// </summary>
    private static string? ExtractFaction(string blueprintName) {
      var dot = blueprintName.LastIndexOf('.');
      return (dot > 0 && dot < blueprintName.Length - 1)
          ? blueprintName.Substring(dot + 1)
          : null;
    }

    private static T? TryGet<T>(ComponentSpec sibling) where T : ComponentSpec =>
        sibling.HasSpec<T>() ? sibling.GetSpec<T>() : null;

    private static YieldInfo? ToYieldInfo(YielderSpec? y) =>
        y == null
            ? null
            : new YieldInfo(y.Yield.Id, y.Yield.Amount, y.RemovalTimeInHours, y.ResourceGroup);

    #endregion

    #region Census

    private (Dictionary<string, int> alive, Dictionary<string, int> dead) Census() {
      var alive = new Dictionary<string, int>();
      var dead = new Dictionary<string, int>();
      var seen = new HashSet<BlockObject>();

      var size = _terrain.Size;
      for (var y = 0; y < size.y; y++) {
        for (var x = 0; x < size.x; x++) {
          var col = new Vector2Int(x, y);
          if (!_terrain.Contains(col)) continue;
          foreach (var surface in _terrain.GetAllHeightsInCell(col)) {
            for (var dz = 0; dz <= VerticalProbeRange; dz++) {
              var v = new Vector3Int(surface.x, surface.y, surface.z + dz);
              foreach (var bo in _blockService.GetObjectsAt(v)) {
                if (!seen.Add(bo)) continue;
                if (!bo.HasComponent<NaturalResource>()) continue;
                var name = bo.GetComponent<BlockObjectSpec>()?.Blueprint?.Name;
                if (name == null) continue;

                // LivingNaturalResource.IsDead is the canonical alive
                // signal; DyingNaturalResource is always-attached state
                // tracking and not a useful predicate. If a flora has no
                // LivingNaturalResource at all, fall back to "alive" --
                // probably a non-lifecycle natural (e.g. ground cover).
                var living = bo.GetComponent<LivingNaturalResource>();
                var isDead = living != null && living.IsDead;
                var bucket = isDead ? dead : alive;
                bucket.TryGetValue(name, out var n);
                bucket[name] = n + 1;
              }
            }
          }
        }
      }
      return (alive, dead);
    }

    #endregion

    #region Logging

    private void LogSummary(long catalogMs, long censusMs,
                            Dictionary<string, int> alive,
                            Dictionary<string, int> dead) {
      var trees = _catalog.CountOfKind(FloraKind.Tree);
      var bushes = _catalog.CountOfKind(FloraKind.Bush);
      var crops = _catalog.CountOfKind(FloraKind.Crop);
      var ground = _catalog.CountOfKind(FloraKind.GroundCover);
      var cuts = _catalog.Entries.Count(e => e.IsCuttable);
      var taps = _catalog.Entries.Count(e => IsTap(e));
      var fruits = _catalog.Entries.Count(e => IsFruit(e));
      var plantable = _catalog.Entries.Count(e => e.IsPlantable);
      var totalAlive = alive.Values.Sum();
      var totalDead = dead.Values.Sum();
      KeystoneLog.Verbose($"[Keystone] Flora catalog: {_catalog.Count} blueprints " +
                 $"({trees} trees, {bushes} bushes, {crops} crops, {ground} ground-cover) -- " +
                 $"{cuts} cuttable, {fruits} fruits, {taps} taps, {plantable} plantable. " +
                 $"{totalAlive} alive, {totalDead} dead. " +
                 $"Catalog {catalogMs} ms, census {censusMs} ms.");
    }

    private void LogEntries(Dictionary<string, int> alive, Dictionary<string, int> dead) {
      var sb = new StringBuilder();
      foreach (var e in _catalog.Entries
                   .OrderBy(e => e.Kind)
                   .ThenByDescending(e => alive.TryGetValue(e.BlueprintName, out var n) ? n : 0)
                   .ThenBy(e => e.BlueprintName)) {
        sb.Length = 0;
        sb.Append("[Keystone]   ").Append(e.BlueprintName).Append(" [").Append(e.Kind).Append(']');
        if (e.Faction != null) {
          sb.Append(" faction=").Append(e.Faction);
        }
        if (e.TemplateName != null && e.TemplateName != e.BlueprintName) {
          sb.Append(" tmpl=").Append(e.TemplateName);
        }
        if (e.IsPlantable) {
          sb.Append(" plantable=").Append(string.Join("+", e.PlantableGroups));
        }

        var aliveCount = alive.TryGetValue(e.BlueprintName, out var a) ? a : 0;
        var deadCount = dead.TryGetValue(e.BlueprintName, out var d) ? d : 0;
        sb.Append("  alive=").Append(aliveCount);
        if (deadCount > 0) sb.Append(" dead=").Append(deadCount);

        if (e.GrowthTimeInDays is { } grow) {
          sb.Append("  grow=").Append(grow.ToString("F1", CultureInfo.InvariantCulture)).Append('d');
        }
        if (e.DaysToDieDry is { } dryDays) {
          sb.Append("  watered(dry=")
            .Append(dryDays.ToString("F1", CultureInfo.InvariantCulture))
            .Append("d)");
        }
        if (e.DaysToDieFlooded is { } floodDays) {
          sb.Append("  flood(")
            .Append(e.MinWaterHeight).Append('-').Append(e.MaxWaterHeight)
            .Append(",die=")
            .Append(floodDays.ToString("F1", CultureInfo.InvariantCulture))
            .Append("d)");
        }

        if (e.IsCuttable) {
          sb.Append("  cuts");
          if (e.RemoveOnCut == false) sb.Append("(persists)");
          AppendYield(sb, e.CutYield);
        }
        if (e.IsGatherable) {
          sb.Append(GatherLabel(e));
          if (e.YieldGrowthTimeInDays is { } regrow) {
            sb.Append("(regrow=")
              .Append(regrow.ToString("F1", CultureInfo.InvariantCulture))
              .Append("d)");
          }
          AppendYield(sb, e.GatherYield);
        }

        KeystoneLog.Verbose(sb.ToString());
      }
    }

    private static void AppendYield(StringBuilder sb, YieldInfo? yield) {
      if (yield == null) return;
      sb.Append(' ').Append("->").Append(yield.GoodId).Append('x').Append(yield.Amount);
      if (!string.IsNullOrEmpty(yield.ResourceGroup)) {
        sb.Append(" grp=").Append(yield.ResourceGroup);
      }
    }

    private static bool IsTap(FloraEntry e) =>
        e.IsGatherable && e.GatherYield?.ResourceGroup == TappableResourceGroup;

    private static bool IsFruit(FloraEntry e) =>
        e.IsGatherable && e.GatherYield?.ResourceGroup == GatherableResourceGroup;

    private static string GatherLabel(FloraEntry e) =>
        IsTap(e) ? "  taps" :
        IsFruit(e) ? "  fruits" :
        "  gathers";

    #endregion

  }

}
