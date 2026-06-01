# Keystone.Mod.Buildings

Mod-side loader for the Core `BuildingCatalog`. Mirrors
`Keystone.Mod.Flora.FloraCatalogLoader` on the building side.

## Pieces

| Type | Role |
|---|---|
| `BuildingCatalogLoader` | `IPostLoadableSingleton`. Walks every `BuildingSpec` blueprint, derives a per-blueprint `BuildingRoles` flag set from spec presence + decorator-attached components, extracts faction + plantable group, and publishes the result into the Core `BuildingCatalog`. Logs a summary table for review. |

Per-voxel building classification (the `IsSettled` / region-split axis)
lives separately in `Keystone.Core.Buildings.BuildingClassifier` plus
the `BuildingQueryAdapter` -- those answer "does this voxel anchor
settlement", whereas the catalog answers "what role does this
blueprint fill".
