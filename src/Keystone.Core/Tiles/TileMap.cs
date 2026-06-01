using System.Collections.Generic;

namespace Keystone.Core.Tiles {

  /// <summary>
  /// Sparse, value-typed annotation map keyed by either
  /// <see cref="TileCoord"/> (per-column data) or <see cref="SurfaceCoord"/>
  /// (per-surface data). Used to attach Keystone-side metadata (surveys,
  /// ecology tags, etc.) to terrain without mutating game state.
  /// </summary>
  /// <typeparam name="TKey">Coordinate type — typically <see cref="TileCoord"/> or <see cref="SurfaceCoord"/>.</typeparam>
  /// <typeparam name="TValue">Per-coordinate value type.</typeparam>
  public sealed class TileMap<TKey, TValue>
      where TKey : struct
      where TValue : struct {

    #region Fields

    private readonly Dictionary<TKey, TValue> _data = new();

    #endregion

    #region Properties

    /// <summary>Number of coordinates currently annotated.</summary>
    public int Count => _data.Count;

    /// <summary>Read-only enumeration of all (coord, value) pairs.</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Entries => _data;

    #endregion

    #region Public API

    /// <summary>Set or overwrite the value at <paramref name="coord"/>.</summary>
    public void Set(TKey coord, TValue value) {
      _data[coord] = value;
    }

    /// <summary>Try to read the value at <paramref name="coord"/>.</summary>
    /// <returns><c>true</c> if a value was present.</returns>
    public bool TryGet(TKey coord, out TValue value) {
      return _data.TryGetValue(coord, out value);
    }

    /// <summary>Remove the entry at <paramref name="coord"/>, if any. Returns <c>true</c> if a value was present.</summary>
    public bool Remove(TKey coord) {
      return _data.Remove(coord);
    }

    /// <summary>Remove all entries.</summary>
    public void Clear() {
      _data.Clear();
    }

    #endregion

  }

}
