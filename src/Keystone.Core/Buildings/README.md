# Keystone.Core.Buildings

Two layers of building classification, kept distinct because they answer
different questions.

## Structural per-voxel: `BuildingKind` + `BuildingClassifier`

Drives the `IsSettled` per-surface flag in `SurfaceSurvey` and the
`Settled` axis in region splitting. The question is: "does this voxel
anchor settlement?"

- `BuildingKind` (enum): `None` / `Building` / `Path`.
- `BuildingClassifier` (static): pure function that decides between the
  three based on two booleans (`hasBuilding`, `isPath`).

The classifier is a pure function so the dual-case rule (a voxel that's
both a Building and a Path classifies as Building) can be unit-tested
without any Timberborn dependency. The Mod-side adapter
(`BuildingQueryAdapter`) gathers the booleans from the game's
`IBlockService` + `IPathService` and delegates to `Classify`.

## Per-blueprint role description: `BuildingRoles` + `BuildingEntry` + `BuildingCatalog`

Drives the cursor display, ecology rules that key off building roles, and
any consumer that wants to ask "what role does this building fill?"
rather than "should this voxel anchor settlement?"

- `BuildingRoles` (Flags enum): `Path`, `Dwelling`, `Workplace`,
  `Industry`, `Farming`, `Storage`, `WaterInfra`, `Mechanical`, `Wonder`,
  `RangedEffect`, `DistrictAnchor`, `Decoration`. Bitwise-combinable
  because most buildings carry multiple roles.
- `BuildingEntry`: per-blueprint record -- name, faction, roles,
  optional plantable group, raw capability list (debug).
- `BuildingCatalog`: read-mostly registry, populated at mod load by
  `Keystone.Mod.Buildings.BuildingCatalogLoader`. Mirrors `FloraCatalog`
  on the building side. Indexes planters by their resource group so
  cross-references with `FloraEntry.PlantableGroups` are one-step.

The two layers do not share state. `BuildingClassifier` consults
per-voxel game services; `BuildingCatalog` consults blueprint specs.
