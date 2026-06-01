using System;
using System.Collections.Generic;

namespace Keystone.Core.Collections {

  /// <summary>
  /// A dictionary that maintains a secondary index grouping its keys by a
  /// caller-supplied projection, so "give me every entry in group G" is
  /// O(entries-in-G) instead of O(whole store).
  ///
  /// <para><b>Why this exists.</b> Several Keystone stores key data by a
  /// composite key (e.g. <c>(RegionId, ChunkX, ChunkY, Kind)</c>) but have a
  /// hot path that needs all entries sharing a prefix of that key (e.g. all
  /// kinds for one chunk, or all chunks for one region). Walking the full
  /// dictionary to filter — the obvious implementation — turns those reads
  /// into O(store) scans that dominate on a developed map. The alternative,
  /// a hand-maintained secondary index alongside the primary dictionary, is
  /// correct but fragile: every mutation site must remember to update both
  /// collections, and a single missed site silently desyncs the index. This
  /// type owns both collections behind one <see cref="this[TKey]"/> /
  /// <see cref="Remove"/> path, so desync is structurally impossible.</para>
  ///
  /// <para><b>Grouping is by projection, not a fixed key shape.</b> The
  /// <typeparamref name="TGroup"/> projection is supplied at construction, so
  /// different stores reuse this with different groupings — chunk values
  /// group by <c>(RegionId, ChunkX, ChunkY)</c> footprint; chunk data groups
  /// by <c>RegionId</c>. The projection must be a pure function of the key
  /// (same key → same group every time); it is re-evaluated on each add and
  /// remove rather than cached per entry.</para>
  ///
  /// <para><b>Invariant.</b> A key is present in the primary map iff it is in
  /// the set at its group here, and no group maps to an empty set (empty
  /// groups are pruned on their last key's removal). <see cref="Count"/>
  /// counts entries; <see cref="GroupCount"/> counts non-empty groups.</para>
  ///
  /// <para><b>Enumeration is live.</b> <see cref="Entries"/>,
  /// <see cref="Keys"/> and <see cref="EntriesForGroup"/> read the live
  /// collections — do not mutate the dictionary while enumerating one of
  /// them. Callers that need to mutate based on a query snapshot the result
  /// into a list first (the same contract the raw <c>Dictionary</c> imposes).</para>
  ///
  /// <para><b>Not thread-safe.</b> Like <see cref="Dictionary{TKey,TValue}"/>;
  /// callers serialise access (Keystone's stores mutate the key-set on the
  /// Unity main thread only).</para>
  /// </summary>
  /// <typeparam name="TKey">Full entry key. Must be non-null.</typeparam>
  /// <typeparam name="TGroup">Group key the secondary index buckets by. Must
  /// be non-null and have value equality (tuples / record structs).</typeparam>
  /// <typeparam name="TValue">Stored value.</typeparam>
  public sealed class GroupedDictionary<TKey, TGroup, TValue>
      where TKey : notnull
      where TGroup : notnull {

    #region Fields

    private readonly Func<TKey, TGroup> _grouper;
    private readonly Dictionary<TKey, TValue> _byKey = new();
    private readonly Dictionary<TGroup, HashSet<TKey>> _byGroup = new();

    #endregion

    #region Construction

    /// <summary>Create a grouped dictionary that buckets each key under
    /// <paramref name="grouper"/>'s result. The projection must be pure
    /// (deterministic in its key).</summary>
    public GroupedDictionary(Func<TKey, TGroup> grouper) {
      _grouper = grouper ?? throw new ArgumentNullException(nameof(grouper));
    }

    #endregion

    #region Properties

    /// <summary>Number of entries across all groups.</summary>
    public int Count => _byKey.Count;

    /// <summary>Number of non-empty groups.</summary>
    public int GroupCount => _byGroup.Count;

    /// <summary>Live view of all keys. Do not mutate the dictionary while
    /// enumerating this.</summary>
    public IEnumerable<TKey> Keys => _byKey.Keys;

    /// <summary>Live view of all entries. Do not mutate the dictionary while
    /// enumerating this.</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Entries => _byKey;

    #endregion

    #region Access

    /// <summary>Get or set the value for <paramref name="key"/>. The setter
    /// adds the key to its group (idempotent on overwrite); the getter throws
    /// if the key is absent, matching <see cref="Dictionary{TKey,TValue}"/>.</summary>
    public TValue this[TKey key] {
      get => _byKey[key];
      set {
        _byKey[key] = value;
        AddToGroup(key);
      }
    }

    /// <summary>Try to read the value for <paramref name="key"/>.</summary>
    public bool TryGetValue(TKey key, out TValue value) => _byKey.TryGetValue(key, out value);

    /// <summary>True if <paramref name="key"/> has an entry.</summary>
    public bool ContainsKey(TKey key) => _byKey.ContainsKey(key);

    /// <summary>Remove <paramref name="key"/>'s entry. Returns true if it
    /// existed. Prunes the group if this was its last key.</summary>
    public bool Remove(TKey key) {
      if (!_byKey.Remove(key)) return false;
      RemoveFromGroup(key);
      return true;
    }

    /// <summary>Drop every entry and every group.</summary>
    public void Clear() {
      _byKey.Clear();
      _byGroup.Clear();
    }

    #endregion

    #region Group queries

    /// <summary>
    /// Yield every entry whose key projects to <paramref name="group"/>.
    /// O(entries-in-group). Empty (and never-seen) groups yield nothing.
    /// Live — do not mutate the dictionary while enumerating.
    /// </summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> EntriesForGroup(TGroup group) {
      if (!_byGroup.TryGetValue(group, out var keys)) yield break;
      foreach (var key in keys) {
        // The invariant guarantees the key is present; index directly so a
        // bug surfaces as a KeyNotFoundException rather than silent skip.
        yield return new KeyValuePair<TKey, TValue>(key, _byKey[key]);
      }
    }

    /// <summary>
    /// The live key set for <paramref name="group"/>, or <c>null</c> if the
    /// group has no entries. Read-only; do not mutate. Cheaper than
    /// <see cref="EntriesForGroup"/> when callers only need keys.
    /// </summary>
    public IReadOnlyCollection<TKey>? KeysForGroup(TGroup group) =>
        _byGroup.TryGetValue(group, out var keys) ? keys : null;

    #endregion

    #region Index maintenance

    private void AddToGroup(TKey key) {
      var group = _grouper(key);
      if (!_byGroup.TryGetValue(group, out var set)) {
        set = new HashSet<TKey>();
        _byGroup[group] = set;
      }
      set.Add(key);
    }

    private void RemoveFromGroup(TKey key) {
      var group = _grouper(key);
      if (_byGroup.TryGetValue(group, out var set) && set.Remove(key) && set.Count == 0) {
        _byGroup.Remove(group);
      }
    }

    #endregion

  }

}
