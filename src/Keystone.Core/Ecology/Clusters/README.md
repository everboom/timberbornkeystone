# Ecology / Clusters

A data layer sitting above `Ecology/Fields` that groups adjacent
chunks into **biome clusters** — connected runs of chunks sharing the
same dominant (Suitability-argmax) biome whose Maturity has cleared a
global threshold. Chunks that don't qualify are absent from the index.

**Non-qualifying conditions (exhaustive):**
- No dominant biome (every biome's Suitability is 0).
- Dominant biome's Maturity below the rebuild threshold.
- Dominant biome not on the `ClusterableBiomes` whitelist. Today
  excludes the three aggressors (Badwater, Contaminated, Dry) plus
  Cave and Monoculture — biomes with no current consumer. Whitelist
  by design, semantically distinct from `BiomesByAggressorTier` (see
  `ChunkClusterIndex.ClusterableBiomes` docstring).
- Resulting connected component has only one chunk. Single-chunk
  clusters carry no signal beyond what a single-chunk dominance query
  already supplies, so they're dropped post-union-find to avoid the
  allocation and bookkeeping.

**Fast paths during rebuild:**
- Region-level early-out: regions whose ecology field has fewer than
  two valid chunks are skipped before `ProcessRegion` runs.
- Chunk-level filter: chunks whose dominant biome is non-clusterable
  return `(null, 0)` from the qualification check, equivalent to the
  "no dominant biome" path.
- Singleton drop: a per-region root-size tally pass after union-find
  lets the collect pass skip emitting any cluster whose connected
  component has only one chunk.

The intended consumers are fauna agents and any future system that
asks "how large is this biome here, and which chunks belong to it?":
fish spawn capacity, alive-time biome viability checks, pathfinding
universe scoping. The cluster layer is consumer-agnostic — it
exposes chunk membership and aggregate tile count; per-tile
predicates (water depth, walkability) stay with the consumer.

## Files

| File | Purpose |
|---|---|
| (key type) | Uses `Keystone.Core.Tiles.ChunkCoord` (`RegionId, GlobalChunkX, GlobalChunkY`) — the same chunk-identity record the rules scheduler keys on. No parallel type here. |
| `ChunkClusterId.cs` | Opaque cluster id, valid only within one rebuild snapshot. |
| `ChunkClusterIndex.cs` | The service. Core: `Rebuild`, `ClusterFor`, `ChunksIn`, `BiomeFor`. Size aggregates: `TileCount`, `ChunkCount`. Maturity aggregates: `AverageMaturity`, `MaxMaturity`, `ChunkCountsAbove` (parallel to public `Thresholds` list). |

## Cadence

Rebuilt once per ecology cycle by `Keystone.Mod.Biomes.ChunkClusterTicker`
(1 game-hour). The ticker drives the incremental API:
`BeginRebuild` at cycle start, `IncludeRegionInRebuild` per region as
the rolling sweep drains, `CommitRebuild` at cycle end. Shadow buffers
live inside `ChunkClusterIndex` so the swap is atomic — queries always
see the previous-cycle snapshot until `CommitRebuild` fires.

Cluster ids are **not stable** across rebuilds — consumers should
remember a `ChunkCoord` and re-resolve via `ClusterFor` on each
periodic check. The `Version` counter bumps on each `CommitRebuild` so
consumers caching ids across cycles can detect invalidation.

The atomic `Rebuild(regions, threshold)` API (Begin + per-region
Include + Commit in one call) is kept for tests and the startup
warmup path (`KeystoneStartupWarmup` → `ChunkClusterTicker.RunCycleNow`),
where atomic semantics simplify reasoning.

## Single-region in v1

Union-find runs only over 4-neighbours inside the same region's
chunk grid. Two adjacent same-biome chunks in different regions
land in separate clusters today. The public API is region-agnostic
so cross-region unioning can be retrofitted without breaking
consumers.

## Algorithm

Per region (only if the region has ≥ 2 valid chunks; otherwise skipped):
1. For each in-field chunk: compute
   `(Suitability argmax ∈ ClusterableBiomes, Maturity ≥ threshold)`.
2. Union-find pass over chunks with matching qualifying biomes (right + down neighbours; every pair visited once).
3. Tally per-root chunk counts so the next step can drop singletons.
4. Walk chunks; group by union-find root into cluster entries, **skipping any root whose tally is 1**; assign sequential ids.

`O(chunks)` per rebuild, dominated by chunk iteration. Path
compression keeps union-find amortised near-constant. For maps with
extensive non-clusterable terrain (e.g. large dry / contaminated /
badwater areas), the whitelist filter shortcuts most of the work
because those chunks short-circuit in step 1 and never participate
in union-find.
