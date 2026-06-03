# Keystone.Core.Persistence

Pure-Core types backing Keystone's save/load. The Mod-side singleton
`Keystone.Mod.Persistence.KeystonePersistence` owns the actual
`ISingletonSaver` / `ISingletonLoader` calls; everything in this folder
is engine-agnostic data and translation logic, drivable from MSTest
without touching Timberborn.

## Save shape

The wire format is **native parallel lists only** (`int`, `float`,
`string`). No `IValueSerializer<T>` -- everything decomposes into
primitives at the seam, so a save written by Keystone is something any
Timberborn loader could read without our types. This keeps the format
honest about what's actually persisted and avoids the version-drift
problems custom serializers carry.

Region member sets, neighbour graphs, ecology fields, and surveyor
state are **not persisted** -- they're pure derivations that the
surveyor + region service rebuild deterministically at PostLoad.

## Pieces

| Type | Role |
|---|---|
| `RegionPersistedRecord` | One region's clock-stamp data (id, `CreatedAt`, `WeatherAtCreation`, `TotalDaysAtCreation`). |
| `RegionValueKey(RegionId, string)` | Composite key for the region-level value store. Constructor enforces non-empty `Kind`. |
| `RegionValueStore` | Keyed store of per-region named float values. Publicly mutable (faction-expansion mods can read/write their own kinds without going through Mod 1 contracts). No mod-id enforcement; pick a unique prefix and stick to it. Region-lifecycle changes (split/merge/remove) flow through its `Inherit`/`MergeFrom`/`RemoveAllValuesFor`, driven by `RegionValueLifecycleHandler`. |
| `ChunkValueKey` | `(RegionId, ChunkX, ChunkY, string)` key. Chunk coordinates are global (`absoluteTileX / RegionEcologyField.ChunkSize`), not bbox-relative -- including `RegionId` keeps Z-stacked regions sharing an XY footprint insulated. |
| `ChunkValueStore` | Keyed store of per-chunk named float values. Mirrors `RegionValueStore`'s shape with one extra spatial dimension. `SortedSnapshot()` returns a freshly-allocated, sorted copy (RegionId, ChunkX, ChunkY, Kind) -- the order `SnapshotCodec` uses on save; **not free**, don't call per-frame. `EntriesForChunk(region, cx, cy)` walks the dictionary once and yields just the matches; use this for per-cursor diagnostics. Mod 1's chunk re-binding now goes through `ChunkReconciler` (below); the store's own `Inherit`/`MergeFrom`/`RemoveAllValuesFor` are retained only as external-mod API (no Mod 1 caller). |
| `ChunkValueRegistry` | Maps chunk-value kind names → ordinal slot indices for the `ChunkDataStore` arrays. Open for registration during Load; `Freeze()` locks it at warmup, fixing `SlotCount`. |
| `KnownValueKinds` | Mod 1's own value-kind constants. `RegionAgeDays` (per-region day accumulator), `ChunkSuitabilityPrefix` (`"keystone.chunk.suitability."`; short-term Suitability channel) and `ChunkMaturityPrefix` (`"keystone.chunk.maturity."`; long-term Maturity channel). Full keys for the biome channels built via `BiomeValueKinds.ForSuitability(biome)` / `BiomeValueKinds.ForMaturity(biome)`. |
| `KeystoneSnapshot` | Mutable buffer used by the persistence singleton's Load -> PostLoad handoff. Drained immediately after. |
| `SnapshotPayload` | Immutable record of parallel lists, the over-the-wire shape. Carries regions, region values, and chunk values. |
| `SnapshotCodec` | `Encode(KeystoneSnapshot) -> SnapshotPayload` and `Decode(SnapshotPayload) -> KeystoneSnapshot`. Sorts at encode by `RegionId.Value` (then key ordinal) so equivalent state produces byte-stable saves. |
| `ChunkData` / `ChunkDataStore` | Parallel ordinal-indexed hot layer (`float[]` per chunk, keyed by `ChunkCoord`) synced forward into `ChunkValueStore` each tick. `ChunkData.Z` carries the chunk's layer explicitly so it can re-bind after its region dies (see reconciliation below). |
| `IChunkOwnerQuery` | Port: "which live region owns this chunk's `(X, Y, Z)` footprint?" Z-strict. Production impl `RegionChunkOwnerQuery` wraps `RegionService.FindRegionByChunkFootprint`; `PrecomputedChunkOwnerQuery` wraps a one-pass index for the full sweep; both fakeable for tests. |
| `ChunkReconciler` | Re-binds chunk data to the region that physically owns each chunk's `(X, Y, Z)` footprint, across region-id churn. Drives the post-topology-flush sweep and the manual full-sweep self-test (NOT save-load — see reconciliation note below). Returns `ChunkReconcileResult` counts + a neutral `ChunkReconcileOutcome` classification. |

## Chunk reconciliation

Per-chunk data is keyed by `(RegionId, X, Y)`, but RegionIds are **not
stable** — a terrain edit or building can split, merge, or kill a region,
stranding or destroying the chunk data keyed under the old id (visible
in-game as an entire cluster of chunks losing all its Maturity at once).
`ChunkReconciler` makes the `(X, Y, Z)` footprint the source of truth and
the RegionId a derived binding that gets reconciled. Each `ChunkData`
carries its `Z` so it can re-home even after its original region is gone.

Per chunk, `ReconcileFromDataStore` looks up the owning region at the
chunk's Z (`IChunkOwnerQuery`, Z-strict) and: keeps it if already correct,
re-keys it onto the new owner in both stores if a different live region
owns the footprint, or **drops** it if no region owns that footprint at
that Z. The drop is the accepted localized loss — only where topology
actually changed, only after the best-effort re-home failed. Collisions
(two regions owned the same chunk, one re-homing onto the other) resolve
**High beats Low**: the record with the greater value wins, no per-slot
blending. (TODO: the real tiebreaker should respect biome precedence
Badwater ▸ Dry ▸ irrigated ▸ wet; magnitude is the simple stand-in.)

Callers: the post-`ProcessChanges` flush sweep in `RegionUpdater.Flush`
(scoped to the regions that flush touched) and the manual map-wide
self-test (`scope: null`, `ChunkReconciliationSelfTest`). Landing those
**retired** the destructive paths this supersedes — the biome ticker's
per-cycle `PruneToValid` and the chunk-store `Inherit` / `MergeFrom` /
`RemoveAllValuesFor` lifecycle forwarding (region-level values still flow
through `RegionValueLifecycleHandler`).

**Save-load rehydration is a separate re-binding path.**
`KeystonePersistence.PostLoadInner` re-homes saved chunk values by calling
`RegionService.FindRegionByChunkFootprint` directly — it does *not* run
through `ChunkReconciler`, so the two paths' collision/drop policies are
independent. Unifying them (routing load through the reconciler) is open.

**Footprint-orphan sweep at save.** The reconciler walks the *data* store,
but the *value* store is what's persisted. A chunk can sit in the value
store with no live data-store counterpart (e.g. a loaded chunk the rolling
biome ticker hasn't re-touched), so when terrain under a *surviving* region
is removed, the data-store reconcile never sees that chunk — it lingers,
keyed under a still-live region, and gets written. On the next load it
re-binds by footprint, finds no owner, and is dropped + reported as lost.
`KeystonePersistence.Save` closes this by sweeping every chunk value before
serialising: using a one-pass `BuildChunkFootprintOwnerIndex`, it drops any
chunk whose `(X, Y, Z)` footprint has no live region — the same predicate
the load path uses, applied where full live context (each region's Z) is
still in hand. Result: saves are self-cleaning, so legitimate topology loss
never resurfaces at the next load. Swept counts log at Verbose (expected
cleanup), distinct from the `Warn` for canonical-id misses.

**Load reports maturity loss by distinct chunk, not value rows.**
`PostLoadInner` splits dropped chunks into maturity-bearing vs
suitability-only (mirroring the reconciler's split): only a non-zero
`keystone.chunk.maturity.*` drop is real, unrecoverable loss. The startup
warning (`SnapshotStartupCheck`) alarms on `DroppedChunkAreasWithMaturity`
(distinct chunk areas) above a small floor, not the raw dropped value-row
count — one destroyed chunk is ~10-20 rows (suitability + maturity per
biome), so the old row count over-reported a single removed chunk as a
cluster. Suitability re-derives within a few ticks, so suitability-only
drops are benign and logged only.

## Schema versioning

`SnapshotPayload.SchemaVersion` is written to every save. Decode
copies it through. The Mod-side `KeystonePersistence`:
- treats missing version as 1 (first release),
- equals current -> loads,
- higher than current -> logs `Debug.LogWarning` and loads
  best-effort (don't crash).
