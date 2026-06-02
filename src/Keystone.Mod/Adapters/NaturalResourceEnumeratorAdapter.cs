using System;
using Keystone.Core.Ports;
using Keystone.Mod.Recipes;
using Timberborn.BlockSystem;
using Timberborn.Growing;
using Timberborn.NaturalResources;
using Timberborn.NaturalResourcesLifecycle;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// Adapter for <see cref="INaturalResourceEnumerator"/> over
  /// Timberborn's <see cref="IBlockService"/> +
  /// <see cref="NaturalResource"/> / <see cref="LivingNaturalResource"/>
  /// / <see cref="KeystoneVariant"/> component probes. Walks every
  /// <see cref="BlockObject"/> at the voxel and emits one
  /// <see cref="NaturalResourceProbe"/> per natural-resource-bearing
  /// entity. Non-natural-resource entities are silently skipped.
  /// </summary>
  public sealed class NaturalResourceEnumeratorAdapter : INaturalResourceEnumerator {

    private readonly IBlockService _blockService;

    public NaturalResourceEnumeratorAdapter(IBlockService blockService) {
      _blockService = blockService;
    }

    /// <inheritdoc />
    public void EnumerateNaturalResourcesAt(
        int x, int y, int z,
        Action<object, NaturalResourceProbe> onProbe) {
      foreach (var bo in _blockService.GetObjectsAt(new Vector3Int(x, y, z))) {
        if (bo == null) continue;
        if (!bo.HasComponent<NaturalResource>()) continue;
        var variant = bo.GetComponent<KeystoneVariant>();
        var isKeystoneOwned = variant != null && !string.IsNullOrEmpty(variant.Class);
        var living = bo.GetComponent<LivingNaturalResource>();
        var isDead = living != null && living.IsDead;
        // Maturity: a tree is a seedling only while its Growable is
        // still maturing. No Growable -> fixed adult (or a non-growing
        // resource) -> treated as grown. Drives the mature-trees
        // aggregate the chunk biome adapter reads for Forest's
        // mature-canopy gate.
        var growable = bo.GetComponent<Growable>();
        var isGrown = growable == null || growable.IsGrown;
        var blueprintName = bo.GetComponent<BlockObjectSpec>()?.Blueprint?.Name ?? string.Empty;
        onProbe(bo, new NaturalResourceProbe(blueprintName, isKeystoneOwned, isDead, isGrown));
      }
    }

  }

}
