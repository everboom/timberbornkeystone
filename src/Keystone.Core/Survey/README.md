# Keystone.Core.Survey

Core-side surveying logic: turns the read-side ports
(`ITerrainQuery`, `IMoistureQuery`, `IContaminationQuery`,
`IWaterQuery`, `IBuildingQuery`) into a `TileMap<SurfaceCoord, SurfaceSurvey>`
that downstream Core systems (`RegionService`, `RegionEcologyField`
builder, biome ticker) read without touching the host engine.

## Pieces

| Type | Role |
|---|---|
| `TerrainSurveyor` | Full-map sweep + incremental column resurvey. Walks every column, classifies stacked surfaces (cave / settled / moist / contaminated / underwater / flowing) into per-voxel `SurfaceSurvey` records, and stores them in a `TileMap` keyed by `SurfaceCoord`. The Mod-side `RegionUpdater` calls `ResurveyColumn` on dirty columns; `KeystoneSurveyor` calls `Survey` once at PostLoad. |
| `PlateauFinder` | Pure flood-fill helper that returns the connected component of like-Z surfaces around a seed. Used by debug overlays (and historically by the highlighter); structural region grouping itself is in `Regions/RegionService`. |
| `ColumnDiff` | Value type representing the before/after surface set for one column. Produced when a column is resurveyed; consumed by `RegionService` to apply incremental updates to the region graph without re-flooding the whole map. |

Tests drive `TerrainSurveyor` and `PlateauFinder` directly with fake
ports -- no game DLLs required.
