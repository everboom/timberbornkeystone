# Surface

Map-global, per-surface (per-voxel) **persisted** float storage for Keystone
ecology layers — the per-tile counterpart to the per-chunk
`ChunkValueStore` / `ChunkData`.

## Why this exists

Some ecology state has to be both **per-tile** (the 4×4 chunk grid is too coarse
to describe a thin, meandering shoreline band) *and* **accumulated over time**
(so a transient flood can't be mistaken for sustained water). Neither existing
layer can do both:

- the per-chunk layer has the time axis but not the resolution;
- the transient `RegionTileData` layer (`Spatial/`, where `waterDistance` lives)
  has the resolution but is recomputed each cycle, with no memory.

The motivating case: riparian flourishes were folded into Grassland and gated on
`mature Grassland AND water-nearby-right-now`. The instantaneous water check let
a momentary flood fire a semi-permanent decoration. This layer restores the
"water has been here long enough" requirement as a per-tile maturity.

## Design

Modelled directly on vanilla `SoilMoistureSimulator`, which solves the identical
problem (accumulated per-surface state on a map whose extent is fixed but whose
composition changes as terrain is dug and raised):

- **Storage** — one dense `float[]` per `SurfaceField`, indexed by
  `MapIndexService`'s 3D (per-voxel) index. Dense, not sparse: the canonical
  consumer fills toward the whole map as the player spreads water, so the dense
  case *is* the success case — and the game stores moisture this way.
- **Terrain composition changes** — subscribes to `IThreadSafeColumnTerrainMap`
  and drains its events on the main thread in `Tick()`:
  `ColumnMovedUp/Down` copy a surface's value to its shifted index (it moved, it
  didn't vanish — zeroing would wipe maturity above any dig); `ColumnReset` zeros
  a destroyed/created surface; `MaxTerrainColumnCountChanged` resizes (the new
  tail zero-fills, so fresh surfaces default to zero).
- **Persistence** — each layer is `MapIndexService.Pack`ed under its own key.
  Adding a layer is additive: old saves lack the key and load it as all-zero, so
  there is no migration. The fixed map extent makes the flat index stable across
  save/load — no reprojection. The store is its own `ISaveableSingleton` (the
  packed-array shape doesn't fit `KeystonePersistence`'s region/chunk
  parallel-list codec; the game keeps such layers in their own simulators too).

## Key types

- **`SurfaceField`** — the enum of persisted layers (currently `RiparianMaturity`).
  Contiguous and zero-based; the store indexes its arrays by the member's value.
  `SurfaceFieldMeta.SaveId` gives each layer a rename-proof persistence id.
- **`SurfaceFieldStore`** — the `ILoadableSingleton` / `ISaveableSingleton` /
  `ITickableSingleton` that owns the arrays, the terrain-change cleanup, and
  persistence. Bound in `KeystoneConfigurator`.

## Not here yet

The accrue/dissipate sweep that writes `RiparianMaturity` (driven by water
proximity, sharing the kernel with the per-chunk `BiomeMaturityUpdater`) and the
rewiring of the flourish gate from the instantaneous water check to a maturity
threshold. See the project memory note `project-riparian-narrow-band`.
