# Keystone.Mod.Ecology

Mod-side driver that fills the Core `RegionEcologyField`s. Single file
for now; companion to `Keystone.Core.Ecology.Fields/`.

## Pieces

| Type | Role |
|---|---|
| `EcologyFieldUpdater` | `ITickableSingleton + IEcologyFieldQuery`. Polls every region's chunked field on a fixed cycle (`TicksPerCycle = 25`, ~5s at 1x speed). Chunk-unit scheduling: at cycle init walks the surveyed surface map once, groups in-region surfaces by (region, chunk) into a flat schedule, allocates / reallocates fields when bbox or channel count changes. Each tick drains a budget of chunks; per-chunk work is bounded by `ChunkSize^2 = 16` surfaces (plus each surface's `dz=0..VerticalProbeRange` block-object probe for entity counts). Settled regions are skipped. Fields are persistent across cycles -- only chunk values are rewritten. Dead naturals route to a synthetic catch-all entity channel (`(dead)`) so a dead Birch doesn't pad the live Birch density. |

The Core-side `RegionEcologyFieldBuilder` is an alternative
construction path used by tests and one-shot bulk builds; production
uses the in-place `WriteChunk` API the updater calls.
