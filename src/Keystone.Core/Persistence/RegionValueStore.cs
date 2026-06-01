using System.Collections.Generic;
using Keystone.Core.Regions;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Keyed store of per-region named float values. Mod 1 owns the
  /// "keystone." prefix; the store is publicly mutable so faction-
  /// expansion mods can read/write their own value kinds without
  /// going through Mod 1 contracts.
  ///
  /// <para>Region-keyed sibling of <see cref="ChunkValueStore"/>; the
  /// chunk variant adds chunk coordinates to the key. Both are
  /// generic kvstores, not biome-channel specific -- the only kind
  /// currently stored here is <c>RegionAgeDays</c>.</para>
  ///
  /// <para><b>Use at your own risk.</b> Anything you write here is
  /// persisted across saves. There's no enforcement of which mod is
  /// allowed to write which kinds; pick a unique prefix (e.g.
  /// <c>"folktails."</c>) and stick to it. Reading an unknown kind
  /// returns <c>null</c>, so it's safe to probe other mods' values
  /// optimistically -- but treat them as opaque, since they may change
  /// between mod versions.</para>
  ///
  /// <para>Order: <see cref="Entries"/> walks ascending by
  /// (RegionId, Kind ordinal) -- the same order
  /// <see cref="SnapshotCodec"/> uses on save. Useful when callers
  /// want stable iteration without resorting at the call site.</para>
  /// </summary>
  public sealed class RegionValueStore {

    #region Fields

    private readonly Dictionary<RegionValueKey, float> _values = new();

    #endregion

    #region Properties

    /// <summary>Number of stored values across all regions and kinds.</summary>
    public int Count => _values.Count;

    /// <summary>
    /// All entries in the store, walked in ascending
    /// (RegionId, Kind ordinal) order. Stable across runs given the
    /// same content.
    /// </summary>
    public IEnumerable<KeyValuePair<RegionValueKey, float>> Entries {
      get {
        var sorted = new List<KeyValuePair<RegionValueKey, float>>(_values);
        sorted.Sort((a, b) => {
          var byId = a.Key.RegionId.Value.CompareTo(b.Key.RegionId.Value);
          if (byId != 0) return byId;
          return string.CompareOrdinal(a.Key.Kind, b.Key.Kind);
        });
        return sorted;
      }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Write a value. <paramref name="kind"/> must be non-empty (the
    /// <see cref="RegionValueKey"/> constructor enforces this).
    ///
    /// <para><b>Use at your own risk for cross-mod writes.</b> No mod-
    /// id enforcement; pick a unique prefix and don't write under
    /// other mods' prefixes. Mod 1 uses <c>"keystone."</c>.</para>
    /// </summary>
    public void Set(RegionId regionId, string kind, float value) {
      _values[new RegionValueKey(regionId, kind)] = value;
    }

    /// <summary>
    /// Read a value, or <c>null</c> if the (region, kind) pair has
    /// never been written.
    /// </summary>
    public float? Get(RegionId regionId, string kind) {
      if (string.IsNullOrEmpty(kind)) return null;
      return _values.TryGetValue(new RegionValueKey(regionId, kind), out var v) ? v : (float?)null;
    }

    /// <summary>Drop every stored value.</summary>
    public void Clear() {
      _values.Clear();
    }

    /// <summary>
    /// Drop every entry keyed under <paramref name="regionId"/>.
    /// Called by the lifecycle handler on <c>RegionService.RegionRemoved</c>
    /// so a dead region's values don't leak into save files or get
    /// silently re-activated if a future region is allocated the same
    /// id.
    /// </summary>
    public void RemoveAllValuesFor(RegionId regionId) {
      List<RegionValueKey>? toRemove = null;
      foreach (var key in _values.Keys) {
        if (key.RegionId == regionId) {
          (toRemove ??= new List<RegionValueKey>()).Add(key);
        }
      }
      if (toRemove == null) return;
      foreach (var key in toRemove) _values.Remove(key);
    }

    /// <summary>
    /// Drop every entry whose <see cref="RegionValueKey.RegionId"/> isn't
    /// in <paramref name="liveIds"/>. Used at PostLoad to scrub orphans
    /// from saves written before the lifecycle handler covered all
    /// removal paths -- a defensive sweep that ensures no orphan
    /// survives across a save/reload boundary. Returns the number of
    /// entries pruned, for diagnostic logging.
    ///
    /// <para>Caller passes a <see cref="HashSet{T}"/> rather than a
    /// looser <c>IReadOnlyCollection</c> so the inner <c>Contains</c>
    /// is O(1); a list-backed collection would make this O(N*M).</para>
    /// </summary>
    public int PruneToLiveRegions(HashSet<RegionId> liveIds) {
      List<RegionValueKey>? toRemove = null;
      foreach (var key in _values.Keys) {
        if (!liveIds.Contains(key.RegionId)) {
          (toRemove ??= new List<RegionValueKey>()).Add(key);
        }
      }
      if (toRemove == null) return 0;
      foreach (var key in toRemove) _values.Remove(key);
      return toRemove.Count;
    }

    /// <summary>
    /// Move every value entry from <paramref name="loser"/> onto
    /// <paramref name="survivor"/>. On per-kind collision the survivor's
    /// pre-merge entry wins (the loser's entry is dropped, not blended).
    /// Loser entries are removed from the store regardless. Used by
    /// region-lifecycle handling when two regions merge into one --
    /// otherwise loser entries leak under a stale RegionId.
    ///
    /// <para><b>Survivor-wins is a starting policy.</b> Per-kind merge
    /// semantics (max, sum, size-weighted average) may be more
    /// appropriate for some accumulators; revisit when value producers
    /// have specific opinions. For Phase 0 this preserves whatever
    /// state the survivor already had and adopts the loser's
    /// non-conflicting kinds.</para>
    /// </summary>
    public void MergeFrom(RegionId loser, RegionId survivor) {
      if (loser == survivor) return;
      List<(string Kind, float Value, bool SurvivorHasIt)>? snapshot = null;
      foreach (var kv in _values) {
        if (kv.Key.RegionId != loser) continue;
        var survivorHasIt = _values.ContainsKey(new RegionValueKey(survivor, kv.Key.Kind));
        (snapshot ??= new List<(string, float, bool)>())
            .Add((kv.Key.Kind, kv.Value, survivorHasIt));
      }
      if (snapshot is null) return;
      foreach (var (kind, value, survivorHasIt) in snapshot) {
        _values.Remove(new RegionValueKey(loser, kind));
        if (!survivorHasIt) {
          _values[new RegionValueKey(survivor, kind)] = value;
        }
      }
    }

    /// <summary>
    /// Copy every value entry from <paramref name="source"/> to a
    /// parallel entry keyed under <paramref name="destination"/>.
    /// Existing destination entries with the same kind are overwritten.
    /// Source entries are left in place -- the caller decides whether
    /// the source still exists.
    ///
    /// <para>Used by region-lifecycle handling: when a region splits,
    /// the orphan piece inherits the parent's values so accumulated
    /// values (region age, sustained-condition counters) reflect the
    /// land's actual history rather than resetting on every split.</para>
    /// </summary>
    public void Inherit(RegionId source, RegionId destination) {
      if (source == destination) return;
      List<KeyValuePair<string, float>>? toCopy = null;
      foreach (var kv in _values) {
        if (kv.Key.RegionId == source) {
          (toCopy ??= new List<KeyValuePair<string, float>>())
              .Add(new KeyValuePair<string, float>(kv.Key.Kind, kv.Value));
        }
      }
      if (toCopy is null) return;
      foreach (var kv in toCopy) {
        _values[new RegionValueKey(destination, kv.Key)] = kv.Value;
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
    public void RehydrateFrom(IEnumerable<KeyValuePair<RegionValueKey, float>> entries) {
      _values.Clear();
      foreach (var kv in entries) {
        _values[kv.Key] = kv.Value;
      }
    }

    #endregion

  }

}
