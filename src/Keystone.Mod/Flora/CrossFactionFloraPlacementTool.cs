using System;
using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Mod.Debug;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.HarmonyPatches;
using Keystone.Mod.Recipes;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.CursorToolSystem;
using Timberborn.FactionSystem;
using Timberborn.Growing;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.NaturalResources;
using Timberborn.TemplateCollectionSystem;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;

namespace Keystone.Mod.Flora {

  /// <summary>
  /// Dev tool: spawns a random faction-incompatible natural resource at
  /// the clicked tile. Class D in the content taxonomy
  /// (see <c>DESIGN.md</c> § "Content classes") -- the entity is a
  /// vanilla flora from the OTHER faction, accessible because
  /// <see cref="CrossFactionProviderBase"/> teaches the collection
  /// service to load both factions' templates.
  ///
  /// <para><c>TemplateCollectionServicePatch</c> strips
  /// <c>PlantableSpec</c>/<c>GatherableSpec</c>/<c>CuttableSpec</c>
  /// from cross-faction templates so the active faction's UIs don't
  /// crash on them; the spawned entity still grows, reacts to the
  /// moisture / lifecycle pipeline, and (if its harvest yield was
  /// kept) can be cut or gathered.</para>
  ///
  /// <para><b>Donor pool.</b> Computed lazily at first click from
  /// <see cref="TemplateCollectionService.AllTemplates"/> filtered to
  /// blueprints that carry <see cref="NaturalResourceSpec"/> but whose
  /// <c>Name</c> is NOT in the active <see cref="FactionSpec"/>'s
  /// <c>TemplateCollectionIds</c>. That is the same "non-native"
  /// definition the strip patch uses, so the tool spawns exactly the
  /// set the strip pass operates on. Each click picks uniformly at
  /// random from the pool -- crops, bushes, trees alike.</para>
  /// </summary>
  public sealed class CrossFactionFloraPlacementTool : ITool, IInputProcessor, IToolDescriptor {

    private const string DisplayNameKey = "Tool.Keystone.CrossFactionFlora.DisplayName";
    private const string DescriptionKey = "Tool.Keystone.CrossFactionFlora.Description";

    /// <summary>Clearance probe height above the click tile. Covers
    /// the tallest vanilla flora we'd place here (trees ~2 voxels);
    /// crops and bushes are well under that, so a generous fixed
    /// value avoids a per-donor clearance lookup.</summary>
    private const int DonorHeight = 2;

    private readonly InputService _inputService;
    private readonly CursorCoordinatesPicker _cursorCoordinatesPicker;
    private readonly NaturalResourceFactory _factory;
    private readonly TemplateCollectionService _templateCollectionService;
    private readonly IBlockService _blockService;
    private readonly ITerrainQuery _terrain;
    private readonly IPlantingMarkQuery _marks;
    private readonly ILoc _loc;
    private readonly Random _rng = new Random();

    /// <summary>Cached cross-faction donor names (lazily computed on
    /// first <see cref="ProcessInput"/>). Null until built. Reset
    /// implicitly across game sessions because the tool is a Game-
    /// scope singleton.</summary>
    private List<string>? _donors;

    public CrossFactionFloraPlacementTool(
        InputService inputService,
        CursorCoordinatesPicker cursorCoordinatesPicker,
        NaturalResourceFactory factory,
        TemplateCollectionService templateCollectionService,
        IBlockService blockService,
        ITerrainQuery terrain,
        IPlantingMarkQuery marks,
        ILoc loc) {
      _inputService = inputService;
      _cursorCoordinatesPicker = cursorCoordinatesPicker;
      _factory = factory;
      _templateCollectionService = templateCollectionService;
      _blockService = blockService;
      _terrain = terrain;
      _marks = marks;
      _loc = loc;
    }

    public ToolDescription DescribeTool() {
      return new ToolDescription.Builder(_loc.T(DisplayNameKey))
          .AddSection(_loc.T(DescriptionKey))
          .Build();
    }

    public void Enter() {
      _inputService.AddInputProcessor(this);
      var donors = EnsureDonors();
      KeystoneLog.Verbose(
          $"[Keystone] CrossFactionFloraPlacementTool entered. Left-click to spawn " +
          $"a random non-native flora from a pool of {donors.Count} blueprints " +
          $"(active='{FactionIdAccessor.CurrentId}'). Esc/right-click to exit.");
    }

    public void Exit() {
      _inputService.RemoveInputProcessor(this);
    }

    public bool ProcessInput() {
      if (_inputService.MouseOverUI) return false;
      if (!_inputService.MainMouseButtonDown) return false;

      var picked = _cursorCoordinatesPicker.Pick();
      if (!picked.HasValue) return false;

      var tile = picked.Value.TileCoordinates;
      if (_marks.IsMarked(tile.x, tile.y, tile.z)) {
        KeystoneLog.Verbose(
            $"[Keystone] CrossFactionFloraPlacementTool: tile {tile} is marked " +
            "for planting; skipping (player intent overrides dev placement).");
        return true;
      }
      if (!VerticalClearance.IsAboveClear(_blockService, _terrain, tile, DonorHeight)) {
        KeystoneLog.Verbose(
            $"[Keystone] CrossFactionFloraPlacementTool: clearance above {tile} " +
            $"insufficient (Height={DonorHeight}); skipping.");
        return true;
      }
      var donors = EnsureDonors();
      if (donors.Count == 0) {
        KeystoneLog.Verbose(
            "[Keystone] CrossFactionFloraPlacementTool: no cross-faction donors " +
            "available (CrossFactionCollectionProvider may not have loaded the " +
            "other faction's NaturalResources). Skipping.");
        return true;
      }
      var donor = donors[_rng.Next(donors.Count)];
      try {
        var resource = _factory.SpawnIgnoringConstraints(donor, tile);
        if (resource == null) {
          KeystoneLog.Verbose(
              $"[Keystone] CrossFactionFloraPlacementTool: SpawnIgnoringConstraints " +
              $"returned null for '{donor}' at {tile} (spawn validation refused -- " +
              "wrong matter type / blocked tile / etc.).");
        } else {
          FastForwardGrowth(resource);
          KeystoneLog.Verbose($"[Keystone] CrossFactionFloraPlacementTool: spawned '{donor}' at {tile}.");
        }
      } catch (Exception ex) {
        KeystoneLog.Warn(
            $"[Keystone] CrossFactionFloraPlacementTool: spawn of '{donor}' at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
      return true; // input consumed
    }

    /// <summary>Collection id of the shared, faction-agnostic vanilla
    /// natural-resource set (Pine, Birch, Oak, BlueberryBush, ...).
    /// Native to every faction, so excluded from the donor pool.</summary>
    private const string CommonNaturalResourceCollectionId = "NaturalResources.Common";

    /// <summary>
    /// Build (and cache) the list of cross-faction donor blueprint
    /// names. A blueprint qualifies iff all of:
    /// <list type="bullet">
    ///   <item>It is loaded into <see cref="TemplateCollectionService.AllTemplates"/>
    ///         and carries <see cref="NaturalResourceSpec"/>.</item>
    ///   <item>Its <c>Name</c> is NOT in the active-faction-native set
    ///         (union of the active <see cref="FactionSpec"/>'s
    ///         <c>TemplateCollectionIds</c>-resolved blueprint names AND
    ///         <see cref="CommonNaturalResourceCollectionId"/>'s
    ///         blueprints).</item>
    ///   <item>Its <c>Name</c> doesn't start with <c>"Keystone"</c>
    ///         (mod-owned flourishes are placed via their own dev tools,
    ///         not the cross-faction button).</item>
    /// </list>
    ///
    /// <para><b>Why a name blacklist instead of collection-id allowlist.</b>
    /// Expansion factions (Emberpelts is the live example) ship their
    /// own <c>NaturalResources.&lt;Faction&gt;</c> collection that
    /// references vanilla blueprints from the donor faction
    /// (Emberpelts is an IronTeeth-derivative and lists IronTeeth flora
    /// like Canola, Corn, Mangrove). Under IronTeeth play, those
    /// blueprints are ALREADY native via IronTeeth's own collection --
    /// they must not appear in the cross-faction donor pool. Walking
    /// non-active per-faction collections and including their blueprints
    /// would re-introduce them via the Emberpelts side, even when the
    /// IronTeeth side already lists them. Computing native names FIRST
    /// and excluding them by name regardless of which collection
    /// referenced them sidesteps this.</para>
    /// </summary>
    private List<string> EnsureDonors() {
      if (_donors != null) return _donors;
      var donors = new List<string>();
      var factionSpec = FactionIdAccessor.CurrentSpec;
      var specs = SpecServiceAccessor.Specs;
      if (factionSpec == null || specs == null) {
        KeystoneLog.Verbose(
            "[Keystone] CrossFactionFloraPlacementTool: cannot build donor pool -- " +
            $"factionSpec={(factionSpec == null ? "null" : "ok")}, " +
            $"specService={(specs == null ? "null" : "ok")}.");
        _donors = donors;
        return donors;
      }
      var nativeNames = ComputeActiveNativeNaturalResourceNames(factionSpec, specs);
      foreach (var bp in _templateCollectionService.AllTemplates) {
        if (!bp.HasSpec<NaturalResourceSpec>()) continue;
        if (nativeNames.Contains(bp.Name)) continue;
        if (bp.Name.StartsWith("Keystone", StringComparison.Ordinal)) continue;
        donors.Add(bp.Name);
      }
      donors.Sort(StringComparer.Ordinal);
      _donors = donors;
      KeystoneLog.Verbose(
          $"[Keystone] CrossFactionFloraPlacementTool: donor pool ({donors.Count}): " +
          string.Join(", ", donors));
      return donors;
    }

    /// <summary>
    /// Names of all <see cref="NaturalResourceSpec"/>-bearing
    /// blueprints that the active faction considers native: the union
    /// of blueprints listed in any <see cref="TemplateCollectionSpec"/>
    /// whose <c>CollectionId</c> appears in the active
    /// <see cref="FactionSpec.TemplateCollectionIds"/>, plus blueprints
    /// in <see cref="CommonNaturalResourceCollectionId"/> (shared
    /// across every faction). Used as a name blacklist when building
    /// the cross-faction donor pool.
    /// </summary>
    private static HashSet<string> ComputeActiveNativeNaturalResourceNames(
        FactionSpec factionSpec, ISpecService specs) {
      var native = new HashSet<string>(StringComparer.Ordinal);
      var nativeCollectionIds = new HashSet<string>(StringComparer.Ordinal) {
          CommonNaturalResourceCollectionId,
      };
      if (!factionSpec.TemplateCollectionIds.IsDefaultOrEmpty) {
        foreach (var id in factionSpec.TemplateCollectionIds) {
          if (!string.IsNullOrEmpty(id)) nativeCollectionIds.Add(id);
        }
      }
      foreach (var collection in specs.GetSpecs<TemplateCollectionSpec>()) {
        if (!nativeCollectionIds.Contains(collection.CollectionId)) continue;
        foreach (var assetRef in collection.Blueprints) {
          var name = ExtractBlueprintName(assetRef.Path);
          if (name != null) native.Add(name);
        }
      }
      return native;
    }

    private static string? ExtractBlueprintName(string path) {
      if (string.IsNullOrEmpty(path)) return null;
      var lastSlash = path.LastIndexOfAny(new[] { '/', '\\' });
      var basename = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
      const string ext = ".blueprint";
      if (basename.EndsWith(ext, StringComparison.Ordinal)) {
        basename = basename.Substring(0, basename.Length - ext.Length);
      }
      return basename.Length > 0 ? basename : null;
    }

    private static void FastForwardGrowth(BaseComponent resource) {
      var growable = resource.GetComponent<Growable>();
      if (growable != null) {
        GrowableTimeTriggerAccessor.FastForwardToMature(growable);
      }
    }

  }

}
