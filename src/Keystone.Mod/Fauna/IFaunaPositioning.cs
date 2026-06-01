using Keystone.Core.Regions;
using Keystone.Core.Tiles;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Read-only position handle that both land
  /// (<see cref="KeystoneFaunaAgent"/>) and aquatic
  /// (<see cref="KeystoneAquaticAgent"/>) fauna agents expose so the
  /// dawn-handler / registry can ask "which cluster does this fauna
  /// currently live in?" without branching on agent type.
  ///
  /// <para>Agents update <see cref="CurrentTile"/> as they move
  /// through waypoints. The region is set at <c>Configure</c> time
  /// and treated as constant for the fauna's lifetime (fauna don't
  /// traverse regions; a fauna that ends up off-region gets culled
  /// rather than re-homed).</para>
  /// </summary>
  public interface IFaunaPositioning {

    /// <summary>The region the fauna was configured into, or null if
    /// the agent hasn't been configured yet.</summary>
    Region? Region { get; }

    /// <summary>The fauna's current tile. Updates as the agent walks
    /// (land) or swims (aquatic) through path waypoints.</summary>
    TileCoord CurrentTile { get; }

  }

}
