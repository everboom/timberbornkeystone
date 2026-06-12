using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using Keystone.Mod.Diagnostics;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.Cutting;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.GameDistricts;
using Timberborn.Gathering;
using Timberborn.Growing;
using Timberborn.Planting;
using Timberborn.SingletonSystem;
using Timberborn.TemplateCollectionSystem;
using Timberborn.TimeSystem;
using Timberborn.Yielding;
using UnityEngine;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Phase 1, prototype P2: a non-interactive entity that responds to
  /// environment ("ambient flora").
  ///
  /// <para><b>What this probe does at PostLoad.</b>
  /// <list type="number">
  ///   <item>Lifts the vanilla Maple blueprint, strips the specs that
  ///         drive interaction (gathering, planting, cutting, yielding,
  ///         forestry registration, the click sound), and constructs a
  ///         renamed Blueprint via the public ctor, passing
  ///         <c>donor.Children</c> through unchanged so the visual
  ///         hierarchy is preserved.</item>
  ///   <item>Spawns the renamed blueprint at three diagnostic locations:
  ///         <list type="bullet">
  ///           <item>+14: a single ambient -- the canonical demo</item>
  ///           <item>+18: three at one tile -- proves multi-spawn at one
  ///                 tile is mechanically allowed (visuals overlap; see
  ///                 <c>docs/timberborn-api.md</c> for why)</item>
  ///           <item>+22: vanilla Maple via NaturalResourceFactory, then
  ///                 ambient on top -- proves the BlockValidator rejects
  ///                 ambient placement on top of pre-existing vanilla
  ///                 resources, leaking the prefab GameObject as a
  ///                 side-effect</item>
  ///         </list></item>
  ///   <item>Per spawn: fast-forwards Growable to adult, disables
  ///         <c>BlockObjectNavMesh</c> so beavers walk through, and sets
  ///         <c>BlockObjectSpec.Overridable=true</c> in the spec so
  ///         buildings can replace the flora at placement time.</item>
  /// </list></para>
  ///
  /// <para><b>What does NOT happen here</b>: per-instance visual
  /// placement at sub-tile positions. Vanilla flora rendering goes
  /// through a custom <c>MeshDrawer.DrawMultipleInstanced</c> path that
  /// bypasses Unity Transform (verified empirically; see the doc).
  /// Custom flora visuals with arbitrary positioning come from
  /// SDK-authored timbermeshes, not runtime offsetting.</para>
  ///
  /// <para><b>Selectability + demolish blocking</b> are handled by the
  /// three Harmony patches in <c>HarmonyPatches/</c>, keyed off
  /// <see cref="HarmonyPatches.AmbientNaming.Prefix"/>. No per-spawn
  /// post-processing is needed for those.</para>
  /// </summary>
  public sealed class StrippedEntityProbe : IPostLoadableSingleton {

    #region Constants

    /// <summary>Vanilla blueprint to lift visuals + base specs from.</summary>
    private const string DonorTemplateName = "Maple";

    /// <summary>X-distance east of the district center for the canonical
    /// single-ambient spawn. Chosen to clear other probes (PassiveObject
    /// at +9, ParticleProbe at -6).</summary>
    private const int SpawnDistance = 14;

    /// <summary>X-distance for the multi-at-one-tile demonstration.</summary>
    private const int StackTestDistance = 18;

    /// <summary>X-distance for the vanilla-vs-ambient placement demonstration.</summary>
    private const int VanillaOverlapDistance = 22;

    #endregion

    #region Fields

    private readonly DistrictCenterRegistry _districts;
    private readonly TemplateCollectionService _templates;
    private readonly BlockObjectFactory _blockObjectFactory;
    private readonly Timberborn.NaturalResources.NaturalResourceFactory _naturalResourceFactory;

    #endregion

    #region Construction

    public StrippedEntityProbe(
        DistrictCenterRegistry districts,
        TemplateCollectionService templates,
        BlockObjectFactory blockObjectFactory,
        Timberborn.NaturalResources.NaturalResourceFactory naturalResourceFactory) {
      _districts = districts;
      _templates = templates;
      _blockObjectFactory = blockObjectFactory;
      _naturalResourceFactory = naturalResourceFactory;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      try {
        var center = PickDistrictCenter();
        if (center == null) {
          KeystoneLog.Verbose("[Keystone] StrippedEntityProbe: no district center; skipping.");
          return;
        }
        if (!center.TryGetComponent<BlockObject>(out var dcBlock)) {
          KeystoneLog.Verbose("[Keystone] StrippedEntityProbe: DC has no BlockObject; skipping.");
          return;
        }

        var donor = _templates.AllTemplates.FirstOrDefault(b => b.Name == DonorTemplateName);
        if (donor == null) {
          KeystoneLog.Verbose(
              $"[Keystone] StrippedEntityProbe: donor blueprint '{DonorTemplateName}' " +
              "not found in AllTemplates; skipping.");
          return;
        }

        var stripped = StripInteractionSpecs(donor);
        var blockObjectSpec = stripped.GetSpec<BlockObjectSpec>();
        if (blockObjectSpec == null) {
          KeystoneLog.Verbose(
              "[Keystone] StrippedEntityProbe: stripped blueprint has no BlockObjectSpec; skipping.");
          return;
        }

        var dc = dcBlock.Coordinates;
        SpawnSingle(blockObjectSpec, dc);
        RunStackTest(blockObjectSpec, dc);
        RunVanillaOverlapTest(blockObjectSpec, dc);
      } catch (Exception ex) {
        KeystoneLog.Warn($"[Keystone] StrippedEntityProbe threw: {ex}");
      }
    }

    #endregion

    #region Spawns

    /// <summary>The canonical ambient flora demo: one stripped Maple at
    /// <c>(dc.x + SpawnDistance, dc.y, dc.z)</c>.</summary>
    private void SpawnSingle(BlockObjectSpec spec, Vector3Int dc) {
      var tileCoord = new Vector3Int(dc.x + SpawnDistance, dc.y, dc.z);
      var entity = _blockObjectFactory.CreateFinished(new EntitySetup.Builder(spec.Blueprint), new Placement(tileCoord));
      if (entity == null) {
        KeystoneLog.Verbose(
            $"[Keystone] StrippedEntityProbe: CreateFinished returned null at {tileCoord}.");
        return;
      }
      PostSpawnPolish(entity);
      KeystoneLog.Verbose(
          $"[Keystone] StrippedEntityProbe: spawned ambient '{DonorTemplateName}' at {tileCoord} " +
          "(DC at " + dc + "). Not chop-able, not in planter UI; reacts to moisture; " +
          "non-selectable; non-demolishable; beavers walk through; buildings can replace.");
    }

    /// <summary>
    /// Multi-spawn at one tile. Documents that <see cref="BlockObjectFactory"/>
    /// allows successive ambient placements at a single tile coord
    /// (the <c>Overridable=true</c> + <c>NavMesh</c>-disabled combo
    /// keeps the validator happy). Their visuals overlap because the
    /// renderer doesn't honour Transform offsets -- see
    /// <c>docs/timberborn-api.md</c> for the architecture.
    /// </summary>
    private void RunStackTest(BlockObjectSpec spec, Vector3Int dc) {
      var tileCoord = new Vector3Int(dc.x + StackTestDistance, dc.y, dc.z);
      var placement = new Placement(tileCoord);
      var spawned = 0;
      for (var i = 0; i < 3; i++) {
        try {
          var entity = _blockObjectFactory.CreateFinished(new EntitySetup.Builder(spec.Blueprint), placement);
          if (entity == null) {
            KeystoneLog.Verbose(
                $"[Keystone] StrippedEntityProbe stack-test [{i}]: CreateFinished returned null.");
            continue;
          }
          PostSpawnPolish(entity);
          spawned++;
        } catch (Exception ex) {
          KeystoneLog.Verbose(
              $"[Keystone] StrippedEntityProbe stack-test [{i}] threw: {ex.GetType().Name}: {ex.Message}");
        }
      }
      KeystoneLog.Verbose(
          $"[Keystone] StrippedEntityProbe stack-test: {spawned}/3 placed at {tileCoord}. " +
          "(Visuals overlap; runtime per-instance visual offset isn't supported -- see doc.)");
    }

    /// <summary>
    /// Vanilla-on-top test: spawn a vanilla Maple via
    /// <c>NaturalResourceFactory.SpawnIgnoringConstraints</c>, then try
    /// to place an ambient on the same tile via the BlockObjectFactory.
    /// Documents that ambient placements are *rejected* on tiles already
    /// occupied by a non-overridable vanilla resource, and that the
    /// rejection leaks an instantiated GameObject (the prefab is
    /// instantiated *before* validation). Real Keystone placement code
    /// must pre-check tile occupancy via BlockService.
    /// </summary>
    private void RunVanillaOverlapTest(BlockObjectSpec spec, Vector3Int dc) {
      var tileCoord = new Vector3Int(dc.x + VanillaOverlapDistance, dc.y, dc.z);
      try {
        var vanilla = _naturalResourceFactory.SpawnIgnoringConstraints(DonorTemplateName, tileCoord);
        if (vanilla == null) {
          KeystoneLog.Verbose(
              $"[Keystone] StrippedEntityProbe overlap-test: vanilla not spawned at {tileCoord}.");
          return;
        }
        KeystoneLog.Verbose(
            $"[Keystone] StrippedEntityProbe overlap-test: vanilla '{DonorTemplateName}' placed at {tileCoord}.");
      } catch (Exception ex) {
        KeystoneLog.Verbose(
            $"[Keystone] StrippedEntityProbe overlap-test (vanilla) threw: {ex.GetType().Name}: {ex.Message}");
        return;
      }

      try {
        _blockObjectFactory.CreateFinished(new EntitySetup.Builder(spec.Blueprint), new Placement(tileCoord));
        KeystoneLog.Verbose(
            $"[Keystone] StrippedEntityProbe overlap-test: ambient placement unexpectedly succeeded " +
            $"at {tileCoord} (was supposed to be rejected by BlockValidator).");
      } catch (Exception) {
        KeystoneLog.Verbose(
            $"[Keystone] StrippedEntityProbe overlap-test: ambient REJECTED at {tileCoord} " +
            "(expected). Rejected ambient leaks an orphaned GameObject because the prefab is " +
            "instantiated before validation. Real placement code: pre-check BlockService.");
      }
    }

    #endregion

    #region Strip + post-spawn

    /// <summary>
    /// Build a new Blueprint from <paramref name="donor"/>'s specs,
    /// omitting the ones that drive player interaction we don't want
    /// (chop, plant, harvest, forestry registration, click sound).
    /// Patches the kept <see cref="BlockObjectSpec"/> to be
    /// <c>Overridable=true</c> so buildings can replace the flora.
    /// Carries <c>donor.Children</c> through unchanged -- those hold the
    /// visual hierarchy (TimbermeshSpec on each named child).
    /// </summary>
    private static Blueprint StripInteractionSpecs(Blueprint donor) {
      // Mix of public types (typeof works) and one internal spec
      // (BasicSelectionSoundSpec) we have to refer to by full name.
      var stripFullNames = new HashSet<string> {
          typeof(GatherableSpec).FullName,
          typeof(PlantableSpec).FullName,
          typeof(YielderSpec).FullName,
          typeof(CuttableSpec).FullName,
          typeof(TreeComponentSpec).FullName,
          typeof(BushSpec).FullName,
          "Timberborn.CoreSound.BasicSelectionSoundSpec",
          // NOT stripped: LabeledEntitySpec (panel needs it), DemolishableSpec
          // (decorator wired to a foundational spec; stripping leaves
          // Demolishable.Awake null-derefing). Both are filtered at the
          // tool-input layer by the Harmony patches.
      };

      var keptSpecs = donor.Specs.Where(s => !stripFullNames.Contains(s.GetType().FullName)).ToList();

      // Mark the kept BlockObjectSpec as overridable so buildings can be
      // placed on top of the flora (engine auto-clears it as part of
      // standard natural-resource clearing).
      for (var i = 0; i < keptSpecs.Count; i++) {
        if (keptSpecs[i] is BlockObjectSpec block) {
          keptSpecs[i] = block with { Overridable = true };
          break;
        }
      }

      // donor.Children is non-negotiable: it carries the visual hierarchy
      // (TimbermeshSpec on the #Models/etc. nested blueprints). Passing
      // empty children produces an entity with no visible mesh that
      // crashes BlockObjectModel.Awake on missing #Models child.
      var newName = HarmonyPatches.AmbientNaming.Prefix + donor.Name;
      return new Blueprint(newName, keptSpecs, donor.Children);
    }

    /// <summary>Per-spawn polish that applies regardless of which test
    /// site spawned the entity.</summary>
    private static void PostSpawnPolish(BlockObject entity) {
      TryFastForwardGrowth(entity);
      DisableNavMeshContribution(entity);
    }

    /// <summary>
    /// Drive Growable to its finished state via the private
    /// <c>_timeTrigger</c> field (<see cref="ITimeTrigger.FastForwardProgress"/>).
    /// Without this, the entity spawns at growth stage 0 (sapling) and
    /// renders at a fraction of full size.
    /// </summary>
    private static void TryFastForwardGrowth(BlockObject entity) {
      var growable = entity.GetComponent<Growable>();
      if (growable == null) return;

      var field = typeof(Growable).GetField("_timeTrigger",
          BindingFlags.Instance | BindingFlags.NonPublic);
      if (field == null) {
        KeystoneLog.Verbose(
            "[Keystone] StrippedEntityProbe: Growable has no '_timeTrigger' field. Schema changed?");
        return;
      }

      if (field.GetValue(growable) is not ITimeTrigger trigger) {
        KeystoneLog.Verbose(
            "[Keystone] StrippedEntityProbe: '_timeTrigger' is not an ITimeTrigger. Schema changed?");
        return;
      }

      trigger.FastForwardProgress(trigger.DaysLeft);
    }

    /// <summary>
    /// Disable the BlockObject's navmesh contribution so beavers walk
    /// through unhindered. Both target types are internal to Timberborn,
    /// so we walk <see cref="BaseComponent.AllComponents"/> and match by
    /// type name rather than compile-time reference.
    /// </summary>
    private static void DisableNavMeshContribution(BlockObject entity) {
      foreach (var component in entity.AllComponents) {
        if (component is not BaseComponent bc) continue;
        var typeName = bc.GetType().Name;
        if (typeName == "BlockObjectNavMesh" || typeName == "BlockObjectPreviewNavMesh") {
          bc.DisableComponent();
        }
      }
    }

    private DistrictCenter? PickDistrictCenter() {
      var finished = _districts.FinishedDistrictCenters;
      if (finished.Count > 0) return finished[0];
      var all = _districts.AllDistrictCenters;
      return all.Count > 0 ? all[0] : null;
    }

    #endregion

  }

}
