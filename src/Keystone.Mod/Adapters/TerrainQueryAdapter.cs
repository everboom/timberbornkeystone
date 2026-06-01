using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Timberborn.TerrainSystem;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="ITerrainQuery"/> implementation backed by Timberborn's
  /// <see cref="ITerrainService"/>. Translates Keystone's
  /// <see cref="TileCoord"/> values into the <see cref="Vector2Int"/> /
  /// <see cref="Vector3Int"/> calls the game expects, and materialises the
  /// per-column surface enumeration into a sorted list.
  /// </summary>
  public sealed class TerrainQueryAdapter : ITerrainQuery {

    #region Fields

    private readonly ITerrainService _terrain;
    private static readonly IReadOnlyList<int> EmptyHeights = new int[0];

    #endregion

    #region Construction

    public TerrainQueryAdapter(ITerrainService terrain) {
      _terrain = terrain;
    }

    #endregion

    #region ITerrainQuery

    /// <inheritdoc />
    public int Width => _terrain.Size.x;

    /// <inheritdoc />
    public int Height => _terrain.Size.y;

    /// <inheritdoc />
    public int MaxHeight => _terrain.MaxTerrainHeight;

    /// <inheritdoc />
    public bool Contains(TileCoord column) {
      return _terrain.Contains(new Vector2Int(column.X, column.Y));
    }

    /// <inheritdoc />
    public IReadOnlyList<int> SurfaceHeightsAt(TileCoord column) {
      var raw = _terrain.GetAllHeightsInCell(new Vector2Int(column.X, column.Y));
      if (raw == null) {
        return EmptyHeights;
      }
      var heights = raw.Select(v => v.z).ToList();
      heights.Sort();
      return heights;
    }

    /// <inheritdoc />
    public bool HasTerrainAbove(SurfaceCoord surface) {
      return _terrain.TryGetDistanceToTerrainAbove(
          new Vector3Int(surface.X, surface.Y, surface.Z),
          out _);
    }

    /// <inheritdoc />
    public bool IsTerrainVoxel(int x, int y, int z) {
      var v = new Vector3Int(x, y, z);
      if (!_terrain.Contains(v)) {
        return false;
      }
      return _terrain.Underground(v);
    }

    #endregion

  }

}
