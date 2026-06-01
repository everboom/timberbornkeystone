using System;
using System.Collections.Generic;
using System.IO;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Settings;
using Keystone.Mod.Surface;
using Keystone.Mod.Survey;
using Timberborn.AssetSystem;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.CursorToolSystem;
using Timberborn.LevelVisibilitySystem;
using Timberborn.NaturalResources;
using Timberborn.Rendering;
using Timberborn.SingletonSystem;
using Timberborn.Timbermesh;
using UnityEngine;

namespace Keystone.Mod.Visualization {

  /// <summary>
  /// Biome "lens" that follows the cursor. Draws colored boxes
  /// whose width scales with maturity — thin pins for low maturity,
  /// wide squares for high maturity. Each box sits just above its
  /// surface, lifted clear of any water column standing on the tile so
  /// it isn't hidden under the water plane. Natural resources in the
  /// circle are hidden.
  /// </summary>
  public sealed class BiomeOverlayRenderer : IPostLoadableSingleton, IUpdatableSingleton {

    #region Constants

    private const int LensRadius = 8;
    private const float BoxHeight = 0.2f;
    private const float MinBoxWidth = 0.05f;
    private const float MaxBoxWidth = 0.95f;
    private const float MaturityCap = 30f;
    private const float VerticalOffset = 0.02f;
    /// <summary>Extra height the suitability-winner box floats above the
    /// maturity-winner box when the two biomes differ, so both read at once.
    /// The suitability winner's Maturity is always ≤ the maturity winner's,
    /// so its box is the smaller of the two and nests centred on top.</summary>
    private const float SuitabilityLift = 0.1f;
    /// <summary>Clearance added above the water surface when floating a box
    /// onto standing water, so the box reads as sitting *on* the water
    /// rather than flush with it (flush looks submerged over shallow
    /// water — a 0.21-deep puddle lifts the box only 0.21 and still reads
    /// as on the bed).</summary>
    private const float WaterSurfaceClearance = 0.3f;
    // OverlayAlpha is now driven by KeystoneUiSettings.BiomeOverlayAlpha.
    // The color constants below carry RGB only; alpha is overridden per
    // frame from the setting via the material property block.
    private const string BoxMeshPath = "NaturalResources/KeystoneOverlay/BiomeBox";

    private static readonly int ColorProperty = Shader.PropertyToID("_BaseColor");

    private static readonly Color ForestColor = new(0.05f, 0.35f, 0.05f, 1f);
    private static readonly Color GrasslandColor = new(0.35f, 0.65f, 0.25f, 1f);
    private static readonly Color MonocultureColor = new(0.80f, 0.50f, 0.20f, 1f);
    private static readonly Color WetlandColor = new(0.10f, 0.55f, 0.85f, 1f);
    private static readonly Color RiverColor = new(0.55f, 0.78f, 0.95f, 1f);
    private static readonly Color LakeColor = new(0.55f, 0.78f, 0.95f, 1f);
    private static readonly Color CaveColor = new(0.50f, 0.50f, 0.50f, 1f);
    private static readonly Color DryColor = new(0.60f, 0.52f, 0.40f, 1f);
    private static readonly Color ContaminatedColor = new(0.75f, 0.10f, 0.10f, 1f);
    private static readonly Color BadwaterColor = new(0.75f, 0.10f, 0.10f, 1f);
    // Teal-green: the wet land margin, reading between grassland green and
    // water blue.
    private static readonly Color RiparianColor = new(0.25f, 0.70f, 0.55f, 1f);

    #endregion

    #region Fields

    private readonly BiomeOverlayToggle _toggle;
    private readonly KeystoneSurveyor _surveyor;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly ITerrainQuery _terrain;
    private readonly IWaterQuery _water;
    private readonly CursorCoordinatesPicker _cursorPicker;
    private readonly IBlockService _blockService;
    private readonly IAssetLoader _assetLoader;
    private readonly TimbermeshImporter _timbermeshImporter;
    private readonly KeystoneUiSettings _uiSettings;
    private readonly RiparianTileQuery _riparian;
    private readonly ILevelVisibilityService _visibility;

    private readonly Dictionary<BiomeKind, MeshDrawer> _drawers = new();
    private readonly Dictionary<BiomeKind, MaterialPropertyBlock> _mpbs = new();
    private readonly HashSet<Renderer> _hiddenRenderers = new();
    private bool _updateFailureLogged;

    #endregion

    #region Construction

    public BiomeOverlayRenderer(
        BiomeOverlayToggle toggle,
        KeystoneSurveyor surveyor,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        ITerrainQuery terrain,
        IWaterQuery water,
        CursorCoordinatesPicker cursorPicker,
        IBlockService blockService,
        IAssetLoader assetLoader,
        TimbermeshImporter timbermeshImporter,
        KeystoneUiSettings uiSettings,
        RiparianTileQuery riparian,
        ILevelVisibilityService visibility) {
      _toggle = toggle;
      _surveyor = surveyor;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _terrain = terrain;
      _water = water;
      _cursorPicker = cursorPicker;
      _blockService = blockService;
      _assetLoader = assetLoader;
      _timbermeshImporter = timbermeshImporter;
      _uiSettings = uiSettings;
      _riparian = riparian;
      _visibility = visibility;
    }

    #endregion

    #region Public state

    public bool IsActive => _toggle.Enabled;

    #endregion

    #region IPostLoadableSingleton

    public void PostLoad() {
      var (boxMesh, boxMaterial) = LoadBoxMeshAndMaterial();

      // Full enum, not BiomeValueKinds.AllBiomes: the overlay renders by
      // dominant biome, which now includes per-tile Riparian (excluded
      // from the per-chunk set). Every biome with a non-clear color gets
      // a drawer.
      foreach (var biome in (BiomeKind[])Enum.GetValues(typeof(BiomeKind))) {
        var color = ColorFor(biome);
        if (color.a <= 0f) continue;
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor(ColorProperty, color);
        _drawers[biome] = new MeshDrawer(boxMesh, boxMaterial, mpb, new MaterialPropertyBlock());
        _mpbs[biome] = mpb;
      }
    }

    private void RefreshDrawerColors(float alpha) {
      foreach (var kv in _mpbs) {
        var color = ColorFor(kv.Key);
        color.a = alpha;
        kv.Value.SetColor(ColorProperty, color);
      }
    }

    private (Mesh mesh, Material material) LoadBoxMeshAndMaterial() {
      try {
        var parent = new GameObject("BiomeBoxImport").transform;
        try {
          var binaryData = _assetLoader.Load<BinaryData>(BoxMeshPath);
          if (binaryData == null) return (CreateFallbackMesh(), CreateFallbackMaterial());
          using var stream = new MemoryStream(binaryData.Bytes);
          _timbermeshImporter.Import(stream, parent);
          var mf = parent.GetComponentInChildren<MeshFilter>();
          var mr = parent.GetComponentInChildren<MeshRenderer>();
          if (mf == null || mr == null) return (CreateFallbackMesh(), CreateFallbackMaterial());
          return (mf.sharedMesh, mr.sharedMaterial ?? CreateFallbackMaterial());
        } finally {
          UnityEngine.Object.Destroy(parent.gameObject);
        }
      } catch {
        return (CreateFallbackMesh(), CreateFallbackMaterial());
      }
    }

    private static Material CreateFallbackMaterial() =>
        new Material(Shader.Find("Sprites/Default"));

    private static Mesh CreateFallbackMesh() {
      var mesh = new Mesh { name = "BiomeBoxFallback" };
      mesh.vertices = new[] {
          new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
          new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f),
          new Vector3(-0.5f, 1f, -0.5f), new Vector3(0.5f, 1f, -0.5f),
          new Vector3(0.5f, 1f, 0.5f), new Vector3(-0.5f, 1f, 0.5f),
      };
      mesh.triangles = new[] {
          0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
          0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6,
          0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
      };
      mesh.RecalculateNormals();
      return mesh;
    }

    #endregion

    #region IUpdatableSingleton

    public void UpdateSingleton() {
      try {
        if (!_toggle.Enabled) {
          RestoreHidden();
          return;
        }

        var cursor = _cursorPicker.Pick();
        if (!cursor.HasValue) {
          RestoreHidden();
          return;
        }

        var center = cursor.Value.TileCoordinates;
        RestoreHidden();
        DrawLens(center.x, center.y);
      } catch (Exception ex) {
        LifecycleGuard.HandleErrorOnce(
            "BiomeOverlayRenderer.UpdateSingleton", "Subsystem failed",
            ex, ref _updateFailureLogged);
      }
    }

    #endregion

    #region Lens

    private void DrawLens(int cx, int cy) {
      var alpha = _uiSettings.BiomeOverlayAlpha;
      RefreshDrawerColors(alpha);
      var r2 = LensRadius * LensRadius;
      // When the player slices the world with the vertical-view (cutaway)
      // slider, surfaces above the cut are hidden -- their overlay boxes
      // must not float above the cutaway. Hoist the at-max check so the
      // common (slider fully up) case pays no per-surface cost; below max,
      // each surface is tested individually below.
      var levelAtMax = _visibility.LevelIsAtMax;

      for (var dy = -LensRadius; dy <= LensRadius; dy++) {
        for (var dx = -LensRadius; dx <= LensRadius; dx++) {
          if (dx * dx + dy * dy > r2) continue;
          var tx = cx + dx;
          var ty = cy + dy;
          if (tx < 0 || tx >= _terrain.Width || ty < 0 || ty >= _terrain.Height)
            continue;

          var column = new TileCoord(tx, ty);
          var heights = _terrain.SurfaceHeightsAt(column);
          if (heights.Count == 0) continue;

          // One box per surface in the column. Columns with stacked
          // surfaces (cave under overhang, terrace under raised
          // platform) need a box for each — the region containment
          // and biome field can differ per Z (a cave-surface region
          // is distinct from its surface-region above), so each
          // surface gets its own region lookup + sample.
          for (var hi = 0; hi < heights.Count; hi++) {
            var z = heights[hi];
            // Respect the cutaway slider: skip surfaces sliced away by the
            // current view level. Same (tx, ty, z) grid coord vanilla uses
            // to hide this surface's natural resources (see HideObjectsAt),
            // so the box's visibility tracks the resources it covers.
            if (!levelAtMax && !_visibility.BlockIsVisible(new Vector3Int(tx, ty, z)))
              continue;
            HideObjectsAt(tx, ty, z);

            var surface = new SurfaceCoord(tx, ty, z);
            var region = _surveyor.Regions.Containing(surface);
            if (region == null) continue;
            var field = _fieldQuery.FieldFor(region.Id);
            if (field == null) continue;

            var (ripSuit, ripMat) = _riparian.Sample(region.Id, surface);
            // Two dominance reads per tile: the maturity winner ("what has
            // established here") is the base box; the suitability winner
            // ("what current conditions favour") is the lens's headline.
            var (suitBiome, suitMaturity) = ChunkBiomeSampler.SampleDominantBiome(
                _biomeValues, region.Id,
                field.OriginX, field.OriginY,
                field.ChunksX, field.ChunksY,
                tx, ty,
                ripSuit, ripMat);
            var (matBiome, matMaturity) = ChunkBiomeSampler.SampleDominantByMaturity(
                _biomeValues, region.Id,
                field.OriginX, field.OriginY,
                field.ChunksX, field.ChunksY,
                tx, ty,
                ripMat);

            var worldPos = CoordinateSystem.GridToWorldCentered(
                new Vector3Int(tx, ty, z));
            // Float the box onto any water standing on this surface so it
            // isn't submerged / hidden under the water plane. Use the
            // column's absolute water-surface height (Floor + WaterDepth,
            // world Y == grid Z 1:1, see CoordinateSystem.GridToWorld)
            // rather than a per-voxel depth lookup: over deep water the
            // surveyed surface Z can sit a cell below the column floor, so
            // WaterDepthAt reads 0 and the box stays on the bed. Dry
            // surfaces return 0 — no lift. Both the base and the lifted
            // suitability box ride on top of this.
            var waterHeight = _water.WaterSurfaceHeightAt(surface);
            if (waterHeight > worldPos.y) {
              worldPos.y = waterHeight + WaterSurfaceClearance;
            }

            // Base layer: the most-established (highest-Maturity) biome. When
            // nothing has accrued Maturity yet, fall back to the suitability
            // winner so a fresh tile still shows its prospective biome.
            var baseBiome = matBiome ?? suitBiome;
            var baseMaturity = matBiome != null ? matMaturity : suitMaturity;
            DrawBiomeBox(baseBiome, baseMaturity, worldPos, 0f);

            // When current conditions favour a different biome than the one
            // established here, draw that suitability winner as a smaller box
            // lifted above, so the player sees both at once. Its Maturity is
            // ≤ the base's by construction, so it nests centred on top.
            if (matBiome != null && suitBiome != null
                && suitBiome.Value != matBiome.Value) {
              DrawBiomeBox(suitBiome.Value, suitMaturity, worldPos, SuitabilityLift);
            }
          }
        }
      }
    }

    /// <summary>
    /// Draw one biome box for <paramref name="biome"/> at the surface
    /// <paramref name="surfaceWorldPos"/> (grid-centred, no vertical offset),
    /// sized by <paramref name="maturity"/> against the biome's cap and
    /// floated <paramref name="lift"/> above the base overlay height. No-op
    /// when the biome is null or has no drawer (clear-coloured biomes).
    /// </summary>
    private void DrawBiomeBox(
        BiomeKind? biome, float maturity, Vector3 surfaceWorldPos, float lift) {
      if (biome == null || !_drawers.TryGetValue(biome.Value, out var drawer))
        return;

      // Riparian maturity (R) tops out at its own ceiling, not the per-chunk
      // MaturityCap, so scale its box by that -- otherwise a fully-mature
      // riparian tile would read as a thin pin. (They're equal today, but the
      // two caps are kept distinct in case they diverge.)
      var cap = biome.Value == BiomeKind.Riparian
          ? RiparianMaturityParameters.Ceiling
          : MaturityCap;
      var width = Mathf.Lerp(MinBoxWidth, MaxBoxWidth,
          Mathf.Min(maturity / cap, 1f));
      var pos = surfaceWorldPos + new Vector3(0f, VerticalOffset + lift, 0f);
      var matrix = Matrix4x4.TRS(
          pos, Quaternion.identity, new Vector3(width, BoxHeight, width));
      drawer.Draw(matrix);
    }

    #endregion

    #region Object hiding

    private void HideObjectsAt(int x, int y, int z) {
      var objects = _blockService.GetObjectsAt(new Vector3Int(x, y, z));
      for (var i = 0; i < objects.Count; i++) {
        var bo = objects[i];
        if (bo.GetComponent<NaturalResource>() == null) continue;
        var renderers = bo.GameObject.GetComponentsInChildren<Renderer>();
        for (var j = 0; j < renderers.Length; j++) {
          var r = renderers[j];
          if (r.enabled) {
            r.enabled = false;
            _hiddenRenderers.Add(r);
          }
        }
      }
    }

    private void RestoreHidden() {
      foreach (var r in _hiddenRenderers) {
        if (r != null) r.enabled = true;
      }
      _hiddenRenderers.Clear();
    }

    #endregion

    #region Color mapping

    public static Color ColorFor(BiomeKind biome) => biome switch {
        BiomeKind.Forest => ForestColor,
        BiomeKind.Grassland => GrasslandColor,
        BiomeKind.Monoculture => MonocultureColor,
        BiomeKind.Wetland => WetlandColor,
        BiomeKind.River => RiverColor,
        BiomeKind.Lake => LakeColor,
        BiomeKind.Cave => CaveColor,
        BiomeKind.Dry => DryColor,
        BiomeKind.Contaminated => ContaminatedColor,
        BiomeKind.Badwater => BadwaterColor,
        BiomeKind.Riparian => RiparianColor,
        _ => Color.clear,
    };

    #endregion

  }

}
