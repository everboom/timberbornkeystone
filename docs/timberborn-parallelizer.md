# Timberborn Parallelizer — Decompilation Report

> Source: decompiled vanilla C# at `D:\Timberborn Assets\Scripts\`, game version v1.0.13.1.

## Overview

Timberborn ships a work-scheduling system in `Timberborn.Multithreading` integrated with the tick system via `Timberborn.TickSystem`. Four vanilla systems use it: the water flow simulator, water renderer, soil moisture simulator, and soil contamination simulator.

## Infrastructure

### Parallelizer

- **Thread count:** `Math.Clamp(PhysicalProcessorCount - 1, 3, 8)` — always 3–8 background threads, spawned at `Load()` and kept alive for the session.
- **Scheduling:** task-graph with explicit dependencies. `Schedule()` returns a `ParallelizerHandle`; subsequent tasks can depend on it. Tasks with met prerequisites enter a `LockingQueue` for worker pickup.
- **Loop tasks:** `IParallelizerLoopTask` scheduled with `Schedule(from, to, batchSize, task)`. The `LoopTaskRunner` splits the range into batches; each batch is enqueued separately, and workers claim the next batch index atomically.
- **Wait:** spin-waits on `_pendingTasks == 0`. Surfaces any captured exceptions on the main thread.
- **Exception handling:** worker exceptions are caught and enqueued into a `ConcurrentQueue<ParallelizerExceptionLog>`. `Wait()` or `ThrowIfHasExceptions()` re-throws them on the main thread.

### Task interfaces

- `IParallelizerSingleTask` — `void Run()`. One-shot work unit.
- `IParallelizerLoopTask` — `void Run(int iteration)`. Called once per batch iteration.

### Tick orchestration (`TickableSingletonService.TickAll()`)

```
1. FinishParallelTick()     — Wait() for PREVIOUS tick's parallel work
2. TickSingletons()         — all ITickableSingleton.Tick() sequentially on main thread
3. StartParallelTick()      — all IParallelTickableSingleton.StartParallelTick() on main thread;
                              workers start executing as dependencies are met
```

This is a **pipelined double-buffer**: parallel work from tick N runs concurrently with tick N+1's main-thread work. Results are one tick stale by design.

### TickOnlyArray lifecycle guard

`TickOnlyArray<T>` enforces access rules:
- `GetSpan()` / `GetReadOnlySpan()` — allowed only during `Tick()` or `Load()` (main thread, no parallel work running).
- `GetArray()` — allowed only during `StartParallelTick()` (main thread, about to hand arrays to background threads).

Prevents accidental cross-phase access at compile-ish time.

## Vanilla parallel systems

| Singleton | Domain |
|---|---|
| `WaterSimulator` | Water flow (2 substeps/tick) |
| `WaterRenderer` | Water texture updates |
| `SoilMoistureSimulator` | Moisture propagation |
| `SoilContaminationSimulator` | Contamination propagation |

### Water simulation pipeline (per substep)

| Step | Task | Type | Batch | Reads | Writes |
|---|---|---|---|---|---|
| 1 | ClearBuffersTask | Single | — | — | Scratch buffers (Array.Clear) |
| 2 | OutflowsUpdateTask | Loop | 1 row | WaterColumn[], ColumnOutflows[] | baseLevelFlows[], directedFlows[] |
| 3 | WaterParametersUpdateTask | Loop | 1 row | baseLevelFlows[], directedFlows[] | WaterColumn[].WaterDepth/Overflow, ColumnOutflows[] |
| 4 | SimulateContaminationTask | Loop | 1 row | WaterColumn[], ColumnOutflows[] | contaminationsBuffer[], baseLevelDiffusions[] |
| 5 | UpdateContaminationTask | Loop | 3 rows | contaminationsBuffer[], baseLevelDiffusions[] | WaterColumn[].Contamination |
| 6 | UpdateWaterSourcesTask | Single | — | waterSources, WaterColumn[] | WaterColumn[] |

Steps are chained via `ContinueWith()` — step N+1 starts only after step N completes across all batches.

### Soil moisture pipeline

| Step | Task | Type | Reads | Writes |
|---|---|---|---|---|
| 1 | MoistureDataPreparationTask | Single | moistureLevels[] | lastTickMoistureLevels[] (snapshot copy) |
| 2 | WateredNeighborsCountingTask | Loop | ReadOnlyWaterColumn[], columnCounts | wateredNeighbours[] |
| 3 | ClusterSaturationCalculationTask | Loop | wateredNeighbours[] (ReadOnly) | clusterSaturations[] |
| 4 | WaterEvaporationCalculationTask | Loop | clusterSaturations[] (ReadOnly) | evaporationModifiers[] |
| 5 | MoistureCalculationTask | Loop | lastTickMoistureLevels[] (ReadOnly, snapshot) | moistureLevels[] |

### Soil contamination pipeline

Same pattern: snapshot-then-compute. Copies `contaminationCandidates[]` into `lastTickContaminationCandidates[]` as step 1, then parallel compute reads the snapshot and writes to the live array.

## Thread safety patterns

All safety is **structural** — no locks, atomics, or synchronization primitives in any simulation task.

### 1. Row-striped spatial partitioning

All loop tasks iterate by Y row. Each worker processes one or more complete rows. Writes are confined to the current row's cells. Reads can reach neighboring rows (e.g. flow neighbors) because the previous pipeline step is complete before the next starts.

### 2. Snapshot / double-buffer

- **Within a system:** soil moisture and contamination copy their state into a snapshot array as step 1 (`lastTickMoistureLevels`, `lastTickContaminationCandidates`). All subsequent steps read from the snapshot (wrapped in `ReadOnlyArray<T>`) and write to the live array.
- **Across systems:** `ThreadSafeWaterMap` copies the water sim's arrays during `Tick()` (main thread, after Wait). Soil moisture and contamination sims read from this snapshot, not from the water sim directly.

### 3. Sequential pipeline via ContinueWith

Tasks within a system chain with `ParallelizerHandle` dependencies. No two steps overlap. Thread safety between steps is guaranteed by the dependency chain.

### 4. ReadOnlyArray<T> as convention

`ReadOnlyArray<T>` wraps a raw `T[]` and exposes only `ref readonly T this[int]`. Task structs receive read data as `ReadOnlyArray<T>` and write targets as `T[]`. Compile-time convention, not runtime enforcement (same underlying array).

### 5. Struct tasks capture data by value

All task types are `readonly struct`. When `Schedule()` is called on the main thread, the struct (containing raw `T[]` references) is copied into the runner. Arrays are not resized during parallel execution (resize only in `Tick()`, after Wait). The main thread doesn't touch the arrays between `StartScheduling()` and `Wait()`.

## Key takeaways for Keystone

1. **No runtime synchronization.** All safety comes from data ownership (spatial partitioning), temporal ordering (dependency chains), and double-buffering.
2. **Flat arrays, not dictionaries.** Every parallel system operates on `T[]` indexed by spatial coordinate. No hashing, no string keys, no heap allocation per access.
3. **One-tick pipeline latency is built in.** The engine's `Wait() → Tick() → StartParallelTick()` sequence means results are always one tick stale. Systems accept this.
4. **TickOnlyArray prevents accidents.** Access is gated by lifecycle phase — you can't accidentally read a parallel-mutated array from the main thread during the wrong phase.
