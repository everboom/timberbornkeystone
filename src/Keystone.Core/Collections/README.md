# Keystone.Core.Collections

General-purpose collection types used across the simulation core. Pure C#
(`netstandard2.1`), no Timberborn/Unity references.

## Types

- **`GroupedDictionary<TKey, TGroup, TValue>`** — a dictionary that maintains a
  secondary index grouping its keys by a caller-supplied projection
  (`Func<TKey, TGroup>`). "Give me every entry in group G" is O(entries-in-G)
  instead of O(whole store). It owns both the primary map and the group index
  behind a single `this[key]` / `Remove` path, so the two **cannot desync** —
  there's no mutation site that updates one without the other.

### Why it exists

Several persistence stores key data by a composite key but have a hot path that
needs every entry sharing a prefix of that key:

| Store              | `TKey`          | `TGroup` (projection)        | Hot query                |
| ------------------ | --------------- | ---------------------------- | ------------------------ |
| `ChunkValueStore`  | `ChunkValueKey` | `(RegionId, ChunkX, ChunkY)` | `EntriesForChunk`        |
| `ChunkDataStore`   | `ChunkCoord`    | `RegionId`                   | `EntriesForRegion`       |

Before this type, those queries filtered the full dictionary on every call,
turning into O(store) scans that dominated `ChunkReconciler`'s per-flush sweep
(per-rehome / per-drop value moves) and the debug overlay's per-frame cursor
read on a developed map. The hand-maintained alternative — a secondary index
sitting beside the primary dictionary — is correct but fragile: every mutation
site (`Set`/`Remove`/`Clear`/merge/inherit/prune/rehydrate — 7–8 per store) has
to remember to update both, and one missed site silently corrupts the index.
Folding both collections into one type makes that class of bug unrepresentable.

### Contract notes

- The projection must be **pure** (same key → same group every call). It is
  re-evaluated on each add/remove, not cached per entry.
- `Entries`, `Keys`, `EntriesForGroup`, and `KeysForGroup` return **live** views
  — do not mutate the dictionary while enumerating one. Callers that mutate
  based on a query snapshot the result into a list first (the same contract the
  raw `Dictionary` imposes).
- Not thread-safe (like `Dictionary`). Keystone's stores mutate the key-set on
  the Unity main thread only.
