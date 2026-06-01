using Keystone.Core.Ports;
using Keystone.Core.Regions;

namespace Keystone.Core.Fauna {

  /// <summary>
  /// <see cref="IRegionTopologyQuery"/> view that treats a tile as
  /// in-region only when it AND all four cardinal neighbours are
  /// members of the same region. Effectively a 1-tile inset of the
  /// underlying region — agents that use it for walkability stay
  /// clear of region boundaries (cliff edges, one-tile-wide gaps)
  /// where motion would clip through terrain.
  ///
  /// <para>Shared between the per-agent walkability composition
  /// (<c>KeystoneFaunaAgent</c>) and the spawn-side tile
  /// validation (<c>FaunaSpawnDrainer</c>) so the predicate
  /// used at spawn time is identical to the one the agent runs after
  /// it's alive. Spawning on a tile the agent's filter would reject
  /// strands the agent (its pathfinder won't accept the source) and
  /// triggers the hourly stuck-despawn check on the next Update —
  /// which produces a constant spawn/despawn loop unless the drainer
  /// is filtering by the same rule.</para>
  /// </summary>
  public sealed class InteriorOnlyTopology : IRegionTopologyQuery {

    private readonly IRegionTopologyQuery _inner;

    public InteriorOnlyTopology(IRegionTopologyQuery inner) {
      _inner = inner;
    }

    /// <inheritdoc />
    public bool ContainsTile(RegionId region, int x, int y) {
      return _inner.ContainsTile(region, x, y)
          && _inner.ContainsTile(region, x + 1, y)
          && _inner.ContainsTile(region, x - 1, y)
          && _inner.ContainsTile(region, x, y + 1)
          && _inner.ContainsTile(region, x, y - 1);
    }

  }

}
