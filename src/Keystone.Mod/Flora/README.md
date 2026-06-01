# Keystone.Mod.Flora

Mod-side flora support: catalog loader plus dev tools for spawning
Class D vanilla flora (both active-faction and cross-faction donors).

## Pieces

| Type | Role |
|---|---|
| `FloraCatalogLoader` | `IPostLoadableSingleton`. Walks every `NaturalResourceSpec` blueprint (broadest "map-spawned biotic element" signal -- trees, bushes, mushrooms, ground cover, crops; covers any mod's flora). Captures spec data (growth time, water tolerance, yield via both `CuttableSpec.Yielder` and `GatherableSpec.Yielder`, planter group), classifies kind via `CropSpec` / `TreeComponentSpec` / `BushSpec` / fallback to `GroundCover`. Then runs a map-walk census via `IBlockService.GetObjectsAt` to count alive vs dead instances per blueprint. Logs a summary. Census uses a map walk rather than `EntityComponentRegistry` because `NaturalResource` doesn't implement `IRegisteredComponent`. |
| `VanillaFloraPlacementTool` | Dev tool. Class D in the content taxonomy: spawns a vanilla active-faction natural resource (hardcoded `Pine`, present in both factions' collections). Fully vanilla -- grows, cuttable, persists in saves. Fast-forwards `Growable` so the spawn renders mature instead of a tiny seedling. |
| `CrossFactionFloraPlacementTool` | Dev tool. Class D in the content taxonomy: spawns a flora from the OTHER faction. Picks `CoffeeBush` (in a Folktails game) or `Maple` (in IronTeeth) based on `FactionIdAccessor.CurrentId`. Relies on the cross-faction collection / material providers in `Debug/` and the spec-stripping `TemplateCollectionServicePatch` to make the donor reachable and non-crashing. |

The companion Class A / Class B / Recipes paths live in `Decoration/`,
`Flourish/`, and `Recipes/`.
