# Keystone.Core.Ecology.Fields

Per-region scalar fields: each region in the world owns one
`RegionEcologyField` carrying smoothed local conditions
(moisture, contamination, water flow, plant and animal densities) at
chunk-scale resolution, and answers per-tile queries via bilinear
interpolation restricted to its own bounding box.

## Why per-region

A regular global grid laid over the map mixes wet-plateau tiles with
dry-plateau tiles in chunks straddling region boundaries — the wrong
model for ecology, where a tile's "context" should reflect only the
region it belongs to. Each field is scoped to its region's bbox and
only in-region tiles contribute to chunk values; consumers always pair
"which region am I in?" with "what does that region's field say at my
tile?"

## Why chunks (and not per-tile or per-region-aggregate)

Per-tile Gaussian blur over a meaningful radius is O(N × R²) per
refresh — too expensive at typical-map sizes. A per-region aggregate
(single value per channel per region) loses all spatial detail: a
plateau with a wet edge and a dry interior would average to "kind of
moist." Chunks at 4×4 tile resolution with bilinear lookup give a
smooth gradient at sub-chunk granularity for O(N) refresh cost, and
per-tile queries are O(1).

## Channels

- **Fixed scalar channels** (`EcologyChannel`): WaterDepth,
  WaterFlowMagnitude, Moisture, Contamination. Stored as per-tile means
  per chunk.
- **Entity channels** (integer-indexed, dynamic): one channel per
  catalogued blueprint -- flora today (`FloraCatalog` populates the
  index map at mod load), fauna once Phase 2 lands. Same shape for
  both: raw counts of in-region entities per chunk. The field type
  knows nothing about which blueprint is at which index, only ints.

## Validity

A chunk with no scalar tile contributions is flagged invalid. At sample
time the bilinear stencil drops invalid corners and renormalises the
remaining weights — interior queries are full 4-point bilinear, queries
near the bbox boundary degrade gracefully to 3, 2, or 1 corner.

## Lifecycle

Fields are derived state, fully recomputable from authoritative tile
data. They do **not** implement `IRegionState` — on region split or
merge, the field is invalidated and the next polling sweep refills it.
This keeps the field independent of the structural plumbing (Chunk D)
and avoids the complexity of redistributing chunk grids through
split/merge.

## Status

Phase 1A-D-A: pure types and the builder. No service, no integration,
no consumers. The Mod-side updater that drives computation lives in a
sibling chunk.
