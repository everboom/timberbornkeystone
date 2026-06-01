using Keystone.Core.Tiles;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Read-side port over the host's soil moisture field. Engine-agnostic
  /// counterpart of Timberborn's <c>ISoilMoistureService</c>; the Mod layer
  /// supplies an adapter that handles index translation.
  ///
  /// The host exposes a two-axis API: a column-level value and a
  /// per-voxel boolean predicate. Both are needed — stacked surfaces in
  /// the same column share the float but can disagree on the boolean.
  /// </summary>
  public interface IMoistureQuery {

    /// <summary>
    /// Soil moisture at the surface of the column at <paramref name="column"/>.
    /// Shared across all stacked surfaces in the column.
    /// <para><b>Units.</b> Game-defined "tiles to nearest water source"
    /// with linear decay: a tile directly adjacent to water reads ~16,
    /// tapering linearly to 0 over 16 tiles. Natural range is therefore
    /// roughly [0, 16] — <i>not</i> [0, 1]. Multiple stacked sources can
    /// nudge it slightly above 16 in edge cases. Core consumers that
    /// want a [0, 1] fraction must normalise themselves; do not assume
    /// the raw value is bounded above by 1.</para>
    /// </summary>
    float MoistureAt(TileCoord column);

    /// <summary>
    /// Per-voxel "is this surface considered moist" predicate as the game
    /// defines it. Stacked surfaces can return different answers because
    /// the result depends on Z relative to the water table, not just the
    /// raw column value.
    /// </summary>
    bool IsMoistAt(SurfaceCoord surface);

  }

}
