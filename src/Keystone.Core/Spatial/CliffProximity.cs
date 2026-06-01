using Keystone.Core.Ports;
using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Spatial helper for cliff-edge detection. Probes the terrain voxels
  /// directly adjacent to a surface in the four Manhattan directions and
  /// reports whether the surface sits above or below them.
  ///
  /// <para><b>Semantics.</b> For surface <c>(x, y, z)</c>, the helper
  /// checks each Manhattan-neighbour column <c>(nx, ny)</c>:</para>
  /// <list type="bullet">
  ///   <item><see cref="IsAboveNeighbor"/> -- true if at least one
  ///         neighbour has <c>(nx, ny, z-1)</c> <b>not</b> occupied by
  ///         natural terrain: the ground drops away by &#8805;1 on that
  ///         side ("on top of a cliff"). Out-of-bounds columns are
  ///         treated as not-occupied, so map-edge tiles register as
  ///         above their (nonexistent) neighbour.</item>
  ///   <item><see cref="IsBelowNeighbor"/> -- true if at least one
  ///         neighbour has <c>(nx, ny, z)</c> occupied by natural
  ///         terrain: the neighbour rises above by &#8805;1 ("at the
  ///         bottom of a cliff"). Out-of-bounds columns are treated as
  ///         not-occupied, so map-edge tiles never register as below.</item>
  /// </list>
  ///
  /// <para><b>Z convention.</b> <c>surface.Z</c> is the walkable air
  /// tile -- the topmost solid voxel sits at <c>z-1</c>. So a same-level
  /// neighbour has solid at <c>z-1</c> and air at <c>z</c>; a one-step
  /// rise puts solid at <c>z</c>; a one-step drop leaves <c>z-1</c>
  /// empty. The predicates probe exactly those two voxels.</para>
  ///
  /// <para><b>Why voxel probes, not surface comparison.</b> Timberborn
  /// columns can contain empty voxels between solid spans (overhangs,
  /// floating platforms, dug pockets). A neighbour column's topmost
  /// surface can be far above <c>z</c> while the voxels at <c>z-1</c>
  /// and <c>z+1</c> are both empty -- so the neighbour is neither
  /// "above" nor "below" <i>at this surface</i>. Surface-Z comparison
  /// would mis-report that case; per-voxel adjacency probes get it
  /// right.</para>
  ///
  /// <para><b>Scope.</b> Only natural terrain voxels count, matching
  /// the existing <see cref="ITerrainQuery.HasTerrainAbove"/> convention.
  /// Player-placed stackable blocks are not considered cliffs by this
  /// helper; if that becomes needed, extend the port with a
  /// stackable-aware probe rather than reinterpreting this one.</para>
  ///
  /// <para>Bound as a singleton in <c>KeystoneConfigurator</c>; consumers
  /// inject <see cref="CliffProximity"/> directly.</para>
  /// </summary>
  public sealed class CliffProximity {

    #region Fields

    private readonly ITerrainQuery _terrain;

    #endregion

    #region Construction

    public CliffProximity(ITerrainQuery terrain) {
      _terrain = terrain;
    }

    #endregion

    #region Queries

    /// <summary>
    /// True if at least one Manhattan-neighbour column has
    /// <c>(nx, ny, surface.Z - 1)</c> empty of natural terrain --
    /// the tile sits on a step-down of &#8805;1. Map-edge columns count
    /// as empty in this direction (a beaver stepping off the edge falls).
    /// </summary>
    public bool IsAboveNeighbor(SurfaceCoord surface) {
      var z = surface.Z - 1;
      return !_terrain.IsTerrainVoxel(surface.X + 1, surface.Y, z)
          || !_terrain.IsTerrainVoxel(surface.X - 1, surface.Y, z)
          || !_terrain.IsTerrainVoxel(surface.X, surface.Y + 1, z)
          || !_terrain.IsTerrainVoxel(surface.X, surface.Y - 1, z);
    }

    /// <summary>
    /// True if at least one Manhattan-neighbour column has
    /// <c>(nx, ny, surface.Z)</c> occupied by natural terrain -- the
    /// tile sits at the bottom of a step-up of &#8805;1. (<c>surface.Z</c>
    /// is the walkable air tile, so the neighbour's terrain reaching
    /// into <c>z</c> means its walkable surface is at least one higher
    /// than ours.) Map-edge neighbours are treated as empty, so they
    /// never register as "above" this surface.
    /// </summary>
    public bool IsBelowNeighbor(SurfaceCoord surface) {
      var z = surface.Z;
      return _terrain.IsTerrainVoxel(surface.X + 1, surface.Y, z)
          || _terrain.IsTerrainVoxel(surface.X - 1, surface.Y, z)
          || _terrain.IsTerrainVoxel(surface.X, surface.Y + 1, z)
          || _terrain.IsTerrainVoxel(surface.X, surface.Y - 1, z);
    }

    #endregion

  }

}
