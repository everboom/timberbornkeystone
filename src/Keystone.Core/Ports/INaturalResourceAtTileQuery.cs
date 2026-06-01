namespace Keystone.Core.Ports {

  /// <summary>
  /// "Does this voxel contain any natural-resource entity right now?"
  /// Narrow read-side port over Timberborn's <c>IBlockService</c> +
  /// <c>NaturalResource</c> component check. The Mod-side adapter
  /// enumerates block objects at the voxel and returns true on the
  /// first <c>NaturalResource</c>-bearing hit.
  ///
  /// <para><b>What this is for.</b> The chunk-biome adapter needs to
  /// dedup player planting marks against tiles that already contain a
  /// realised entity. A coarse "any natural resource" predicate is
  /// correct for that use case because non-plantable natural resources
  /// (rocks, etc.) can't carry a mark in the first place, so the only
  /// risk is a player planting over a vanilla GroundCover — accepted
  /// as negligibly rare and not worth a per-kind classifier here.</para>
  /// </summary>
  public interface INaturalResourceAtTileQuery {

    /// <summary>True iff the voxel at
    /// (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>)
    /// hosts any block object carrying a <c>NaturalResource</c>
    /// component. Out-of-bounds coordinates and empty voxels both
    /// return false.</summary>
    bool HasNaturalResourceAt(int x, int y, int z);

  }

}
