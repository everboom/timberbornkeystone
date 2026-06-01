using System;
using System.Linq;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.GameDistricts;
using Timberborn.NaturalResourcesMoisture;
using Timberborn.SingletonSystem;
using Timberborn.TemplateCollectionSystem;
using UnityEngine;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Phase 1, prototype P2 (validation): does the second
  /// <see cref="Blueprint(Blueprint, ComponentSpec, ComponentSpec)"/>
  /// constructor preserve the prefab association of the original?
  ///
  /// <para>The first ctor (name + specs + children) creates a fully
  /// independent Blueprint that the prefab pipeline doesn't recognize --
  /// our <c>StrippedEntityProbe</c> proved this by crashing in
  /// <c>BlockObjectModel.Awake</c> looking for <c>#Models</c>. Vanilla
  /// flora visuals come from a Unity .prefab asset, asset-loaded by
  /// name, not synthesized from specs.</para>
  ///
  /// <para>The second ctor is documented as "replace one spec with
  /// another". Open question: does the resulting Blueprint inherit the
  /// donor's prefab association, or is it a fully-distinct object that
  /// also misses the cache?</para>
  ///
  /// <para><b>Test setup.</b> Take vanilla Maple, derive a new Blueprint
  /// by replacing its <see cref="WateredNaturalResourceSpec"/> with the
  /// same instance (no actual mutation -- a no-op replacement). Spawn
  /// via <see cref="BlockObjectFactory"/>. Log:
  /// <list type="bullet">
  ///   <item>Does <c>derived.Name</c> match Maple? (Tells us if the
  ///         second ctor preserves identity by name.)</item>
  ///   <item>Does the spawn produce a visible Maple-shaped object?
  ///         (Tells us if the prefab association survives.)</item>
  /// </list></para>
  ///
  /// <para><b>Outcomes.</b>
  /// <list type="bullet">
  ///   <item><b>Visual appears, no crash:</b> the second ctor preserves
  ///         the prefab. We have a workaround for option 2 -- chain
  ///         replacements to remove unwanted specs (substituting with
  ///         a benign no-op spec). Useful as a backup.</item>
  ///   <item><b>Same #Models crash:</b> the prefab is keyed by Blueprint
  ///         reference, the second ctor doesn't help. Option 2 dead;
  ///         SDK route confirmed as the only path.</item>
  /// </list></para>
  /// </summary>
  public sealed class DerivedBlueprintProbe : IPostLoadableSingleton {

    #region Constants

    private const string DonorTemplateName = "Maple";

    /// <summary>X-distance east of DC. Well clear of other probes
    /// (PassiveObject is at +9, StrippedEntity was at +14).</summary>
    private const int SpawnDistance = 18;

    #endregion

    #region Fields

    private readonly DistrictCenterRegistry _districts;
    private readonly TemplateCollectionService _templates;
    private readonly BlockObjectFactory _blockObjectFactory;

    #endregion

    #region Construction

    public DerivedBlueprintProbe(
        DistrictCenterRegistry districts,
        TemplateCollectionService templates,
        BlockObjectFactory blockObjectFactory) {
      _districts = districts;
      _templates = templates;
      _blockObjectFactory = blockObjectFactory;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      try {
        var center = PickDistrictCenter();
        if (center == null) {
          KeystoneLog.Verbose("[Keystone] DerivedBlueprintProbe: no district center; skipping.");
          return;
        }
        if (!center.TryGetComponent<BlockObject>(out var dcBlock)) {
          KeystoneLog.Verbose("[Keystone] DerivedBlueprintProbe: DC has no BlockObject; skipping.");
          return;
        }

        var donor = _templates.AllTemplates.FirstOrDefault(b => b.Name == DonorTemplateName);
        if (donor == null) {
          KeystoneLog.Verbose(
              $"[Keystone] DerivedBlueprintProbe: donor '{DonorTemplateName}' not found.");
          return;
        }

        var originalWateredSpec = donor.GetSpec<WateredNaturalResourceSpec>();
        if (originalWateredSpec == null) {
          KeystoneLog.Verbose(
              "[Keystone] DerivedBlueprintProbe: donor has no WateredNaturalResourceSpec; " +
              "switching to a different donor would help. Skipping.");
          return;
        }

        // No-op replacement: pass the same instance for both. We're not
        // testing spec mutation -- we're testing whether the second ctor
        // produces a Blueprint that the prefab pipeline still recognizes
        // as belonging to the donor.
        var derived = new Blueprint(donor, originalWateredSpec, originalWateredSpec);

        KeystoneLog.Verbose(
            $"[Keystone] DerivedBlueprintProbe: derived '{derived.Name}' from donor '{donor.Name}'. " +
            $"Same name? {derived.Name == donor.Name}. " +
            $"Same Blueprint instance? {ReferenceEquals(derived, donor)}. " +
            $"Spec count -- donor: {donor.Specs.Length}, derived: {derived.Specs.Length}.");

        var blockObjectSpec = derived.GetSpec<BlockObjectSpec>();
        if (blockObjectSpec == null) {
          KeystoneLog.Verbose(
              "[Keystone] DerivedBlueprintProbe: derived blueprint has no BlockObjectSpec; skipping.");
          return;
        }

        var dc = dcBlock.Coordinates;
        var tileCoord = new Vector3Int(dc.x + SpawnDistance, dc.y, dc.z);
        var placement = new Placement(tileCoord);

        var entity = _blockObjectFactory.CreateFinished(blockObjectSpec, placement);
        if (entity == null) {
          KeystoneLog.Verbose(
              $"[Keystone] DerivedBlueprintProbe: CreateFinished returned null at {tileCoord}.");
          return;
        }

        KeystoneLog.Verbose(
            $"[Keystone] DerivedBlueprintProbe: spawned derived '{derived.Name}' at {tileCoord} " +
            $"(DC at {dc}). If visible: option 2 viable -- second ctor preserves prefab assoc. " +
            $"If invisible/crashed: option 2 dead -- prefab is reference-keyed.");
      } catch (Exception ex) {
        KeystoneLog.Warn($"[Keystone] DerivedBlueprintProbe threw: {ex}");
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
