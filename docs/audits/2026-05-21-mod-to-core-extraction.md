# Audit: `Keystone.Mod` → `Keystone.Core` extraction opportunities

**Date:** 2026-05-21
**Goal:** Find pure logic stuck in the Mod layer that could move to Core to improve testability.
**Method:** Read-only audit; no code changes. Cross-file scan of `src/Keystone.Mod/` against the existing port/adapter convention.

Effort hints: **S** = an hour, **M** = half-day, **L** = multi-session refactor.

---

## Tier 1 — clear wins, low effort

### 1.1 `RollingSweepTicker<TUnit>` → Core (with thin Mod adapter)

- **Where:** `src/Keystone.Mod/Sweep/RollingSweepTicker.cs:49-251`
- **Now:** The whole rolling-sweep amortisation algorithm (cycle anchoring, Fisher-Yates shuffle, fractional drain, first-cycle bootstrap, `RunCycleNow`) is pure math over `IClock` + `PerfTracker`. Only game coupling: `Timberborn.TickSystem.ITickableSingleton` (one-method interface) and `PerfTracker.Track`.
- **Move:** Algorithm into `Keystone.Core.Scheduling.RollingSweep<TUnit>`; Mod-side keeps a sealed `RollingSweepTickerAdapter<TUnit> : ITickableSingleton` that wraps it and forwards `Tick()`.
- **Port needed:** `IPerfScope` (or `Func<string, IDisposable>`). `Diagnostics/PerfStats` is already Core; only `PerfTracker.Track` lives Mod-side.
- **Testable:** cycle bootstrap behaviour, drain proportionality to elapsed `IClock.TotalDaysElapsed`, "empty schedule still fires `OnCycleComplete`", `RunCycleNow` semantics, dt=0 first-cycle guarantee. None of this is tested today. Past bugs in this class (first-cycle dt drift, anchor-in-the-past) are exactly the kind a Core test pins.
- **Effort:** S

### 1.2 `FlourishCatalog` parsing path → Core

- **Where:** `src/Keystone.Mod/Recipes/FlourishCatalog.cs:101-524` — `TryParseAttrition`, `TryParseEntry`, `NormaliseHabitats`, `NormaliseAttritionClasses`, `NormaliseWeight`, `EnumerateBlueprintNames`, the bucketing logic.
- **Now:** Only Mod-y things are `ISpecService.GetSpecs<KeystoneRecipeBookSpec>()` and `UDebug.LogWarning` for parse errors. Everything else operates over already-immutable record fields and produces Core records (`ClassXRecipe`, `AttritionRecipe`).
- **Move:** Pull parse + bucketing into `Keystone.Core.Biomes.RecipeBookCompiler` taking `IEnumerable<RecipeBookData>` (plain DTO with spec fields) and an `Action<string>` warner. Mod-side `FlourishCatalog` becomes "fetch specs, project to DTOs, call Core compiler, expose its indexed result."
- **Testable:** every parser branch — unknown biome, missing level, bad attrition action, mixed-category bucket warning, density clamps, ScaleBy with bad min/max. All currently untested and a known bug-attractor (`NormaliseAttritionClasses` silently swallowed `"D"` once before the vanilla-species branch was added).
- **Effort:** M (lots of parse cases; mechanical)

### 1.3 `BiomeLevelCatalog.TryApply` validation → Core

- **Where:** `src/Keystone.Mod/Recipes/BiomeLevelCatalog.cs:107-168`
- **Now:** `TryApply` is pure: validates a `BiomeLevelEntry`, derives density and mode, calls `BiomeLevelTable.Define` (already Core). Only the warning logs are Mod-shaped.
- **Move:** Static helper `Keystone.Core.Biomes.BiomeLevelEntryValidator.TryApply(BiomeLevelTable, BiomeKind, BiomeLevelEntry, Action<string>)`. `BiomeLevelEntry` itself is already Core-friendly.
- **Testable:** clamp, sentinel-density, mode parse, upper-not-greater-than-lower.
- **Effort:** S

### 1.4 `InteriorOnlyTopology` and `MaturityFilterTopology` — already pure, relocate

- **Where:** `src/Keystone.Mod/Fauna/InteriorOnlyTopology.cs`, `src/Keystone.Mod/Fauna/MaturityFilterTopology.cs`
- **Now:** Both implement Core's `IRegionTopologyQuery` and depend only on Core types (`ChunkValueStore`, `RegionEcologyField`, `ChunkBiomeSampler`, `BiomeKind`). **Zero Unity/Timberborn refs**. They live in Mod purely because `FaunaSpawnDrainer` instantiates them.
- **Move:** Drop into `Keystone.Core.Regions/` (or `Keystone.Core.Fauna/`). No port needed.
- **Testable:** spawn-tile predicate parity with agent walkability — the documented contract is "drainer's pick must match agent's filter," but there's no test asserting that.
- **Effort:** S (literally `git mv`)

### 1.5 `MultiplierFor` lookup tables in settings — extract category→multiplier projection

- **Where:** `src/Keystone.Mod/Settings/KeystoneFloraSettings.cs:94-101` and the parallel `KeystoneFaunaSettings.cs`.
- **Now:** Settings classes are irreducibly Mod (`ModSettingsOwner`), but the value→multiplier projection isn't.
- **Move:** A Core `DensityMultiplierTable` value type with a small dict and `Lookup(string category)`. Settings hand it live values per cycle; Core type does the lookup. `Categories` constants ride along.
- **Testable:** unknown-category pass-through, case-sensitivity contract. Currently asserted only by reading the docstring.
- **Effort:** S — borderline; only worth it if you want the contract pinned.

---

## Tier 2 — worth doing, more work

### 2.1 `ChunkBiomeAdapter.Build` → Core, behind a new port

- **Where:** `src/Keystone.Mod/Biomes/ChunkBiomeAdapter.cs:42-298`
- **Now:** Real decision-making — depth/flow thresholds, dry-vs-irrigated split, contaminated-water derivation, Simpson dominance over plantables+marks dedup. Only Timberborn touch: `_blockService.GetObjectsAt` inside `TileHasPlantableEntity` (mark-vs-realised-entity dedup).
- **Port needed:** `INaturalResourceAtTileQuery.HasNaturalResourceAt(int x, int y, int z) → bool`. Adapter wraps `IBlockService.GetObjectsAt` + the `NaturalResource` component check (~10 lines).
- **Move:** All of `Build`, `AggregateFromChannels`, `AggregatePlantables`, `EnsurePlantableIndexCached`. `FloraCatalog` and `RegionEcologyField` are already Core.
- **Testable:** every `ChunkBiomeInputs` derivation — does irrigated correctly zero out on water-bearing chunks? does Simpson dominance return 1.0 on single-species and 1/N on uniform N? does mark/entity dedup actually drop double counts? **None tested today.** This is the largest pure-logic island in Mod — concrete numeric outputs that flow straight into `BiomeSuitabilityUpdater`. Past bugs here have been hard to spot in-game (cf. the "ContaminatedWaterFraction derived from soil-side" comment at line 138-143 documenting a previously-shipped wrong answer).
- **Effort:** M

### 2.2 `EcologyFieldUpdater.AccumulateEntities` + scratch aggregation → Core

- **Where:** `src/Keystone.Mod/Ecology/EcologyFieldUpdater.cs:441-590`
- **Now:** `ProcessUnit`'s aggregation logic (per-channel contributions sum, divide by sample count, hand to `RegionEcologyField.WriteChunk`) is pure. `AccumulateEntities` is mixed: reads the `BlockObject` graph (Timberborn) but every decision is "increment a counter if this entity is a NaturalResource, not Keystone-class-stamped, has a known blueprint, and isn't dead."
- **Port needed:** `INaturalResourceEnumerator` (or extend `IBuildingQuery`) with `EnumerateNaturalResourcesAt(int x,y,z, Action<NaturalResourceProbe>)` where probe is `{ string BlueprintName; bool IsKeystoneOwned; bool IsDead; }`. Adapter implements over `IBlockService` + `NaturalResource`/`LivingNaturalResource`/`KeystoneVariant` components.
- **Move:** Per-chunk fold (`ProcessUnit:441-501`) → `Keystone.Core.Ecology.Fields.ChunkAggregator` taking surfaces list + the four pollable queries. Validation across threshold constants (`SaturationStrengthThreshold = 14.5f`, `WaterContaminationThreshold = 0.05f`) becomes testable.
- **Testable:** per-channel mean correctness; per-chunk dedup; entity-channel routing; dead-natural catch-all bucket; the **Keystone-owned exclusion** invariant — silent failure mode is "decor-dense chunks self-amplify," exactly the kind of bug a unit test should catch and a player almost certainly won't.
- **Effort:** M–L

### 2.3 `WetlandMistDirector.RollDailySchedule` + `TryScheduleTile` → Core

- **Where:** `src/Keystone.Mod/Atmosphere/WetlandMistDirector.cs:268-541` (logic), `547-622` (Unity spawn)
- **Now:** Schedule-building (per-chunk Wetland gate + Maturity, per-tile neighbour check, deterministic seeded RNG, water-depth gate, day-wrap on despawn) is engine-agnostic. Unity only enters in `SpawnMistAt` and the `Tick` driver.
- **Move:** `MistScheduler` Core class producing `ScheduledMist` records given clock + region + field state + water query. Mod-side keeps Unity instantiation + active-list lifecycle.
- **Testable:** foggy-night gate determinism, neighbour-4 chunk-edge behaviour, day-wrap despawn timing, the "mid-window load → skip today" rule (documented and tricky), water-depth band exclusion. Currently zero tests — this class has the most subtle time-handling in the project after `RollingSweepTicker`.
- **Effort:** M

### 2.4 `AttritionHandler` — pull rule-evaluation loop out of entity walk

- **Where:** `src/Keystone.Mod/Recipes/AttritionHandler.cs:119-228`
- **Now:** Probability resolution (`EffectiveProbability`), habitat predicates, recipe-class/vanilla-species matching, and "should this entity die this cycle" are pure given a small probe struct per entity. Bernoulli roll is Core-friendly with injected `Random`. Only entity walk + `ApplyAction` (which calls `EntityService.Delete` or sets `KeystoneFlourish` state) are irreducibly Mod.
- **Port needed:** Nothing new; the flora-probe shape from §2.2 covers targeting predicates.
- **Move:** `AttritionEvaluator` Core class that, given a recipe bucket, a list of target probes, a `RegionEcologyField` sample, and a `Random`, returns `(probe, action)` pairs. Mod-side handler converts entities to probes, calls evaluator, applies actions.
- **Testable:** ScaleBy-channel interpolation, ProbabilityAtMin at the field-null edge, habitat include/exclude precedence, Class-D vanilla-species targeting, "destroyed entity not re-rolled by next recipe in bucket."
- **Effort:** M

---

## Tier 3 — judgment calls (borderline)

- **3.1 `BuildingCatalogLoader` capability synthesis** — pure functions but `Blueprint.Specs` / `TemplateModule.Decorators` reads need an `ISpecCatalogQuery` port that's more glue than the win justifies for a one-shot PostLoad. Skip.
- **3.2 `FloraCatalogLoader.BuildEntries` / `ClassifyKind` / `ExtractFaction`** — 5-line pure functions, but mostly `ComponentSpec.GetSpec<T>` plumbing. Skip unless you want `ExtractFaction` boundary cases tested (cf. global memory `extractfaction` — has bitten before).
- **3.3 `ChunkRulesApplier.ProcessUnit`** — `progress` derivation is Core-shaped but the handler interface is Mod. Leave it unless §2.4 lands and the interface naturally migrates.

---

## Anti-recommendations — don't move these

- **All `*Spec` and `*Component` types** (`KeystoneVariant`, `KeystoneFlourish`, `KeystoneBiomeLevelsSpec`, `KeystoneRecipeBookSpec`, all spawn handlers): irreducibly Timberborn-shaped (`BaseComponent`, `[Serialize]`, `ComponentSpec` base).
- **`KeystoneConfigurator`, `KeystoneModStarter`**: Bindito + lifecycle wiring.
- **`HarmonyPatches/*`**: Harmony depends on Timberborn assemblies.
- **`FaunaCycleTicker`, `FaunaSpawnDrainer`**: Decision loops *look* extractable but each touches `KeystoneFaunaRegistry.Entry.Position`, `EntityService`, `TemplateCollectionService`, and `KeystoneFrustumFilter` (Unity Camera). The genuinely-pure island here is the capacity formula `floor(score · capAtSat · mult)` — 4 lines, duplicated in two files, not worth promoting alone.
- **`KeystonePersistence.Load/Save`**: Already uses the port pattern correctly (`SnapshotCodec` is Core).
- **`RegionUpdater`**: Debounce/dirty-set logic looks extractable but `BlockObject`, `BlockObjectSetEvent`, `PositionedBlocks` are all Timberborn.
- **`SpawnHandlerBase<TRecipe>`**: Looks Core-shaped but `IsMarked(Vector3Int)` and per-handler RNG/scratch are intermingled with Mod subclasses. Primitives it uses (`FlourishThreshold`, `WeightedPick`) are already Core.

---

## Cross-cutting note

The existing port/adapter convention (described in `src/Keystone.Mod/Recipes/README.md` and `src/Keystone.Core/Ports/README.md`) is correct and the Tier 1/Tier 2 candidates extend it rather than rethinking it. Two new ports proposed (`IPerfScope` for the sweep ticker; `INaturalResourceAtTileQuery` for chunk aggregation + biome adapter) sit cleanly alongside the existing eight.
