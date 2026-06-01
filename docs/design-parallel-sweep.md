# Design: Parallel Sweep Architecture

> Status: draft, updated 2026-05-27.  
> Context: v0.5 planning — moving rolling-sweep compute off the main thread.

## Problem

Keystone's rolling sweep tickers amortize per-chunk biome computation across many ticks to avoid frame stalls. This works but limits update cadence (hourly at best) and consumes main-thread budget. The mod already takes as much or more tick time as the base game itself — and v0.5 adds two new expensive systems (per-surface water-distance scanning, map-wide sea connectivity analysis) that would push the main-thread budget past what amortization alone can absorb. Timberborn provides `IParallelizer` and `IParallelTickableSingleton` for exactly this class of work.

## Goals

1. Move the expensive per-chunk compute (biome scoring, suitability drift, maturity integration) off the main thread.
2. Preserve the rolling-sweep amortization pattern — batches per tick, not the full map in one tick — but run the batches on worker threads.
3. Make the data layer fast for parallel-path access (no string hashing, no dictionary probes on the hot path).
4. Preserve the cross-mod API: other mods can register named value slots and read/write chunk data by name.
5. Keep persistence stable across sessions where mods are added or removed.

## Non-goals

- Removing the `ChunkValueStore` dictionary entirely. It stays as the mod-API / persistence / debug-panel layer.
- Changing the biome scoring formulas or ecology field structure.
- Parallelizing non-sweep systems (entity spawning, fauna agents, etc.).

## Architecture

### Two-layer data model

```
┌──────────────────────────────────────────────────────┐
│  Parallel layer (hot path)                           │
│                                                      │
│  ChunkDataStore                                      │
│    key: (RegionId, ChunkX, ChunkY)                   │
│    value: ChunkData { float[] values }               │
│           indexed by slot ordinal                     │
│                                                      │
│  Written by: background worker threads (sweep)       │
│  Read by: main-thread gameplay code (spawns, overlay) │
├──────────────────────────────────────────────────────┤
│  Public layer (mod API, persistence, debug)           │
│                                                      │
│  ChunkValueStore (existing)                          │
│    key: (RegionId, ChunkX, ChunkY, Kind string)      │
│    value: float                                      │
│                                                      │
│  Synced from parallel layer during Tick() on main    │
│  thread. One tick stale relative to parallel layer.  │
└──────────────────────────────────────────────────────┘
```

### Value registry

A session-scoped registry mapping named value kinds to ordinal indices.

- **Registration phase** (game startup, before first tick): Keystone registers its biome slots (e.g. `"keystone.chunk.suitability.Forest"`, `"keystone.chunk.maturity.Forest"`). Other mods can register their own slots via a public API. Each registration returns an `int` ordinal.
- **Freeze**: once the game session begins, the registry locks. No new registrations. The total slot count determines the `float[]` size for every chunk.
- **Ordinal access**: all hot-path code uses ordinals. No string operations at runtime.
- **Persistence**: values are serialized by name (not ordinal). On load, names are resolved against the current session's registry. Unknown names are discarded with a warning; missing slots get default values (0). This handles mods being added or removed between save and load.

### Chunk data structure

```
ChunkData {
    float[] Values;   // length = registry.SlotCount, indexed by ordinal
}
```

The suitability/maturity pair for each biome occupies two adjacent slots. The "near water distance" field (and any future per-chunk signals) occupies additional slots registered during startup.

### Chunk store

A dictionary keyed by `(RegionId, ChunkX, ChunkY)` holding `ChunkData` instances. The key set is managed by topology events (region creation, split, merge, removal) — same lifecycle as today's `ChunkValueStore`.

Between topology events, the key set is stable. Background threads write to `ChunkData.Values[]` at non-overlapping ordinal indices across chunks. Main-thread code reads from the same arrays, accepting one-tick staleness.

### Execution model

The sweep singleton implements both `ITickableSingleton` and `IParallelTickableSingleton`:

```
Engine calls Wait()           ← previous tick's parallel batch joins
                              ← guaranteed: parallel layer is quiescent

Engine calls Tick()           ← main thread
  1. Sync completed results from parallel layer → ChunkValueStore
  2. Main-thread bookkeeping (prune, merge, lifecycle)
  3. Advance rolling-sweep cursor (decide next batch)

Engine calls StartParallelTick()  ← main thread
  1. Capture game-state inputs for the batch (ecology fields, water depths)
  2. Schedule batch via IParallelizer.Schedule()
  3. Background threads start computing immediately
```

The rolling sweep still controls which chunks are in each tick's batch and how fast the cycle progresses. The only change is that `ProcessUnit()` runs on worker threads instead of the main thread.

### Sync to public layer

During `Tick()`, after `Wait()` has guaranteed the parallel layer is quiescent:

1. Walk the chunks that were in the previous tick's batch.
2. For each chunk, for each registered slot, write the value to `ChunkValueStore` by name.
3. Cost: O(batch_size × slot_count) dictionary writes per tick. Amortized across the cycle, same as the sweep itself.

This keeps the mod API, persistence layer, and debug panel consistent without ever touching them from a background thread.

### Thread safety argument

- **Parallel layer writes** happen only during the parallel phase (between `StartParallelTick()` and next `Wait()`). Each chunk in the batch is processed by exactly one worker. Different workers write to different `ChunkData` instances (different keys in the chunk store) — no shared mutable state.
- **Parallel layer reads** from main-thread code happen only during `Tick()` (after `Wait()`). The parallel layer is quiescent. No concurrent access.
- **Game-state reads** during the parallel phase — see [Service thread safety](#service-thread-safety) below for the full analysis. Summary: scalar field services (water, moisture, contamination) are safe; entity enumeration and planting mark rect queries are not.
- **ChunkValueStore** is only accessed from the main thread (sync writes in `Tick()`, mod reads any time). No thread-safety concerns.
- **Topology events** (region split/merge/remove) modify the chunk store's key set. These fire on the main thread during `Tick()`. The parallel phase never adds or removes keys — it only writes values for existing keys.

### Service thread safety

Investigation of the Timberborn services Keystone's sweep tickers read during `ProcessUnit`. The question: which can be called from `IParallelizer` worker threads, and which need snapshotting?

#### Safe off-thread (no snapshotting needed)

| Port | Timberborn service | Backing store | Why safe |
|---|---|---|---|
| `IWaterQuery` | `IThreadSafeWaterMap` | Snapshot arrays (`float[]`, `byte[]`) | Explicitly designed for parallel reads. Named `_threadSafe*`. Timberborn's own `SoilMoistureSimulator` and `SoilContaminationSimulator` (both `IParallelTickableSingleton`) read from it during their parallel phase. Snapshots are copied from `WaterSimulator` during the sequential `Tick()` phase and stable throughout the parallel window. |
| `IMoistureQuery` | `ISoilMoistureService` | `_threadSafeMoistureLevels: float[]` | Same snapshot pattern. Flat array reads via `CellToIndex()` (pure arithmetic). `Array.Resize` only runs during sequential `Tick()`, never during the parallel phase. |
| `IContaminationQuery` | `ISoilContaminationService` | `_threadSafeContaminationLevels: float[]` | Structurally identical to moisture. Same snapshot-copy pattern, same timing guarantees. |

All three services use Timberborn's `_threadSafe*` naming convention and follow the same pattern: a private snapshot array is populated from the authoritative source during the sequential `Tick()` phase (after `FinishParallelTick()` has joined all worker threads), then read by `IParallelTickableSingleton` workers during the parallel phase. The snapshot is stable for the entire parallel window by construction.

`MapIndexService.CellToIndex()` (shared dependency for all three) is `(y+1)*Stride + x + 1` — pure arithmetic on fields set once at `Load()` and never mutated.

#### Unsafe — needs snapshotting

| Port | Timberborn service | Risk | Severity |
|---|---|---|---|
| `INaturalResourceEnumerator` | `IBlockService.GetObjectsAt()` | **Three concurrent-access hazards:** (1) `WorldBlock._blockObjects` is a `List<BlockObject>` shared between the returned struct copy and the live array — main thread can `Add()`/`Remove()` while a worker iterates. (2) `BlockObject.GetComponent<T>()` → `ComponentCache.GetCachedComponent<T>()` → `TypeIndexMap.CacheType<T>()` mutates a plain `Dictionary<Type, object>` on first lookup for a type. (3) `BlockObject`'s implicit `operator bool` calls Unity's `Object.CompareBaseObjects()`, which accesses the native object tracking table. | **Critical** — data corruption, crashes, Unity thread assertion failures. |
| `IPlantingMarkQuery.MarksInTileRect()` | `PlantingMarkAdapter._buckets` | Iterates a `Dictionary<_,List<_>>` that the main thread mutates via EventBus callbacks (`OnPlantingCoordinatesSet/Unset`). `Dictionary` + `List` concurrent read/write is undefined behavior. | **High** — corruption, exceptions. |

**Note:** `IPlantingMarkQuery.IsMarked()` and `MarkedSpecies()` are effectively safe — they read single elements from `PlantingMap._resourceIds[x,y,z]` (`string[,,]`), which is an atomic reference read. Worst case is a momentarily stale answer, not corruption.

#### Input-capture strategy

The split between main thread and workers follows the adapter boundary, not the service-safety boundary. Game-state reads produce value-type inputs on the main thread; pure compute consumes those inputs on workers. No raw entity or block-service snapshotting is needed.

**Phase 1 (`ChunkBiomeTicker`):** `ChunkBiomeAdapter.Build()` runs on the main thread during `Tick()`, producing a `ChunkBiomeInputs` struct per chunk. The struct contains only floats and bools — plain value type, no references to game objects. Workers receive the pre-built `ChunkBiomeInputs[]` array and run `SuitabilityUpdater.Tick()` + `MaturityUpdater.Tick()` against it. The adapter is the snapshot.

**Phase 2 (`EcologyFieldUpdater`):** entity enumeration runs on the main thread during `Tick()`, writing per-surface entity counts to ecology field channels. Scalar channel reads (water, moisture, contamination) move to workers, reading directly from thread-safe snapshot arrays. The entity counts are stable in the field between sweeps — workers read them without synchronization.

## Migration priority

The order is driven by the split between game-state reads and pure compute. Game-state reads (entity enumeration, block service, planting marks) stay on the main thread — they access thread-unsafe Timberborn internals and are not the bottleneck. Pure compute (biome scoring, drift dynamics, distance transforms) moves to worker threads — it's where the budget goes and where new systems need room.

The natural snapshot boundary is the adapter output: `ChunkBiomeInputs` for biome compute, ecology field channels for distance scanning. These are plain value types already produced on the main thread. No raw entity or block-service snapshotting needed.

### Phase 1: `ChunkBiomeTicker`

Per-chunk biome compute: suitability drift, maturity integration, distance-from-shore. Per-chunk independent, pure math on `ChunkData` arrays.

**Why first:** cleanest parallelization target. The updaters (`BiomeSuitabilityUpdater.Tick`, `BiomeMaturityUpdater.Tick`) are already pure functions on `ChunkData` + `ChunkBiomeInputs`. No Unity APIs, no entity access, no game-state reads. The parallel data layer (value registry + ordinal-indexed `ChunkData`) is already in place.

**Migration:**
- `ChunkBiomeAdapter.Build()` stays on main thread during `Tick()`. It reads `RegionEcologyField` channels (safe — written by `EcologyFieldUpdater` during its main-thread sweep, stable between sweeps) but also calls `IPlantingMarkQuery.MarksInTileRect()` and `INaturalResourceAtTileQuery.HasNaturalResourceAt()` (unsafe — `IBlockService` internals). Building inputs for the batch on the main thread and handing the `ChunkBiomeInputs[]` array to workers avoids all thread-safety concerns.
- `SuitabilityUpdater.Tick()` + `MaturityUpdater.Tick()` run on workers. Each chunk in the batch gets its own `ChunkData` instance — no shared mutable state between workers.
- Per-instance `_speciesCountScratch` in `ChunkBiomeAdapter` is only used during the main-thread `Build()` call, so no per-worker copies needed.
- `ChunkValueStore` sync stays on the main thread during `Tick()`, same as today.
- **Distance-from-shore** is a natural addition here: a per-chunk BFS/distance transform reading water depth from `IThreadSafeWaterMap` (confirmed safe off-thread) and writing to a new `ChunkData` slot. Runs as an additional worker-thread pass after suitability/maturity, same batch.

### Phase 2: `EcologyFieldUpdater` (partial)

The heaviest existing sweep. Per-chunk work walks every surface, probes water/moisture/contamination via port adapters, and enumerates natural resources vertically through the block column.

**What moves to workers:** the scalar channel reads (water depth, moisture, contamination) per surface. These use Timberborn's `_threadSafe*` snapshot arrays, confirmed safe for parallel reads. This is the bulk of the per-surface cost.

**What stays on main thread:** entity enumeration via `INaturalResourceEnumerator` (routes through `IBlockService.GetObjectsAt()` — three concurrent-access hazards, see [Service thread safety](#service-thread-safety)). This is cheaper than the scalar reads and is already amortized by the rolling sweep.

**Migration:**
- Split `ProcessUnit()` into a main-thread entity-enumeration pass and a worker-thread scalar-channel pass. The entity pass runs during `Tick()`, writes per-surface entity counts to a batch buffer. The scalar pass runs during `StartParallelTick()`, reads from thread-safe water/moisture/contamination services, writes to ecology field channels.
- Per-instance scratch state (`_scratchScalars`, `_scratchEntities`, `_scratchSeen`) must become per-worker-thread for the scalar pass (thread-local or allocated per batch item).
- New water-distance scan is a natural extension of the scalar pass (per-surface, reads water state from `IThreadSafeWaterMap`, writes distance to ecology field).

### Phase 3: Sea connectivity (new system)

Map-wide analysis of which water bodies are connected, needed for the sea/ocean biome. Essentially a flood-fill or union-find over the water column grid: two tiles with water depth > 0 that share an edge (or are connected through a chain of such tiles) belong to the same water body. The result is a per-column water-body ID and per-body metadata (total area, average depth, is-open-to-map-edge = "sea" vs enclosed = "lake").

**Characteristics:**
- Heavy at first computation (full-map flood fill), cheap to maintain incrementally on water-state changes (re-flood from changed columns).
- Natural fit for `IParallelizer` loop tasks partitioned by row (same pattern as Timberborn's water sim).
- Output is a flat per-column array of water-body IDs — consumers (biome scoring, sea biome rules) index directly.
- Update cadence: event-driven on significant water-state changes (new water source, dam built/removed, drought), not per-tick. Between events the cached connectivity is stable.

### Not parallelized

| Ticker | Reason |
|---|---|
| `ChunkRulesApplier` | Biome sampling is parallelizable but handler dispatch (`IRuleHandler.OnUnit`) spawns/despawns flora via Unity `Instantiate` — main-thread-only. A compute/dispatch split is possible but lower priority. |
| `ChunkClusterTicker` | Incremental fold into a shared shadow index. Not naturally parallelizable without restructuring the fold. |
| `FaunaCycleTicker` | Entity lifecycle, frustum checks (`Camera.main`), despawn calls. Main-thread bound. |
| `RegionValueTicker` | Adds dt to ~500 region-age counters. Negligible cost. |
| `KeystoneDecorationRegistry` | Controller ticks manipulate Unity GameObjects. Main-thread-only. |

## Open questions

1. **Sweep cadence.** With compute off the main thread, the cycle can be faster. How fast? Full map every N ticks? Adaptive based on how much changed?
2. ~~**Game-state snapshot scope.**~~ **Resolved.** The split follows the adapter boundary: game-state reads (entity enumeration, planting marks, block service) stay on the main thread and produce value-type inputs; pure compute runs on workers consuming those inputs. Scalar field services (water, moisture, contamination) are confirmed safe off-thread via Timberborn's `_threadSafe*` snapshot pattern. No raw entity or block-service snapshotting needed.
3. **Water-distance field placement.** Computed during the `EcologyFieldUpdater` sweep (per-surface, checks neighbors for water depth > 0, writes distance 0/1/2 to the ecology field) or as a separate slot in the parallel data layer computed by the biome ticker? The former is more natural (it's a surface-level signal); the latter avoids growing the field updater further. Water depth reads are confirmed safe off-thread via `IThreadSafeWaterMap`, so the neighbor scan can run on worker threads either way.
4. **Sea connectivity trigger.** Pure event-driven (re-flood on water-state change events), or periodic with a long cadence (every N game-hours) as a safety net against missed events?
5. **`ChunkRulesApplier` compute/dispatch split.** Worth doing in v0.5, or defer until it becomes a bottleneck?
