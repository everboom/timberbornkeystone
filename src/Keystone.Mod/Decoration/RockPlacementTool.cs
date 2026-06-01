using System;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Timberborn.CursorToolSystem;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.Timbermesh;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using UnityEngine;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// Dev tool: spawns a Class-A decoration cloning the
  /// <c>Rock_medium_1</c> Keystone-original natural-resource blueprint,
  /// then swaps the renderer's material based on the dominant biome
  /// under the cursor.
  ///
  /// <para><b>Biome-driven variant.</b> The blueprint's
  /// <c>TimbermeshSpec</c> nominally references material
  /// <c>KeystoneRock</c> (untinted), but at spawn time this tool
  /// overrides the renderer based on which biome the chunk under the
  /// cursor scores highest in:
  /// <list type="bullet">
  ///   <item>Mossy → Wetland, River, Lake
  ///         (wild irrigated / sheltered / "green" conditions).</item>
  ///   <item>Default (untinted) → Monoculture
  ///         ("colonized" land: beaver-developed, low ecological
  ///         diversity but not arid).</item>
  ///   <item>Dry → Dry, Contaminated, Badwater,
  ///         River, Lake (anything else: the dry palette reads as
  ///         "bare/exposed stone").</item>
  /// </list>
  /// The fallback for water biomes (River/Lake) to "dry" rather than
  /// "mossy" is a deliberate aesthetic call: a freshly-placed rock at
  /// a riverbank should look unweathered, not pre-aged with moss.</para>
  ///
  /// <para><b>Why the variant choice is at spawn, not runtime.</b>
  /// This is a one-shot tool: rocks placed during play don't change
  /// biome classification later. For reactive per-tick tint
  /// adjustments (e.g., the rock greens up as moss takes over), a
  /// separate <c>IDecorationController</c> on the decoration would do
  /// that via <see cref="MaterialPropertyBlock"/> — not implemented
  /// here because the Quaternius variants are different .mat assets,
  /// not parameter tweaks on a shared material.</para>
  ///
  /// <para><b>Refusal cases.</b> Same as
  /// <see cref="Keystone.Mod.Flourish.FlourishPlacementTool"/>: cursor
  /// off-map, no region, no ecology field. Click is consumed, no spawn,
  /// warning logged.</para>
  /// </summary>
  public sealed class RockPlacementTool : ITool, IInputProcessor, IToolDescriptor {

    #region Constants

    private const string DonorBlueprintName = "Rock_medium_1";
    private const string DisplayNameKey = "Tool.Keystone.Rock.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.Rock.Description";

    /// <summary>Material names shipped in <c>MaterialCollection.Keystone</c>;
    /// each matches the <c>.mat</c> filename in the asset bundle.
    /// <c>KeystoneRock</c> is the baseline (no tint — raw texture);
    /// <c>_Mossy</c> and <c>_Dry</c> are the biome-tinted variants.
    /// The <c>.timbermesh</c> references the baseline name natively;
    /// this tool overrides per-spawn for biome-appropriate variants.</summary>
    private const string DefaultMaterialName = "KeystoneRock";
    private const string MossyMaterialName = "KeystoneRock_Mossy";
    private const string DryMaterialName = "KeystoneRock_Dry";

    /// <summary>Cached enum values. Same allocation-avoidance reason
    /// as the rest of the periodic ecology pipeline.</summary>
    private static readonly BiomeKind[] AllBiomes =
        (BiomeKind[])Enum.GetValues(typeof(BiomeKind));

    #endregion

    #region Fields + ctor

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly KeystoneDecorationRegistry _registry;
    private readonly IPlantingMarkQuery _marks;
    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly IMaterialRepository _materials;
    private readonly ILoc _loc;

    public RockPlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        KeystoneDecorationRegistry registry,
        IPlantingMarkQuery marks,
        RegionService regions,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        IMaterialRepository materials,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _registry = registry;
      _marks = marks;
      _regions = regions;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _materials = materials;
      _loc = loc;
    }

    #endregion

    #region ITool / IToolDescriptor

    public ToolDescription DescribeTool() {
      return new ToolDescription.Builder(_loc.T(DisplayNameKey))
          .AddSection(_loc.T(DescriptionKey))
          .Build();
    }

    public void Enter() {
      _inputService.AddInputProcessor(this);
      KeystoneLog.Verbose(
          "[Keystone] RockPlacementTool entered. Left-click a tile to " +
          "spawn a Keystone rock variant (mossy or dry) appropriate to " +
          "the biome under the cursor. Esc/right-click to exit.");
    }

    public void Exit() {
      _inputService.RemoveInputProcessor(this);
    }

    #endregion

    #region IInputProcessor

    public bool ProcessInput() {
      if (_inputService.MouseOverUI) return false;
      if (!_inputService.MainMouseButtonDown) return false;

      var picked = _cursorCoordinatesPicker.Pick();
      if (!picked.HasValue) return false;

      var tile = picked.Value.TileCoordinates;
      try {
        SpawnAt(tile);
      } catch (Exception ex) {
        KeystoneLog.Warn(
            $"[Keystone] RockPlacementTool: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true;
    }

    #endregion

    #region Spawn + variant selection

    private void SpawnAt(Vector3Int tile) {
      if (_marks.IsMarked(tile.x, tile.y, tile.z)) {
        KeystoneLog.Verbose(
            $"[Keystone] RockPlacementTool: tile {tile} is marked for " +
            "planting; skipping (player intent overrides dev placement).");
        return;
      }

      var biome = ResolveBiome(tile);
      var materialName = MaterialNameForBiome(biome);

      var decoration = _registry.Spawn(DonorBlueprintName, tile, controller: null);
      if (decoration == null) {
        // Warning already logged by KeystoneDecorationRegistry.
        return;
      }
      ApplyVariantMaterial(decoration.Root, materialName, biome);
    }

    /// <summary>Picks the highest-scoring biome at the cursor's tile.
    /// Unlike <see cref="Keystone.Mod.Flourish.FlourishPlacementTool"/>
    /// we don't gate on recipe registration -- every biome maps to a
    /// material so we always have a variant to pick.</summary>
    private BiomeKind ResolveBiome(Vector3Int tile) {
      var surface = new SurfaceCoord(tile.x, tile.y, tile.z);
      var region = _regions.Containing(surface);
      if (region == null) {
        KeystoneLog.Verbose(
            $"[Keystone] RockPlacementTool: no region at {tile}; " +
            "defaulting to Dry variant.");
        return BiomeKind.Dry;
      }
      var field = _fieldQuery.FieldFor(region.Id);
      if (field == null) {
        KeystoneLog.Verbose(
            $"[Keystone] RockPlacementTool: no ecology field for region " +
            $"{region.Id}; defaulting to Dry variant.");
        return BiomeKind.Dry;
      }

      var bestBiome = BiomeKind.Dry;
      var bestScore = float.NegativeInfinity;
      for (var i = 0; i < AllBiomes.Length; i++) {
        var b = AllBiomes[i];
        var score = ChunkBiomeSampler.SampleSuitability(
            _biomeValues, region.Id, b,
            field.OriginX, field.OriginY,
            field.ChunksX, field.ChunksY,
            tile.x, tile.y);
        if (score > bestScore) {
          bestScore = score;
          bestBiome = b;
        }
      }
      return bestBiome;
    }

    /// <summary>Biome → material name mapping. Three buckets:
    /// mossy for submerged/saturated biomes (rocks under water grow
    /// moss/algae); default/untinted for irrigated land (exposed
    /// surface rocks read as clean stone); dry for arid/contaminated.
    /// Keep in sync with <c>KeystoneRockTintService.SuffixForBiome</c> —
    /// they implement the same policy via different code paths
    /// (spawn-time material name vs runtime suffix).</summary>
    private static string MaterialNameForBiome(BiomeKind biome) {
      switch (biome) {
        // Submerged -- mossy.
        case BiomeKind.Wetland:
        case BiomeKind.River:
        case BiomeKind.Lake:
          return MossyMaterialName;
        // Irrigated land (sheltered, open) -- base/untinted.
        case BiomeKind.Grassland:
        case BiomeKind.Forest:
        case BiomeKind.Cave:
        case BiomeKind.Monoculture:
          return DefaultMaterialName;
        // Arid / contaminated -- dry.
        case BiomeKind.Dry:
        case BiomeKind.Contaminated:
        case BiomeKind.Badwater:
        default:
          return DryMaterialName;
      }
    }

    private void ApplyVariantMaterial(GameObject? root, string materialName, BiomeKind biome) {
      if (root == null) {
        KeystoneLog.Verbose(
            "[Keystone] RockPlacementTool: spawned decoration had null Root; " +
            "skipping material swap.");
        return;
      }
      var renderer = root.GetComponentInChildren<MeshRenderer>();
      if (renderer == null) {
        KeystoneLog.Verbose(
            "[Keystone] RockPlacementTool: spawned rock has no MeshRenderer; " +
            "skipping material swap.");
        return;
      }
      var material = _materials.GetMaterial(materialName);
      if (material == null) {
        // Asset-bundle / registration bug: the material exists in the
        // mod's expectations but isn't being served. Keep loud even
        // though the surrounding tool is dev-gated -- losing this
        // signal during asset work has burned us before.
        KeystoneLog.Warn(
            $"[Keystone] RockPlacementTool: IMaterialRepository.GetMaterial" +
            $"('{materialName}') returned null. Check that " +
            "MaterialCollection.Keystone lists it and that " +
            "KeystoneMaterialProvider is bound.");
        return;
      }
      renderer.sharedMaterial = material;
      KeystoneLog.Verbose(
          $"[Keystone] RockPlacementTool: spawned rock as '{materialName}' " +
          $"for biome {biome}.");
    }

    #endregion

  }

}
