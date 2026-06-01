using System.Collections.Generic;
using Keystone.Core.Ecology.Clusters;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// FIFO worklist of clusters that <see cref="FaunaCycleTicker"/> has
  /// observed to have an outstanding spawn deficit. Drained by
  /// <see cref="FaunaSpawnDrainer"/> at frame cadence with off-frustum
  /// filtering, so per-frame Instantiate cost is bounded and the
  /// visual pop is hidden from the player.
  ///
  /// <para><b>Dedup by cluster id.</b> Enqueueing a cluster already
  /// present is a no-op — the queue carries each cluster at most once.
  /// Avoids redundant drain visits when the sweep re-enqueues a cluster
  /// that the drainer hasn't gotten to yet (camera blocking, large
  /// queue, etc.).</para>
  ///
  /// <para><b>Stale ids are tolerated.</b> <see cref="ChunkClusterId"/>
  /// is NOT stable across <see cref="ChunkClusterIndex.Rebuild"/> calls,
  /// so a queue entry may name a cluster that no longer exists by the
  /// time the drainer visits. The drainer is responsible for filtering;
  /// the queue itself is unaware of cluster lifecycle.</para>
  /// </summary>
  public sealed class FaunaSpawnQueue {

    private readonly Queue<ChunkClusterId> _order = new();
    private readonly HashSet<ChunkClusterId> _membership = new();

    /// <summary>Number of distinct clusters currently queued.</summary>
    public int Count => _order.Count;

    /// <summary>Add a cluster to the back of the queue. No-op if the
    /// cluster is already queued.</summary>
    public void Enqueue(ChunkClusterId clusterId) {
      if (_membership.Add(clusterId)) {
        _order.Enqueue(clusterId);
      }
    }

    /// <summary>Pop the front cluster, if any.</summary>
    public bool TryDequeue(out ChunkClusterId clusterId) {
      if (_order.Count == 0) {
        clusterId = default;
        return false;
      }
      clusterId = _order.Dequeue();
      _membership.Remove(clusterId);
      return true;
    }

    /// <summary>Drop all entries. Called when the cluster index rebuilds
    /// (cluster ids become invalid) so the queue isn't left holding a
    /// pile of stale references.</summary>
    public void Clear() {
      _order.Clear();
      _membership.Clear();
    }

  }

}
