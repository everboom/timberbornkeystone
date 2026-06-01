namespace Keystone.Core.Ecology.Clusters {

  /// <summary>
  /// Opaque identifier for one cluster within a single
  /// <see cref="ChunkClusterIndex"/> rebuild snapshot. IDs are NOT
  /// stable across rebuilds — a cluster keeping the exact same
  /// member chunks may be assigned a different <see cref="Value"/>
  /// after a rebuild. Consumers that need a stable handle should
  /// store the <see cref="ChunkCoord"/> of a fish's current chunk
  /// and re-resolve via
  /// <see cref="ChunkClusterIndex.ClusterFor(Keystone.Core.Regions.RegionId, int, int)"/>
  /// on each query.
  /// </summary>
  public readonly record struct ChunkClusterId(int Value);

}
