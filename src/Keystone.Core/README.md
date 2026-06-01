# Keystone.Core

Pure-C# simulation core for Keystone. **No references to Unity,
Timberborn, or Bindito are permitted in this project.** Targets
`netstandard2.1` so it builds and runs under MSTest without any game
DLLs on disk.

## Why isolated

Keeping Core free of game-engine types is what makes it unit-testable
under plain `dotnet test`. Anything that needs `UnityEngine.*` or
`Timberborn.*` belongs in `Keystone.Mod`. Read-side game state reaches
Core through `Ports/`; Mod-side adapters implement those ports.

## Subsystems

| Folder | Purpose |
|---|---|
| `Tiles/` | Coordinate primitives (`TileCoord`, `SurfaceCoord`, `FlowVector`, `TileMap<TKey,TValue>`). |
| `Time/` | Game-cycle-aware timestamps (`GameTimestamp`, `WeatherKind`) and the `IClock` port. |
| `Ports/` | Read-side interfaces over host state (terrain / moisture / contamination / water / buildings). |
| `Survey/` | Full-map sweep + incremental column resurvey -> `TileMap<SurfaceCoord, SurfaceSurvey>`. Plus `PlateauFinder`, `ColumnDiff`. |
| `Ecology/` | `SurfaceSurvey` raw inputs; `Fields/` per-region chunked scalar fields with bilinear sampling. |
| `Regions/` | Structural regions (4-connected components of surfaces sharing `(Z, IsCave, IsSettled)`). `RegionService.Index()` does flood-fill assignment; incremental updates apply on terrain / building events. |
| `Biomes/` | Two-channel per-chunk biome state: short-term Suitability (drift toward stress-aware target, clamped `[0, 1]`) and long-term Maturity (day-scale integrator). Per-biome level ladder (`BiomeLevel`, `BiomeLevelTable`). Bilinear / dominant-biome sampler. `ClassARecipe` and `ClassBRecipe` shapes with `LevelId` + `MaxDensity`. `FlourishThreshold` per-tile activation hash. Both channels live in `Persistence/ChunkValueStore` under namespaced kinds. |
| `Buildings/` | Two-layer classification: per-voxel `BuildingKind` (drives settlement / region splits) and per-blueprint `BuildingRoles` + `BuildingCatalog` (drives UI / ecology rules). |
| `Flora/` | Runtime `FloraCatalog` of the loaded game's flora blueprints (vanilla + mods). |
| `Persistence/` | Save/load value types, codec, parallel-list payload. Mod-side glue is in `Keystone.Mod.Persistence`. |
| `Diagnostics/` | `PerfStats` ring-buffer for per-scope timing; the dispatcher lives Mod-side. |
| `Compatibility/` | Polyfills (`IsExternalInit`) for C# 9+ syntax under `netstandard2.1`. |

Each folder has its own README with type-level detail.
