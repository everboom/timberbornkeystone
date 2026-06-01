# Keystone.Core.Ecology

Per-surface raw inputs to the ecology layer plus the per-region
chunked-field machinery that aggregates them.

## What lives here

- `SurfaceSurvey` -- snapshot of one surface voxel's raw ecological
  inputs (moisture / contamination distance and predicates, water
  depth + flow, IsCave, IsSettled). Pure data; deliberately carries no
  derived "ecology tag" -- classification happens at the region/chunk
  level, not per-surface, because collapsing orthogonal axes into one
  bucket forces an arbitrary priority.
- `Fields/` -- per-region chunked scalar fields
  (`RegionEcologyField`, `RegionEcologyFieldBuilder`,
  `IEcologyFieldQuery`, `EcologyChannel`). See its own README.

The Mod-side aggregator (`Keystone.Mod.Ecology.EcologyFieldUpdater`)
walks the surveyor's `TileMap<SurfaceCoord, SurfaceSurvey>` and writes
chunk values into each region's field on a polling cycle.
