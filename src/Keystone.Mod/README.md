# Keystone.Mod

Timberborn-facing layer of the Keystone mod. Compiles to **`Code.dll`**
-- the assembly Timberborn's loader picks up from the mod folder.
References `UnityEngine.*`, `Timberborn.*`, `Bindito.*`, and
`HarmonyLib`.

## Purpose

Adapts `Keystone.Core` into Timberborn's runtime: registers services
through Bindito, attaches simulation hooks to the tick / save / event
systems, applies a small set of Harmony patches, and surfaces dev /
debug UI.

## Top-level types

| Type | Role |
|---|---|
| `KeystoneModStarter` | `IModStarter`. Earliest entry point; runs once at game startup. Applies the Harmony patch set and asserts the patched-method count matches the expected value. |
| `KeystoneConfigurator` | `[Context("Game")]` Bindito root. Registers everything Keystone needs in the Game scope -- ports/adapters, singletons (surveyor, region updater, persistence, tickers, decoration registry), `MultiBind<TemplateModule>` decorators for `KeystoneFlourishSpec`, the cross-faction collection / material providers, and the toolbar group. |

## Subsystems

| Folder | Purpose |
|---|---|
| `Adapters/` | Implementations of the `Keystone.Core.Ports` interfaces over Timberborn services. |
| `Survey/` | `KeystoneSurveyor` (one-shot PostLoad sweep) + `RegionUpdater` (event-driven incremental updates). |
| `Ecology/` | `EcologyFieldUpdater` -- polling driver that fills per-region chunked fields. |
| `Biomes/` | `ChunkBiomeAdapter` + `ChunkBiomeTicker` -- translates field state into biome inputs and ticks the per-chunk Suitability and Maturity values. |
| `Persistence/` | `KeystonePersistence` save/load owner; `RegionValueLifecycleHandler` for region-value split / merge / remove events; `RegionValueTicker` (per-chunk sweep lives in `Biomes/ChunkBiomeTicker`; chunk re-binding on topology change is `Keystone.Core.Persistence.ChunkReconciler`). |
| `Flora/` | `FloraCatalogLoader` plus Class D dev placement tools (active-faction + cross-faction vanilla flora). |
| `Buildings/` | `BuildingCatalogLoader`. |
| `Decoration/` | Class A pipeline: `KeystoneDecorationRegistry`, `IDecorationController`s, dev placement tools for particle and reactive-flora variants. |
| `Flourish/` | Class B pipeline: `KeystoneFlourishSpec` marker, `KeystoneFlourish` per-entity decorator, dev placement tool. |
| `Recipes/` | Recipe-book + level + per-entity-variant specs (`KeystoneRecipeBookSpec`, `KeystoneBiomeLevelsSpec`, `KeystoneVariantSpec`); catalogs (`FlourishCatalog`, `BiomeLevelCatalog`); Class A / B / C handlers that drive flora off the dominant biome's Maturity + level progress. |
| `HarmonyPatches/` | Cross-faction template strip + ambient-entity interaction suppression patches. |
| `Toolbar/` | `KeystoneToolGroup` -- bottom-bar element grouping the dev placement tools. |
| `Diagnostics/` | `PerfTracker` dispatcher + `KeystonePerfWindow` floating overlay (Alt+Shift+K). |
| `Visualization/` | `PlateauHighlighter` -- per-frame region + cursor-chunk highlight. |
| `Debug/` | `KeystoneDebugPanel` plus the cross-faction collection / material providers and the early Phase 1 prototype probes. |

Each folder has its own README with type-level detail.

## Build & deploy

`dotnet build` produces `Code.dll` and copies it (along with
`Keystone.Core.dll` and `mod/manifest.json`) into
`%USERPROFILE%\Documents\Timberborn\Mods\Keystone\`. Disable the deploy
step with `dotnet build -p:KeystoneDeploy=false`.

References to game and engine assemblies are resolved by wildcard
against `$(TimberbornManagedDir)` (set in repo-root
`Directory.Build.props`). Override per-machine via the
`KEYSTONE_TIMBERBORN_DIR` environment variable.
