using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.GameDistricts;
using Timberborn.Growing;
using Timberborn.NaturalResources;
using Timberborn.SingletonSystem;
using Timberborn.TemplateCollectionSystem;
using Timberborn.TemplateSystem;
using Timberborn.TerrainSystem;
using Timberborn.TimbermeshMaterials;
using Timberborn.TimeSystem;
using UnityEngine;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Phase 1, prototype #1: prove out the cross-faction natural-resource
  /// spawn path. On <see cref="PostLoad"/>:
  /// <list type="number">
  ///   <item>Dump every <see cref="TemplateCollectionSpec.CollectionId"/> and
  ///         <see cref="TemplateSpec.TemplateName"/> for diagnostics.</item>
  ///   <item>Spawn one row of every Ironteeth-only natural resource at
  ///         <c>x = dc.x + IronteethRowDistance</c>, walking <c>+y</c>.</item>
  ///   <item>Spawn one row of every Folktails-only natural resource at
  ///         <c>x = dc.x + FolktailsRowDistance</c>, walking <c>+y</c>.</item>
  ///   <item>Probe a known cross-faction building blueprint to confirm
  ///         buildings stay locked (we don't load <c>Buildings.*</c>).</item>
  /// </list>
  ///
  /// <para><b>Why both lists every time.</b> We're verifying the cross-faction
  /// unlock works in both directions: the active faction always sees its own
  /// row (sanity check), and the other faction's row only appears if the
  /// cross-faction <c>NaturalResources</c> collection was loaded by
  /// <see cref="CrossFactionCollectionProvider"/>. Two side-by-side lines
  /// in-game make the result obvious at a glance.</para>
  /// </summary>
  public sealed class KeystoneSpawnProbe : IPostLoadableSingleton {

    #region Constants

    /// <summary>X-distance from the district center for the Ironteeth-only resource row.</summary>
    private const int IronteethRowDistance = 5;

    /// <summary>X-distance from the district center for the Folktails-only resource row.</summary>
    private const int FolktailsRowDistance = 6;

    /// <summary>
    /// Cross-faction building probe target. Buildings shipped post-faction-split
    /// shouldn't have backward-compatible aliases (no pre-split saves to support),
    /// so this should be the cleanest test of "what does a missing template
    /// actually do?". We deliberately don't load <c>Buildings.IronTeeth</c>,
    /// so this is expected to fail with "Blueprint not found".
    /// </summary>
    private const string CrossFactionBuildingTemplate = "Metalsmith.IronTeeth";

    /// <summary>Tile-distance from the district center for the building probe.</summary>
    private const int BuildingSpawnDistance = 10;

    /// <summary>
    /// Templates that appear only in <c>NaturalResources.IronTeeth</c>
    /// (verified by diffing the per-faction template dumps). Spawning these
    /// from a Folktails game is the cross-faction unlock test.
    /// </summary>
    private static readonly string[] IronteethOnlyResources = {
        "Mangrove", "CoffeeBush",
        "Canola", "Cassava", "Corn", "Eggplant", "Kohlrabi", "Soybean",
    };

    /// <summary>
    /// Templates that appear only in <c>NaturalResources.Folktails</c>.
    /// Spawning these from an Ironteeth game is the symmetric test.
    /// </summary>
    private static readonly string[] FolktailsOnlyResources = {
        "Maple", "ChestnutTree",
        "Dandelion", "Sunflower", "Cattail", "Spadderdock",
        "Carrot", "Wheat", "Potato",
    };

    #endregion

    #region Fields

    private readonly DistrictCenterRegistry _districts;
    private readonly NaturalResourceFactory _factory;
    private readonly ITerrainService _terrain;
    private readonly TemplateService _templates;
    private readonly ISpecService _specs;
    private readonly BlockObjectFactory _blockFactory;

    #endregion

    #region Construction

    public KeystoneSpawnProbe(
        DistrictCenterRegistry districts,
        NaturalResourceFactory factory,
        ITerrainService terrain,
        TemplateService templates,
        ISpecService specs,
        BlockObjectFactory blockFactory) {
      _districts = districts;
      _factory = factory;
      _terrain = terrain;
      _templates = templates;
      _specs = specs;
      _blockFactory = blockFactory;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      DumpCollectionIds();
      DumpMaterialCollectionIds();
      SpawnFactionRow("Ironteeth-only", IronteethOnlyResources, IronteethRowDistance);
      SpawnFactionRow("Folktails-only", FolktailsOnlyResources, FolktailsRowDistance);
      TryCrossFactionBuilding();
    }

    #endregion

    #region Diagnostics

    private void DumpCollectionIds() {
      try {
        var collections = _specs.GetSpecs<TemplateCollectionSpec>().ToList();
        var buffer = new StringBuilder();
        buffer.Append("[Keystone] SpawnProbe: ").Append(collections.Count).AppendLine(" TemplateCollectionSpec(s):");
        foreach (var c in collections) {
          buffer.Append("  CollectionId=").Append(c.CollectionId)
                .Append("  blueprints=").Append(c.Blueprints.Length)
                .AppendLine();
        }
        KeystoneLog.Verbose(buffer.ToString());
      } catch (Exception ex) {
        KeystoneLog.Verbose($"[Keystone] SpawnProbe: collection-id dump failed: {ex}");
      }
    }

    private void DumpMaterialCollectionIds() {
      try {
        var collections = _specs.GetSpecs<MaterialCollectionSpec>().ToList();
        var buffer = new StringBuilder();
        buffer.Append("[Keystone] SpawnProbe: ").Append(collections.Count).AppendLine(" MaterialCollectionSpec(s):");
        foreach (var c in collections) {
          buffer.Append("  CollectionId=").Append(c.CollectionId)
                .Append("  materials=").Append(c.Materials.Length)
                .AppendLine();
        }
        KeystoneLog.Verbose(buffer.ToString());
      } catch (Exception ex) {
        KeystoneLog.Verbose($"[Keystone] SpawnProbe: material-collection dump failed: {ex}");
      }
    }

    private void DumpTemplateNames() {
      try {
        var names = _templates.GetAll<TemplateSpec>()
            .Select(s => s.TemplateName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var buffer = new StringBuilder();
        buffer.Append("[Keystone] SpawnProbe: ").Append(names.Count).AppendLine(" templates:");
        foreach (var name in names) {
          buffer.Append("  ").AppendLine(name);
        }
        KeystoneLog.Verbose(buffer.ToString());
      } catch (Exception ex) {
        KeystoneLog.Verbose($"[Keystone] SpawnProbe: template dump failed: {ex}");
      }
    }

    #endregion

    #region Resource-row spawn

    /// <summary>
    /// Spawn each template in <paramref name="templates"/> in a column of
    /// tiles starting at <c>(dc.x + xOffset, dc.y, dc.z)</c>, advancing
    /// along <c>+y</c> by one tile per resource. Each spawned <see cref="Growable"/>
    /// is fast-forwarded to its finished state so trees appear adult.
    /// </summary>
    private void SpawnFactionRow(string label, IReadOnlyList<string> templates, int xOffset) {
      DistrictCenter? center = PickDistrictCenter();
      if (center == null) {
        KeystoneLog.Verbose($"[Keystone] SpawnProbe[{label}]: no district center; skipping row.");
        return;
      }
      if (!center.TryGetComponent<BlockObject>(out var block)) {
        KeystoneLog.Verbose($"[Keystone] SpawnProbe[{label}]: district center has no BlockObject; skipping row.");
        return;
      }

      var dc = block.Coordinates;
      KeystoneLog.Verbose(
          $"[Keystone] SpawnProbe[{label}]: DC at {dc}, spawning {templates.Count} resource(s) " +
          $"at x={dc.x + xOffset}, y in [{dc.y}..{dc.y + templates.Count - 1}], z={dc.z}.");

      for (var i = 0; i < templates.Count; i++) {
        var name = templates[i];
        var coord = new Vector3Int(dc.x + xOffset, dc.y + i, dc.z);

        if (!_terrain.Contains(new Vector2Int(coord.x, coord.y))) {
          KeystoneLog.Verbose($"[Keystone] SpawnProbe[{label}]: '{name}' target {coord} off map; skipping.");
          continue;
        }

        try {
          var spawned = _factory.SpawnIgnoringConstraints(name, coord);
          if (spawned == null) {
            KeystoneLog.Verbose(
                $"[Keystone] SpawnProbe[{label}]: '{name}' returned null at {coord} -- " +
                "template not resolvable for the active faction's registry.");
            continue;
          }
          KeystoneLog.Verbose($"[Keystone] SpawnProbe[{label}]: '{name}' -> {spawned.GameObject.name} at {coord}.");
          TryFastForwardGrowth(spawned);
        } catch (Exception ex) {
          KeystoneLog.Verbose($"[Keystone] SpawnProbe[{label}]: '{name}' at {coord} threw: {ex}");
        }
      }
    }

    /// <summary>
    /// Push the resource's <see cref="Growable"/> through to its finished
    /// state by reaching for the private <c>_timeTrigger</c> field and
    /// driving it via the public <see cref="ITimeTrigger"/> API. Reflection
    /// is unavoidable -- <see cref="Growable"/> exposes nothing public --
    /// but everything we do through the trigger is the same path the
    /// game itself uses when growth completes naturally.
    /// </summary>
    private static void TryFastForwardGrowth(NaturalResource resource) {
      var growable = resource.GetComponent<Growable>();
      if (growable == null) {
        // Crops and similar resources don't carry Growable; no warning -- expected.
        return;
      }

      var field = typeof(Growable).GetField("_timeTrigger", BindingFlags.Instance | BindingFlags.NonPublic);
      if (field == null) {
        KeystoneLog.Verbose("[Keystone] SpawnProbe: Growable has no '_timeTrigger' field. Schema changed?");
        return;
      }

      if (field.GetValue(growable) is not ITimeTrigger trigger) {
        KeystoneLog.Verbose("[Keystone] SpawnProbe: '_timeTrigger' is not an ITimeTrigger. Schema changed?");
        return;
      }

      trigger.FastForwardProgress(trigger.DaysLeft);
    }

    #endregion

    #region Cross-faction building probe

    /// <summary>
    /// Cross-faction reachability probe. Tries to look up an
    /// IronTeeth-only building blueprint and, if found, spawn a finished
    /// instance. Buildings post-faction-split don't carry backward-
    /// compatible aliases, so this isolates whether the registry itself
    /// is faction-filtered or whether only the surface enumeration is.
    /// We deliberately don't load <c>Buildings.IronTeeth</c>, so on
    /// Folktails this is expected to throw "Blueprint not found".
    /// </summary>
    private void TryCrossFactionBuilding() {
      Blueprint? blueprint;
      try {
        blueprint = _specs.GetBlueprint(CrossFactionBuildingTemplate);
      } catch (Exception ex) {
        KeystoneLog.Verbose(
            $"[Keystone] SpawnProbe: GetBlueprint('{CrossFactionBuildingTemplate}') threw " +
            $"({ex.GetType().Name}: {ex.Message}). Treating as 'not registered'.");
        return;
      }

      if (blueprint == null) {
        KeystoneLog.Verbose(
            $"[Keystone] SpawnProbe: GetBlueprint('{CrossFactionBuildingTemplate}') returned null. " +
            "Cross-faction building template is not reachable on this faction.");
        return;
      }

      KeystoneLog.Verbose(
          $"[Keystone] SpawnProbe: GetBlueprint('{CrossFactionBuildingTemplate}') returned a blueprint. " +
          "Cross-faction building template IS reachable -- attempting to spawn it.");

      if (!blueprint.HasSpec<BlockObjectSpec>()) {
        KeystoneLog.Verbose(
            $"[Keystone] SpawnProbe: '{CrossFactionBuildingTemplate}' blueprint has no BlockObjectSpec; cannot spawn.");
        return;
      }

      var spec = blueprint.GetSpec<BlockObjectSpec>();
      DistrictCenter? center = PickDistrictCenter();
      if (center == null) {
        KeystoneLog.Verbose("[Keystone] SpawnProbe: no district center for building probe; skipping.");
        return;
      }
      if (!center.TryGetComponent<BlockObject>(out var dcBlock)) {
        KeystoneLog.Verbose("[Keystone] SpawnProbe: district center has no BlockObject; skipping building probe.");
        return;
      }

      var dc = dcBlock.Coordinates;
      var coord = new Vector3Int(dc.x + BuildingSpawnDistance, dc.y, dc.z);

      try {
        var built = _blockFactory.CreateFinished(spec, new Placement(coord));
        KeystoneLog.Verbose(
            $"[Keystone] SpawnProbe: CreateFinished('{CrossFactionBuildingTemplate}', {coord}) -> " +
            $"{(built == null ? "null" : built.GameObject.name)}.");
      } catch (Exception ex) {
        KeystoneLog.Verbose(
            $"[Keystone] SpawnProbe: CreateFinished('{CrossFactionBuildingTemplate}') threw: {ex}");
      }
    }

    #endregion

    #region Helpers

    private DistrictCenter? PickDistrictCenter() {
      var finished = _districts.FinishedDistrictCenters;
      if (finished.Count > 0) return finished[0];
      var all = _districts.AllDistrictCenters;
      return all.Count > 0 ? all[0] : null;
    }

    #endregion

  }

}
