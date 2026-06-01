using Keystone.Core.Tiles;
using UnityEngine;

namespace Keystone.Mod.Decoration {

  /// <summary>
  /// A single live non-block-object decoration (Class A passive or
  /// Class B eco-responsive). Owns its <see cref="Root"/>
  /// GameObject, knows the tile it sits on, and may carry an optional
  /// reactivity controller. Decorations without a controller are
  /// inert -- pure passive visuals that don't read ecology services.
  ///
  /// <para>Created and tracked by
  /// <see cref="KeystoneDecorationRegistry"/>. Not an entity, not
  /// persisted, not registered with the entity system. Determinism is
  /// the registry's responsibility -- this type is just data + a ref.</para>
  /// </summary>
  public sealed class KeystoneDecoration {

    /// <summary>The cloned visual GameObject. Owned by this decoration;
    /// destroyed when the decoration is despawned.</summary>
    public GameObject Root { get; }

    /// <summary>Tile (column + height) this decoration sits on.</summary>
    public Vector3Int Tile { get; }

    /// <summary>Surface coord matching <see cref="Tile"/>, cached for
    /// per-voxel ecology queries (e.g. <c>IMoistureQuery.IsMoistAt</c>).</summary>
    public SurfaceCoord Surface { get; }

    /// <summary>Optional reactivity controller. Null = inert decoration;
    /// the registry skips Tick for these.</summary>
    public IDecorationController? Controller { get; }

    public KeystoneDecoration(
        GameObject root,
        Vector3Int tile,
        IDecorationController? controller) {
      Root = root;
      Tile = tile;
      Surface = new SurfaceCoord(tile.x, tile.y, tile.z);
      Controller = controller;
    }

  }

}
