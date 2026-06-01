using System.Collections.Generic;
using Keystone.Core.Buildings;
using Keystone.Core.Ecology;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;

namespace Keystone.Core.Survey {

  /// <summary>
  /// Walks every column of the playable map, enumerates each column's
  /// stacked buildable surfaces, and records the surface's structural
  /// state (cave roofing, settled-infrastructure halo). Single-pass:
  /// surfaces are pure data, no derived classification.
  ///
  /// <para><b>Why no volatile ecological fields here.</b> Water depth,
  /// flow, moisture, and contamination change continuously during
  /// play without firing any event the surveyor listens to (the
  /// dirty-column hooks track only terrain-height and building
  /// placement). Caching those values silently goes stale. Consumers
  /// that need them call <c>IWaterQuery</c> / <c>IMoistureQuery</c> /
  /// <c>IContaminationQuery</c> directly. The structural fields kept
  /// here -- <c>IsCave</c>, <c>IsSettled</c> -- are expensive to
  /// compute and only change on the events the surveyor does
  /// track.</para>
  ///
  /// Pure simulation code: no Timberborn or Unity references. The Mod
  /// layer triggers <see cref="Survey"/> at the right point in the game
  /// lifecycle and supplies adapters for the read-side ports.
  /// </summary>
  public sealed class TerrainSurveyor {

    #region Fields

    private readonly ITerrainQuery _terrain;
    private readonly IBuildingQuery _building;
    private readonly IBlockingQuery _blocking;

    private readonly TileMap<SurfaceCoord, SurfaceSurvey> _surfaces = new();

    /// <summary>Per-column surface heights cached during the pass so consumers (debug panel, region builder) can list a column's surfaces without re-calling the port.</summary>
    private readonly Dictionary<TileCoord, IReadOnlyList<int>> _columnHeights = new();

    private static readonly IReadOnlyList<int> EmptyHeights = new int[0];

    /// <summary>
    /// Cumulative count of <see cref="IBuildingQuery.ClassifyAt"/> calls made
    /// by <see cref="ComputeIsSettled"/> since construction. Each settled
    /// computation issues up to 9 (self + 8-neighbour halo, early-exiting on
    /// the first Building hit). Callers sample the delta across a resurvey
    /// batch to see how much of the batch's cost is the building port — the
    /// suspected dominant cost when many columns are dirtied at once. Never
    /// resets; consumers diff against a prior snapshot.
    /// </summary>
    public long SettledClassifyCalls => _settledClassifyCalls;

    private long _settledClassifyCalls;

    #endregion

    #region Properties

    /// <summary>Read-only access to the per-surface survey populated by <see cref="Survey"/>.</summary>
    public TileMap<SurfaceCoord, SurfaceSurvey> Surfaces => _surfaces;

    #endregion

    #region Column lookups

    /// <summary>
    /// Surface heights (sorted ascending) in the column at
    /// <paramref name="column"/>, as captured by the most recent
    /// <see cref="Survey"/> pass. Empty list if the column wasn't surveyed
    /// or had no surfaces.
    /// </summary>
    public IReadOnlyList<int> ColumnSurfaceHeights(TileCoord column) {
      return _columnHeights.TryGetValue(column, out var heights) ? heights : EmptyHeights;
    }

    #endregion

    #region Construction

    /// <summary>Inject the read-side ports. Terrain drives the column
    /// walk and the cave probe; building drives the settled halo;
    /// blocking flags surfaces occupied by natural impassables that
    /// should be excluded from region membership. Volatile-input
    /// ports (water / moisture / contamination) are intentionally NOT
    /// injected here -- the surveyor never reads them. Consumers that
    /// need those values query the ports directly.</summary>
    public TerrainSurveyor(
        ITerrainQuery terrain,
        IBuildingQuery building,
        IBlockingQuery blocking) {
      _terrain = terrain;
      _building = building;
      _blocking = blocking;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Re-scan every column on the map and replace the contents of
    /// <see cref="Surfaces"/>. Returns a summary callers can log without
    /// re-iterating the map.
    /// </summary>
    public SurveyResult Survey() {
      _surfaces.Clear();
      _columnHeights.Clear();

      var width = _terrain.Width;
      var height = _terrain.Height;
      for (var x = 0; x < width; x++) {
        for (var y = 0; y < height; y++) {
          var column = new TileCoord(x, y);
          if (!_terrain.Contains(column)) {
            continue;
          }
          ResurveyColumnInternal(column);
        }
      }

      return new SurveyResult(_surfaces.Count, _columnHeights.Count);
    }

    /// <summary>
    /// Re-scan a single column. Updates <see cref="Surfaces"/> and the
    /// internal column-heights cache for that column, then returns a
    /// <see cref="ColumnDiff"/> describing which surfaces left or
    /// joined the region graph -- the changes that affect region
    /// membership.
    ///
    /// <para>A surface is <i>in</i> the region graph iff its survey
    /// reports <c>IsBlocked == false</c>. Region identity within the
    /// graph is <c>(Z, IsCave, IsSettled)</c>. Diff semantics:</para>
    /// <list type="bullet">
    ///   <item>Disappeared (no longer surveyed) → detach.</item>
    ///   <item>Was in-graph, now blocked → detach.</item>
    ///   <item>Was blocked, now in-graph → attach.</item>
    ///   <item>In-graph both, but (IsCave, IsSettled) flipped → detach + attach.</item>
    ///   <item>Newly appeared and in-graph → attach.</item>
    ///   <item>Newly appeared and blocked → no diff (never in any region).</item>
    /// </list>
    ///
    /// <para>Surfaces that survived with all three structural axes
    /// unchanged get their pollable fields (moisture, contamination,
    /// water, flow) refreshed in place; they don't appear in the diff
    /// because they didn't change region.</para>
    /// </summary>
    public ColumnDiff ResurveyColumn(TileCoord column) {
      // Snapshot pre-resurvey structural state so we can produce a diff.
      // Region identity depends on (Z, IsCave, IsSettled, IsBlocked):
      // the first three define which region a surface joins, the last
      // controls whether it joins any region at all.
      var oldHeights = ColumnSurfaceHeights(column);
      var oldStateByZ = new Dictionary<int, (bool isCave, bool isSettled, bool isBlocked)>(oldHeights.Count);
      for (var i = 0; i < oldHeights.Count; i++) {
        var z = oldHeights[i];
        if (_surfaces.TryGet(new SurfaceCoord(column.X, column.Y, z), out var s)) {
          oldStateByZ[z] = (s.IsCave, s.IsSettled, s.IsBlocked);
        }
      }

      var newStateByZ = ResurveyColumnInternal(column);

      // Build the diff.
      List<SurfaceCoord>? detached = null;
      List<SurfaceCoord>? attached = null;
      foreach (var kv in oldStateByZ) {
        var z = kv.Key;
        var oldState = kv.Value;
        var coord = new SurfaceCoord(column.X, column.Y, z);
        if (!newStateByZ.TryGetValue(z, out var newState)) {
          // Surface disappeared. Only emit a detach if it was actually
          // in the region graph -- a blocked surface that vanishes was
          // never in any region.
          if (!oldState.isBlocked) {
            (detached ??= new List<SurfaceCoord>()).Add(coord);
          }
        } else if (oldState != newState) {
          var wasInGraph = !oldState.isBlocked;
          var isInGraph = !newState.IsBlocked;
          if (wasInGraph && !isInGraph) {
            // Newly blocked: leave the old region. No attach -- not in
            // any region now.
            (detached ??= new List<SurfaceCoord>()).Add(coord);
          } else if (!wasInGraph && isInGraph) {
            // Newly unblocked: enter the region graph. No detach -- it
            // wasn't in any region.
            (attached ??= new List<SurfaceCoord>()).Add(coord);
          } else if (wasInGraph && isInGraph) {
            // (IsCave, IsSettled) flipped while staying in-graph --
            // detach from old region, re-attach to new.
            (detached ??= new List<SurfaceCoord>()).Add(coord);
            (attached ??= new List<SurfaceCoord>()).Add(coord);
          }
          // else: blocked both before and after -- no region change.
        }
      }
      foreach (var kv in newStateByZ) {
        if (oldStateByZ.ContainsKey(kv.Key)) continue;
        // Newly appeared. Only attach if it's in-graph.
        if (!kv.Value.IsBlocked) {
          (attached ??= new List<SurfaceCoord>()).Add(new SurfaceCoord(column.X, column.Y, kv.Key));
        }
      }

      if (detached == null && attached == null) {
        return ColumnDiff.Empty;
      }
      return new ColumnDiff(
          (IReadOnlyCollection<SurfaceCoord>?)detached ?? System.Array.Empty<SurfaceCoord>(),
          (IReadOnlyCollection<SurfaceCoord>?)attached ?? System.Array.Empty<SurfaceCoord>());
    }

    #endregion

    #region Internal pass logic

    /// <summary>
    /// Re-poll a column. Updates <see cref="_surfaces"/> and
    /// <see cref="_columnHeights"/> for this column. Removes any surfaces
    /// that are no longer present. Returns a per-Z structural-state
    /// snapshot (IsCave, IsSettled, IsBlocked) for the post-pass
    /// surfaces, which <see cref="ResurveyColumn"/> uses to compute its
    /// diff against the pre-pass snapshot.
    /// </summary>
    private Dictionary<int, (bool IsCave, bool IsSettled, bool IsBlocked)> ResurveyColumnInternal(TileCoord column) {
      var newStateByZ = new Dictionary<int, (bool, bool, bool)>();
      if (!_terrain.Contains(column)) {
        // Out of bounds; ensure column has no entries.
        ClearColumnSurfaces(column);
        _columnHeights.Remove(column);
        return newStateByZ;
      }

      var heights = _terrain.SurfaceHeightsAt(column);
      if (heights.Count == 0) {
        ClearColumnSurfaces(column);
        _columnHeights.Remove(column);
        return newStateByZ;
      }

      // Drop any old surfaces in this column whose Z is no longer present.
      // (Surfaces whose Z survives will be overwritten with fresh data.)
      var oldHeights = ColumnSurfaceHeights(column);
      var newZs = new HashSet<int>(heights);
      for (var i = 0; i < oldHeights.Count; i++) {
        if (!newZs.Contains(oldHeights[i])) {
          _surfaces.Remove(new SurfaceCoord(column.X, column.Y, oldHeights[i]));
        }
      }

      _columnHeights[column] = heights;

      for (var i = 0; i < heights.Count; i++) {
        var surface = new SurfaceCoord(column.X, column.Y, heights[i]);
        var isCave = _terrain.HasTerrainAbove(surface);
        var isSettled = ComputeIsSettled(surface);
        var isBlocked = _blocking.IsBlockedAt(surface);
        _surfaces.Set(surface, new SurfaceSurvey(IsCave: isCave, IsSettled: isSettled, IsBlocked: isBlocked));
        newStateByZ[heights[i]] = (isCave, isSettled, isBlocked);
      }

      return newStateByZ;
    }

    /// <summary>
    /// Per-surface "is this part of player infrastructure" decision.
    /// A surface is Settled iff:
    /// <list type="bullet">
    ///   <item>The surface voxel itself contains a Building, OR</item>
    ///   <item>At least one of its 8 lateral neighbor voxels (4-connected
    ///         + diagonals, at the same Z) contains a Building.</item>
    /// </list>
    ///
    /// <para><b>Halo, not path-tracing.</b> The rule is a simple 1-voxel
    /// halo around every Building -- it doesn't matter what occupies the
    /// halo voxel (path, grass, ramp, nothing). This deliberately
    /// includes empty grass tiles wedged between buildings (the courtyard
    /// case) and the immediate yard around an isolated building. An
    /// earlier formulation gated the halo on "path occupies the voxel,"
    /// which produced fragile gaps wherever buildings weren't directly
    /// connected by paths. The path classification is still produced by
    /// <see cref="IBuildingQuery"/> for other consumers; it's just no
    /// longer part of Settled.</para>
    ///
    /// <para><b>Z convention.</b> <see cref="SurfaceCoord"/>'s Z is the
    /// air voxel directly above the topmost terrain voxel -- the
    /// "buildable position" where placed structures sit. Buildings,
    /// paths, and trees all occupy <c>surface.Z</c> (and possibly
    /// higher Z for multi-tile-tall structures).</para>
    /// </summary>
    private bool ComputeIsSettled(SurfaceCoord surface) {
      // Self-check: both Building (full settle + aura) and BuildingNoAura
      // (settle without aura) mean the voxel itself is settled.
      _settledClassifyCalls++;
      var selfKind = _building.ClassifyAt(surface);
      if (selfKind == BuildingKind.Building || selfKind == BuildingKind.BuildingNoAura) {
        return true;
      }
      // Aura check: only normal Building propagates settlement to the
      // 1-tile halo. BuildingNoAura is deliberately excluded so small
      // decorations / single-tile utility buildings don't sterilize a
      // 3x3 area around themselves.
      for (var dx = -1; dx <= 1; dx++) {
        for (var dy = -1; dy <= 1; dy++) {
          if (dx == 0 && dy == 0) continue;
          _settledClassifyCalls++;
          if (_building.ClassifyAt(new SurfaceCoord(surface.X + dx, surface.Y + dy, surface.Z)) == BuildingKind.Building) {
            return true;
          }
        }
      }
      return false;
    }

    private void ClearColumnSurfaces(TileCoord column) {
      var oldHeights = ColumnSurfaceHeights(column);
      for (var i = 0; i < oldHeights.Count; i++) {
        _surfaces.Remove(new SurfaceCoord(column.X, column.Y, oldHeights[i]));
      }
    }

    #endregion

  }

  /// <summary>
  /// Aggregate stats from a single <see cref="TerrainSurveyor.Survey"/> pass.
  /// </summary>
  /// <param name="Surfaces">Total number of surface voxels surveyed (stacked surfaces in a column count separately).</param>
  /// <param name="Columns">Number of in-bounds columns that contributed at least one surface.</param>
  public readonly record struct SurveyResult(int Surfaces, int Columns);

}
