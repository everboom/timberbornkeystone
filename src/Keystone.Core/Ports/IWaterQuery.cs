using Keystone.Core.Tiles;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Read-side port over the host's surface-water field. Engine-agnostic
  /// counterpart of Timberborn's <c>IThreadSafeWaterMap</c>.
  ///
  /// Per-voxel: each surface can sit dry, under shallow water, or under a
  /// flowing channel. Stacked surfaces in a column can have wildly
  /// different water states (e.g., a buried platform with no water above
  /// it vs. the river running on top).
  /// </summary>
  public interface IWaterQuery {

    /// <summary>
    /// Depth of water sitting above <paramref name="surface"/>. Zero for
    /// dry surfaces. Game-defined units (typically voxel heights).
    ///
    /// <para><b>Caveat:</b> this is a per-voxel lookup — the host resolves
    /// the water column whose <c>[Floor, Ceiling)</c> band contains the
    /// surface's exact Z and returns 0 when none does. Callers that want
    /// "how high does the water stand on this tile" (e.g. to float a marker
    /// on the surface) should use <see cref="WaterSurfaceHeightAt"/>
    /// instead: a surface Z that sits even one cell off the column floor
    /// (common over deep water) reads 0 here while the column is plainly
    /// flooded.</para>
    /// </summary>
    float WaterDepthAt(SurfaceCoord surface);

    /// <summary>
    /// Absolute height (grid-Z / world-Y units, 1:1) of the water surface
    /// resting on <paramref name="surface"/>, i.e. the column's
    /// <c>Floor + WaterDepth</c> — the same "water height" the vanilla
    /// "Water columns" debug panel reports. Returns 0 when no water column
    /// rests on this surface (dry).
    ///
    /// <para>Resolved by enumerating the column stack at the tile and
    /// matching the water body that sits on this surface, rather than by a
    /// per-voxel <see cref="WaterDepthAt"/> band lookup — so it stays
    /// correct over deep water, where the surveyed surface Z can fall just
    /// below the water column's floor and a depth lookup returns 0.</para>
    /// </summary>
    float WaterSurfaceHeightAt(SurfaceCoord surface);

    /// <summary>
    /// Horizontal flow vector at <paramref name="surface"/>.
    /// <see cref="FlowVector.Zero"/> for stagnant or dry surfaces.
    /// </summary>
    FlowVector FlowAt(SurfaceCoord surface);

    /// <summary>
    /// True if any voxel in the column at <paramref name="column"/>
    /// contains water at any elevation. Column-level predicate, so a
    /// pond or river anywhere in the stack returns true regardless of
    /// where the asking surface sits within the column.
    ///
    /// <para>Used by spatial-proximity helpers in
    /// <c>Keystone.Core.Spatial</c> (e.g.
    /// <c>WaterProximity.BordersWater</c>) to gate placement on
    /// water adjacency without each consumer reimplementing the
    /// neighbour walk.</para>
    /// </summary>
    bool HasWaterAtColumn(TileCoord column);

    /// <summary>
    /// Contamination level of the water column sitting at
    /// <paramref name="surface"/>. Distinct from
    /// <see cref="IContaminationQuery"/>'s soil contamination —
    /// that one reports how saturated the soil is with the badwater
    /// plume; this reports whether the *water itself* at this voxel
    /// is contaminated (badwater vs clean). Game-defined units,
    /// typically a 0..1 saturation. Returns 0 for surfaces with no
    /// water above them.
    /// </summary>
    float WaterContaminationAt(SurfaceCoord surface);

  }

}
