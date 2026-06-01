using System;
using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;

namespace Keystone.Core.Fauna {

  /// <summary>
  /// Region-bounded A* pathfinder for fauna agents.
  ///
  /// <para><b>Two-stage pipeline:</b>
  /// <list type="number">
  /// <item>4-connected A* over the region's tile grid (via
  ///       <see cref="IRegionTopologyQuery"/>), Manhattan-distance
  ///       heuristic. Produces an orthogonal polyline of tile
  ///       waypoints.</item>
  /// <item>Line-of-sight pruning: for each waypoint, find the
  ///       farthest later waypoint reachable via a straight line
  ///       still entirely inside the region, drop the intermediate
  ///       waypoints. Collapses long straight runs and lets the
  ///       agent take diagonal shortcuts that 4-connected A* can't
  ///       express directly.</item>
  /// </list></para>
  ///
  /// <para><b>Why this is cheap.</b> A Keystone region is by
  /// definition connected, so most paths inside one collapse to
  /// just <c>[start, goal]</c> after smoothing — the region's own
  /// boundary is the only wall. Pruning retains intermediate
  /// waypoints only when there's an obstacle <i>inside</i> the
  /// region (e.g., a building the player placed mid-region).</para>
  ///
  /// <para><b>No data copying.</b> The pathfinder reads region
  /// membership through <see cref="IRegionTopologyQuery"/> live
  /// during the search. Production wires this to
  /// <see cref="RegionService"/>'s already-indexed mapping; tests
  /// use a hand-rolled fake. Either way, no snapshotting.</para>
  ///
  /// <para><b>Limitations.</b> 4-connected A* with linear-scan open
  /// set — not a tuned binary heap. Fine for region-sized graphs
  /// (typical regions are dozens to low thousands of tiles); the
  /// open set rarely exceeds a few dozen entries. Bresenham line-
  /// of-sight can cut corners in tight diagonal gaps; in practice
  /// regions are wide enough that this isn't visible. Revisit both
  /// if profiling shows hot.</para>
  /// </summary>
  public static class FaunaPathfinder {

    #region Public API

    /// <summary>
    /// Find a path from <paramref name="start"/> to <paramref name="goal"/>
    /// constrained to tiles inside <paramref name="region"/>. Returns a
    /// list of waypoints (including <paramref name="start"/> and
    /// <paramref name="goal"/>) after line-of-sight smoothing, or
    /// <c>null</c> if no path exists (either endpoint outside the
    /// region, or fully disconnected within).
    /// </summary>
    public static IReadOnlyList<TileCoord>? FindPath(
        IRegionTopologyQuery topology,
        RegionId region,
        TileCoord start,
        TileCoord goal) {
      if (!topology.ContainsTile(region, start.X, start.Y)) return null;
      if (!topology.ContainsTile(region, goal.X, goal.Y)) return null;
      if (start.Equals(goal)) return new[] { start };

      var raw = FindRawPath(topology, region, start, goal);
      if (raw == null) return null;
      return SmoothPath(topology, region, raw);
    }

    #endregion

    #region Random walk

    /// <summary>
    /// Generate a path by walking outward from <paramref name="start"/>
    /// in a randomly chosen direction for a random number of steps.
    /// Uses a greedy best-first expansion biased toward a random target
    /// point, so the path trends in one direction rather than
    /// meandering. Returns the smoothed path, or <c>null</c> if the
    /// start tile has no walkable neighbors.
    /// </summary>
    /// <param name="steps">Target number of tiles to walk. The actual
    /// path may be shorter if the walkable area is smaller.</param>
    public static IReadOnlyList<TileCoord>? RandomWalk(
        IRegionTopologyQuery topology,
        RegionId region,
        TileCoord start,
        int steps,
        Random rng) {
      if (!topology.ContainsTile(region, start.X, start.Y)) return null;
      if (steps <= 0) return null;

      // Pick a random direction to bias the walk toward.
      var angle = rng.NextDouble() * 2.0 * Math.PI;
      var biasX = (float)Math.Cos(angle);
      var biasY = (float)Math.Sin(angle);

      var path = new List<TileCoord>(steps + 1) { start };
      var visited = new HashSet<TileCoord> { start };
      var current = start;

      for (var s = 0; s < steps; s++) {
        TileCoord? best = null;
        var bestScore = float.MinValue;

        // Evaluate 4-connected neighbors.
        TryNeighbor(topology, region, visited, current, 1, 0, biasX, biasY, rng, ref best, ref bestScore);
        TryNeighbor(topology, region, visited, current, -1, 0, biasX, biasY, rng, ref best, ref bestScore);
        TryNeighbor(topology, region, visited, current, 0, 1, biasX, biasY, rng, ref best, ref bestScore);
        TryNeighbor(topology, region, visited, current, 0, -1, biasX, biasY, rng, ref best, ref bestScore);

        if (best == null) break;
        current = best.Value;
        path.Add(current);
        visited.Add(current);
      }

      if (path.Count < 2) return null;
      return SmoothPath(topology, region, path);
    }

    private static void TryNeighbor(
        IRegionTopologyQuery topology, RegionId region,
        HashSet<TileCoord> visited, TileCoord current,
        int dx, int dy, float biasX, float biasY, Random rng,
        ref TileCoord? best, ref float bestScore) {
      var nx = current.X + dx;
      var ny = current.Y + dy;
      var neighbor = new TileCoord(nx, ny);
      if (visited.Contains(neighbor)) return;
      if (!topology.ContainsTile(region, nx, ny)) return;
      // Score: dot product with bias direction + small random jitter
      // so ties don't always resolve the same way.
      var score = dx * biasX + dy * biasY + (float)rng.NextDouble() * 0.3f;
      if (score > bestScore) {
        bestScore = score;
        best = neighbor;
      }
    }

    #endregion

    #region A* (raw)

    /// <summary>Exposed for tests — raw A* output before smoothing.
    /// Callers in production should use <see cref="FindPath"/>.</summary>
    public static IReadOnlyList<TileCoord>? FindRawPath(
        IRegionTopologyQuery topology,
        RegionId region,
        TileCoord start,
        TileCoord goal) {
      // Open set as a flat list with linear-scan pop — region-sized
      // graphs keep this well under the threshold where a heap pays
      // for itself.
      var open = new List<TileCoord> { start };
      var cameFrom = new Dictionary<TileCoord, TileCoord>();
      var gScore = new Dictionary<TileCoord, float> { [start] = 0f };
      var fScore = new Dictionary<TileCoord, float> {
          [start] = ManhattanDistance(start, goal),
      };
      // Track membership for O(1) "is this in open" without a second collection scan.
      var openSet = new HashSet<TileCoord> { start };

      while (open.Count > 0) {
        var current = PopLowestF(open, fScore);
        openSet.Remove(current);
        if (current.Equals(goal)) {
          return Reconstruct(cameFrom, current);
        }
        // 4-connected neighbors. Unrolled to avoid an allocation per node.
        TryRelax(topology, region, current, new TileCoord(current.X + 1, current.Y), goal, gScore, fScore, cameFrom, open, openSet);
        TryRelax(topology, region, current, new TileCoord(current.X - 1, current.Y), goal, gScore, fScore, cameFrom, open, openSet);
        TryRelax(topology, region, current, new TileCoord(current.X, current.Y + 1), goal, gScore, fScore, cameFrom, open, openSet);
        TryRelax(topology, region, current, new TileCoord(current.X, current.Y - 1), goal, gScore, fScore, cameFrom, open, openSet);
      }

      return null;
    }

    private static void TryRelax(
        IRegionTopologyQuery topology, RegionId region,
        TileCoord current, TileCoord neighbor, TileCoord goal,
        Dictionary<TileCoord, float> gScore,
        Dictionary<TileCoord, float> fScore,
        Dictionary<TileCoord, TileCoord> cameFrom,
        List<TileCoord> open, HashSet<TileCoord> openSet) {
      if (!topology.ContainsTile(region, neighbor.X, neighbor.Y)) return;
      var tentativeG = gScore[current] + 1f;
      if (gScore.TryGetValue(neighbor, out var existingG) && tentativeG >= existingG) return;
      cameFrom[neighbor] = current;
      gScore[neighbor] = tentativeG;
      fScore[neighbor] = tentativeG + ManhattanDistance(neighbor, goal);
      if (openSet.Add(neighbor)) {
        open.Add(neighbor);
      }
    }

    private static TileCoord PopLowestF(List<TileCoord> open, Dictionary<TileCoord, float> fScore) {
      var bestIdx = 0;
      var bestF = fScore[open[0]];
      for (var i = 1; i < open.Count; i++) {
        var f = fScore[open[i]];
        if (f < bestF) {
          bestF = f;
          bestIdx = i;
        }
      }
      var result = open[bestIdx];
      // Swap-and-pop to avoid shifting the tail every iteration.
      open[bestIdx] = open[open.Count - 1];
      open.RemoveAt(open.Count - 1);
      return result;
    }

    private static List<TileCoord> Reconstruct(
        Dictionary<TileCoord, TileCoord> cameFrom, TileCoord current) {
      var path = new List<TileCoord> { current };
      while (cameFrom.TryGetValue(current, out var prev)) {
        current = prev;
        path.Add(current);
      }
      path.Reverse();
      return path;
    }

    private static float ManhattanDistance(TileCoord a, TileCoord b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    #endregion

    #region Line-of-sight smoothing

    /// <summary>Exposed for tests — smooths a raw waypoint sequence
    /// by dropping intermediates that a straight line could bypass.
    /// Production goes through <see cref="FindPath"/>.</summary>
    public static IReadOnlyList<TileCoord> SmoothPath(
        IRegionTopologyQuery topology,
        RegionId region,
        IReadOnlyList<TileCoord> raw) {
      if (raw.Count <= 2) return raw;
      var result = new List<TileCoord> { raw[0] };
      var anchor = 0;
      while (anchor < raw.Count - 1) {
        // Find the farthest j reachable from anchor by a straight line.
        // Start from the end and walk back; the first success is the
        // farthest, so this is O(remaining) amortised over the whole
        // path in the common case where most waypoints collapse.
        var farthest = anchor + 1;
        for (var j = raw.Count - 1; j > anchor + 1; j--) {
          if (LineOfSight(topology, region, raw[anchor], raw[j])) {
            farthest = j;
            break;
          }
        }
        result.Add(raw[farthest]);
        anchor = farthest;
      }
      return result;
    }

    /// <summary>Bresenham-style line walk from <paramref name="a"/>
    /// to <paramref name="b"/>. Returns true iff every tile under
    /// the line is in the region.
    ///
    /// <para><b>Corner-cutting note:</b> standard Bresenham can pass
    /// diagonally through a single point that touches two blocked
    /// tiles on either side. We accept that — Keystone regions are
    /// typically wide enough that single-tile gaps are rare, and the
    /// visual cost (an agent briefly clipping a corner) is small.
    /// If it becomes a problem, switch to a supercover line
    /// (each grid cell the line geometrically crosses, not just the
    /// Bresenham sample).</para>
    /// </summary>
    private static bool LineOfSight(
        IRegionTopologyQuery topology, RegionId region, TileCoord a, TileCoord b) {
      var x = a.X;
      var y = a.Y;
      var dx = Math.Abs(b.X - a.X);
      var dy = Math.Abs(b.Y - a.Y);
      var sx = a.X < b.X ? 1 : -1;
      var sy = a.Y < b.Y ? 1 : -1;
      var err = dx - dy;
      while (true) {
        if (!topology.ContainsTile(region, x, y)) return false;
        if (x == b.X && y == b.Y) return true;
        var e2 = 2 * err;
        if (e2 > -dy) { err -= dy; x += sx; }
        if (e2 < dx) { err += dx; y += sy; }
      }
    }

    #endregion

  }

}
