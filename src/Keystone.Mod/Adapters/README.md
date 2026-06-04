# Keystone.Mod.Adapters

Implementations of the `Keystone.Core.Ports` interfaces over Timberborn
services. Each adapter is a thin translation layer registered with
`Bind<IPort>().To<Adapter>().AsSingleton()` in `KeystoneConfigurator`.

## Pieces

| Type | Role |
|---|---|
| `TerrainQueryAdapter` | `ITerrainQuery` over `ITerrainService`. |
| `MoistureQueryAdapter` | `IMoistureQuery` over `ISoilMoistureService` + `MapIndexService`. |
| `ContaminationQueryAdapter` | `IContaminationQuery` over `ISoilContaminationService` + `MapIndexService`. |
| `WaterQueryAdapter` | `IWaterQuery` over Timberborn's water services -- per-surface depth + flow vector + per-column water-presence flag (`IsWaterOnAnyHeight`). |
| `BuildingQueryAdapter` | `IBuildingQuery` over `IBlockService` + `IPathService`. Uses `EnterableSpec` as the building discriminator, delegates the dual-case rule (Building+Path → Building) to `BuildingClassifier` in Core. Skips natural elements via `BlockObjectClassification.IsNaturalComponent` (authoritative, vanilla component types) and `LacksBuildingSpec` (heuristic for spec-only naturals — applied after explicit Keystone tags so a no-`BuildingSpec` BO with a Keystone tag classifies per the tag). |
| `BlockingQueryAdapter` | `IBlockingQuery` over `IBlockService`. Returns true for any voxel occupied by a BO whose source `Blueprint.Name` is in `Keystone.Core.Buildings.BlueprintNamePolicy.BlockingNaturalNames` (natural dams, blockages, geysers, overhangs). Drives the per-surface `IsBlocked` flag in `SurfaceSurvey` -- blocked surfaces are excluded from region membership entirely. |
| `PlantingMarkAdapter` | `IPlantingMarkQuery` over Timberborn's `PlantingService`. Per-tile lookups (`IsResourceAt`, `GetResourceAt`) are O(1). `MarksInTileRect` is served from an internal 4x4-tile spatial bucket maintained reactively via `PlantingCoordinatesSetEvent` / `PlantingCoordinatesUnsetEvent`, so per-call cost scales with marks-in-rect rather than the world-wide mark count. Implements `ILoadableSingleton`/`IUnloadableSingleton` for the event subscription. |
| `CuttingMarkAdapter` | `ICuttingMarkQuery` over Timberborn's `TreeCuttingArea`. Per-tile-only port — single `IsInCuttingArea` lookup, which is already an O(1) hashset contains. No reactive index needed (no rect query in the port surface). |
| `CuttingAreaWriter` | **Write-side.** `ICuttingAreaWriter` over Timberborn's `TreeCuttingArea` — batched `MarkForCutting` / `UnmarkForCutting` forwarding to `AddCoordinates` / `RemoveCoordinates` (which post their own change events). The only place `(X,Y,Z)` tuples become `Vector3Int`. Drops empty batches so an empty drag doesn't fire a spurious change event. Backs the logging brush (`Keystone.Mod.Cutting`). |
| `GameClockAdapter` | `IClock` over `GameCycleService` + `WeatherService` + `HazardousWeatherService` + `IDayNightCycle`. Exposes `Now` (Cycle/CycleDay/PartialCycleDay), `CurrentWeather`, and `TotalDaysElapsed` for flat real-valued time math. |
| `BlockObjectClassification` | `internal static`. Shared "what kind of block object is this" predicates used by `BuildingQueryAdapter`, the event-driven `RegionUpdater` dirty-set, and the surveyor's diagnostic dump. `IsNatural` is a two-phase check (entity-component sniff for `NaturalResource` / `Crop` / `Gatherable` / `Growable` / `Yielder`, plus inverse-discriminator on `BlockObjectSpec` lacking `BuildingSpec`). `IsBlocking` is the curated whitelist match against `Keystone.Core.Buildings.BlueprintNamePolicy.BlockingNaturalNames` -- the subset of naturals whose placement / removal DOES change the region graph. |
