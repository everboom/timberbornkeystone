# Growth (Mod)

Timberborn-facing layer for the biome growth bonus. Wires the pure
Core calculation into the game's entity lifecycle, tick system, and
entity panel UI.

## Key types

| Type | Purpose |
|---|---|
| `KeystoneGrowthBonus` | `TickableComponent` attached via `AddDecorator<GrowableSpec, _>()` to every growable entity. Self-filters at startup to a `NaturalResourceSpec` entity with a matching target biome: Forest for trees (`TreeComponentSpec`), Wetland for aquatic plants (`FloodableNaturalResourceSpec.MinWaterHeight > 0`), Grassland for **land crops** (non-aquatic, `CropSpec` **and** `PlantableSpec` present). The land-crop gate is intentionally narrow — `PlantableSpec` (which `TemplateCollectionServicePatch` strips for crops the active faction can't sow) excludes water crops, wild bushes, modded non-crops, and cross-faction / faction-disabled crops. Trees are *not* plantability-gated (Keystone plants cross-faction trees as wild Forest content on purpose). Runs biome checks ~3x/day on tick cadence; also refreshes on demand when the entity panel is open. Calls `Growable.IncreaseGrowthProgress` to apply the bonus. |
| `KeystoneGrowthBonusFragment` | `IEntityPanelFragment` singleton registered as a bottom panel section. Shows qualitative growth bonus status (tier + factor description) with colored text via `ILoc.T()`. Reads live state from `KeystoneGrowthBonus` on the selected entity. Uses `NineSliceVisualElement` with `bg-sub-box--green` / `entity-sub-panel` classes for Timberborn-consistent styling. |

## Competing-biome warnings

Each target biome has an antithetical biome that suppresses the bonus:

| Target | Competitor | Warning |
|---|---|---|
| Forest | Monoculture | "Add species diversity to increase growth bonus." |
| Grassland | Monoculture | "Add species diversity to increase growth bonus." |
| Wetland | River | "Water flow is too strong for a wetland to develop here." |

## Settings

`KeystoneFloraSettings.GrowthBonusPercent` — "Max biome growth bonus"
slider (0-100%, default 20%). Read live each tick; changing mid-game
takes effect on the next biome check.

## Registration

In `KeystoneConfigurator`:
- `Bind<KeystoneGrowthBonus>().AsTransient()` — per-entity component.
- `Bind<KeystoneGrowthBonusFragment>().AsSingleton()` — panel fragment.
- `MultiBind<EntityPanelModule>().ToProvider<GrowthBonusFragmentProvider>()` — panel wiring.
- `AddDecorator<GrowableSpec, KeystoneGrowthBonus>()` — attachment trigger.
