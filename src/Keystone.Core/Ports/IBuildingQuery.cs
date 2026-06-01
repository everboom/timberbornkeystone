using Keystone.Core.Buildings;
using Keystone.Core.Tiles;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Read-side port over the host's block-object and path services. Used
  /// by the surveyor to determine whether a surface is "Settled" --
  /// i.e., occupied by or directly adjacent to player-placed
  /// infrastructure.
  ///
  /// <para>The port is per-voxel (not per-surface): the surveyor passes
  /// in a coordinate that may or may not be a surface (e.g., the voxel
  /// directly above a surface, where buildings sit). The
  /// <see cref="SurfaceCoord"/> type is reused as a 3D voxel reference
  /// for this purpose; semantically it just needs (X, Y, Z).</para>
  /// </summary>
  public interface IBuildingQuery {

    /// <summary>
    /// Classify the voxel at <paramref name="voxel"/> as a Building, a
    /// Path, or None. Implementations should follow the dual-case rule
    /// (see <see cref="BuildingClassifier"/>) -- a voxel with both
    /// <c>BuildingSpec</c> and a path registration classifies as
    /// Building.
    /// </summary>
    BuildingKind ClassifyAt(SurfaceCoord voxel);

  }

}
