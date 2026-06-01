using System;
using System.Collections.Generic;
using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Per-region per-tile value storage. Picks its internal
  /// representation at construction based on fill ratio:
  /// <list type="bullet">
  ///   <item><b>Dense</b> (fill &gt; 25%): flat channel-major array
  ///   indexed directly by <c>(x, y, slot)</c>. Zero overhead.</item>
  ///   <item><b>Sparse</b> (fill &le; 25%): indexed tile map. A
  ///   bbox-sized index array maps each position to a compact ordinal
  ///   (or -1 for out-of-region tiles). The values array is sized to
  ///   actual tile count, not bbox area.</item>
  /// </list>
  ///
  /// <para>The public API is identical for both paths. Consumers
  /// never know which representation is active.</para>
  ///
  /// <para><b>Thread safety.</b> During the parallel sweep phase,
  /// each worker writes to tiles within its chunk. Two chunks never
  /// overlap in tile space, so concurrent writes to different chunks
  /// target different array indices — no synchronization needed.
  /// This holds for both representations.</para>
  /// </summary>
  public sealed class RegionTileData {

    #region Constants

    private const float DenseFillThreshold = 0.25f;

    #endregion

    #region Fields

    private readonly float[] _values;
    private readonly int[] _tileIndex;
    private readonly int _tileCount;
    private readonly bool _sparse;

    #endregion

    #region Properties

    /// <summary>Tile-space X of the bbox lower-left corner.</summary>
    public int OriginX { get; }

    /// <summary>Tile-space Y of the bbox lower-left corner.</summary>
    public int OriginY { get; }

    /// <summary>Bbox width in tiles.</summary>
    public int Width { get; }

    /// <summary>Bbox height in tiles.</summary>
    public int Height { get; }

    /// <summary>Number of value slots per tile.</summary>
    public int SlotCount { get; }

    /// <summary>Number of valid tiles in the region (may be less than
    /// <c>Width * Height</c> for sparse regions).</summary>
    public int TileCount => _tileCount;

    /// <summary>True when using the sparse indexed representation.</summary>
    public bool IsSparse => _sparse;

    #endregion

    #region Construction

    /// <summary>Create a dense tile data covering the full bbox.
    /// Every tile in the bbox is addressable.</summary>
    public RegionTileData(int originX, int originY, int width, int height, int slotCount) {
      if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
      if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
      if (slotCount < 0) throw new ArgumentOutOfRangeException(nameof(slotCount));
      OriginX = originX;
      OriginY = originY;
      Width = width;
      Height = height;
      SlotCount = slotCount;
      _tileCount = width * height;
      _sparse = false;
      _tileIndex = Array.Empty<int>();
      _values = new float[_tileCount * slotCount];
    }

    /// <summary>Create tile data for a known set of valid tiles.
    /// Picks dense or sparse representation based on fill ratio
    /// (valid tiles / bbox area).</summary>
    public RegionTileData(
        int originX, int originY, int width, int height,
        int slotCount, IReadOnlyList<TileCoord> validTiles) {
      if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
      if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
      if (slotCount < 0) throw new ArgumentOutOfRangeException(nameof(slotCount));
      if (validTiles == null) throw new ArgumentNullException(nameof(validTiles));
      OriginX = originX;
      OriginY = originY;
      Width = width;
      Height = height;
      SlotCount = slotCount;

      var bboxArea = width * height;
      var fillRatio = validTiles.Count / (float)bboxArea;

      if (fillRatio > DenseFillThreshold || validTiles.Count == bboxArea) {
        _tileCount = bboxArea;
        _sparse = false;
        _tileIndex = Array.Empty<int>();
        _values = new float[bboxArea * slotCount];
      } else {
        _tileCount = validTiles.Count;
        _sparse = true;
        _tileIndex = new int[bboxArea];
        Array.Fill(_tileIndex, -1);
        for (var i = 0; i < validTiles.Count; i++) {
          var t = validTiles[i];
          var bboxIdx = (t.Y - originY) * width + (t.X - originX);
          _tileIndex[bboxIdx] = i;
        }
        _values = new float[_tileCount * slotCount];
      }
    }

    #endregion

    #region Access

    /// <summary>Read the value at tile <c>(x, y)</c> for
    /// <paramref name="slotOrdinal"/>. Returns 0 for out-of-region
    /// tiles in sparse mode.</summary>
    public float Get(int x, int y, int slotOrdinal) {
      var idx = CompactIndex(x, y);
      if (idx < 0) return 0f;
      return _values[slotOrdinal * _tileCount + idx];
    }

    /// <summary>Write <paramref name="value"/> at tile <c>(x, y)</c>
    /// for <paramref name="slotOrdinal"/>. No-op for out-of-region
    /// tiles in sparse mode.</summary>
    public void Set(int x, int y, int slotOrdinal, float value) {
      var idx = CompactIndex(x, y);
      if (idx < 0) return;
      _values[slotOrdinal * _tileCount + idx] = value;
    }

    /// <summary>Check whether tile <c>(x, y)</c> falls within this
    /// data's bounding box.</summary>
    public bool Contains(int x, int y) {
      return x >= OriginX && x < OriginX + Width
          && y >= OriginY && y < OriginY + Height;
    }

    /// <summary>Reset all values to zero.</summary>
    public void Clear() {
      Array.Clear(_values, 0, _values.Length);
    }

    #endregion

    #region Internals

    private int CompactIndex(int x, int y) {
      var bboxIdx = (y - OriginY) * Width + (x - OriginX);
      if (_sparse) {
        return _tileIndex[bboxIdx];
      }
      return bboxIdx;
    }

    #endregion

  }

}
