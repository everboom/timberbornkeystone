# Keystone.Core.Spatial

Pure-function spatial helpers over the Core ports. Aggregate per-column /
per-tile primitive queries (water presence, terrain height, etc.) into
neighbourhood predicates that placement filters and handlers can call
directly, instead of every caller reimplementing the same neighbour-walk
loop with its own bounds handling.

## Pieces

| Type | Role |
|---|---|
| `WaterProximity` | "Does this column border water?" — Moore-neighbour walk (8-connected, orthogonal + diagonal) over `IWaterQuery.HasWaterAtColumn`, bounds-checked against `ITerrainQuery`. Self is not counted. Used by `WaterEdgeRecipeFilter` and any future "must touch water" code path. |
| `CliffProximity` | "Does this surface sit on or against a cliff?" — per-voxel adjacency probes over `ITerrainQuery.IsTerrainVoxel`. `IsAboveNeighbor(surface)` is true when at least one Manhattan-neighbour column has empty space at `surface.Z - 1` (the ground drops away). `IsBelowNeighbor(surface)` is true when at least one Manhattan-neighbour has natural terrain at `surface.Z` (the neighbour rises above). Surface-Z comparison would mis-report columns with overhangs / floating geometry; per-voxel probes get them right. Out-of-bounds neighbours read as empty, so map edges register as above (not below) their nonexistent neighbour. Walkable-tile Z convention: terrain solid sits at `z-1`, walkable air at `z`. |
| `IRecipeFilter` | Spatial-eligibility predicate consulted by the spawn handlers before the activation gate. Each implementation has a string `Name` (matches the recipe's `Filter` field) and an `IsEligible(SurfaceCoord)` method. Adding a new filter type = new file + one `MultiBind` line in the configurator; no churn in handlers. The dispatch registry (`RecipeFilterRegistry`) lives in `src/Keystone.Mod/Recipes/` because it logs warnings on unknown filter names — Core stays UnityEngine-free. |
| `WaterEdgeRecipeFilter` | `IRecipeFilter` implementation. Name `"WaterEdge"`. Eligible iff `WaterProximity.BordersWater(surface.Column)`. Used by Riparian L1 mini-flourishes. |
| `RiverBankRecipeFilter` | `IRecipeFilter` implementation. Name `"RiverBank"`. Eligible iff `CliffProximity.IsBelowNeighbor(surface) && !CliffProximity.IsAboveNeighbor(surface)` — a tile that sits at the bottom of a step-up but is not itself a cliff top. Admits the wall under a river bank; rejects waterfall edges (which look down on the basin below as well as up at the bank wall above). Used by River L1 mini-flourishes. |

When adding a new helper here: keep it Core (no Timberborn refs), drive
it from existing port primitives where possible, and add tests that
exercise the neighbourhood semantics with hand-rolled fake ports.
