using Keystone.Core.Ports;
using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Computes signed Chebyshev distance to the water/land boundary,
  /// capped at ±2, under a <i>path-connected, single-step-vertical</i>
  /// rule. Positive = land tile, distance to nearest water. Negative =
  /// water tile, distance to nearest land (shore). Zero is unused — every
  /// tile is either water or land.
  ///
  /// <para><b>Vertical rule.</b> The <i>first</i> step away from the
  /// queried tile may change elevation by at most
  /// <see cref="MaxHeightDifference"/> (one Z, up or down — a single
  /// walkable step); every step after that is horizontal (the same Z the
  /// first step landed on). The vertical tolerance never compounds with
  /// distance, so a staircase of ±1 steps can't register a faraway,
  /// higher/lower body as "near".</para>
  ///
  /// <para><b>Path-connected.</b> A distance-2 result needs a real
  /// stepping tile at a compatible height; water/land behind a 2+ block
  /// wall is not counted, because nothing bridges to it.</para>
  ///
  /// <para><b>Allocation-free.</b> All probes are exact-Z point queries
  /// (<see cref="ITerrainQuery.IsTerrainVoxel"/> /
  /// <see cref="IWaterQuery.WaterDepthAt"/>) — we never enumerate a
  /// column's surfaces. Surface existence is derived as
  /// <c>IsTerrainVoxel(z-1) &amp;&amp; !IsTerrainVoxel(z)</c>, which is
  /// exactly the game's <c>OnGround</c> / <c>GetAllHeightsInCell</c>
  /// condition (verified against the decompiled TerrainService), so the
  /// surface set is identical to the old enumerate-and-scan approach —
  /// just without the per-call list allocation that was serialising the
  /// warmup's worker threads on the GC.</para>
  ///
  /// <para>Values: -999 = deep water, -2 = water 2 tiles from land,
  /// -1 = water adjacent to land (shore), 1 = land adjacent to water,
  /// 2 = land 2 tiles from water, 999 = dry land far from water.</para>
  /// </summary>
  public static class WaterDistanceCalculator {

    #region Constants

    /// <summary>Sentinel for land tiles beyond the max search depth.</summary>
    public const int OutOfRange = 999;

    /// <summary>Sentinel for water tiles far from any shore.</summary>
    public const int DeepWater = -999;

    /// <summary>Maximum Z difference for the single permitted vertical
    /// step (the first step away from the queried tile).</summary>
    public const int MaxHeightDifference = 1;

    /// <summary>Maximum horizontal (Chebyshev) search distance. A batched
    /// caller only needs terrain/water data within this many tiles of its
    /// region to get correct results — used by the field updater's
    /// dead-region early-out.</summary>
    public const int MaxSearchDistance = 2;

    #endregion

    #region Surface / top-Z primitives

    /// <summary>True if a walkable ground surface sits at exactly
    /// <c>(x, y, z)</c>: terrain immediately below, open above. This is
    /// the game's <c>OnGround</c> condition expressed over the existing
    /// <see cref="ITerrainQuery.IsTerrainVoxel"/> probe, so it allocates
    /// nothing and matches the column's enumerated surfaces exactly.</summary>
    public static bool HasSurfaceAt(ITerrainQuery terrain, int x, int y, int z) {
      return terrain.IsTerrainVoxel(x, y, z - 1) && !terrain.IsTerrainVoxel(x, y, z);
    }

    /// <summary>The highest surface Z in the column, found by scanning
    /// down from the top of terrain — the non-allocating equivalent of
    /// taking the last entry of the column's surface enumeration. Returns
    /// false (and <paramref name="z"/> = 0) for a column with no surface.</summary>
    public static bool TryGetTopSurfaceZ(ITerrainQuery terrain, int x, int y, out int z) {
      for (z = terrain.MaxHeight + 1; z >= 0; z--) {
        if (HasSurfaceAt(terrain, x, y, z)) return true;
      }
      z = 0;
      return false;
    }

    #endregion

    #region Entry point

    /// <summary>Compute signed water distance for the surface at
    /// <c>(x, y, surfaceZ)</c>. Positive = land (distance to water),
    /// negative = water (distance to shore), magnitude capped at 2.</summary>
    public static int Compute(int x, int y, int surfaceZ,
        IWaterQuery water, ITerrainQuery terrain) {
      var column = new TileCoord(x, y);
      if (water.HasWaterAtColumn(column)) {
        return ComputeShoreDistance(x, y, surfaceZ, water, terrain);
      }
      return ComputeWaterDistance(x, y, surfaceZ, water, terrain);
    }

    #endregion

    #region Land → water

    private static int ComputeWaterDistance(int x, int y, int z0,
        IWaterQuery water, ITerrainQuery terrain) {
      // Distance 1: a Moore neighbor has water on a surface within ±1 of
      // the origin's Z (the single permitted vertical step lands on it).
      for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
          if (dx == 0 && dy == 0) continue;
          var nx = x + dx;
          var ny = y + dy;
          for (var L = z0 - MaxHeightDifference; L <= z0 + MaxHeightDifference; L++) {
            if (HasWaterSurface(water, terrain, nx, ny, L)) return 1;
          }
        }
      }

      // Distance 2: step once onto a neighbor N with a surface at level L
      // (|L − z0| ≤ 1), then a *horizontal* step at L must reach water.
      // Path-connected: N's surface must exist, so water behind a taller
      // wall is unreachable.
      for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
          if (dx == 0 && dy == 0) continue;
          var nx = x + dx;
          var ny = y + dy;
          for (var L = z0 - MaxHeightDifference; L <= z0 + MaxHeightDifference; L++) {
            if (!HasSurfaceAt(terrain, nx, ny, L)) continue;
            if (HasWaterAtLevelAdjacent(water, terrain, nx, ny, L)) return 2;
          }
        }
      }

      return OutOfRange;
    }

    #endregion

    #region Water → land (shore)

    private static int ComputeShoreDistance(int x, int y, int z0,
        IWaterQuery water, ITerrainQuery terrain) {
      // Distance -1: a Moore neighbor is land on a surface within ±1 of z0.
      for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
          if (dx == 0 && dy == 0) continue;
          var nx = x + dx;
          var ny = y + dy;
          for (var L = z0 - MaxHeightDifference; L <= z0 + MaxHeightDifference; L++) {
            if (HasLandSurface(water, terrain, nx, ny, L)) return -1;
          }
        }
      }

      // Distance -2: mirror of the land case — step ±1 onto a surface at
      // level L, then a horizontal step at L must reach land.
      for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
          if (dx == 0 && dy == 0) continue;
          var nx = x + dx;
          var ny = y + dy;
          for (var L = z0 - MaxHeightDifference; L <= z0 + MaxHeightDifference; L++) {
            if (!HasSurfaceAt(terrain, nx, ny, L)) continue;
            if (HasLandAtLevelAdjacent(water, terrain, nx, ny, L)) return -2;
          }
        }
      }

      return DeepWater;
    }

    #endregion

    #region Horizontal-step probes

    /// <summary>Any Moore neighbor of <c>(x, y)</c> with water on a
    /// surface at exactly <paramref name="level"/> — the horizontal
    /// continuation step, which carries no vertical tolerance.</summary>
    private static bool HasWaterAtLevelAdjacent(
        IWaterQuery water, ITerrainQuery terrain, int x, int y, int level) {
      for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
          if (dx == 0 && dy == 0) continue;
          if (HasWaterSurface(water, terrain, x + dx, y + dy, level)) return true;
        }
      }
      return false;
    }

    private static bool HasLandAtLevelAdjacent(
        IWaterQuery water, ITerrainQuery terrain, int x, int y, int level) {
      for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
          if (dx == 0 && dy == 0) continue;
          if (HasLandSurface(water, terrain, x + dx, y + dy, level)) return true;
        }
      }
      return false;
    }

    #endregion

    #region Surface classification

    /// <summary>A ground surface at <c>(x, y, level)</c> that has water on
    /// it. Exact-Z; the surface check anchors the water to a real
    /// surface, so trace/under-column water at other levels is ignored.</summary>
    private static bool HasWaterSurface(
        IWaterQuery water, ITerrainQuery terrain, int x, int y, int level) {
      return HasSurfaceAt(terrain, x, y, level)
          && water.WaterDepthAt(new SurfaceCoord(x, y, level)) > 0f;
    }

    /// <summary>A ground surface at <c>(x, y, level)</c> in a column with
    /// no water anywhere in it — i.e. dry land. Mirrors the original
    /// land probe's column-level "no water" gate.</summary>
    private static bool HasLandSurface(
        IWaterQuery water, ITerrainQuery terrain, int x, int y, int level) {
      return !water.HasWaterAtColumn(new TileCoord(x, y))
          && HasSurfaceAt(terrain, x, y, level);
    }

    #endregion

  }

}
