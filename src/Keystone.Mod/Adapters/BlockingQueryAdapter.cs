using Keystone.Core.Buildings;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="IBlockingQuery"/> implementation. Asks
  /// <see cref="IBlockService"/> for every block object occupying the
  /// voxel and returns true iff any of them has a source
  /// <see cref="Timberborn.BlueprintSystem.Blueprint.Name"/> in the
  /// <see cref="BlueprintNamePolicy.BlockingNaturalNames"/> whitelist.
  ///
  /// <para><b>Why blueprint name and not a component sniff.</b> See
  /// the discussion on <see cref="BlueprintNamePolicy.BlockingNaturalNames"/>.
  /// The short version: the underlying obstacle specs
  /// (<c>WaterObstacleSpec</c> etc.) are reusable by other mods for
  /// non-natural content, so a curated name list is the stable
  /// discriminator.</para>
  ///
  /// <para><b>Cost.</b> One <c>GetObjectsAt</c> call (already
  /// dictionary-indexed by voxel inside Timberborn) plus, per BO, a
  /// <c>GetComponent&lt;BlockObjectSpec&gt;</c> and one hash-set
  /// lookup. Called per surface during column resurveys -- a handful
  /// of objects per voxel and a small whitelist; well under the
  /// existing per-voxel building classification cost.</para>
  /// </summary>
  public sealed class BlockingQueryAdapter : IBlockingQuery {

    #region Fields

    private readonly IBlockService _blockService;
    private readonly System.Collections.Generic.HashSet<string> _loggedExceptions =
        new(System.StringComparer.Ordinal);

    #endregion

    #region Construction

    public BlockingQueryAdapter(IBlockService blockService) {
      _blockService = blockService;
    }

    #endregion

    #region IBlockingQuery

    /// <inheritdoc />
    public bool IsBlockedAt(SurfaceCoord voxel) {
      var v = new Vector3Int(voxel.X, voxel.Y, voxel.Z);
      // Per-BO isolation: this adapter is called per-surface during
      // column resurveys. A malformed third-party BO at this voxel
      // (BlockObjectSpec or Blueprint accessor throws) shouldn't
      // change the surface's blocked-ness silently or take out the
      // resurvey. Treat per-BO failures as "doesn't block here."
      foreach (var bo in _blockService.GetObjectsAt(v)) {
        if (bo == null) continue;
        try {
          var spec = bo.GetComponent<BlockObjectSpec>();
          if (spec == null) continue;
          var blueprint = spec.Blueprint;
          if (blueprint == null || string.IsNullOrEmpty(blueprint.Name)) continue;
          if (BlueprintNamePolicy.IsBlockingNatural(blueprint.Name)) {
            return true;
          }
        } catch (System.Exception ex) {
          LifecycleGuard.HandleErrorByType(
              "BlockingQueryAdapter.IsBlockedAt", "Per-tile errors", ex, _loggedExceptions);
        }
      }
      return false;
    }

    #endregion

  }

}
