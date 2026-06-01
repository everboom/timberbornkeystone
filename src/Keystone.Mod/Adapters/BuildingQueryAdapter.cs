using Keystone.Core.Buildings;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Wellbeing;
using Timberborn.BlockSystem;
using Timberborn.EnterableSystem;
using Timberborn.PathSystem;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="IBuildingQuery"/> implementation backed by Timberborn's
  /// <see cref="IBlockService"/> + <see cref="IPathService"/>.
  /// Classification is delegated to <see cref="BuildingClassifier"/> in
  /// Core; this adapter just gathers the two booleans.
  ///
  /// <para><b>Building discriminator: <c>EnterableSpec</c>.</b> Every
  /// real Timberborn building -- houses, district center, farmhouse,
  /// stockpiles -- has the <see cref="EnterableSpec"/> on its
  /// <see cref="BlockObjectSpec"/>, marking "beavers enter this".
  /// Paths and natural ramps lack it. This is the cleanest single
  /// signal for "structural building, not a path", verified
  /// empirically against vanilla data.</para>
  ///
  /// <para><b>Natural elements skipped via the Core classifier's
  /// two-arm split.</b> Trees, crops, gatherable resources, growables,
  /// yielders carry one of the vanilla component types and are caught
  /// by <see cref="BlockObjectClassification.IsNaturalComponent"/> —
  /// authoritative skip, beats every other signal. Spec-only
  /// naturals (water obstacles / rocks, dry objects / badwater
  /// residue, natural water sources, natural ramps) declare no such
  /// component but also no <c>BuildingSpec</c>, so they're caught by
  /// the heuristic arm via
  /// <see cref="BlockObjectClassification.LacksBuildingSpec"/> — also
  /// skip, but ordered below the explicit Keystone tags so a
  /// third-party mod BO that lacks <c>BuildingSpec</c> but carries a
  /// no-aura / transparent / Nature tag (e.g. Tree of Life)
  /// classifies per the tag, not as a spec-only natural.</para>
  ///
  /// <para><b>Fallback for non-enterable buildings.</b> Some structures
  /// (decorations, walls, future mod content) have <c>BuildingSpec</c>
  /// but no <c>EnterableSpec</c>. We classify them as Building unless
  /// the voxel is also a path -- preserving the principle that path
  /// status doesn't promote a non-enterable thing to Building, but
  /// non-path non-natural BOs default to Building.</para>
  /// </summary>
  public sealed class BuildingQueryAdapter : IBuildingQuery {

    #region Fields

    private readonly IBlockService _blockService;
    private readonly IPathService _pathService;
    private readonly System.Collections.Generic.HashSet<string> _loggedExceptions =
        new(System.StringComparer.Ordinal);

    #endregion

    #region Construction

    public BuildingQueryAdapter(IBlockService blockService, IPathService pathService) {
      _blockService = blockService;
      _pathService = pathService;
    }

    #endregion

    #region IBuildingQuery

    /// <inheritdoc />
    public BuildingKind ClassifyAt(SurfaceCoord voxel) {
      var v = new Vector3Int(voxel.X, voxel.Y, voxel.Z);
      var isPath = _pathService.IsPath(v);

      // Adapter responsibility: gather per-BO signals from the
      // Timberborn-side components, then ask the Core policy
      // (BlockObjectClassifier) what each one contributes. The
      // priority order — and the entire "what beats what" rule set —
      // lives in Core where it can be unit-tested without a Unity host.
      var hasBuilding = false;
      var hasNoAuraBuilding = false;
      // Per-BO isolation: this adapter is called on every classification
      // query (region indexing, settled-halo computation, etc.). A
      // single malformed third-party BO at this voxel (component access
      // throws, BlockObjectSpec divergent shape) would otherwise take
      // out the whole tile's classification and cascade into wrong
      // region membership. Treat per-BO failures as "skip this BO,
      // contributes nothing."
      foreach (var bo in _blockService.GetObjectsAt(v)) {
        if (bo == null) continue;
        try {
          var spec = bo.GetComponent<BlockObjectSpec>();
          var blueprintName = spec?.Blueprint?.Name ?? string.Empty;
          // Tagged-transparent / tagged-no-aura signals OR two paths:
          //   (a) spec attached to the BO (used by Nature buildings,
          //       which need it for biome data, and by external mods
          //       opting their own content in); and
          //   (b) blueprint name appears in Keystone's Core whitelist
          //       (BlueprintNamePolicy.{Transparent,NoAura}BuildingNames).
          // The name-lookup path keeps Keystone's footprint off vanilla
          // blueprints — no per-blueprint modifier injection needed for
          // the bulk of the explicit list entries.
          var signals = new BlockObjectSignals(
              IsNaturalComponent: BlockObjectClassification.IsNaturalComponent(bo),
              IsTaggedTransparent:
                  (spec != null && spec.HasSpec<KeystoneEcologyTransparentSpec>())
                  || BlueprintNamePolicy.IsTransparentByName(blueprintName),
              IsTaggedNoAura:
                  (spec != null && spec.HasSpec<KeystoneEcologyNoAuraSpec>())
                  || BlueprintNamePolicy.IsNoAuraByName(blueprintName),
              HasEnterableSpec: spec != null && spec.HasSpec<EnterableSpec>(),
              LacksBuildingSpec: BlockObjectClassification.LacksBuildingSpec(bo),
              VoxelIsPath: isPath,
              MatchesStructuralPathName: BlueprintNamePolicy.IsStructuralPath(blueprintName));
          switch (BlockObjectClassifier.Classify(signals)) {
            case BlockObjectFootprint.Building:
              hasBuilding = true;
              break;
            case BlockObjectFootprint.NoAura:
              hasNoAuraBuilding = true;
              break;
            // BlockObjectFootprint.Skip: contributes nothing.
          }
        } catch (System.Exception ex) {
          LifecycleGuard.HandleErrorByType(
              "BuildingQueryAdapter.ClassifyAt", "Per-tile errors", ex, _loggedExceptions);
        }
      }

      return BuildingClassifier.Classify(hasBuilding, hasNoAuraBuilding, isPath);
    }

    #endregion

  }

}
