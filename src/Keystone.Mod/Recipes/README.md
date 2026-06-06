# Keystone.Mod.Recipes

Mod-side glue between the Core biome / level abstractions and the
spawn drivers that put flora on tiles. Four responsibilities:
specs that declare data (recipes, level ranges, per-entity
variants), catalogs that drain those specs at PostLoad, rule
handlers that contain the per-class spawn logic, and a single
chunk-driven scheduler (`ChunkRulesApplier`) that visits every
chunk each cycle and applies the active level's rules via the
registered handlers.

## Specs (data layer)

| Type | Role |
|---|---|
| `KeystoneRecipeBookSpec` | A list of recipe entries on a Keystone-authored "recipe book" blueprint. Each entry carries `Class`, `BlueprintName` (or `BlueprintNames` array), `Biome`, `Level`, `Filter`, `Weight`. The catalog walks every book at PostLoad and dispatches by `Class`. **Recipes are decoupled from the blueprints they reference**: the same blueprint can appear in multiple recipes with different classes. Mods extend by shipping their own recipe-book blueprints. |
| `KeystoneBiomeLevelsSpec` | A list of level entries `(LevelId, LowerMaturity, UpperMaturity, Density)` on a level-table blueprint. Empty `Biome` = default ladder applied to every biome; non-empty `Biome` = per-biome override. `BiomeLevelCatalog` populates the Core `BiomeLevelTable` from these. |
| `KeystoneVariantSpec` | Marker spec on every Keystone-spawn-eligible blueprint. Triggers attachment of `KeystoneVariant` component carrying the per-entity class designation. Required for Harmony selection-suppression to apply on save/load (runtime-added components don't survive Timberborn's entity persistence; spec-driven decoration does). |
| `KeystoneFlourishSpec` | Marker spec triggering the `KeystoneFlourish` lifecycle component (visual phase × life-status × health) on the entity. Independent of class — a blueprint carrying it gets lifecycle visuals regardless of which recipe spawned it. Lives in `Flourish/`. |

## Per-entity components (runtime layer)

| Type | Role |
|---|---|
| `KeystoneVariant` | `BaseComponent + IPersistentEntity`. Holds the `Class` string (`"A"`, `"B"`, `"C"`) stamped at spawn time by the handler. Persisted across save/load. The Harmony selection-suppression patches gate on `Class == "B"`. |
| `KeystoneBiomeLevels` | No-op marker component paired with `KeystoneBiomeLevelsSpec` — exists only so the spec surfaces via `ISpecService.GetSpecs` for the level catalog. |

## Catalogs (PostLoad layer)

| Type | Role |
|---|---|
| `BiomeLevelCatalog` | `IPostLoadableSingleton`. Two-pass merge: defaults first (apply to every `BiomeKind`), then per-biome overrides (overwrite matching level ids). Pushes results into Core `BiomeLevelTable`. |
| `FlourishCatalog` | `IPostLoadableSingleton`. Walks every `KeystoneRecipeBookSpec` and dispatches each entry by `Class` (`"A"` / `"B"` / `"C"` / `"D"`). Indexes by `(BiomeKind, LevelId)` tuple for fast handler lookup. Code-side fallback via `MultiBind<ClassXRecipe>` for prototypes. |
| `BlueprintResolver` | `IPostLoadableSingleton`. At PostLoad, eagerly walks every `BlockObjectSpec` once and caches `name -> Blueprint` so subsequent lookups are O(1). Mod reload re-runs PostLoad and rebuilds the cache. Lookups for unrecognised names log once and cache `null`. |

## Scheduler and rule handlers (per-cycle layer)

A single scheduler — `ChunkRulesApplier` — visits every `(region,
chunk)` pair once per cycle in randomised order. Per chunk it
walks every surveyed surface inside, resolves the surface's dominant
biome + maturity per tile (Suitability-pass gate + max-Suitability tiebreak;
see `ChunkBiomeSampler.SampleDominantBiome`), iterates the active
levels for that biome, and dispatches to each bound `IRuleHandler`.
The dominance read happens per surface, not per chunk, so the
bilinear smoothing in `ChunkBiomeSampler` survives across chunk
boundaries -- a per-chunk read would carve 4-tile hard edges along
the chunk grid.

The shared dispatch shape inside a handler:

```
sample dominant biome at tile  →  for each active level for that biome:
  pre-filter recipes (per-recipe Filter, e.g. "WaterEdge")  →
  activation gate                                            →
  pick recipe                                                →
  spawn (per-class lifecycle).
```

The four spawn handlers differ in **lifecycle**, not dispatch:

| Type | Lifecycle differences |
|---|---|
| `ClassASpawnHandler` | Per-cycle reconciliation. Decoration is non-`BlockObject`; spawned via `KeystoneDecorationRegistry`. `OnCycleStart` clears a "seen" scratch; `OnCycleComplete` despawns anything in the live set not seen this cycle (sub-threshold tiles drop out). |
| `ClassBSpawnHandler` | One-shot persistent. Spawns via `BlockObjectFactory.CreateFinished`, stamps `KeystoneVariant.Class = "B"` so Harmony patches suppress selection / demolish. **No attempt-memo**: Class B's permanence is enforced by Harmony (the player can't remove the entity), and the runtime `IBlockService` occupancy check catches "already spawned, still there" across cycles and save/load. |
| `ClassCSpawnHandler` | Same as B but stamps `KeystoneVariant.Class = "C"`. The player *can* demolish a Class C entity; on the next cycle the handler re-evaluates the now-empty tile and re-spawns if conditions still warrant ("weeds the player can pull out, but they grow back"). Same TileOccupied-driven dispatch as B; the difference is purely in selection/demolish behaviour of the spawned entity. |
| `ClassDSpawnHandler` | Same dispatch as Class B/C but the per-tile activation gate is an **RNG roll against `level.Density`** instead of the deterministic hash. Population accumulates over real time as the dice keep rolling. A `(tile, levelId)` memo records successful spawns so the handler doesn't re-spawn a cut tree -- vanilla `ReproducibleSpec` handles regrowth instead. The memo is set on actual spawn success only; failed activation rolls and blocked spawns re-roll next cycle. **Live Class B is not replaced** (retracted 2026-06-06): a Class D activation that fires on a tile holding a live Class B Keystone entity now aborts rather than demolishing it -- vanilla flora no longer supersedes the *live* mini-flourish layer. The handler still clears *dead* Keystone flourishes of any class (biome recovery) and fully-harvested vanilla stumps from the tile before spawning. Live Class B / C / D occupants block (Class C is the player's domain; Class D doesn't stack with itself), as do vanilla buildings and natural-resource entities. **Save/load limitation**: the memo is session-local, so a "save → cut → reload" sequence currently re-enables spawning at the cut tile. Persisting the memo is a future round of work. |

All four spawn handlers extend `SpawnHandlerBase<TRecipe>`, which
owns the dispatch primitives (filter eligibility, activation hash,
weighted pick).

`AttritionHandler` is the second rule type and implements
`IRuleHandler` directly (no spawn-shaped dispatch). Each cycle, per
surface in a dominant-biome chunk at an active level, it finds
Keystone Class B / C entities at that surface and rolls each
`AttritionRecipe`'s per-entity Bernoulli; on hit, applies `Kill`
(set `KeystoneFlourish.LifeStatus = Dead`, visual switches to
`#Dead`, entity persists) or `Destroy` (`EntityService.Delete`).
Class A is parsed but currently skipped — its semantics need to
coordinate with the spawn handler's reconcile-and-despawn loop and
that's a separate design pass. Class D (vanilla flora) is excluded
by design.

Attrition rules live alongside spawn recipes in the same
`KeystoneRecipeBookSpec`, under a separate `Attritions` array of
`AttritionEntry` records (`Biome`, `Level`, `Action`, `Classes`,
`Probability`, `Filter`).

## Two schedulers, by design

Rule application is intentionally separate from chunk-value updates:

| Scheduler | Cadence | Job |
|---|---|---|
| `Keystone.Mod.Biomes.ChunkBiomeTicker` | Frequent (per game-tick) | Update per-chunk Suitability and Maturity. Don't miss ecological events. |
| `ChunkRulesApplier` | One game-day | Apply all level rules to all chunks via bound `IRuleHandler` handlers. |

The split: Suitability / Maturity must update faster than spawns so the spawn pass
sees a settled biome state; spawns must be slower than the channel updates so
entity births/deaths don't flicker.

## Activation rule

Shared by all four classes:

```
# Two-stage activation per tile:
#   Stage 1 (Suitability axis): Suitability-pass gate + max-Suitability tiebreak among passers.
#   Stage 2 (Maturity axis): level gates on the winner's accumulated Maturity.
(dominantBiome, maturity) = SampleDominantBiome(tile)
if dominantBiome is null: skip tile   # no biome's Suitability passes the gate here

for each level in BiomeLevelTable.LevelsFor(dominantBiome):
  if maturity < level.LowerMaturity: skip level

  recipes = catalog.<Class>For(dominantBiome, level.LevelId)
  eligible = recipes filtered by per-recipe Filter at this tile
  if eligible.empty: skip level

  activate? = (Class A/B/C)  ComputeActivation(tile, biome, level.LevelId) < level.Density
            | (Class D)      rng.NextDouble() < level.Density
  if not activate?: skip level

  pickHash = ComputePick(tile, biome, level.LevelId)   // hash-based for all classes
  recipe   = WeightedPick(eligible, pickHash)          // by recipe.Weight
  spawn recipe.BlueprintName at tile (per-class lifecycle)
```

The only per-class difference is the **activation source**: A/B/C use the
deterministic per-tile hash so placements are reproducible across reloads;
D uses an RNG roll so vanilla-flora populations accumulate over real time
without needing to re-derive the same set of trees on every world load.

Three properties fall out of this:

- **Density lives on the level**, not the recipe. Adding more recipes to a `(biome, level)` bucket broadens variety without changing the fraction of tiles that activate per cycle. With density 0.33 and N recipes, exactly 33% of eligible tiles end up with one of the N (whichever one `pickHash` selects).
- **Filter narrows the candidate pool per tile.** Currently `"WaterEdge"` (uses `Keystone.Core.Spatial.WaterProximity.BordersWater`, for Riparian L1) and `"RiverBank"` (uses `Keystone.Core.Spatial.CliffProximity` — `IsBelowNeighbor && !IsAboveNeighbor`, for River L1). Empty filter = no constraint. Recipes with unrecognised filters never fire (warned once at first encounter).
- **Cumulative levels.** As Maturity grows past one level's `LowerMaturity` and into the next's, both stay active simultaneously. L1 + L2 + L3 stack.
- **Class D memo.** Class D additionally remembers `(tile, levelId)` pairs it has spawned at, so a tree the player has cut isn't re-spawned by Keystone (vanilla `ReproducibleSpec` handles regrowth). The memo is session-local; persistence across save/load is future work.
- **Player density multiplier.** `SpawnHandlerBase.GetDensityMultiplier(recipes)` returns a per-bucket scaling factor applied on top of `level.Density` in both activation gates. Class B reads `KeystoneFloraSettings.ClassBDensityPercent` (one global value). Class D reads `recipes[0].Category` and looks up the matching per-category slider (`Trees` / `Bushes` / `Crops`) via `KeystoneFloraSettings.MultiplierFor`. `FlourishCatalog` enforces single-category buckets at PostLoad so `recipes[0].Category` is well-defined. Class A/C don't override the hook; their multiplier is always `1f`.

## Where the recipes come from

Two registration paths, both merged at PostLoad:

- **Blueprint-driven (preferred)**: a Keystone-authored blueprint
  carries `KeystoneRecipeBookSpec` listing many recipe entries.
  Faction-expansion mods can ship their own recipe-book blueprints.
  No C# code required from the modder.
- **Code-registered fallback**: `MultiBind<ClassARecipe>().ToInstance(...)` /
  `MultiBind<ClassBRecipe>...` / `MultiBind<ClassCRecipe>...` /
  `MultiBind<ClassDRecipe>...`. Useful for prototypes or for cases
  where one donor needs to register against multiple biomes / levels
  programmatically.

## Dev placement tool

`Keystone.Mod.Flourish.FlourishPlacementTool` (lives in `Flourish/`,
not here) force-places a Class B recipe on cursor click. It bypasses
the level-activation gate, picks the cursor's dominant biome by
*Suitability* (not Maturity, because Suitability is a faster-to-update signal
that works on freshly-built terrain before Maturity has accrued),
and instantiates a random recipe registered for that biome via
`FlourishCatalog.ClassBForBiome`. Stamps `KeystoneVariant.Class = "B"`
post-spawn so force-placed and handler-spawned entities are
indistinguishable.
