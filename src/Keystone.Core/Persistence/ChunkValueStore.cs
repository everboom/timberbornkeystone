using System.Collections.Generic;
using Keystone.Core.Collections;
using Keystone.Core.Regions;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Keyed store of per-chunk named float values. Mirrors
  /// <see cref="RegionValueStore"/>'s shape with one extra spatial
  /// dimension: the key adds chunk coordinates on the global chunk
  /// lattice (see <see cref="ChunkValueKey"/>) so consumers can attach
  /// values at chunk-scale granularity without giving up the per-region
  /// insulation Z-stacked regions need.
  ///
  /// <para>Mod 1 owns the <c>"keystone."</c> prefix; the store is
  /// publicly mutable so faction-expansion mods can read/write their
  /// own value kinds without going through Mod 1 contracts.</para>
  ///
  /// <para><b>Use at your own risk.</b> Anything you write here is
  /// persisted across saves. There's no enforcement of which mod is
  /// allowed to write which kinds; pick a unique prefix (e.g.
  /// <c>"folktails."</c>) and stick to it. Reading an unknown kind
  /// returns <c>null</c>, so it's safe to probe other mods' values
  /// optimistically -- but treat them as opaque, since they may change
  /// between mod versions.</para>
  ///
  /// <para>Order: <see cref="SortedSnapshot"/> walks ascending by
  /// (RegionId, ChunkX, ChunkY, Kind ordinal) -- the same order
  /// <see cref="SnapshotCodec"/> uses on save. The method name is
  /// deliberate: it allocates a full copy of the store and sorts it,
  /// so it's not free and not safe to call per-frame. For per-(region,
  /// chunk) reads use <see cref="EntriesForChunk"/>.</para>
  /// </summary>
  public sealed class ChunkValueStore {

    #region Fields

    /// <summary>
    /// Primary store keyed by full <see cref="ChunkValueKey"/>, grouped by
    /// <c>(RegionId, ChunkX, ChunkY)</c> footprint so
    /// <see cref="EntriesForChunk"/> returns one chunk's handful of entries
    /// in O(entries-for-that-chunk) instead of walking the whole store. The
    /// grouped dictionary maintains the footprint index in lockstep with the
    /// primary map at every mutation site, so the two can never desync.
    /// </summary>
    private readonly GroupedDictionary<ChunkValueKey, (RegionId Region, int ChunkX, int ChunkY), float>
        _values = new(k => (k.RegionId, k.ChunkX, k.ChunkY));

    #endregion

    #region Properties

    /// <summary>Number of stored values across all regions, chunks and kinds.</summary>
    public int Count => _values.Count;

    /// <summary>
    /// Allocate a copy of the store sorted ascending by
    /// (RegionId, ChunkX, ChunkY, Kind ordinal). Stable across runs
    /// given the same content. Intended for the save path
    /// (<see cref="SnapshotCodec"/>) and for tests that assert
    /// deterministic order -- the cost (full copy + sort) is paid
    /// at every call, so don't put this on a per-frame path. Per-
    /// chunk reads should use <see cref="EntriesForChunk"/>.
    /// </summary>
    public List<KeyValuePair<ChunkValueKey, float>> SortedSnapshot() {
      var sorted = new List<KeyValuePair<ChunkValueKey, float>>(_values.Entries);
      sorted.Sort((a, b) => {
        var byId = a.Key.RegionId.Value.CompareTo(b.Key.RegionId.Value);
        if (byId != 0) return byId;
        var byCx = a.Key.ChunkX.CompareTo(b.Key.ChunkX);
        if (byCx != 0) return byCx;
        var byCy = a.Key.ChunkY.CompareTo(b.Key.ChunkY);
        if (byCy != 0) return byCy;
        return string.CompareOrdinal(a.Key.Kind, b.Key.Kind);
      });
      return sorted;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Write a value. <paramref name="kind"/> must be non-empty (the
    /// <see cref="ChunkValueKey"/> constructor enforces this).
    ///
    /// <para><b>Use at your own risk for cross-mod writes.</b> No mod-
    /// id enforcement; pick a unique prefix and don't write under
    /// other mods' prefixes. Mod 1 uses <c>"keystone."</c>.</para>
    /// </summary>
    public void Set(RegionId regionId, int chunkX, int chunkY, string kind, float value) {
      _values[new ChunkValueKey(regionId, chunkX, chunkY, kind)] = value;
    }

    /// <summary>
    /// Read a value, or <c>null</c> if the (region, chunk, kind) triple
    /// has never been written.
    /// </summary>
    public float? Get(RegionId regionId, int chunkX, int chunkY, string kind) {
      if (string.IsNullOrEmpty(kind)) return null;
      return _values.TryGetValue(new ChunkValueKey(regionId, chunkX, chunkY, kind), out var v)
          ? v : (float?)null;
    }

    /// <summary>
    /// Drop a single entry. Returns true if an entry existed and was
    /// removed; false if there was nothing there. Producers of decay-
    /// capable values call this when a value drops to zero so the
    /// store stays sparse and the display surface (e.g. debug panel)
    /// doesn't list dead entries.
    /// </summary>
    public bool Remove(RegionId regionId, int chunkX, int chunkY, string kind) {
      if (string.IsNullOrEmpty(kind)) return false;
      return _values.Remove(new ChunkValueKey(regionId, chunkX, chunkY, kind));
    }

    /// <summary>
    /// Yield every entry whose key matches <paramref name="regionId"/>,
    /// <paramref name="chunkX"/> and <paramref name="chunkY"/>. Unsorted;
    /// callers that need a stable display order should sort the (small)
    /// returned subset themselves.
    ///
    /// <para>O(entries-for-that-chunk): served by the grouped dictionary's
    /// per-footprint index, not a scan. This sits on two hot paths — the
    /// debug overlay's per-frame cursor read and <see cref="ChunkReconciler"/>'s
    /// per-rehome / per-drop value move — both of which previously walked the
    /// whole store per call and dominated their respective costs on a
    /// developed map (tens of thousands of entries).</para>
    /// </summary>
    public IEnumerable<KeyValuePair<ChunkValueKey, float>> EntriesForChunk(
        RegionId regionId, int chunkX, int chunkY) {
      return _values.EntriesForGroup((regionId, chunkX, chunkY));
    }

    /// <summary>Drop every stored value.</summary>
    public void Clear() {
      _values.Clear();
    }

    /// <summary>
    /// Drop every entry keyed under <paramref name="regionId"/>.
    ///
    /// <para><b>No Mod 1 caller.</b> Mod 1's per-chunk re-binding on region
    /// split/merge/remove moved to <see cref="ChunkReconciler"/> (spatial
    /// footprint), so this is no longer invoked from
    /// <c>RegionValueLifecycleHandler</c> — it's retained as external-mod
    /// store API for callers managing their own region-keyed chunk values.
    /// (Contrast the still-live region-level
    /// <see cref="RegionValueStore.RemoveAllValuesFor"/>.)</para>
    /// </summary>
    public void RemoveAllValuesFor(RegionId regionId) {
      List<ChunkValueKey>? toRemove = null;
      foreach (var key in _values.Keys) {
        if (key.RegionId == regionId) {
          (toRemove ??= new List<ChunkValueKey>()).Add(key);
        }
      }
      if (toRemove == null) return;
      foreach (var key in toRemove) _values.Remove(key);
    }

    /// <summary>
    /// Drop every entry whose <see cref="ChunkValueKey.RegionId"/> isn't
    /// in <paramref name="liveIds"/>. Used at PostLoad to scrub orphans
    /// from saves. Returns the number pruned for diagnostic logging.
    ///
    /// <para>Caller passes a <see cref="HashSet{T}"/> for O(1)
    /// <c>Contains</c>; a list-backed collection would make this
    /// O(N*M).</para>
    /// </summary>
    public int PruneToLiveRegions(HashSet<RegionId> liveIds) {
      List<ChunkValueKey>? toRemove = null;
      foreach (var key in _values.Keys) {
        if (!liveIds.Contains(key.RegionId)) {
          (toRemove ??= new List<ChunkValueKey>()).Add(key);
        }
      }
      if (toRemove == null) return 0;
      foreach (var key in toRemove) _values.Remove(key);
      return toRemove.Count;
    }

    /// <summary>
    /// Move every chunk-value entry from <paramref name="loser"/> onto
    /// <paramref name="survivor"/>. On per-(chunk, kind) collision the
    /// survivor's pre-merge entry wins (the loser's entry is dropped).
    /// Loser entries are removed from the store regardless.
    ///
    /// <para><b>No Mod 1 caller.</b> Mod 1's chunk re-binding on region
    /// merge moved to <see cref="ChunkReconciler"/> (which re-homes by
    /// spatial footprint, High-beats-Low on collision); this is retained
    /// as external-mod store API. Contrast the still-live region-level
    /// <see cref="RegionValueStore.MergeFrom"/>.</para>
    ///
    /// <para><b>Survivor-wins is a starting policy.</b> See
    /// <see cref="RegionValueStore.MergeFrom"/> for the same caveat:
    /// per-kind merge semantics (max / sum / size-weighted average)
    /// may suit some chunk accumulators better; revisit when value
    /// producers have specific opinions.</para>
    /// </summary>
    public void MergeFrom(RegionId loser, RegionId survivor) {
      if (loser == survivor) return;
      List<(int Cx, int Cy, string Kind, float Value, bool SurvivorHasIt)>? snapshot = null;
      foreach (var kv in _values.Entries) {
        if (kv.Key.RegionId != loser) continue;
        var survivorHasIt = _values.ContainsKey(
            new ChunkValueKey(survivor, kv.Key.ChunkX, kv.Key.ChunkY, kv.Key.Kind));
        (snapshot ??= new List<(int, int, string, float, bool)>())
            .Add((kv.Key.ChunkX, kv.Key.ChunkY, kv.Key.Kind, kv.Value, survivorHasIt));
      }
      if (snapshot is null) return;
      foreach (var (cx, cy, kind, value, survivorHasIt) in snapshot) {
        _values.Remove(new ChunkValueKey(loser, cx, cy, kind));
        if (!survivorHasIt) {
          _values[new ChunkValueKey(survivor, cx, cy, kind)] = value;
        }
      }
    }

    /// <summary>
    /// Copy every value entry from <paramref name="source"/> to a
    /// parallel entry keyed under <paramref name="destination"/>.
    /// Existing destination entries with the same (chunkX, chunkY, kind)
    /// are overwritten. Source entries are left in place -- the caller
    /// decides whether the source still exists.
    ///
    /// <para><b>No Mod 1 caller.</b> Mod 1's chunk re-binding on region
    /// split moved to <see cref="ChunkReconciler"/> (the orphan's chunks
    /// re-home to it by spatial footprint, precisely, rather than copying
    /// the whole parent en masse); this is retained as external-mod store
    /// API. Contrast the still-live region-level
    /// <see cref="RegionValueStore.Inherit"/>.</para>
    /// </summary>
    public void Inherit(RegionId source, RegionId destination) {
      if (source == destination) return;
      // Snapshot the source entries before mutating, in case the
      // backing dictionary's internal layout would be disturbed by
      // simultaneous reads + writes.
      List<(int Cx, int Cy, string Kind, float Value)>? toCopy = null;
      foreach (var kv in _values.Entries) {
        if (kv.Key.RegionId == source) {
          (toCopy ??= new List<(int, int, string, float)>())
              .Add((kv.Key.ChunkX, kv.Key.ChunkY, kv.Key.Kind, kv.Value));
        }
      }
      if (toCopy is null) return;
      foreach (var (cx, cy, kind, value) in toCopy) {
        _values[new ChunkValueKey(destination, cx, cy, kind)] = value;
      }
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Replace the store's contents with <paramref name="entries"/>.
    /// Called by <c>KeystonePersistence</c> during PostLoad to drain
    /// a freshly-decoded snapshot. Public only because the persistence
    /// layer lives in a different assembly; not for general use --
    /// regular writes should go through <see cref="Set"/>.
    /// </summary>
    public void RehydrateFrom(IEnumerable<KeyValuePair<ChunkValueKey, float>> entries) {
      _values.Clear();
      foreach (var kv in entries) {
        _values[kv.Key] = kv.Value;
      }
    }

    #endregion

  }

}
