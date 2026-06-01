# Keystone.Mod.Persistence

Mod-side glue for save/load and the per-tick value accumulators. All
singletons live in the `Game` Bindito context; bindings in
`KeystoneConfigurator`.

## Pieces

| Type | Role |
|---|---|
| `KeystonePersistence` | `ILoadableSingleton + IPostLoadableSingleton + ISaveableSingleton`. Single owner of all Timberborn loader/saver calls. `Load` reads the saved blob into a private `KeystoneSnapshot` buffer. `PostLoad` first calls `KeystoneSurveyor.EnsurePostLoaded()` (idempotent) so regions are freshly Indexed with deterministic ids, then drains the buffer onto `RegionService.RestoreCreatedAt`, `RegionValueStore.RehydrateFrom`, and `ChunkValueStore.RehydrateFrom` (re-homing saved chunk values to live regions by footprint via `RegionService.FindRegionByChunkFootprint`). `Save` reads live state into a fresh snapshot, encodes via `SnapshotCodec`. |
| `RegionValueLifecycleHandler` | `ILoadableSingleton`. Subscribes to `RegionService.RegionSplit` / `RegionMerged` / `RegionRemoved` and forwards them into `RegionValueStore` via `Inherit` / `MergeFrom` / `RemoveAllValuesFor`, so accumulated region values follow topology changes instead of leaking under a stale RegionId. **Region-level only** — per-chunk stores are no longer handled here; chunk data is re-bound by spatial footprint in `Keystone.Core.Persistence.ChunkReconciler` (see that folder's README). Merge policy is provisional (survivor-wins). |
| `RegionValueTicker` | `ITickableSingleton + IPostLoadableSingleton`. Each tick, increments `KnownValueKinds.RegionAgeDays` by `dt` (in days) for every live region. Anchors `_lastTotalDays` in PostLoad so the first tick doesn't see a giant dt. |

The per-chunk producer is `Keystone.Mod.Biomes.ChunkBiomeTicker` --
it owns the per-region chunk sweep and runs the Suitability and Maturity
updaters. Stale-chunk cleanup is no longer done there (the old per-cycle
`PruneToValid` was retired); chunks now follow region topology via
`ChunkReconciler`, run on each topology flush from `RegionUpdater.Flush`.

## Why no `OrderingAttribute`?

The generated API dump shows `Timberborn.SingletonSystem.OrderingAttribute`
exists, but the dump doesn't expose its constructor signature, and
neither the SDK sample mods nor the live game's reflectable code
demonstrate its usage in a form we can copy. Rather than guess, we
enforce the "surveyor PostLoads before persistence rehydrates"
ordering with a re-entrancy guard on `KeystoneSurveyor.PostLoad()` and
an explicit `EnsurePostLoaded()` hook that `KeystonePersistence.PostLoad`
calls first. Idempotent: if Timberborn happened to PostLoad the
surveyor first anyway, the second call short-circuits.

## What's NOT persisted

- Region member sets (`RegionService._surfaceToRegion`)
- Region neighbour graph
- Region size counter
- Ecology fields (`RegionEcologyField`)
- Surveyor surface map

All of the above are pure derivations of terrain + the freshly-Indexed
region map; rebuilding them on load is cheaper, simpler, and
guaranteed-consistent compared to persisting and risking divergence.

Per-chunk biome Suitability and Maturity values are persisted from
`ChunkValueStore` (see `Keystone.Core.Persistence`) under namespaced
kinds. At runtime the hot copy lives in the ordinal-indexed
`ChunkDataStore` and is synced forward into `ChunkValueStore` each tick;
on load, `ChunkDataStore` is rebuilt from the rehydrated `ChunkValueStore`
during warmup. Save/load is whatever the persistence layer writes for the
suitability-prefix and maturity-prefix
kinds.
