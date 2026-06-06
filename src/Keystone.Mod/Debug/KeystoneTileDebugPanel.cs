using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Keystone.Core.Biomes;
using Keystone.Core.Buildings;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Flora;
using Keystone.Core.Flourish;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;
using Keystone.Mod.Flourish;
using Keystone.Mod.Recipes;
using Keystone.Mod.Surface;
using Keystone.Mod.Survey;
using Keystone.Mod.Wellbeing;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.CursorToolSystem;
using Timberborn.DebuggingUI;
using Timberborn.EntitySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.NaturalResourcesMoisture;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Tile-scope half of the Keystone debug overlay. Shows everything
  /// keyed to the cursor's specific voxel: cursor coordinates, cliff
  /// status, and every <see cref="BlockObject"/> at the tile with
  /// catalog classification plus extended lifecycle detail for
  /// <see cref="KeystoneFlourish"/> and <see cref="KeystoneRockTint"/>
  /// entities — the diagnostic surface for the attrition pipeline.
  ///
  /// <para><b>Companion panel:</b> column-scope content (per-surface
  /// region + survey staleness) and chunk-scope content (bilinear
  /// field sample, per-biome chunk values) live in
  /// <see cref="KeystoneChunkDebugPanel"/>. The split lets each panel
  /// stay short enough to read at a glance and lets the user expand
  /// only the half they care about.</para>
  ///
  /// <para><b>Side-channel HACK preserved.</b> Touching
  /// <see cref="KeystoneTilePanelActivity.MarkActive"/> in
  /// <see cref="GetText"/> is the signal <c>PlateauHighlighter</c>
  /// reads to know "the user is looking at the Keystone debug overlay."
  /// See <see cref="KeystoneTilePanelActivity"/> for the full rationale.</para>
  /// </summary>
  public sealed class KeystoneTileDebugPanel : ILoadableSingleton, IDebuggingPanel {

    #region Constants

    private const string PanelName = "Keystone (tile)";

    #endregion

    #region Fields

    private readonly DebuggingPanel _panel;
    private readonly CursorDebugger _cursor;
    private readonly KeystoneSurveyor _surveyor;
    private readonly CliffProximity _cliffs;
    private readonly IBlockService _blockService;
    private readonly IWaterQuery _water;
    private readonly IMoistureQuery _moisture;
    private readonly IContaminationQuery _contamination;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly FloraCatalog _flora;
    private readonly BuildingCatalog _buildings;
    private readonly KeystoneTilePanelActivity _activity;
    private readonly TileSlotRegistry _tileSlotRegistry;
    private readonly SurfaceFieldStore _surfaceFields;
    private readonly RiparianTileQuery _riparian;
    private readonly StringBuilder _buffer = new();


    #endregion

    #region Construction

    public KeystoneTileDebugPanel(
        DebuggingPanel panel,
        CursorDebugger cursor,
        KeystoneSurveyor surveyor,
        CliffProximity cliffs,
        IBlockService blockService,
        IWaterQuery water,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        FloraCatalog flora,
        BuildingCatalog buildings,
        KeystoneTilePanelActivity activity,
        TileSlotRegistry tileSlotRegistry,
        SurfaceFieldStore surfaceFields,
        RiparianTileQuery riparian) {
      _panel = panel;
      _cursor = cursor;
      _surveyor = surveyor;
      _cliffs = cliffs;
      _blockService = blockService;
      _water = water;
      _moisture = moisture;
      _contamination = contamination;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _flora = flora;
      _buildings = buildings;
      _activity = activity;
      _tileSlotRegistry = tileSlotRegistry;
      _surfaceFields = surfaceFields;
      _riparian = riparian;
    }

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      _panel.AddDebuggingPanel(this, PanelName);
    }

    #endregion

    #region IDebuggingPanel

    /// <inheritdoc />
    public string GetText() {
      _activity.MarkActive();
      _buffer.Clear();

      if (!_cursor.Active) {
        _buffer.Append("(hover the map to inspect a tile)");
        return _buffer.ToString();
      }

      // Section layout convention (mirrors KeystoneChunkDebugPanel):
      //   0-indent: section header or top-level fact
      //   2-indent: item within a section
      //   4-indent: sub-detail of an item
      // Blank line between sections.
      var c = _cursor.Coordinates;
      _buffer.AppendLine($"Cursor: ({c.x},{c.y},{c.z})");
      _buffer.AppendLine($"Cliff: {DescribeCliff(c)}");

      // Tile-state and dominant-biome lines snap to the closest
      // surveyed surface in the column. Hovering mid-air or off the
      // edge of the surveyed area gives null and the lines are
      // suppressed (no fake "dry / no biome" answer for unreal tiles).
      var resolved = ResolveSurveyedSurface(c);
      if (resolved.HasValue) {
        var surface = resolved.Value;
        _buffer.AppendLine($"State: {DescribeTileState(surface)}");
        _buffer.AppendLine($"Biome: {DescribeBiome(surface)}");
        _buffer.AppendLine($"Water distance: {DescribeWaterDistance(surface)}");
        _buffer.AppendLine($"Riparian maturity: {DescribeRiparianMaturity(surface)}");
      }

      _buffer.AppendLine();
      AppendCursorBlockObjects(c);

      // Trim trailing newline.
      if (_buffer.Length > 0 && _buffer[_buffer.Length - 1] == '\n') {
        _buffer.Length--;
      }
      return _buffer.ToString();
    }

    #endregion

    #region Cliff status

    /// <summary>One-token description of whether the cursor's surface
    /// sits above or below any of its four Manhattan neighbours.
    /// Used inline by the cursor-section header.</summary>
    private string DescribeCliff(Vector3Int cursor) {
      var surface = new SurfaceCoord(cursor.x, cursor.y, cursor.z);
      var above = _cliffs.IsAboveNeighbor(surface);
      var below = _cliffs.IsBelowNeighbor(surface);
      if (above && below) return "above and below neighbour";
      if (above) return "above neighbour";
      if (below) return "below neighbour";
      return "flat";
    }

    /// <summary>Resolve the cursor's voxel to a surveyed surface,
    /// snapping to the closest cached surface in the same column when
    /// the cursor isn't directly on one. Returns <c>null</c> when the
    /// column has no surveyed surfaces (off-map / unsurveyed) so
    /// callers can suppress per-surface readouts cleanly.</summary>
    private SurfaceCoord? ResolveSurveyedSurface(Vector3Int cursor) {
      var column = new TileCoord(cursor.x, cursor.y);
      var heights = _surveyor.Core.ColumnSurfaceHeights(column);
      if (heights.Count == 0) return null;
      var bestZ = heights[0];
      var bestDist = Math.Abs(bestZ - cursor.z);
      for (var i = 1; i < heights.Count; i++) {
        var d = Math.Abs(heights[i] - cursor.z);
        if (d < bestDist) {
          bestDist = d;
          bestZ = heights[i];
        }
      }
      return new SurfaceCoord(cursor.x, cursor.y, bestZ);
    }

    /// <summary>Map per-tile environmental signals to the same
    /// mutually-exclusive ordering <see cref="KeystoneRockTint"/> uses:
    /// submerged-with-badwater, submerged-with-water, contaminated,
    /// irrigated, dry. The ordering matters — a contaminated wet tile
    /// reads as "contaminated", not "irrigated".
    ///
    /// <para><b>Badwater detection queries the water column itself</b>
    /// via <see cref="IWaterQuery.WaterContaminationAt"/> (which wraps
    /// Timberborn's <c>IThreadSafeWaterMap.ColumnContamination</c>) —
    /// this is the contamination of the *water* at this voxel, not
    /// the soil. The earlier soil-side queries (<c>ContaminationAt</c>
    /// + <c>IsContaminatedAt</c>) report on the badwater plume in the
    /// dirt, which can lag the water by hours and miss fresh
    /// badwater pools entirely.</para></summary>
    private string DescribeTileState(SurfaceCoord surface) {
      var depth = _water.WaterDepthAt(surface);
      if (depth > 0f) {
        var waterContam = _water.WaterContaminationAt(surface);
        if (waterContam > 0f) {
          return string.Format(
              CultureInfo.InvariantCulture,
              "submerged in badwater (water contamination {0:F2})",
              waterContam);
        }
        return "submerged in water";
      }
      if (_contamination.IsContaminatedAt(surface)) return "contaminated";
      if (_moisture.IsMoistAt(surface)) return "irrigated";
      return "dry";
    }

    /// <summary>Per-tile dominant Keystone biome (bilinear-sampled
    /// per <see cref="ChunkBiomeSampler.SampleDominantBiome"/>) plus
    /// the same biome's per-tile suitability and maturity values.
    /// Returns <c>"(none)"</c> when no biome scores above zero at
    /// the tile (settled regions, unsampled chunks, or genuinely
    /// neutral terrain), <c>"(no field)"</c> when the cursor's
    /// region has no ecology field.</summary>
    private string DescribeBiome(SurfaceCoord surface) {
      var region = _surveyor.Regions.Containing(surface);
      if (region == null) return "(no region)";
      var field = _fieldQuery.FieldFor(region.Id);
      if (field == null) {
        return region.IsSettled
            ? "(settled -- ecology fields skipped)"
            : "(no field yet)";
      }
      var (ripSuit, ripMat) = _riparian.Sample(region.Id, surface);
      var (biome, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          _biomeValues, region.Id,
          field.OriginX, field.OriginY,
          field.ChunksX, field.ChunksY,
          surface.X, surface.Y,
          ripSuit, ripMat);
      if (biome == null) return "(none)";
      if (biome.Value == BiomeKind.Riparian) {
        // Riparian is per-tile: no per-chunk Suitability slot to sample
        // (SampleSuitability would throw). Report the per-tile inputs.
        return string.Format(
            CultureInfo.InvariantCulture,
            "Riparian (per-tile suitability {0:F2}, maturity {1:F2})",
            ripSuit, ripMat);
      }
      var suitability = ChunkBiomeSampler.SampleSuitability(
          _biomeValues, region.Id, biome.Value,
          field.OriginX, field.OriginY,
          field.ChunksX, field.ChunksY,
          surface.X, surface.Y);
      return string.Format(
          CultureInfo.InvariantCulture,
          "{0} (suitability {1:F2}, maturity {2:F2})",
          biome.Value, suitability, maturity);
    }

    private string DescribeWaterDistance(SurfaceCoord surface) {
      var region = _surveyor.Regions.Containing(surface);
      if (region == null) return "(no region)";
      var tileData = _fieldQuery.TileDataFor(region.Id);
      if (tileData == null) return "(no tile data)";
      var slot = _tileSlotRegistry.TryOrdinalFor("keystone.tile.waterDistance");
      if (!slot.HasValue) return "(slot not registered)";
      if (!tileData.Contains(surface.X, surface.Y)) return "(outside bbox)";
      var dist = (int)tileData.Get(surface.X, surface.Y, slot.Value);
      if (dist == WaterDistanceCalculator.OutOfRange) return "- (dry land)";
      if (dist == WaterDistanceCalculator.DeepWater) return "- (deep water)";
      return dist switch {
        -2 => "-2 (water, 2 from shore)",
        -1 => "-1 (water, shore)",
         1 => "1 (land, adjacent)",
         2 => "2 (land, 2 from water)",
        _ => dist.ToString(),
      };
    }

    /// <summary>Per-tile riparian maturity from
    /// <see cref="SurfaceFieldStore"/> -- the sustained-near-water
    /// accumulator (in maturity-days, shown as value / ceiling) that
    /// gates Grassland's riparian flourishes. Distinct from the biome
    /// maturity on the Biome line above: this is the per-surface signal
    /// that rises only while the tile stays near water and dissipates
    /// when it doesn't, so a transient flood barely moves it. Returns a
    /// marker when the cursor's surface has no entry in the terrain
    /// column map (e.g. a mid-air voxel snapped to a surveyed height the
    /// map doesn't treat as a ceiling).</summary>
    private string DescribeRiparianMaturity(SurfaceCoord surface) {
      if (!_surfaceFields.TryResolveSurfaceIndex(surface.X, surface.Y, surface.Z, out var index3D)) {
        return "(no mapped surface)";
      }
      var maturity = _surfaceFields.GetAt(SurfaceField.RiparianMaturity, index3D);
      return string.Format(
          CultureInfo.InvariantCulture,
          "{0:F2} / {1:F0} days",
          maturity, RiparianMaturityParameters.Ceiling);
    }

    #endregion

    #region BlockObject dump

    /// <summary>
    /// For each <see cref="BlockObject"/> at the cursor's voxel print a
    /// header line (blueprint + catalog classification) and, when the
    /// entity carries Keystone-specific lifecycle components, an
    /// extended detail line:
    /// <list type="bullet">
    ///   <item><see cref="KeystoneFlourish"/> → plant-type tier
    ///         (dry/water/irrigated derived from blueprint specs),
    ///         health (healthy/unhealthy/dead derived from runtime
    ///         lifecycle state), phase (seedling/mature/stump),
    ///         Keystone-variant class.</item>
    ///   <item><see cref="KeystoneRockTint"/> → current tint variant
    ///         (base/mossy/dry), Keystone-variant class.</item>
    /// </list>
    /// Vanilla flora and buildings get the original short
    /// classification only; entities recognised by neither catalog
    /// fall through with an "(inert / unrecognised)" tag.
    /// </summary>
    private void AppendCursorBlockObjects(Vector3Int cursor) {
      _buffer.AppendLine("Objects:");
      var anyAtAll = false;
      foreach (var bo in _blockService.GetObjectsAt(cursor)) {
        anyAtAll = true;
        var spec = bo.GetComponent<BlockObjectSpec>();
        var blueprint = spec?.Blueprint?.Name ?? "(no blueprint)";
        var classification = ClassifyForCursor(bo, spec);
        _buffer.AppendLine(classification != null
            ? $"  {blueprint}: {classification}"
            : $"  {blueprint}: (inert / unrecognised)");
        AppendLabel(bo, spec);
        AppendBlockObjectShape(bo);
        AppendKeystoneDetail(bo, spec);
      }
      if (!anyAtAll) {
        _buffer.AppendLine("  (none at this voxel)");
      }
    }

    /// <summary>Print the entity's display label. This is the only
    /// in-game way to confirm a flourish's <see cref="LabeledEntitySpec"/>
    /// wired up, since flourishes are deliberately non-selectable (no
    /// collider, plus Class-B selection suppression) so the entity panel
    /// can't be opened on them. The spec is load-bearing: vanilla's
    /// <c>BlockObject.AddToServiceAfterLoad</c> dereferences
    /// <c>GetComponent&lt;LabeledEntitySpec&gt;().DisplayNameLocKey</c> on
    /// its stale-entity cleanup path and NREs without it.
    ///
    /// <para>Three states are distinguished: the runtime
    /// <see cref="LabeledEntity"/> is present (decorator attached and the
    /// loc key resolves — prints the localized name); the blueprint
    /// carries the spec but no runtime component (decorator gap); or the
    /// spec is absent entirely.</para></summary>
    private void AppendLabel(BlockObject bo, BlockObjectSpec? spec) {
      var specKey = spec?.Blueprint?.GetSpec<LabeledEntitySpec>()?.DisplayNameLocKey;
      var labeled = bo.GetComponent<LabeledEntity>();
      if (labeled != null) {
        _buffer.AppendLine($"    label: \"{labeled.DisplayName}\" (key {specKey ?? "?"})");
      } else if (specKey != null) {
        _buffer.AppendLine(
            $"    label: spec present (key {specKey}) but LabeledEntity not attached");
      } else {
        _buffer.AppendLine("    label: (no LabeledEntitySpec)");
      }
    }

    /// <summary>Print declared BO size, actual occupied footprint
    /// (count + bbox), and entrance position. Diagnostic for cases
    /// where a large BO declares many <c>Occupations: None</c>
    /// blocks so the effective footprint is much smaller than the
    /// declared bounding box (e.g. Tree of Life: 15×15×9 declared,
    /// ~3×3 occupied), and to spot where the entrance is when it
    /// sits well away from the BO's anchor.</summary>
    private void AppendBlockObjectShape(BlockObject bo) {
      var declared = bo.Blocks.Size;
      var minX = int.MaxValue; var maxX = int.MinValue;
      var minY = int.MaxValue; var maxY = int.MinValue;
      var minZ = int.MaxValue; var maxZ = int.MinValue;
      var occCount = 0;
      foreach (var c in bo.PositionedBlocks.GetOccupiedCoordinates()) {
        occCount++;
        if (c.x < minX) minX = c.x;
        if (c.x > maxX) maxX = c.x;
        if (c.y < minY) minY = c.y;
        if (c.y > maxY) maxY = c.y;
        if (c.z < minZ) minZ = c.z;
        if (c.z > maxZ) maxZ = c.z;
      }
      var allCount = 0;
      foreach (var _ in bo.PositionedBlocks.GetAllCoordinates()) allCount++;

      var origin = bo.Coordinates;
      if (occCount > 0) {
        var fx = maxX - minX + 1;
        var fy = maxY - minY + 1;
        var fz = maxZ - minZ + 1;
        _buffer.AppendLine(
            $"    size declared {declared.x}x{declared.y}x{declared.z} "
            + $"at origin ({origin.x},{origin.y},{origin.z}); "
            + $"occupied {occCount}/{allCount} tiles in {fx}x{fy}x{fz} bbox "
            + $"({minX},{minY},{minZ})-({maxX},{maxY},{maxZ})");
      } else {
        _buffer.AppendLine(
            $"    size declared {declared.x}x{declared.y}x{declared.z} "
            + $"at origin ({origin.x},{origin.y},{origin.z}); "
            + $"occupied 0/{allCount} tiles (all blocks are Occupations:None)");
      }

      if (bo.HasEntrance) {
        var local = bo.Entrance.Coordinates;
        var world = bo.TransformCoordinates(local);
        _buffer.AppendLine(
            $"    entrance: local ({local.x},{local.y},{local.z}) -> "
            + $"world ({world.x},{world.y},{world.z})");
      } else {
        _buffer.AppendLine("    entrance: (none declared)");
      }
    }

    /// <summary>Catalog-driven short description (alive/dead, kind,
    /// harvest tags, plantable group) for naturals;
    /// faction/roles/planter-group for buildings. Returns
    /// <c>null</c> for entities not in either catalog.</summary>
    private string? ClassifyForCursor(BlockObject bo, BlockObjectSpec? spec) {
      var name = spec?.Blueprint?.Name;
      var floraEntry = name != null ? _flora.Get(name) : null;
      if (floraEntry != null) {
        return DescribeFlora(bo, floraEntry);
      }
      var buildingEntry = name != null ? _buildings.Get(name) : null;
      if (buildingEntry != null) {
        return DescribeBuilding(buildingEntry);
      }
      return null;
    }

    private static string DescribeFlora(BlockObject bo, FloraEntry entry) {
      var living = bo.GetComponent<LivingNaturalResource>();
      var alive = living == null || !living.IsDead;
      var kind = entry.Kind switch {
          FloraKind.Tree => "tree",
          FloraKind.Bush => "bush",
          FloraKind.Crop => "crop",
          FloraKind.GroundCover => "ground cover",
          _ => "natural",
      };
      var sb = new StringBuilder();
      sb.Append(alive ? "alive " : "dead ").Append(kind);
      if (entry.IsCuttable) sb.Append(", cuts");
      if (entry.IsGatherable) {
        sb.Append(entry.GatherYield?.ResourceGroup switch {
            "Tappable" => ", taps",
            "Gatherable" => ", fruits",
            _ => ", gathers",
        });
      }
      if (entry.IsPlantable) {
        sb.Append(", plantable as ").Append(string.Join("+", entry.PlantableGroups));
      }
      return sb.ToString();
    }

    private static string DescribeBuilding(BuildingEntry entry) {
      var sb = new StringBuilder();
      if (entry.Faction != null) sb.Append(entry.Faction).Append(' ');
      sb.Append(FormatRoles(entry.Roles));
      if (entry.PlantableGroup != null) {
        sb.Append(" (plants ").Append(entry.PlantableGroup).Append(')');
      }
      return sb.ToString();
    }

    private static string FormatRoles(BuildingRoles roles) {
      if (roles == BuildingRoles.None) return "(no roles)";
      var parts = new List<string>();
      foreach (var role in (BuildingRoles[])Enum.GetValues(typeof(BuildingRoles))) {
        if (role == BuildingRoles.None) continue;
        if ((roles & role) != 0) parts.Add(role.ToString().ToLowerInvariant());
      }
      return string.Join("+", parts);
    }

    /// <summary>Print an indented detail line for Keystone-bearing
    /// entities. Three recognised kinds today: flourishes (plant-type
    /// + lifecycle state), rock-tint entities (current variant), and
    /// Nature sources (per-pass scan diagnostic). Silent for entities
    /// with none of these components.</summary>
    private void AppendKeystoneDetail(BlockObject bo, BlockObjectSpec? spec) {
      var nature = bo.GetComponent<KeystoneNatureSource>();
      if (nature != null) {
        AppendNatureSourceDiagnostic(nature);
      }
      var flourish = bo.GetComponent<KeystoneFlourish>();
      if (flourish != null) {
        var plantType = ClassifyFlourishType(spec);
        var health = ClassifyFlourishHealth(flourish);
        var phase = flourish.CurrentPhase.ToString().ToLowerInvariant();
        var variant = bo.GetComponent<KeystoneVariant>();
        var classLabel = variant != null && !string.IsNullOrEmpty(variant.Class)
            ? $", class {variant.Class}"
            : "";
        _buffer.AppendLine(
            $"    flourish: {plantType}, {health}, {phase}{classLabel}");
        return;
      }
      var rock = bo.GetComponent<KeystoneRockTint>();
      if (rock != null) {
        var variant = bo.GetComponent<KeystoneVariant>();
        var classLabel = variant != null && !string.IsNullOrEmpty(variant.Class)
            ? $", class {variant.Class}"
            : "";
        _buffer.AppendLine(
            $"    rock: tint={rock.CurrentVariantLabel()}{classLabel}");
      }
      var overgrowth = bo.GetComponent<Keystone.Mod.Overgrowth.KeystoneOvergrowth>();
      if (overgrowth != null) {
        _buffer.AppendLine(overgrowth.IsOvergrown
            ? string.Format(CultureInfo.InvariantCulture,
                "    overgrowth: ON, maturity {0:F2}", overgrowth.Maturity)
            : (overgrowth.CanOvergrow
                ? "    overgrowth: off"
                : "    overgrowth: off (water tree)"));
      }
    }

    /// <summary>Run the Nature source's inspection scan and emit a
    /// per-pass diagnostic block. Useful when a Nature source has the
    /// spec wired correctly but the scoring is mysteriously zero —
    /// the breakdown tells you exactly which pass returns nothing
    /// (chunk neighborhood, surfaces below baseZ, region resolution,
    /// cluster resolution, per-source raw sums).</summary>
    private void AppendNatureSourceDiagnostic(KeystoneNatureSource nature) {
      var r = nature.RunInspectionScan();
      if (!r.HasResult) {
        _buffer.AppendLine("    nature: (no PositionedBlocks — preview / unplaced)");
        return;
      }
      _buffer.AppendLine(
          $"    nature scan: foundationTopZ={r.FoundationTopZ}, chunks={r.ChunkNeighborhood.Count}, "
          + $"(region,chunk) considered={r.ChunksConsidered} aboveZ={r.ChunksAboveZ} kept={r.ChunksKept}, "
          + $"sampled-surfaces={r.SampledSurfaces.Count}, "
          + $"clusters={r.ClusterCount}");
      if (r.PerSource.Count == 0) {
        _buffer.AppendLine("      (no sources resolved on spec — bad biome name?)");
        return;
      }
      for (var i = 0; i < r.PerSource.Count; i++) {
        var s = r.PerSource[i];
        _buffer.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "      {0,-9} clusters={1} sumRaw={2:F2} score={3:F2} rate={4:F2}",
            s.Biome, s.ClusterCount, s.SumRaw, s.Score, s.Rate));
      }
    }

    /// <summary>Map blueprint specs to the plant-type tier the
    /// generator distinguishes:
    /// <list type="bullet">
    ///   <item><see cref="KeystoneDryNaturalResourceSpec"/> present →
    ///         <c>dry</c> (no soil-moisture requirement; preferred
    ///         state IS dry).</item>
    ///   <item><see cref="FloodableNaturalResourceSpec.MinWaterHeight"/>
    ///         ≥ 1 → <c>water</c> (aquatic; requires standing water).</item>
    ///   <item><see cref="WateredNaturalResourceSpec"/> present →
    ///         <c>irrigated</c> (default land plant; soil moisture
    ///         drives the dying transition).</item>
    ///   <item>none of the above → <c>unspecified</c> (shouldn't
    ///         happen for generator-produced flourishes; suggests a
    ///         hand-authored blueprint missing a habitat marker).</item>
    /// </list></summary>
    private static string ClassifyFlourishType(BlockObjectSpec? spec) {
      var blueprint = spec?.Blueprint;
      if (blueprint == null) return "unspecified";
      if (blueprint.GetSpec<KeystoneDryNaturalResourceSpec>() != null) return "dry";
      var floodable = blueprint.GetSpec<FloodableNaturalResourceSpec>();
      if (floodable != null && floodable.MinWaterHeight >= 1) return "water";
      if (blueprint.GetSpec<WateredNaturalResourceSpec>() != null) return "irrigated";
      return "unspecified";
    }

    /// <summary>Map the flourish's two-axis lifecycle state to a
    /// three-state label: <c>dead</c> (any phase, LifeStatus=Dead),
    /// <c>unhealthy</c> (alive but Health=Dry, i.e.
    /// <c>DyingNaturalResource</c> currently reports drying), or
    /// <c>healthy</c> (alive and Health=Healthy).</summary>
    private static string ClassifyFlourishHealth(KeystoneFlourish flourish) {
      if (flourish.CurrentLifeStatus == FlourishLifeStatus.Dead) return "dead";
      return flourish.CurrentHealth == FlourishHealth.Dry ? "unhealthy" : "healthy";
    }

    #endregion

  }

}
