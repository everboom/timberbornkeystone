using Keystone.Core.Ports;
using Timberborn.Forestry;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="ICuttingMarkQuery"/> implementation backed by
  /// Timberborn's <see cref="TreeCuttingArea"/>. Per-tile lookups
  /// dispatch straight to the service's O(1) hashset contains.
  ///
  /// <para><b>No reactive index.</b> Unlike
  /// <see cref="PlantingMarkAdapter"/>, this adapter doesn't maintain
  /// its own spatial bucket — the port surface is per-tile only and
  /// <see cref="TreeCuttingArea.IsInCuttingArea"/> is already a hashset
  /// lookup. Add a rect query and the bucket if a future consumer needs
  /// to walk many tiles at once.</para>
  /// </summary>
  public sealed class CuttingMarkAdapter : ICuttingMarkQuery {

    private readonly TreeCuttingArea _cuttingArea;

    public CuttingMarkAdapter(TreeCuttingArea cuttingArea) {
      _cuttingArea = cuttingArea;
    }

    /// <inheritdoc />
    public bool IsMarkedForCutting(int x, int y, int z) {
      return _cuttingArea.IsInCuttingArea(new Vector3Int(x, y, z));
    }

  }

}
