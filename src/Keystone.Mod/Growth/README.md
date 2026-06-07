# Growth (Mod)

Timberborn-facing layer for the biome growth bonus. Wires the pure
Core calculation into the game's entity lifecycle, tick system, and
entity panel UI.

## Key types

| Type | Purpose |
|---|---|
| `KeystoneGrowthBonus` | `TickableComponent` attached via `AddDecorator<GrowableSpec, _>()` to every growable entity. Self-filters at startup to a `NaturalResourceSpec` entity with a matching target biome: Forest for trees (`TreeComponentSpec`), Wetland for aquatic plants (`FloodableNaturalResourceSpec.MinWaterHeight > 0`), Grassland for **land crops** (non-aquatic, `CropSpec` **and** `PlantableSpec` present). The land-crop gate is intentionally narrow — `PlantableSpec` (which `TemplateCollectionServicePatch` strips for crops the active faction can't sow) excludes water crops, wild bushes, modded non-crops, and cross-faction / faction-disabled crops. Trees are *not* plantability-gated (Keystone plants cross-faction trees as wild Forest content on purpose). Runs biome checks ~3x/day on tick cadence; also refreshes on demand when the entity panel is open. Calls `Growable.IncreaseGrowthProgress` to apply the bonus. `ComputeSignals()` (UI-only) assembles the `GrowthSignals` bundle for the panel: cached suitability/maturity/bonus plus two on-demand dominant-biome reads (`SampleDominantByMaturity` / `SampleDominantBiome`) and, for Forest, the mature-canopy gate via `ChunkBiomeAdapter.Build` (the gate isn't a persisted channel). These extra reads stay off the tick hot path. |
| `KeystoneGrowthBonusFragment` | `IEntityPanelFragment` singleton. Shows a one-line **flavor verdict** (from the pure `GrowthDiagnostics.Classify`) plus a small bonus %, and registers a **dynamic hover tooltip** (`ITooltipRegistrar.Register(_root, Func<string>)`) carrying the full technical breakdown — established biome vs. needed, current suitability + limiting factor, canopy state, formula. The tooltip closure reads a live `GrowthSignals` cache refreshed each frame in `UpdateFragment`, so it survives per-selection swaps. `NineSliceVisualElement` + `bg-sub-box--green` / `entity-sub-panel` styling. |

## Verdict → flavor line

The pure `Keystone.Core.Growth.GrowthDiagnostics.Classify` maps the signal
bundle to a `GrowthVerdict`; the fragment maps that to a flavor string:

| Verdict | When | Flavor line |
|---|---|---|
| Thriving | established target + favorable conditions | "Thriving in established {B}." `(+N%)` |
| Benefiting | a meaningful bonus is applied (≥ margin) | "Growing well — {B} is taking hold." `(+N%)` |
| Establishing | actively establishing now — real current suitability, maturity still building | "Taking root — {B} is establishing." `(+N%)` |
| Potential | viable but not started — diverse young Forest canopy gated to ~0 current suitability | "No growth bonus yet — {B} will establish as the saplings mature." |
| Hostile | toxic ground, or moisture mismatch | "No growth bonus — {reason}." |
| WrongBiome | a different non-hostile biome is established here | "No growth bonus — needs {B}, this is established {O}." (Monoculture → "No growth bonus — too little species variety for {B}.") |
| Dormant | nothing established, conditions not favorable | "No growth bonus — no established {B} here." |

The no-bonus verdicts are framed "No growth bonus — …" so the line reads as a
statement about the bonus, not a judgment on the plant. The `(+N%)` suffix
rides only on the bonus-positive verdicts (Thriving / Benefiting /
Establishing). The technical detail (raw suitability/maturity numbers, the
dominant-biome match, the limiting factor) lives in the hover tooltip.

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
