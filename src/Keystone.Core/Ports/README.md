# Keystone.Core.Ports

Engine-agnostic interfaces that let Core read host state without
referencing Timberborn or Unity assemblies. Each port is declared in
primitive types and `Keystone.Core` value types only.

The Mod layer provides adapters that implement these interfaces over
the real game services and registers the interface -> adapter binding
with Bindito. Tests substitute trivial fakes.

## Current ports

| Port | Surface |
|---|---|
| `ITerrainQuery` | Map bounds, surface heights, per-voxel `IsTerrainVoxel(x, y, z)` for adjacency probes (used by `CliffProximity`). |
| `IMoistureQuery` | Per-column soil moisture (irrigation distance) plus the per-voxel `IsMoistAt` predicate the game uses internally. |
| `IContaminationQuery` | Per-column soil contamination plus `IsContaminatedAt` predicate. |
| `IWaterQuery` | Per-surface water depth + horizontal flow vector + per-column water-presence flag. |
| `IBuildingQuery` | Per-voxel `BuildingKind` classification (Building / Path / None). Implementations follow the dual-case rule (Building+Path -> Building) via the `BuildingClassifier` in Core. |
| `IPlantingMarkQuery` | Per-tile read over the host's planting-designation system (Forester / Farmhouse marks): `IsMarked`, `MarkedSpecies`, and a `MarksInTileRect(minX, minY, maxX, maxY)` rect query. The rect query is the chunk-aggregator's hot path; the adapter is expected to maintain a spatial index so cost scales with marks-in-rect rather than world-wide mark count. Marks override handler placement at the per-tile level and contribute to the chunk's plantable count / species so a freshly-drawn area reads as monoculture before any sapling sprouts. |
| `ICuttingMarkQuery` | Per-tile read over the host's tree-cutting designation area: `IsMarkedForCutting(x, y, z)`. Used by Class D handler with a neighbour-tree heuristic — bushes/crops/ground-cover never spawn on marked tiles, trees only spawn when an adult cuttable tree exists in the 8-tile Moore neighbourhood. Lets clear-cuts self-stabilise (forest fades as cutting proceeds) while selective harvesting inside a Keystone forest still regrows. |

### Write-side ports

These flow Core → Mod: Core decides, the adapter mutates host state. The
read-side ports above are the inverse (the adapter answers, Core reads).

| Port | Surface |
|---|---|
| `ICuttingAreaWriter` | Batched write over the host's tree-cutting **area registry** (`Timberborn.Forestry.TreeCuttingArea`): `MarkForCutting(coords)` / `UnmarkForCutting(coords)`. Backs the logging brush (`Keystone.Core.Cutting.LoggingSelector`). Distinct from the read-side `ICuttingMarkQuery`, which reads a single tree's `Cuttable.IsMarked`; this writes the coordinate registry the cut pipeline actually consumes (the canonical designation path, not per-tree `Cuttable.Mark()`). |

`IClock` lives next to its value types in `Keystone.Core.Time` rather
than here.

When adding a new port: keep its signatures in primitives + Core types,
write a Mod-side adapter, register it in `KeystoneConfigurator`, and
add at least one Core test that drives consumers with a fake
implementation.
