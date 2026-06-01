using Keystone.Core.Ports;
using Timberborn.BlockSystem;
using Timberborn.NaturalResources;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// Adapter for <see cref="INaturalResourceAtTileQuery"/> over
  /// Timberborn's <see cref="IBlockService"/>. Enumerates block
  /// objects at the queried voxel and returns true on the first
  /// entity carrying a <see cref="NaturalResource"/> component.
  /// </summary>
  public sealed class NaturalResourceAtTileAdapter : INaturalResourceAtTileQuery {

    private readonly IBlockService _blockService;

    public NaturalResourceAtTileAdapter(IBlockService blockService) {
      _blockService = blockService;
    }

    /// <inheritdoc />
    public bool HasNaturalResourceAt(int x, int y, int z) {
      foreach (var bo in _blockService.GetObjectsAt(new Vector3Int(x, y, z))) {
        if (bo == null) continue;
        if (bo.HasComponent<NaturalResource>()) return true;
      }
      return false;
    }

  }

}
