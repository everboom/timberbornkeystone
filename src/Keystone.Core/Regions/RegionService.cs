using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Diagnostics;
using Keystone.Core.Persistence;
using Keystone.Core.Survey;
using Keystone.Core.Time;
using Keystone.Core.Tiles;

namespace Keystone.Core.Regions {

  /// <summary>
  /// Owns the surface -> region mapping and the set of <see cref="Region"/>
  /// objects. Provides one-shot full <see cref="Index"/> for initial setup
  /// and incremental <see cref="ProcessChanges"/> for event-driven updates
  /// triggered by terrain edits.
  ///
  /// <para><b>Single source of truth.</b> Region membership lives in
  /// <c>_surfaceToRegion</c>; <see cref="Region.Size"/> is a maintained
  /// counter, but no Region object holds an explicit member list. When a
  /// caller needs to enumerate a region's surfaces (debug UI, split
  /// detection orphan-finding), <see cref="SurfacesInRegion"/> reverse-
  /// scans on demand. This avoids the two-sources-of-truth problem and
  /// matches the polling-sweep model in DESIGN.md.</para>
  ///
  /// <para><b>Stable IDs across edits.</b> A region keeps its
  /// <see cref="RegionId"/> as long as it has at least one member and
  /// isn't merged out. Local edits leave most regions untouched, so
  /// their IDs persist naturally across many flushes -- this is what
  /// will let downstream eco-state (Chunk D + 1B) attach to specific
  /// regions reliably.</para>
  /// </summary>
  public sealed class RegionService {

    #region Fields

    private readonly TerrainSurveyor _surveyor;
    private readonly IClock _clock;
    private readonly Dictionary<RegionId, Region> _byId = new();
    private readonly Dictionary<SurfaceCoord, RegionId> _surfaceToRegion = new();
    private int _nextId;

    // Per-flush diagnostic counters. Reset at the top of ProcessChanges and
    // reported through the supplied IPerfScope at the end. RegionService is
    // single-threaded (topology mutations run on the Unity main thread), so
    // plain instance fields need no synchronisation. These exist to expose
    // the algorithmic cost the split/merge path incurs — each full reverse-
    // scan of _surfaceToRegion is O(all surfaces on the map), and the
    // split-check BFS walks an entire region; on the main landmass both are
    // O(map), which is the documented spike source.
    private int _dbgReverseScans;
    private long _dbgBfsSurfaces;
    private int _dbgSplitChecks;

    #endregion

    #region Properties

    /// <summary>All regions discovered so far. Read-only view.</summary>
    public IReadOnlyCollection<Region> All => _byId.Values;

    /// <summary>Number of regions currently tracked.</summary>
    public int Count => _byId.Count;

    /// <summary>
    /// Monotonic counter bumped whenever the region topology changes
    /// (full re-Index, incremental ProcessChanges flush). Consumers
    /// that derive expensive per-region scratch (chunk grids, valid
    /// sets, schedules) compare this against their last-built value
    /// to skip rebuilding when nothing has moved.
    ///
    /// <para><b>Conservative bumping.</b> The counter advances even
    /// when a flush turned out to be a no-op; over-bumping costs one
    /// extra schedule rebuild on the consumer side, under-bumping
    /// would leave stale schedules pointing at stale (region, chunk)
    /// coordinates. Tradeoff lands clearly on the safe side.</para>
    /// </summary>
    public int TopologyVersion { get; private set; }

    #endregion

    #region Lifecycle events

    /// <summary>
    /// Fired once per orphan piece during a split, immediately after the
    /// new region object is added to the index. Args are
    /// <c>(parentId, orphanId)</c>.
    ///
    /// <para>Lets observers (e.g. <c>RegionScoreStore</c>) carry per-
    /// region accumulated state forward onto the orphan -- the land
    /// hasn't suddenly become younger just because we drew a new id
    /// boundary across it.</para>
    /// </summary>
    public event System.Action<RegionId, RegionId>? RegionSplit;

    /// <summary>
    /// Fired during a merge, before the loser is removed from the
    /// index. Args are <c>(loserId, survivorId)</c>.
    ///
    /// <para>Currently emitted for observability but not yet consumed --
    /// the merge policy for accumulator scores (max? sum? size-weighted?)
    /// hasn't been resolved. Subscribers should be cautious about
    /// fixing a policy here without coordination.</para>
    /// </summary>
    public event System.Action<RegionId, RegionId>? RegionMerged;

    /// <summary>
    /// Fired immediately before a region is removed from the index
    /// (because its <see cref="Region.Size"/> hit zero or split-detection
    /// found inconsistency). The argument is the dying region's id.
    ///
    /// <para>Subscribers (notably the score stores' lifecycle handler)
    /// use this to drop entries keyed under the dead RegionId so they
    /// don't (a) leak in the save file, or (b) get silently re-activated
    /// if a future region happens to be allocated the same id.</para>
    /// </summary>
    public event System.Action<RegionId>? RegionRemoved;

    #endregion

    #region Diagnostic counters

    /// <summary>Cumulative count of <see cref="RegionSplit"/> events
    /// fired since construction. Activity-panel readers sample this
    /// against a previous snapshot to compute "splits today." Never
    /// decrements; survives across all operations that fire splits.</summary>
    public long RegionSplitCount { get; private set; }

    /// <summary>Cumulative count of <see cref="RegionMerged"/> events
    /// fired since construction.</summary>
    public long RegionMergedCount { get; private set; }

    /// <summary>Cumulative count of <see cref="RegionRemoved"/> events
    /// fired since construction. Note: merge losers go through
    /// <see cref="RegionMerged"/>, NOT <see cref="RegionRemoved"/>;
    /// this counter only tracks size-hit-zero and
    /// split-detection-inconsistency removals.</summary>
    public long RegionRemovedCount { get; private set; }

    /// <summary>Cumulative count of regions created since construction
    /// (any path: initial <c>Index()</c>, <c>ProcessChanges</c> seeding
    /// a fresh component, split-orphan id allocation). Useful for
    /// "regions created today" diagnostics; pairs with the removed /
    /// merged counters to characterise region churn.</summary>
    public long RegionsCreatedCount { get; private set; }

    #endregion

    #region Construction

    public RegionService(TerrainSurveyor surveyor, IClock clock) {
      _surveyor = surveyor;
      _clock = clock;
    }

    #endregion

    #region Public lookups

    /// <summary>Look up the region by id. Null if unknown.</summary>
    public Region? Get(RegionId id) =>
        _byId.TryGetValue(id, out var r) ? r : null;

    /// <summary>
    /// Find the region containing <paramref name="surface"/>.
    /// Returns null if the surface wasn't surveyed or isn't in any region.
    /// </summary>
    public Region? Containing(SurfaceCoord surface) {
      if (!_surfaceToRegion.TryGetValue(surface, out var id)) {
        return null;
      }
      return _byId.TryGetValue(id, out var r) ? r : null;
    }

    /// <summary>
    /// Enumerate every surface currently assigned to <paramref name="regionId"/>.
    /// O(all surfaces) reverse-scan -- intended for occasional callers
    /// (debug UI, split-detection orphan finding). Hot paths should
    /// avoid this.
    /// </summary>
    public IEnumerable<SurfaceCoord> SurfacesInRegion(RegionId regionId) {
      foreach (var kv in _surfaceToRegion) {
        if (kv.Value == regionId) {
          yield return kv.Key;
        }
      }
    }

    /// <summary>
    /// Find the majority-owner region for a chunk's spatial footprint.
    /// Walks every tile in <c>[chunkX*chunkSize, (chunkX+1)*chunkSize)
    /// × [chunkY*chunkSize, (chunkY+1)*chunkSize)</c>, collects the
    /// region for each surface in those columns, and returns the region
    /// with the highest member-surface count in the footprint. Ties
    /// broken by lowest <see cref="RegionId"/> for determinism.
    /// Returns null if the footprint contains no qualifying surfaces
    /// (e.g., every voxel is blocked, out of bounds, or -- when
    /// <paramref name="targetZ"/> is supplied -- no surface exists at
    /// that Z in the footprint).
    ///
    /// <para><b>Z constraint -- LOAD-BEARING for chunk-value rescue.</b>
    /// When <paramref name="targetZ"/> is non-null, only surfaces at
    /// exactly that Z count. A region's identity includes its Z layer
    /// (a region only contains surfaces at <see cref="Region.Z"/>), so
    /// this is equivalent to "only regions at that Z." The parameter
    /// is optional ONLY to support callers with no per-record Z
    /// context (e.g. v1 saves without a representative surface);
    /// chunk-value rescue MUST pass it. Without it, vertically stacked
    /// regions at the same <c>(X, Y)</c> compete and a higher stacked
    /// region can win the majority vote -- silently reattaching the
    /// saved data to the wrong layer, which is the failure mode the
    /// Z-invariant in <c>ChunkValueKey</c> exists to prevent. Passing
    /// the saved region's Z makes the rescue strict to the original
    /// layer, dropping the value when no matching Z exists (better to
    /// rebuild the small amount of lost state than to misapply it).</para>
    ///
    /// <para><b>Purpose.</b> Persistence-load chunk-value rescue.
    /// Saved chunk values are keyed by <c>(SavedRegionId, ChunkX, ChunkY)</c>;
    /// when the saved region's identity no longer maps to a live region
    /// (terrain split by a new blockage, mod set changed, etc.), the
    /// chunk's spatial footprint can still anchor it to whichever live
    /// region owns the majority of surfaces in that footprint at the
    /// saved Z. See <c>KeystonePersistence.PostLoadInner</c>.</para>
    ///
    /// <para><b>Cost.</b> <c>chunkSize²</c> column lookups (16 for the
    /// production <c>ChunkSize=4</c>), plus a constant number of surface
    /// probes per column. Designed for one-shot use at load.</para>
    /// </summary>
    public RegionId? FindRegionByChunkFootprint(int chunkX, int chunkY, int chunkSize, int? targetZ = null) {
      Dictionary<RegionId, int>? counts = null;
      var x0 = chunkX * chunkSize;
      var y0 = chunkY * chunkSize;
      for (var dx = 0; dx < chunkSize; dx++) {
        for (var dy = 0; dy < chunkSize; dy++) {
          var col = new TileCoord(x0 + dx, y0 + dy);
          var heights = _surveyor.ColumnSurfaceHeights(col);
          for (var i = 0; i < heights.Count; i++) {
            var z = heights[i];
            if (targetZ.HasValue && z != targetZ.Value) continue;
            var surface = new SurfaceCoord(col.X, col.Y, z);
            if (!_surfaceToRegion.TryGetValue(surface, out var id)) continue;
            counts ??= new Dictionary<RegionId, int>();
            counts.TryGetValue(id, out var c);
            counts[id] = c + 1;
          }
        }
      }
      if (counts is null) return null;
      RegionId best = default;
      var bestCount = -1;
      foreach (var kv in counts) {
        if (kv.Value > bestCount
            || (kv.Value == bestCount && kv.Key.Value < best.Value)) {
          best = kv.Key;
          bestCount = kv.Value;
        }
      }
      return best;
    }

    /// <summary>
    /// Precompute, in a single pass over all assigned surfaces, the
    /// majority-owner region for every populated <c>(chunkX, chunkY, z)</c>
    /// on the global chunk lattice — the batch equivalent of calling
    /// <see cref="FindRegionByChunkFootprint"/> for every chunk, but
    /// O(surfaces) total rather than O(chunks × chunkSize²) surface probes.
    /// Majority winner and lowest-<see cref="RegionId"/> tiebreak match
    /// <see cref="FindRegionByChunkFootprint"/> exactly, so a lookup in the
    /// returned map is interchangeable with a call to it.
    ///
    /// <para><b>When to use.</b> The map-wide reconciliation sweep
    /// (<c>ChunkReconciler</c> driven by the manual self-test) calls the
    /// owner query once per chunk; the per-chunk footprint walk dominates
    /// its cost on a developed map. Building this index once and looking up
    /// O(1) per chunk collapses that. The scoped per-flush reconcile
    /// touches few chunks and uses the per-chunk query directly, so it
    /// doesn't pay to build this.</para>
    ///
    /// <para>A chunk coordinate absent from the map has no assigned surface
    /// at that <c>(chunkXY, z)</c> — equivalent to
    /// <see cref="FindRegionByChunkFootprint"/> returning null.</para>
    ///
    /// <para>Each entry also carries the full set of regions present in the
    /// footprint (<see cref="ChunkFootprintOwnership.Present"/>), so a
    /// consumer can ask "does region R still own this chunk?" without a
    /// second pass — what the chunk reconciler needs to tell a stranded
    /// chunk from a valid minority co-owner.</para>
    /// </summary>
    public IReadOnlyDictionary<(int ChunkX, int ChunkY, int Z), ChunkFootprintOwnership>
        BuildChunkFootprintOwnerIndex(int chunkSize) {
      var counts = new Dictionary<(int, int, int), Dictionary<RegionId, int>>();
      foreach (var kv in _surfaceToRegion) {
        var s = kv.Key;
        var key = (s.X / chunkSize, s.Y / chunkSize, s.Z);
        if (!counts.TryGetValue(key, out var byRegion)) {
          byRegion = new Dictionary<RegionId, int>();
          counts[key] = byRegion;
        }
        byRegion.TryGetValue(kv.Value, out var c);
        byRegion[kv.Value] = c + 1;
      }
      var result =
          new Dictionary<(int ChunkX, int ChunkY, int Z), ChunkFootprintOwnership>(counts.Count);
      foreach (var kv in counts) {
        RegionId best = default;
        var bestCount = -1;
        var present = new RegionId[kv.Value.Count];
        var pi = 0;
        foreach (var rc in kv.Value) {
          present[pi++] = rc.Key;
          if (rc.Value > bestCount
              || (rc.Value == bestCount && rc.Key.Value < best.Value)) {
            best = rc.Key;
            bestCount = rc.Value;
          }
        }
        result[kv.Key] = new ChunkFootprintOwnership(best, present);
      }
      return result;
    }

    /// <summary>
    /// True if <paramref name="region"/> has at least one surface in the
    /// chunk footprint at exactly <paramref name="z"/>. The live-query
    /// counterpart to <see cref="ChunkFootprintOwnership.Contains"/>; used
    /// by the chunk reconciler to keep a chunk whose keyed region still
    /// owns surfaces there (even as a minority co-owner) and only re-home
    /// genuinely stranded chunks. Early-returns on the first match, so it's
    /// cheaper than <see cref="FindRegionByChunkFootprint"/> (which must
    /// count every surface to find the majority).
    /// </summary>
    public bool RegionHasSurfaceInChunkFootprint(
        RegionId region, int chunkX, int chunkY, int chunkSize, int z) {
      var x0 = chunkX * chunkSize;
      var y0 = chunkY * chunkSize;
      for (var dx = 0; dx < chunkSize; dx++) {
        for (var dy = 0; dy < chunkSize; dy++) {
          var col = new TileCoord(x0 + dx, y0 + dy);
          var heights = _surveyor.ColumnSurfaceHeights(col);
          for (var i = 0; i < heights.Count; i++) {
            if (heights[i] != z) continue;
            var surface = new SurfaceCoord(col.X, col.Y, z);
            if (_surfaceToRegion.TryGetValue(surface, out var id)
                && id.Value == region.Value) {
              return true;
            }
          }
        }
      }
      return false;
    }

    #endregion

    #region Full index

    /// <summary>
    /// Reindex every surveyed surface from scratch. Discards prior
    /// region state and assigns fresh ids. Intended for the one-shot
    /// initial pass at <c>PostLoad</c>; incremental updates after that
    /// go through <see cref="ProcessChanges"/>.
    /// </summary>
    public void Index() {
      _byId.Clear();
      _surfaceToRegion.Clear();
      _nextId = 0;

      var now = _clock.Now;
      var weather = _clock.CurrentWeather;
      var totalDays = _clock.TotalDaysElapsed;

      // Iterate seeds in deterministic (X, Y, Z) order so id assignment
      // is independent of the underlying Dictionary's iteration order.
      // This is what makes "save the world, reload, re-Index" produce
      // the same region ids -- the contract every IRegionState consumer
      // relies on for keying its persisted state.
      var sortedSurfaces = new List<SurfaceCoord>(_surveyor.Surfaces.Count);
      foreach (var entry in _surveyor.Surfaces.Entries) {
        sortedSurfaces.Add(entry.Key);
      }
      sortedSurfaces.Sort();

      var visited = new HashSet<SurfaceCoord>();
      foreach (var key in sortedSurfaces) {
        if (visited.Contains(key)) continue;
        if (!_surveyor.Surfaces.TryGet(key, out var seedSurvey)) continue;
        if (seedSurvey.IsBlocked) continue; // blocked surfaces don't seed regions
        var members = FloodFillByStructuralAxes(
            key, key.Z, seedSurvey.IsCave, seedSurvey.IsSettled, visited);
        var id = new RegionId(_nextId++);
        var region = new Region(
            id, key.Z, seedSurvey.IsCave, seedSurvey.IsSettled,
            members.Count, now, weather, totalDays);
        _byId[id] = region;
        RegionsCreatedCount++;
        foreach (var coord in members) {
          _surfaceToRegion[coord] = id;
        }
      }

      // Link the neighbour topology, walking each surface's lateral and
      // 1-voxel-cliff neighbours. Iterating in sorted order isn't
      // strictly required for correctness (HashSet adds compose
      // commutatively), but it keeps any logs / diagnostics stable
      // too -- worth the negligible cost.
      foreach (var key in sortedSurfaces) {
        LinkNeighborsAt(key);
      }

      TopologyVersion++;
    }

    /// <summary>
    /// Compute the mapping from current live <see cref="RegionId"/>
    /// to the <see cref="RegionId"/> that a fresh <see cref="Index"/>
    /// pass would assign on the current surveyor state. Pure: doesn't
    /// mutate live state.
    ///
    /// <para><b>Why this exists.</b> <see cref="Index"/> resets
    /// <c>_nextId</c> to 0 and assigns IDs in deterministic sorted-
    /// surface-coord seed order. <see cref="ProcessChanges"/> allocates
    /// fresh IDs from the live <c>_nextId</c>, which only grows. A save
    /// written with live IDs that originated in <see cref="ProcessChanges"/>
    /// won't match the IDs a fresh <see cref="Index"/> produces on
    /// reload, and the rehydration layer silently prunes the mismatched
    /// entries. Persisting under the canonical IDs this method returns
    /// makes save→load round-trips stable regardless of which path
    /// allocated each live ID.</para>
    ///
    /// <para><b>Algorithm.</b> Walks surfaces in the same deterministic
    /// sort order <see cref="Index"/> uses. The first time a live region
    /// is encountered, it claims the next canonical ID (0, 1, 2, ...).
    /// Subsequent surfaces in the same live region are skipped. Output
    /// is equivalent to what <see cref="Index"/> would assign because
    /// both algorithms iterate the same sorted-surface stream and
    /// allocate IDs at first-appearance of each connected component.</para>
    ///
    /// <para><b>Assumptions.</b> Live regions are consistent with what
    /// <see cref="Index"/> would produce -- i.e., each live region is
    /// exactly a connected component under the same (Z, IsCave,
    /// IsSettled) equivalence. If <see cref="ProcessChanges"/> has
    /// silently broken that invariant (e.g. a region spans across a
    /// structural boundary), the returned map will canonicalize the
    /// drifted layout, not what <see cref="Index"/> would produce. Such
    /// drift is a bug in <see cref="ProcessChanges"/> and out of scope
    /// for this method.</para>
    /// </summary>
    public IReadOnlyDictionary<RegionId, RegionId> ComputeCanonicalIdMap() {
      var map = new Dictionary<RegionId, RegionId>(_byId.Count);
      var nextCanonicalId = 0;
      var sortedSurfaces = new List<SurfaceCoord>(_surveyor.Surfaces.Count);
      foreach (var entry in _surveyor.Surfaces.Entries) {
        sortedSurfaces.Add(entry.Key);
      }
      sortedSurfaces.Sort();
      foreach (var key in sortedSurfaces) {
        if (!_surfaceToRegion.TryGetValue(key, out var liveId)) continue;
        if (map.ContainsKey(liveId)) continue;
        map[liveId] = new RegionId(nextCanonicalId++);
      }
      return map;
    }

    /// <summary>
    /// Compute, for every live region, its representative
    /// <see cref="SurfaceCoord"/>: the min-sorted member of the region
    /// in the same total order <see cref="Index"/> uses. Pure: doesn't
    /// mutate live state.
    ///
    /// <para><b>Why this exists.</b> The persistence layer stores one
    /// representative per region so that on reload it has a recovery
    /// path even when the saved <see cref="RegionId"/> doesn't match a
    /// freshly-Indexed live region: look up the representative in the
    /// live surface→region map and reattach the saved data to whichever
    /// region contains that surface. The representative survives mod-
    /// set drift, surveyor edge cases, and bugs in
    /// <see cref="ComputeCanonicalIdMap"/> -- as long as the same
    /// physical surface still exists and is still on the same
    /// (Z, IsCave, IsSettled) layer.</para>
    ///
    /// <para>Uses the same sorted-surface walk as
    /// <see cref="ComputeCanonicalIdMap"/>, so the representative of a
    /// region is the same surface that would seed it under
    /// <see cref="Index"/>.</para>
    /// </summary>
    public IReadOnlyDictionary<RegionId, SurfaceCoord> ComputeRepresentativeSurfaces() {
      var map = new Dictionary<RegionId, SurfaceCoord>(_byId.Count);
      var sortedSurfaces = new List<SurfaceCoord>(_surveyor.Surfaces.Count);
      foreach (var entry in _surveyor.Surfaces.Entries) {
        sortedSurfaces.Add(entry.Key);
      }
      sortedSurfaces.Sort();
      foreach (var key in sortedSurfaces) {
        if (!_surfaceToRegion.TryGetValue(key, out var liveId)) continue;
        if (map.ContainsKey(liveId)) continue;
        map[liveId] = key;
      }
      return map;
    }


    /// <summary>
    /// Overwrite each live region's <see cref="Region.CreatedAt"/>,
    /// <see cref="Region.WeatherAtCreation"/>, and
    /// <see cref="Region.TotalDaysAtCreation"/> from <paramref name="stamps"/>.
    /// Intended exclusively for the Mod-side persistence layer's
    /// PostLoad path, where regions have just been freshly Indexed (so
    /// every persisted id is expected to map to a live region) and need
    /// their original creation-time data restored.
    ///
    /// <para>If a saved id has no live counterpart, the entry is
    /// silently dropped and a warning is emitted via <paramref name="warn"/>
    /// (when supplied). This is defensive: terrain that disappeared
    /// between save and load (e.g. due to mod set changes) won't have
    /// regenerated regions to attach to. Inverse case (live region with
    /// no saved entry) is left alone -- its <c>CreatedAt</c> will be
    /// "now" from the freshly-Indexed pass, which is the most honest
    /// available answer.</para>
    /// <para>Returns the number of dropped entries so callers can
    /// surface "we lost region history for N regions" up to the
    /// player-facing startup report.</para>
    /// </summary>
    public int RestoreCreatedAt(
        IReadOnlyDictionary<RegionId, RegionPersistedRecord> stamps,
        Action<string>? warn = null) {
      if (stamps is null) {
        throw new ArgumentNullException(nameof(stamps));
      }
      var dropped = 0;
      foreach (var kv in stamps) {
        if (!_byId.TryGetValue(kv.Key, out var region)) {
          warn?.Invoke($"[Keystone] persisted region {kv.Key} not present after re-Index; dropping creation stamp.");
          dropped++;
          continue;
        }
        region.CreatedAt = kv.Value.CreatedAt;
        region.WeatherAtCreation = kv.Value.WeatherAtCreation;
        region.TotalDaysAtCreation = kv.Value.TotalDaysAtCreation;
      }
      return dropped;
    }

    #endregion

    #region Incremental updates

    /// <summary>
    /// Apply a batch of surface changes. <paramref name="detached"/> contains
    /// surfaces that disappeared or had <c>IsCave</c> flip and need to leave
    /// their old region; <paramref name="attached"/> contains surfaces that
    /// appeared or had <c>IsCave</c> flip and need to join a region.
    ///
    /// A surface whose <c>IsCave</c> flipped in place appears in both lists.
    /// </summary>
    /// <returns>The set of region ids this call touched in any way —
    /// detached-from, attached-to, merge survivors and losers, split
    /// parents and orphans, and removed regions. Callers use it to scope
    /// downstream work (notably <c>ChunkReconciler</c>'s post-flush sweep)
    /// to exactly the regions whose footprint ownership may have shifted,
    /// rather than re-scanning the whole map. Includes ids that no longer
    /// exist after the call (merge losers, removed regions) so their
    /// stranded chunk data can be re-homed or dropped.</returns>
    public IReadOnlyCollection<RegionId> ProcessChanges(
        IReadOnlyCollection<SurfaceCoord> detached,
        IReadOnlyCollection<SurfaceCoord> attached,
        IPerfScope? perf = null) {
      _dbgReverseScans = 0;
      _dbgBfsSurfaces = 0;
      _dbgSplitChecks = 0;

      // Determinism: sort inputs so that id allocation order
      // (Phase 2 attach, Phase 3 split orphans) is independent of the
      // caller's iteration order. Callers (RegionUpdater, tests) pass
      // lists built from per-column resurveys; concatenation order is
      // not formally guaranteed, and even when it is, sorting here
      // means the determinism contract belongs to RegionService rather
      // than every caller.
      var sortedDetached = new List<SurfaceCoord>(detached);
      sortedDetached.Sort();
      var sortedAttached = new List<SurfaceCoord>(attached);
      sortedAttached.Sort();

      var affectedRegions = new HashSet<RegionId>();
      // Superset of affectedRegions that, unlike it, is never pruned —
      // it accumulates every id touched (including merge losers and
      // removed regions) and is returned for downstream reconciliation
      // scoping.
      var touched = new HashSet<RegionId>();

      // Phase 1: detach
      using (perf?.Track("RegionService.Phase1.Detach")) {
      foreach (var surface in sortedDetached) {
        if (!_surfaceToRegion.TryGetValue(surface, out var oldId)) {
          continue;
        }
        _surfaceToRegion.Remove(surface);
        touched.Add(oldId);
        if (!_byId.TryGetValue(oldId, out var region)) {
          continue;
        }
        region.Size--;
        if (region.Size <= 0) {
          RemoveRegion(oldId);
          affectedRegions.Remove(oldId);
        } else {
          affectedRegions.Add(oldId);
        }
      }
      }

      // Phase 2: attach (with merge)
      using (perf?.Track("RegionService.Phase2.Attach")) {
      foreach (var surface in sortedAttached) {
        if (!_surveyor.Surfaces.TryGet(surface, out var data)) {
          // Surface no longer in survey -- probably detached and not actually re-attached.
          continue;
        }
        if (data.IsBlocked) {
          // Surveyor's diff should not put blocked surfaces in attached;
          // defensive guard against an out-of-sync caller.
          continue;
        }
        if (_surfaceToRegion.ContainsKey(surface)) {
          // Already attached (e.g., another flush already handled it). Skip.
          continue;
        }

        var compatibleNeighborRegions = CollectCompatibleNeighborRegions(surface, data.IsCave, data.IsSettled);
        if (compatibleNeighborRegions.Count == 0) {
          // Spawn a new region.
          var newId = new RegionId(_nextId++);
          var newRegion = new Region(newId, surface.Z, data.IsCave, data.IsSettled, size: 1, _clock.Now, _clock.CurrentWeather, _clock.TotalDaysElapsed);
          _byId[newId] = newRegion;
          RegionsCreatedCount++;
          _surfaceToRegion[surface] = newId;
          touched.Add(newId);
        } else if (compatibleNeighborRegions.Count == 1) {
          var existingId = First(compatibleNeighborRegions);
          _byId[existingId].Size++;
          _surfaceToRegion[surface] = existingId;
          touched.Add(existingId);
        } else {
          // Merge: pick survivor (largest, ties broken by lowest id) then merge losers in.
          var survivor = PickSurvivor(compatibleNeighborRegions);
          touched.Add(survivor);
          // Iterate losers in deterministic id order so that AbsorbStates
          // sees them in the same sequence across runs (matters when an
          // IRegionState's absorption is not commutative across multiple
          // simultaneous losers).
          foreach (var loser in compatibleNeighborRegions.OrderBy(r => r.Value)) {
            if (loser.Value == survivor.Value) continue;
            MergeInto(loser, survivor);
            touched.Add(loser); // dead now; its chunk data must re-home to survivor
            // Normally a merge loser is dropped from the split-check set —
            // it no longer exists. But if the loser had ALSO lost members
            // earlier in this same flush (it was in affectedRegions), a
            // Phase-1 detach may have bisected it, and MergeInto just
            // retagged ALL its surfaces — including the now-disconnected
            // component — onto the survivor. The survivor can therefore
            // span disconnected geometry, which would break the
            // connected-component invariant ComputeCanonicalIdMap relies on.
            // Transfer the split obligation to the survivor so Phase 3
            // splits the stranded piece back off.
            if (affectedRegions.Remove(loser)) {
              affectedRegions.Add(survivor);
            }
          }
          _byId[survivor].Size++;
          _surfaceToRegion[surface] = survivor;
        }

        // Topology: the newly attached surface may bridge its region to
        // adjacent regions (lateral siblings split by IsCave, or 1-voxel
        // cliff neighbours). Refresh the edges around it. Wild ↔ settled
        // is not linked; the helper enforces that.
        LinkNeighborsAt(surface);
      }
      }

      // Phase 3: split-check regions that lost members. Iterate in id
      // order so orphan-pieces get freshly-allocated ids in a
      // deterministic sequence.
      using (perf?.Track("RegionService.Phase3.SplitCheck")) {
      foreach (var regionId in affectedRegions.OrderBy(r => r.Value)) {
        DetectAndHandleSplit(regionId, touched);
      }
      }

      // Diagnostic counters: surface the algorithmic cost of this flush so
      // the perf panel shows not just "Phase 3 took N ms" but why — how many
      // full-map reverse-scans ran, how many surfaces the split BFS walked,
      // how many regions were split-checked. Counts, not durations —
      // RecordCount files them in the counter table.
      if (perf != null) {
        perf.RecordCount("RegionService.SplitChecks", _dbgSplitChecks);
        perf.RecordCount("RegionService.ReverseScans", _dbgReverseScans);
        perf.RecordCount("RegionService.BfsSurfaces", _dbgBfsSurfaces);
      }

      TopologyVersion++;
      return touched;
    }

    #endregion

    #region Phase-2 helpers

    private HashSet<RegionId> CollectCompatibleNeighborRegions(SurfaceCoord surface, bool isCave, bool isSettled) {
      var result = new HashSet<RegionId>();
      AddNeighborRegion(surface.X, surface.Y + 1, surface.Z, isCave, isSettled, result);
      AddNeighborRegion(surface.X + 1, surface.Y, surface.Z, isCave, isSettled, result);
      AddNeighborRegion(surface.X, surface.Y - 1, surface.Z, isCave, isSettled, result);
      AddNeighborRegion(surface.X - 1, surface.Y, surface.Z, isCave, isSettled, result);
      return result;
    }

    private void AddNeighborRegion(int nx, int ny, int z, bool isCave, bool isSettled, HashSet<RegionId> result) {
      var coord = new SurfaceCoord(nx, ny, z);
      if (!_surveyor.Surfaces.TryGet(coord, out var data)) return;
      if (data.IsBlocked) return; // a blocked neighbor isn't in any region
      if (data.IsCave != isCave) return;
      if (data.IsSettled != isSettled) return;
      if (!_surfaceToRegion.TryGetValue(coord, out var id)) return;
      result.Add(id);
    }

    private RegionId PickSurvivor(HashSet<RegionId> candidates) {
      RegionId best = default;
      var bestSize = -1;
      foreach (var id in candidates) {
        if (!_byId.TryGetValue(id, out var r)) continue;
        if (r.Size > bestSize || (r.Size == bestSize && id.Value < best.Value)) {
          best = id;
          bestSize = r.Size;
        }
      }
      return best;
    }

    private void MergeInto(RegionId loser, RegionId survivor) {
      if (!_byId.TryGetValue(loser, out var loserRegion)) return;
      if (!_byId.TryGetValue(survivor, out var survivorRegion)) return;

      // Reverse-scan to find loser's surfaces; retag.
      _dbgReverseScans++;
      var toRetag = new List<SurfaceCoord>();
      foreach (var kv in _surfaceToRegion) {
        if (kv.Value == loser) {
          toRetag.Add(kv.Key);
        }
      }
      foreach (var coord in toRetag) {
        _surfaceToRegion[coord] = survivor;
      }

      // Collapse state through IRegionState.Absorbing using pre-merge sizes.
      AbsorbStates(survivorRegion, loserRegion);

      // Topology: survivor inherits loser's neighbour edges. Each n that
      // pointed at loser must now point at survivor instead (reverse
      // pointer update). Stale entries (n already gone from _byId) are
      // dropped on the floor -- conservative-after-split staleness is
      // self-pruning. Self-loops are also scrubbed.
      TransferNeighborsOnMerge(loserRegion, survivorRegion);

      survivorRegion.Size += loserRegion.Size;
      RegionMergedCount++;
      RegionMerged?.Invoke(loser, survivor);
      _byId.Remove(loser);
    }

    /// <summary>
    /// Collapse <paramref name="loser"/>'s state into <paramref name="survivor"/>'s
    /// via each value's <see cref="IRegionState.Absorbing"/>. Both
    /// regions' <see cref="Region.Size"/> still reflects their pre-merge
    /// sizes when this is called.
    /// </summary>
    private static void AbsorbStates(Region survivor, Region loser) {
      // Gather every state-type key across both.
      HashSet<Type>? allKeys = null;
      foreach (var k in survivor.States.Keys) {
        (allKeys ??= new HashSet<Type>()).Add(k);
      }
      foreach (var k in loser.States.Keys) {
        (allKeys ??= new HashSet<Type>()).Add(k);
      }
      if (allKeys is null) return;

      foreach (var key in allKeys) {
        survivor.States.TryGetValue(key, out var sState);
        loser.States.TryGetValue(key, out var lState);
        if (sState is not null) {
          // Survivor speaks; absorb other (which may be null).
          survivor.States[key] = sState.Absorbing(lState, survivor.Size, loser.Size);
        } else if (lState is not null) {
          // Survivor was silent on this state; let loser absorb the (default) survivor's side.
          // Symmetric to the above call but with sides swapped.
          survivor.States[key] = lState.Absorbing(null, loser.Size, survivor.Size);
        }
      }
    }

    private static RegionId First(HashSet<RegionId> set) {
      foreach (var id in set) return id;
      return default;
    }

    #endregion

    #region Phase-3 split detection

    private void DetectAndHandleSplit(RegionId regionId, HashSet<RegionId> touched) {
      if (!_byId.TryGetValue(regionId, out var region)) return;
      if (region.Size <= 0) {
        RemoveRegion(regionId);
        return;
      }

      _dbgSplitChecks++;
      var seed = FindAnyMember(regionId);
      if (seed is null) {
        // No surface tagged with this region but Size > 0 -- internal drift; clean up.
        RemoveRegion(regionId);
        return;
      }

      var firstComponent = BfsByRegionId(seed.Value, regionId);
      if (firstComponent.Count == region.Size) {
        // Single connected component -- no split.
        return;
      }

      // Split detected. Snapshot parent state before mutating Size, so every
      // child (including the kept-id piece) gets its proportional share.
      var parentSize = region.Size;
      var parentStates = SnapshotStates(region);

      // First component keeps the original id; size and state shrink by its ratio.
      region.Size = firstComponent.Count;
      DistributeStateToPiece(region, parentStates, parentSize, firstComponent.Count);

      // Find and process orphan components, one at a time.
      while (true) {
        var orphanSeed = FindAnyMemberNotIn(regionId, firstComponent);
        if (orphanSeed is null) break;
        var orphanComponent = BfsByRegionId(orphanSeed.Value, regionId);
        var newId = new RegionId(_nextId++);
        touched.Add(newId);
        // Inherit CreatedAt and WeatherAtCreation from the parent -- the land didn't suddenly become younger.
        var newRegion = new Region(
            newId, region.Z, region.IsCave, region.IsSettled,
            orphanComponent.Count,
            region.CreatedAt, region.WeatherAtCreation, region.TotalDaysAtCreation);
        _byId[newId] = newRegion;
        RegionsCreatedCount++;
        RegionSplitCount++;
        RegionSplit?.Invoke(regionId, newId);
        // Conservative-on-split topology: orphan inherits the parent's
        // neighbour set wholesale, and each of those neighbours gains a
        // reverse pointer to the new piece. May produce stale edges
        // (the orphan may not actually touch every parent neighbour
        // physically) -- those self-prune on the next merge or death
        // event involving the affected pair. The kept-id piece needs
        // no change: its Region object is the parent's and already
        // carries the right set.
        foreach (var n in region.NeighborsMutable) {
          newRegion.NeighborsMutable.Add(n);
          if (_byId.TryGetValue(n, out var nRegion)) {
            nRegion.NeighborsMutable.Add(newId);
          }
        }
        foreach (var coord in orphanComponent) {
          _surfaceToRegion[coord] = newId;
        }
        DistributeStateToPiece(newRegion, parentStates, parentSize, orphanComponent.Count);
      }
    }

    /// <summary>Snapshot a region's state map so we can redistribute after Size mutation.</summary>
    private static Dictionary<Type, IRegionState>? SnapshotStates(Region r) {
      if (r.States.Count == 0) return null;
      return new Dictionary<Type, IRegionState>(r.States);
    }

    /// <summary>
    /// Replace <paramref name="piece"/>'s state map with values derived
    /// from the parent snapshot via <see cref="IRegionState.ForChildOnSplit"/>.
    /// Called once per piece of a split (including the kept-id piece).
    /// </summary>
    private static void DistributeStateToPiece(
        Region piece,
        Dictionary<Type, IRegionState>? parentStates,
        int parentSize,
        int pieceSize) {
      piece.States.Clear();
      if (parentStates is null) return;
      var ratio = (double)pieceSize / parentSize;
      foreach (var kv in parentStates) {
        piece.States[kv.Key] = kv.Value.ForChildOnSplit(ratio);
      }
    }

    /// <summary>
    /// Return the lowest-sorting (by <see cref="SurfaceCoord.CompareTo"/>)
    /// member of <paramref name="regionId"/>, or <c>null</c> if the region
    /// has no surfaces. The min-key choice is what makes split detection
    /// deterministic: <c>DetectAndHandleSplit</c> uses the seed to grow
    /// the "first component" that keeps the parent id, so the choice
    /// must not depend on Dictionary iteration order.
    /// </summary>
    private SurfaceCoord? FindAnyMember(RegionId regionId) {
      _dbgReverseScans++;
      SurfaceCoord? best = null;
      foreach (var kv in _surfaceToRegion) {
        if (kv.Value != regionId) continue;
        if (best is null || kv.Key.CompareTo(best.Value) < 0) {
          best = kv.Key;
        }
      }
      return best;
    }

    /// <summary>
    /// Same min-by-(X,Y,Z) selection as <see cref="FindAnyMember"/>, but
    /// excluding members already accounted for in
    /// <paramref name="exclude"/>. Used to find the next orphan
    /// component during split detection; deterministic order ensures
    /// orphan-id allocation is reproducible.
    /// </summary>
    private SurfaceCoord? FindAnyMemberNotIn(RegionId regionId, HashSet<SurfaceCoord> exclude) {
      _dbgReverseScans++;
      SurfaceCoord? best = null;
      foreach (var kv in _surfaceToRegion) {
        if (kv.Value != regionId) continue;
        if (exclude.Contains(kv.Key)) continue;
        if (best is null || kv.Key.CompareTo(best.Value) < 0) {
          best = kv.Key;
        }
      }
      return best;
    }

    private HashSet<SurfaceCoord> BfsByRegionId(SurfaceCoord seed, RegionId regionId) {
      var visited = new HashSet<SurfaceCoord> { seed };
      var queue = new Queue<SurfaceCoord>();
      queue.Enqueue(seed);
      while (queue.Count > 0) {
        var current = queue.Dequeue();
        TryEnqueueForRegion(current.X, current.Y + 1, current.Z, regionId, visited, queue);
        TryEnqueueForRegion(current.X + 1, current.Y, current.Z, regionId, visited, queue);
        TryEnqueueForRegion(current.X, current.Y - 1, current.Z, regionId, visited, queue);
        TryEnqueueForRegion(current.X - 1, current.Y, current.Z, regionId, visited, queue);
      }
      _dbgBfsSurfaces += visited.Count;
      return visited;
    }

    private void TryEnqueueForRegion(
        int nx, int ny, int z, RegionId regionId,
        HashSet<SurfaceCoord> visited, Queue<SurfaceCoord> queue) {
      var coord = new SurfaceCoord(nx, ny, z);
      if (visited.Contains(coord)) return;
      if (!_surfaceToRegion.TryGetValue(coord, out var id)) return;
      if (id.Value != regionId.Value) return;
      visited.Add(coord);
      queue.Enqueue(coord);
    }

    #endregion

    #region Neighbour topology

    /// <summary>
    /// Link <paramref name="surface"/>'s region to any foreign region(s)
    /// it borders. Border definition:
    /// <list type="bullet">
    ///   <item><b>Cardinal</b> (N/S/E/W): always probed at Z, Z-1, Z+1.
    ///         Captures lateral siblings (split by IsCave) and 1-voxel
    ///         cliff edges in the four primary directions.</item>
    ///   <item><b>Diagonal</b> (NE/NW/SE/SW): probed only when BOTH of
    ///         the two cardinal columns that frame the diagonal contain
    ///         no surface anywhere in the ±1 Z window. The condition
    ///         picks up diagonal staircases (where each voxel-region
    ///         has no cardinal neighbours and would otherwise be a
    ///         disconnected island) without double-linking plateaus
    ///         that already have richer cardinal connectivity.</item>
    /// </list>
    /// Wild ↔ settled is suppressed (settled regions are walled off
    /// from the eco graph).
    /// </summary>
    /// <remarks>
    /// Three-Z probing per direction handles the "1-voxel cliff" rule
    /// symmetrically: if the neighbour column's surface is 1 lower,
    /// that's a cliff edge; if 1 higher, we're at the bottom of theirs
    /// (the same edge from the other side). Larger drops fall outside
    /// the window. A column can in principle host surfaces at both Z
    /// and Z+1 (1-voxel-thick layer with airspace either side); we
    /// link to every valid hit in that case.
    /// </remarks>
    private void LinkNeighborsAt(SurfaceCoord surface) {
      if (!_surfaceToRegion.TryGetValue(surface, out var thisId)) return;
      if (!_byId.TryGetValue(thisId, out var thisRegion)) return;

      var anyN = ScanColumnAndLink(thisRegion, surface.X, surface.Y + 1, surface.Z);
      var anyS = ScanColumnAndLink(thisRegion, surface.X, surface.Y - 1, surface.Z);
      var anyE = ScanColumnAndLink(thisRegion, surface.X + 1, surface.Y, surface.Z);
      var anyW = ScanColumnAndLink(thisRegion, surface.X - 1, surface.Y, surface.Z);

      // Diagonal fallback: corners only count when both adjacent
      // cardinal columns are completely empty in the ±1 Z window.
      if (!anyN && !anyE) ScanColumnAndLink(thisRegion, surface.X + 1, surface.Y + 1, surface.Z);
      if (!anyN && !anyW) ScanColumnAndLink(thisRegion, surface.X - 1, surface.Y + 1, surface.Z);
      if (!anyS && !anyE) ScanColumnAndLink(thisRegion, surface.X + 1, surface.Y - 1, surface.Z);
      if (!anyS && !anyW) ScanColumnAndLink(thisRegion, surface.X - 1, surface.Y - 1, surface.Z);
    }

    /// <summary>
    /// Probe column <c>(nx, ny)</c> at <paramref name="z"/>, <c>z-1</c>,
    /// and <c>z+1</c>: link <paramref name="thisRegion"/> to any foreign
    /// region surface found, and return whether <i>any</i> surface (own
    /// or foreign) exists in the column within the ±1 Z window. The
    /// "any surface" return is what gates the diagonal fallback in
    /// <see cref="LinkNeighborsAt"/> -- the linking is the actual
    /// neighbour-graph maintenance.
    /// </summary>
    private bool ScanColumnAndLink(Region thisRegion, int nx, int ny, int z) {
      var anyAtZ = TryLinkAt(thisRegion, nx, ny, z);
      var anyAtZdown = TryLinkAt(thisRegion, nx, ny, z - 1);
      var anyAtZup = TryLinkAt(thisRegion, nx, ny, z + 1);
      return anyAtZ || anyAtZdown || anyAtZup;
    }

    /// <summary>
    /// Returns true iff a surface exists at <c>(nx, ny, nz)</c>. When
    /// it exists and belongs to a different region whose
    /// <see cref="Region.IsSettled"/> matches <paramref name="thisRegion"/>'s,
    /// adds the symmetric neighbour edge.
    /// </summary>
    private bool TryLinkAt(Region thisRegion, int nx, int ny, int nz) {
      var coord = new SurfaceCoord(nx, ny, nz);
      if (!_surveyor.Surfaces.TryGet(coord, out _)) return false;
      // From here on the surface exists -- always return true so the
      // diagonal-skip condition is honoured -- but only link when the
      // other side is a region we're allowed to neighbour.
      if (!_surfaceToRegion.TryGetValue(coord, out var otherId)) return true;
      if (otherId.Value == thisRegion.Id.Value) return true;
      if (!_byId.TryGetValue(otherId, out var otherRegion)) return true;
      if (otherRegion.IsSettled != thisRegion.IsSettled) return true;
      thisRegion.NeighborsMutable.Add(otherId);
      otherRegion.NeighborsMutable.Add(thisRegion.Id);
      return true;
    }

    /// <summary>
    /// Reroute every neighbour pointer that referred to
    /// <paramref name="loser"/> so it now refers to
    /// <paramref name="survivor"/>, and copy the (rerouted) edges into
    /// the survivor's set. Self-loops (survivor → survivor) are
    /// discarded. Stale neighbour entries (ids no longer in
    /// <c>_byId</c>) are silently dropped -- their reverse pointer
    /// already doesn't exist.
    /// </summary>
    private void TransferNeighborsOnMerge(Region loser, Region survivor) {
      foreach (var n in loser.NeighborsMutable) {
        if (n.Value == survivor.Id.Value) continue;
        if (!_byId.TryGetValue(n, out var nRegion)) continue;
        nRegion.NeighborsMutable.Remove(loser.Id);
        nRegion.NeighborsMutable.Add(survivor.Id);
        survivor.NeighborsMutable.Add(n);
      }
      loser.NeighborsMutable.Clear();
      // Survivor never points at itself.
      survivor.NeighborsMutable.Remove(survivor.Id);
      // And not at the loser any more either.
      survivor.NeighborsMutable.Remove(loser.Id);
    }

    /// <summary>
    /// Remove a region from the world: scrub its id from every
    /// neighbour's set, then drop it from <c>_byId</c>. Used when a
    /// region's <see cref="Region.Size"/> hits zero or split-detection
    /// finds inconsistency.
    /// </summary>
    private void RemoveRegion(RegionId id) {
      if (!_byId.TryGetValue(id, out var region)) return;
      RegionRemovedCount++;
      // Notify subscribers before mutation so they can still query the
      // region if needed; mirrors the timing of RegionMerged.
      RegionRemoved?.Invoke(id);
      foreach (var n in region.NeighborsMutable) {
        if (_byId.TryGetValue(n, out var nRegion)) {
          nRegion.NeighborsMutable.Remove(id);
        }
      }
      region.NeighborsMutable.Clear();
      _byId.Remove(id);
    }

    #endregion

    #region Index-time flood-fill (used by full Index)

    private HashSet<SurfaceCoord> FloodFillByStructuralAxes(
        SurfaceCoord seed, int z, bool isCave, bool isSettled, HashSet<SurfaceCoord> globalVisited) {
      var members = new HashSet<SurfaceCoord> { seed };
      globalVisited.Add(seed);
      var queue = new Queue<SurfaceCoord>();
      queue.Enqueue(seed);
      while (queue.Count > 0) {
        var current = queue.Dequeue();
        TryEnqueueByStructuralAxes(current.X, current.Y + 1, z, isCave, isSettled, members, globalVisited, queue);
        TryEnqueueByStructuralAxes(current.X + 1, current.Y, z, isCave, isSettled, members, globalVisited, queue);
        TryEnqueueByStructuralAxes(current.X, current.Y - 1, z, isCave, isSettled, members, globalVisited, queue);
        TryEnqueueByStructuralAxes(current.X - 1, current.Y, z, isCave, isSettled, members, globalVisited, queue);
      }
      return members;
    }

    private void TryEnqueueByStructuralAxes(
        int nx, int ny, int z, bool isCave, bool isSettled,
        HashSet<SurfaceCoord> members,
        HashSet<SurfaceCoord> globalVisited,
        Queue<SurfaceCoord> queue) {
      var neighbor = new SurfaceCoord(nx, ny, z);
      if (members.Contains(neighbor)) return;
      if (!_surveyor.Surfaces.TryGet(neighbor, out var survey)) return;
      if (survey.IsBlocked) return; // blocked surfaces are not in any region
      if (survey.IsCave != isCave) return;
      if (survey.IsSettled != isSettled) return;
      members.Add(neighbor);
      globalVisited.Add(neighbor);
      queue.Enqueue(neighbor);
    }

    #endregion

  }

}
