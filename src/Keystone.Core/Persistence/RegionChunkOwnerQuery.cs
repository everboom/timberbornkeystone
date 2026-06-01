using Keystone.Core.Ecology.Fields;
using Keystone.Core.Regions;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Production <see cref="IChunkOwnerQuery"/>: resolves a chunk's owning
  /// region by majority surface footprint at a strict Z, over the live
  /// <see cref="RegionService"/>. Pins the chunk size to
  /// <see cref="RegionEcologyField.ChunkSize"/> so callers don't have to
  /// thread it through.
  ///
  /// <para>Pure Core — <see cref="RegionService"/> carries no Timberborn
  /// types — so the whole reconciliation stack stays unit-testable; this
  /// adapter is the thin seam between the store-level reconciler and the
  /// region graph.</para>
  /// </summary>
  public sealed class RegionChunkOwnerQuery : IChunkOwnerQuery {

    private readonly RegionService _regions;

    public RegionChunkOwnerQuery(RegionService regions) {
      _regions = regions;
    }

    /// <inheritdoc />
    public RegionId? OwnerOfChunk(int chunkX, int chunkY, int z) {
      return _regions.FindRegionByChunkFootprint(
          chunkX, chunkY, RegionEcologyField.ChunkSize, z);
    }

    /// <inheritdoc />
    public bool RegionOwnsChunk(RegionId region, int chunkX, int chunkY, int z) {
      return _regions.RegionHasSurfaceInChunkFootprint(
          region, chunkX, chunkY, RegionEcologyField.ChunkSize, z);
    }

  }

}
