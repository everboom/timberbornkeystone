using System.Collections.Generic;
using Keystone.Core.Collections;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Parallel data layer for per-chunk values. Keyed by
  /// <see cref="ChunkCoord"/> (region + global chunk coords), each
  /// entry holds a <see cref="ChunkData"/> with one <c>float</c> per
  /// <see cref="ChunkValueRegistry"/> slot.
  ///
  /// <para><b>Relationship to <see cref="ChunkValueStore"/>.</b> This
  /// store is the fast, ordinal-indexed hot path. The legacy
  /// <see cref="ChunkValueStore"/> (string-keyed) remains as the
  /// mod-API / persistence / debug-panel layer. A sync pass on the
  /// main thread copies completed results from here into
  /// <see cref="ChunkValueStore"/> each tick.</para>
  ///
  /// <para><b>Key-set stability.</b> Between topology events (region
  /// split/merge/remove), the set of keys is fixed. Background
  /// threads write to <see cref="ChunkData.Values"/> at non-overlapping
  /// chunk coords; the main thread reads from chunks whose sweep batch
  /// has already completed. Topology events fire on the main thread
  /// during <c>Tick()</c> and may add or remove keys — never during
  /// the parallel phase.</para>
  ///
  /// <para><b>Lifecycle on topology change.</b> Chunk data follows region
  /// split/merge/remove via spatial reconciliation, not events: after a
  /// topology flush, <see cref="ChunkReconciler"/> re-binds each chunk to
  /// the region that owns its <c>(X, Y, Z)</c> footprint. (The store's own
  /// <see cref="Inherit"/> / <see cref="MergeFrom"/> / <see cref="RemoveAllFor"/>
  /// are retained only as external-mod API and have no Mod 1 caller.)</para>
  /// </summary>
  public sealed class ChunkDataStore {

    #region Fields

    private readonly ChunkValueRegistry _registry;

    /// <summary>
    /// Primary store keyed by <see cref="ChunkCoord"/>, grouped by
    /// <see cref="RegionId"/> so per-region operations —
    /// <see cref="ChunkReconciler"/>'s scoped sweep via
    /// <see cref="EntriesForRegion"/>, plus <see cref="RemoveAllFor"/> /
    /// <see cref="Inherit"/> / <see cref="MergeFrom"/> — iterate one region's
    /// chunks in O(region's chunks) instead of walking the whole store. The
    /// grouped dictionary maintains the region index in lockstep with the
    /// primary map, so the two can never desync.
    /// </summary>
    private readonly GroupedDictionary<ChunkCoord, RegionId, ChunkData> _chunks =
        new(coord => coord.RegionId);

    #endregion

    #region Construction

    /// <summary>
    /// Create a store backed by the given <paramref name="registry"/>.
    /// Every <see cref="ChunkData"/> allocated by this store will have
    /// <see cref="ChunkValueRegistry.SlotCount"/> slots.
    /// </summary>
    public ChunkDataStore(ChunkValueRegistry registry) {
      _registry = registry;
    }

    #endregion

    #region Properties

    /// <summary>Number of chunks in the store.</summary>
    public int Count => _chunks.Count;

    /// <summary>The registry that determines slot count.</summary>
    public ChunkValueRegistry Registry => _registry;

    #endregion

    #region Access

    /// <summary>
    /// Get the <see cref="ChunkData"/> for the given chunk, or
    /// <c>null</c> if the chunk has no entry.
    /// </summary>
    public ChunkData? Get(ChunkCoord coord) {
      return _chunks.TryGetValue(coord, out var data) ? data : null;
    }

    /// <summary>
    /// Get the <see cref="ChunkData"/> for the given chunk, or
    /// <c>null</c> if the chunk has no entry.
    /// </summary>
    public ChunkData? Get(RegionId regionId, int chunkX, int chunkY) {
      return Get(new ChunkCoord(regionId, chunkX, chunkY));
    }

    /// <summary>
    /// Get the <see cref="ChunkData"/> for the given chunk, creating
    /// a zero-initialized entry if one doesn't exist.
    /// </summary>
    public ChunkData GetOrCreate(ChunkCoord coord) {
      if (_chunks.TryGetValue(coord, out var data)) return data;
      data = new ChunkData(_registry.SlotCount);
      _chunks[coord] = data;
      return data;
    }

    /// <summary>
    /// Get the <see cref="ChunkData"/> for the given chunk, creating
    /// a zero-initialized entry if one doesn't exist.
    /// </summary>
    public ChunkData GetOrCreate(RegionId regionId, int chunkX, int chunkY) {
      return GetOrCreate(new ChunkCoord(regionId, chunkX, chunkY));
    }

    /// <summary>All entries. For enumeration by the sync layer or
    /// diagnostics.</summary>
    public IEnumerable<KeyValuePair<ChunkCoord, ChunkData>> Entries => _chunks.Entries;

    /// <summary>
    /// Every entry currently keyed under <paramref name="regionId"/>, served
    /// O(region's chunks) by the region index. This is what lets
    /// <see cref="ChunkReconciler"/>'s per-flush sweep visit only the chunks
    /// of the regions a topology flush actually touched, instead of walking
    /// the whole store to filter. Live — do not mutate the store while
    /// enumerating; callers that mutate snapshot the result first.
    /// </summary>
    public IEnumerable<KeyValuePair<ChunkCoord, ChunkData>> EntriesForRegion(RegionId regionId) =>
        _chunks.EntriesForGroup(regionId);

    #endregion

    #region Lifecycle

    /// <summary>Drop every entry.</summary>
    public void Clear() {
      _chunks.Clear();
    }

    /// <summary>
    /// Drop the single entry at <paramref name="coord"/>. Returns true if
    /// an entry existed and was removed. Used by <c>ChunkReconciler</c>
    /// to re-home a chunk's data from a stale region key onto the region
    /// that physically owns its footprint.
    /// </summary>
    public bool Remove(ChunkCoord coord) {
      return _chunks.Remove(coord);
    }

    /// <summary>
    /// Drop every entry keyed under <paramref name="regionId"/>.
    /// <para><b>No Mod 1 caller</b> — per-chunk re-binding on region
    /// removal moved to <see cref="ChunkReconciler"/>; retained as
    /// external-mod store API.</para>
    /// </summary>
    public void RemoveAllFor(RegionId regionId) {
      var keys = _chunks.KeysForGroup(regionId);
      if (keys == null) return;
      // Snapshot before mutating: KeysForGroup returns the live group set.
      foreach (var key in new List<ChunkCoord>(keys)) _chunks.Remove(key);
    }

    /// <summary>
    /// Copy every entry from <paramref name="source"/> to a parallel
    /// entry keyed under <paramref name="destination"/>. Existing
    /// destination entries are overwritten. Source entries are left
    /// in place.
    /// <para><b>No Mod 1 caller</b> — split re-binding moved to
    /// <see cref="ChunkReconciler"/> (re-homes the orphan's chunks by
    /// footprint, not a wholesale parent copy); retained as external-mod
    /// store API.</para>
    /// </summary>
    public void Inherit(RegionId source, RegionId destination) {
      if (source == destination) return;
      List<(ChunkCoord Coord, ChunkData Data)>? toCopy = null;
      foreach (var kv in _chunks.EntriesForGroup(source)) {
        (toCopy ??= new List<(ChunkCoord, ChunkData)>()).Add((kv.Key, kv.Value));
      }
      if (toCopy == null) return;
      foreach (var (coord, data) in toCopy) {
        var destCoord = new ChunkCoord(destination, coord.GlobalChunkX, coord.GlobalChunkY);
        var destData = GetOrCreate(destCoord);
        destData.CopyFrom(data);
      }
    }

    /// <summary>
    /// Move every entry from <paramref name="loser"/> onto
    /// <paramref name="survivor"/>. Survivor-wins on collision.
    /// Loser entries are removed.
    /// <para><b>No Mod 1 caller</b> — merge re-binding moved to
    /// <see cref="ChunkReconciler"/>; retained as external-mod store API.</para>
    /// </summary>
    public void MergeFrom(RegionId loser, RegionId survivor) {
      if (loser == survivor) return;
      List<(ChunkCoord Coord, ChunkData Data)>? snapshot = null;
      foreach (var kv in _chunks.EntriesForGroup(loser)) {
        (snapshot ??= new List<(ChunkCoord, ChunkData)>()).Add((kv.Key, kv.Value));
      }
      if (snapshot == null) return;
      foreach (var (coord, data) in snapshot) {
        _chunks.Remove(coord);
        var survivorCoord = new ChunkCoord(survivor, coord.GlobalChunkX, coord.GlobalChunkY);
        if (!_chunks.ContainsKey(survivorCoord)) {
          var survivorData = new ChunkData(_registry.SlotCount);
          survivorData.CopyFrom(data);
          _chunks[survivorCoord] = survivorData;
        }
      }
    }

    /// <summary>
    /// Drop every entry whose region isn't in <paramref name="liveIds"/>.
    /// Used at PostLoad to scrub orphans.
    /// </summary>
    public int PruneToLiveRegions(HashSet<RegionId> liveIds) {
      List<ChunkCoord>? toRemove = null;
      foreach (var key in _chunks.Keys) {
        if (!liveIds.Contains(key.RegionId))
          (toRemove ??= new List<ChunkCoord>()).Add(key);
      }
      if (toRemove == null) return 0;
      foreach (var key in toRemove) _chunks.Remove(key);
      return toRemove.Count;
    }

    #endregion

  }

}
