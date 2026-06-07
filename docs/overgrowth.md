# Overgrowth — dead-tree recovery (GitHub issue #33)

Nature reclaims deadwood: a dead tree goes **barren → overgrown → reseeded
into a new living tree**, while the overgrowth itself lives, dries, and
dies as its own organism. Implemented as an **additive overlay on existing
trees** — the host tree (vanilla or faction) is never modified, keeping
all its native behaviour (growth, cutting, reproduction). This is a
"Class D + data" augmentation, **not** a new spawn class.

See `src/Keystone.Mod/Overgrowth/` for the code and its README.

## Player-facing arc

```
barren dead tree  →  overgrown (flourishes appear)  →  replaced by a new seedling
                                                        (overgrown from the start)
```
…happening only where the biome is recovering. In drought it stalls or
reverses, and the overgrowth visibly dries and dies.

## Two independent state axes (per tree, on `KeystoneOvergrowth`)

### Axis 1 — succession phase
| Transition | Gate |
|---|---|
| **Barren → Overgrown** | Grassland/Forest **biome maturity ≥ threshold** at the tile, then a random roll. Applies to **living and dead** trees (living-tree decoration is in, but rate-limited — see Perf). |
| **Dead → Reseeded** | **Dead trees only.** Requires **both** (a) **reclamation maturity ≥ threshold** and (b) sufficient biome maturity. The host need **not** be overgrown — the reclamation clock runs on barren dead trees too (overgrowth is purely cosmetic). Effect: delete the dead tree, spawn a new seedling from the **Class-D spawn table** (birch-heavy, others by existing weights — no special "same species" rule), enable overgrowth on it immediately, **and drop the felled tree's wood onto the new seedling's own `GoodStack` for a lumberjack to haul** (mimics a real cut). |

**Rate = the existing per-level `BiomeLevel.Mode`** (no new rate machinery):
- **Living trees → `Deterministic`** levels — hash-gated, coverage **capped** at
  `Density × progress`. Decorates up to a fixed fraction and no further (keeps a
  boring area from over-growing).
- **Dead trees → `Stochastic`** levels — per-cycle RNG roll that **accumulates**;
  every dead tree eventually overgrows → "slowly but surely all replaced."
- So they are **separate levels** (different Mode, unrelated maturity bands). An
  `OvergrowthEntry`'s `Target` (`Dead`/`Live`) tells the handler which tree state
  to act on; the level's `Mode` drives the rate shape — the entry carries **no**
  probability of its own.

This means `OvergrowthHandler` **extends `SpawnHandlerBase<OvergrowthRecipe>`** and
reuses its full deterministic/stochastic dispatch; the only override is
`OnRecipeChosen` → "find a tree of `Target` state at the surface and `Apply()`"
instead of spawning a blueprint. `OvergrowthEntry` mirrors a spawn `RecipeEntry`
(`Biome`, `Level`, `Weight`, `Filter`, `Composition`) + a `Target` field, living in
a new `Overgrowths` array on `KeystoneRecipeBookSpec` (parallel to `Attritions`).
(Classes A–E are taken — A decoration, B block, C respawnable, D vanilla flora, E
fauna — so overgrowth is a new rule *family*, not a class.)

### Axis 2 — reclamation maturity (the gameplay clock, decoupled from the visual)
- **Maturity points**, accrued by a **dead** host: `+R`/game-day while the
  tile is moist; **`−2R`/game-day while dry/contaminated**; floored at 0.
  (Slow accrue, fast decay — the mod's grammar.) These gate the reseed
  transition; drought/badwater erode recovery progress.
- **Runs whether or not the tree is overgrown.** The visual overgrowth and
  this clock are independent, so the player can throttle the graphical
  overgrowth rate without changing replacement speed (two separate sliders,
  `KeystoneOvergrowthSettings`). A living host has no clock.
- The **overgrowth overlay's** own reversible **alive ↔ drying** look is
  moisture-derived by the decoration controller; its terminal **death** +
  **cleanup** are delegated to existing systems (below). That's cosmetic and
  separate from the reclamation clock above.

## Death & cleanup — reuse existing systems, extended to a new target (DONE)

The overgrowth is a component on the tree + a registry decoration, **not** a
Keystone entity, so each path below adds a small code path to *recognise
overgrowth as a target* — reusing the existing policy/cadence.

- **Badwater → instant self-kill.** `KeystoneOvergrowth.Tick()` runs the
  **same** predicate Class B uses (`FlourishVisuals.ShouldDieFromBadwater`
  + `BadwaterContaminationThreshold`), every tick: standing in contaminated
  water (depth > 0, contamination > 0.1) → `Kill()` outright. Also stops
  badwater from reading as healthy "moisture" for maturity.
- **Dry biome → attrition kill.** The Dry attrition rule lists the
  **`"Overgrowth"` token in its `Classes`** (`["B","C","Overgrowth"]`);
  `AttritionHandler` Kills the overgrowth on the tree at that tile on the
  same filter/probability it kills irrigated flourishes. (A token in the
  existing `Classes` list — uniform with B/C targeting, no special flag.)
- **Cleanup = decay ticker.** `KeystoneOvergrowthDecayTicker` mirrors
  `KeystoneFlourishDecayTicker` (~10%/day) but `Clear()`s dead overgrowth
  (`KeystoneOvergrowth` is `IRegisteredComponent` for enumeration).
- After cleanup the tree returns to **barren** and can **overgrow again** if
  biome maturity later recovers (cyclical — drought *interrupts* recovery,
  doesn't permanently disqualify the tree); maturity points start fresh.

## Drivers (who runs what)

| Concern | Owner |
|---|---|
| Overgrow + maturity accrual/decay + reseed | **`OvergrowthHandler : IRuleHandler`** on the existing `ChunkRulesApplier` per-chunk pass — it already resolves biome + maturity per surface, so the ecology gates come for free (same infrastructure as Class A/B/C/D spawn + attrition). |
| Reversible alive/drying visual | `FloraLifecycleMoistureController` on the decoration (per-tile moisture). |
| Terminal death | Dry-biome attrition (extended). |
| Removal of dead overgrowth | Decay ticker (extended). |

## Attachment & persistence (built, validated)

- Attached to **every tree** via `AddDecorator<TreeComponentSpec, KeystoneOvergrowth>`
  — vanilla and any faction's, no hardcoded list. **Water-based trees**
  (`FloodableNaturalResourceSpec.MinWaterHeight > 0`) self-filter out.
- **Persisted** (`IPersistentEntity`): is-decorated + `decorationId`
  (+ phase, dead flag, maturity points once those land). The decoration
  GameObject is non-persisted and re-spawned from `decorationId` in
  `InitializeEntity` on load.
- **Save-portable**: removing Keystone strips the orphan component data and
  loads the trees intact (verified) — persisting on vanilla trees is safe.

## Performance

Activation rides the **low-frequency chunk pass**, never a per-tree
per-frame tick. The cost to watch is the **count of simultaneous overlays**,
not the bookkeeping. Living-tree overgrowth is deliberately **lower
probability/density** than dead-tree (poor visibility + cost), tuned via the
handler's roll.

## Species & timing

- **Species** on reseed: the Grassland Class-D weighted table (reuse
  `ClassDSpawnHandler`'s pick). Options open for biome-specific tables later.
- **Timing**: transitions on a **10–30 game-day** scale, randomized, with
  minimums — never a deterministic timer.

## Build slices

- **Done**: mechanism + lifecycle-managed decoration overlay + persistence
  (dev-triggered, all land trees).
- **C1**: dead-tree detection + maturity points (accrue/decay) + integrate
  the overgrowth into Dry attrition (kill) and the decay ticker (cleanup);
  `KeystoneOvergrowth` → `IRegisteredComponent`.
- **C2**: the `OvergrowthHandler` succession state machine on `ChunkRulesApplier`
  — biome-maturity-gated overgrow + maturity-gated reseed flag.
- **C3 (DONE)**: the actual reseed. A new `Reseed` target on the overgrowth
  recipe family; `OvergrowthHandler` gates on (host tree dead) + (overgrowth
  alive, not killed) + (`Maturity ≥ MaturityThreshold`) + (biome maturity, via
  the recipe's level band), then calls `OvergrowthReseeder`:
  1. read the dead tree's `Cuttable.Yielder.Yield` (full wood for a grown
     drought-killed tree; 0 for a dead sapling),
  2. `EntityService.Delete` the dead tree (frees the tile synchronously; fires
     the standard removal events),
  3. weighted Class-D pick from the recipe's `SourceLevel` table →
     `ClassDSpawnHandler.TrySpawnClassD` at the same tile,
  4. `Apply()` the overgrowth onto the new seedling (overgrown from the start),
  5. `GoodStack.EnableGoodStack(yield)` on the **new seedling's own** stack +
     register with `GoodStackService<LumberjackFlagSpec>` so a lumberjack
     hauls it (the retriever has no alive/dead host check — wood is haulable
     off a living seedling).

  No free-standing log-pile entity is created (vanilla has none — felled wood
  always sits on a host); the new seedling *is* the host, which collapses the
  seedling-vs-wood tile conflict. Grassland content authored; **needs a Unity
  Mod Builder rebuild** to reach the game.

## Open / tune in-game

Exact maturity thresholds and `R`; living-tree overgrowth density; the
trunk **ivy** content (a flat composition dropped into the same overlay);
whether overgrowth ever carries a gameplay effect vs purely visual.
