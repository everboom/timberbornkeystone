# Keystone — Roadmap

A working sequence document. Pairs with `DESIGN.md` (what we're building) — this one is *in what order*.

Granularity is intentionally rough at this stage. Phase 0 and early phase 1 are spec'd in more detail because they're closest; later phases are sketched. Re-plan as we learn.

## Mod boundaries

Per `DESIGN.md` § Mod architecture, the project splits into:

- **Base mod: Keystone** (this repo, Phases 1–2). Observation + simulation + content + visuals + animals. The complete faction-agnostic ecology experience.
- **Faction integration mods** (Phase 3, separate repos). Folktails- and Iron-Teeth-themed content layered on top of the base mod via the public API and contracts. Third-party content-mod integration via small `Keystone-Compat<X>` packages.

Roadmap phases below describe what lands in the base mod (Phases 1–2) versus what's deferred to faction mods (Phase 3). The Mod 1 / Mod 2 split from earlier roadmap versions has been collapsed: rather than splitting observation from content into two installable mods, the base mod owns the complete vertical slice — analytics, visuals, animals, wellbeing. Faction mods extend it horizontally with themed content. The earlier "Mod 1 ships standalone as an analytics overlay" framing is retired; the analytics overlay still exists as debug UI but isn't its own deliverable.

## Phase 0 — Substrate

**Goal:** Land the base mod's foundation end-to-end. Regions, catalogs, persistence, perf tracking — everything needed to observe + score + persist game state. **No world modification, no puppet content.** If Phase 0 works, Phase 1's biome detection and scoring build on a solid platform. If it reveals a roadblock (perf, determinism, persistence API gap), we want to know now, not after weeks of content work.

Phase 0 scope is **substrate only** — proving the modding stack and the read pattern end-to-end. The end-to-end "vertical slice with songbirds" framing of an earlier roadmap version moves to Phase 2 (animals are no longer in Phase 1).

Deliverables:

1. ✅ Project skeleton: builds into Timberborn's mod folder, deploys cleanly. (Done.)
2. ✅ Per-surface metadata storage in singletons (`TerrainSurveyor`, `RegionService`, `EcologyFieldUpdater`, `FloraCatalog`, `BuildingCatalog`). The vanilla `Shift+Alt+X` debugger surfaces our cursor overlay for free. (Done.)
3. ✅ Region detection + neighbour topology. Plateau-shaped 4-connected components on `(Z, IsCave, IsSettled)`. Deterministic IDs across runs. (Done — formerly Phase 1A-A and 1A-B.)
4. ✅ Catalogs (flora + building) with role flags, faction extraction, plantable groups. (Done.)
5. ✅ **Persistence.** All Keystone state survives save/load. (Done.)
   - Region identity + clock-stamps survive reload (`RegionService.RestoreCreatedAt`, schema-versioned via `SnapshotCodec`).
   - Two parallel value stores both persist: `RegionValueStore` (per-region kinds) and `ChunkValueStore` (per-chunk kinds — the per-biome Suitability and Maturity channels live here). Originally scoped as per-region only in the roadmap; the chunk store was added as the temporal model moved to per-chunk granularity (see 1A).
   - Per-entity state for Keystone-spawned flourishes persists via `IPersistentEntity` (`KeystoneFlourish` persists `Phase` + `LifeStatus`; `KeystoneVariant` persists the spawning class). Required so attrition kills survive save/load and so Harmony selection-suppression keeps gating on the right class after reload.
   - Pure-derivation state (catalogs, surface surveys, ecology fields) is *not* persisted — rebuilt on load.
   - New-game detection wired off `GameLoader.IsNewGame`; on fresh settlements the warmup seeds Maturity with one notional day at the snapped Suitability and runs a synchronous pre-game rule pass.
6. ✅ Basic logging via `Debug.Log`. (Done; consider a Keystone-specific log channel later.)
7. ✅ **`PerfTracker` service and debug panel.** (Done.) `PerfTracker` wraps subsystem updates with `Stopwatch`; `KeystonePerfPanel` exposes per-subsystem avg/P99 timings. `KeystoneDebugPanel` provides the high-level overlay (chunk biome Suitability and Maturity, regions, dominant biome at cursor).
8. ⚠️ **Empirical perf investigation.** Continuous in-game visibility is in place via `KeystonePerfPanel`; rolling-sweep cadences (1 game-hour for ecology fields + biome scoring, 1 game-day for chunk rules) have been chosen and informally validated against the existing test maps. No formal report against the budget in DESIGN.md § Performance has been produced yet; the open empirical questions there are still on the table.

What's explicitly *not* in Phase 0:

- **Biome classification.** Phase 1A.
- **Long-term Maturity computation.** Schema is provisioned in Phase 0; computation is Phase 1B.
- **Connectivity beyond region neighbours.** Phase 2A.
- **Visual flourishes.** Phase 1D.
- **Animal puppets + audio.** Phase 2B.
- **Custom player-facing UI.** Vanilla debug tools only in Phase 0.
- **The temporal eco-health model.** Phase 1B.

**Exit criteria:** A player loads a vanilla Timberborn map. The mod scans it, builds the region/flora/building substrate, computes one minimal score per region, persists everything to the save. Save and reload — region ids and the score survive. PerfTracker confirms the substrate stays under perf budget at maximum time speed.

## Phase 1 — MVP: core simulator + visual feedback

**Goal:** Ship a minimum viable ecology mod — the per-chunk biome simulator is in place, the player sees biome-driven visual variety bloom and decay across the map in response to their decisions about water and land, and the mod is stable enough to be played end-to-end. Faction-agnostic, single base mod. **Animals, wellbeing, and cross-region connectivity move to Phase 2.**

### Working from effect axes, not substrate-first

The 1A → 1E breakdown below organizes the *underlying work* by system. Decisions about *what to build first* are driven by the effect axes in `DESIGN.md` § Layers of feedback — eight categories (static visuals, interactive flora, decorative mobiles, animal agents, sound, atmospheric particles, lighting, beaver wellbeing area effects). **Phase 1 commits to axes 1, 2, 6** (visual feedback only); axis 8 (wellbeing) and axes 3/4 (decorative mobiles + animal agents) all move to Phase 2.

Phase 1 is broken into sub-milestones that can iterate as separate releases. Each one stabilizes before the next builds on it.

### Prototype capabilities (validate before designing the rung)

Before building any specific rung of the reward ladder, we need to know each effect axis is technically achievable. These are small standalone validations, not delivered features:

- Place passive non-interactive objects in the world (axis 1)
- Modify existing assets at runtime — material tints, texture swaps (axis 1, alternative path)
- Spawn full game-object trees that grow, are pathing-aware, harvestable (axis 2)
- Cross-faction prefab instantiation — e.g. spawn a Folktails Pine in an IronTeeth game and verify beavers interact with it normally (axis 2, the "free" reward path)
- Spawn / move / animate decorative entities (axes 3, 4 — Phase 2 but cheap to verify in Phase 1)
- Apply a per-beaver wellbeing modifier driven by which chunk the beaver stands in (axis 8) — investigate `RangedEffectBuilding` machinery, decide whether to attach virtual emitters to chunks or hook in via a more direct API
- Emit / control particle systems via runtime API (axis 6)
- Despawn anything we placed — clean up when chunk state changes (all axes)
- *Backlog, not Phase 1:* tile shader overlay (axis 9) — a per-tile render layer driven by chunk state, mirroring how vanilla displays soil-moisture and contamination overlays. Implementation route uncertain; investigate when the existing axes are no longer the bottleneck on player feedback. Prior art to watch: [Ustice/Timberborn-Wildfire](https://github.com/Ustice/Timberborn-Wildfire) — author building this technique for wildfire visualization (per Discord; not yet in the public repo at time of logging).

For decorative content (axes 1, 3, 6), placements regenerate from chunk state + a stable RNG seed each session rather than persisting individually. For full game objects (axis 2), we use the game's own placement persistence. For wellbeing (axis 8), the modifier value derives from current chunk state — no separate persistence layer.

### 1A — Core biome detection

**Reframed since the original roadmap was written.** The 5-biome list + per-region exclusive classification has been replaced by **10 biomes evaluated as continuous channels per 4×4 chunk** — see DESIGN.md § Biomes for the full list (Forest, Grassland, Monoculture, River, Lake, Wetland, Cave, Dry, Contaminated, Badwater). Multiple biomes can have positive Suitability on the same chunk simultaneously; biome "classification" is a *channel state vector*, not a label. (Riparian was folded into Grassland in v0.6, then reintroduced in v0.6.4 as an eleventh, *per-tile* "partial biome" — its own `BiomeKind` and dominance participant, but sourced per-surface and excluded from the per-chunk channel set above; see DESIGN.md § Biomes and the Backlog's open re-fold proposal.)

**Why the change:** per-region exclusive labels couldn't represent transitions or mixed conditions (a partially-treed irrigated patch is part Forest, part Grassland). Per-chunk continuous channels fall out cleanly from the rolling-sweep architecture and feed the per-tile bilinear interpolation that drives content placement and the upcoming wellbeing layer.

**What survives from the original framing:**
- Regions remain the *identity / persistence / topology* unit. `RegionService.Index()` flood-fills on `(Z, IsCave)` connected components, clock-stamps regions via `IClock` (`GameTimestamp` + `WeatherKind`), and surface→region lookup is O(1).
- Cave is a region-splitter at the structural level (roofed surfaces are their own region regardless of lateral neighbours).
- Cliff is *not* a tile tag or a biome — it's an inter-region edge property. Confirmed in code.

**What changed:**
- Biome state lives on **chunks** (`ChunkValueStore`), not regions. The roadmap's "region-level biome classification" framing is obsolete.
- Sub-zones (1A-D) are obsoleted by chunk granularity. A region's structurally-uniform interior naturally develops different per-chunk states without needing a separate sub-zone abstraction.

Sub-milestones:
- ~~**1A-A:** Surfaces become pure raw data.~~ ✅ Done.
- ~~**1A-B:** Region indexing — each plateau becomes a `Region` object with id, age, members.~~ ✅ Done.
- ✅ **1A-C:** Event-driven region updates. `RegionUpdater` listens for terrain/building events, debounces, and re-Indexes; flushed pre-save by `KeystonePersistence.Save` to prevent saving stale topology.
- ⊘ **1A-D:** Obsoleted by per-chunk continuous scoring (see above).
- ✅ **1A-E (new):** Per-chunk biome scoring pipeline. `ChunkBiomeTicker` runs an hourly rolling sweep: `ChunkBiomeAdapter` builds `ChunkBiomeInputs` from ecology fields, `BiomeSuitabilityUpdater` drifts the per-biome Suitability channel toward its target. Suitability is clamped `[0, 1]`; targets above 1 model sustained stress, deep negatives model contamination/drought (see DESIGN.md § Temporal model).

### 1B — Temporal eco-health model

✅ **Done**, per-chunk rather than per-region. Implemented as two channels in `ChunkValueStore`:

- **Suitability** (`keystone.chunk.biome.<biome>`): hour-scale short-term channel, clamped `[0, 1]`. Drifts toward a target computed from current ecology-field state. Linear rise; proportional drop on stress. Negative target magnitude encodes stress severity (drought ≈ -1000, contamination ≈ -10000), so the same drift formula handles both gentle decay and catastrophic crash. See `BiomeSuitabilityUpdater` and `BiomeTargets`.
- **Maturity** (`keystone.chunk.maturity.<biome>`): day-scale long-term integration in game-days. Hybrid model — **exponential accrue, linear decay**. Accrue under `dM/dt = α·Suitability − β_accrue·M` toward the per-biome asymptote `α/β_accrue` (Forest/Grassland/Wetland/River/Lake/Riparian/Cave 30 d, Badwater 15, Contaminated 12.5, Dry 10, Monoculture 3.5). Decay at the matrix-driven `(decaying, dominant) → clear-time` rate (Badwater dominant 0.5 d, Contaminated 1 d, water-family 5 d, land-family 7 d, etc.) with succession-free peer-drift cells, a Dry-column kill order, and a drought-intensity ramp for water-family biomes. Toxic scars (Badwater, Contaminated) decay at a flat baseline 1/day once they're no longer dominant; while they hold dominance their Maturity holds. See `BiomeMaturityUpdater` and `MaturityParameters`. Implementation matches DESIGN.md § Maturity.

Both channels persist via `KeystoneSnapshot` + `ChunkValueStore`. Recovery-slower-than-decline is encoded structurally in the two-channel split (Maturity can only rise as fast as Suitability allows, and decays faster than it accrues under stress). New games seed Maturity with one notional day at the snapped Suitability during `KeystoneStartupWarmup`.

### 1C — Level + class architecture (cross-cutting)

✅ **Done.** Not in the original roadmap; emerged during 1B/1D as the bridge between the Maturity channel and content placement. Two coupled systems:

- **Biome level table** (`BiomeLevelTable`): per-biome ladder of `(LevelId, LowerMaturity, UpperMaturity, Density)` ranges. Populated at PostLoad by `BiomeLevelCatalog` from `KeystoneBiomeLevelsSpec` instances (default ladder + per-biome overrides). Level count, range bounds, and density caps are per-biome — Dry has L1 (kill non-dry @ 0.5) and L2 (spawn dry @ 1.0); River has only L1; Grassland uses the default ladder.
- **Content class taxonomy** (Class A through E): see DESIGN.md § Content classes. Class A through D are implemented; E (mobile fauna) is Phase 2. Per-class handlers (`ClassASpawnHandler` … `ClassDSpawnHandler` plus `AttritionHandler`) plug into `ChunkRulesApplier` via `IRuleHandler` and run on the same daily rolling sweep.
- **Recipe book** (`KeystoneRecipeBookSpec`): blueprint-side data carrying `(Class, Biome, Level, BlueprintName, Filter, Weight)` entries plus attrition entries (`Kill`/`Destroy` actions with optional channel-scaled probability and habitat excludes). `FlourishCatalog` walks every book at PostLoad and indexes by `(Class, Biome, LevelId)`. Mod-extensible by construction — third-party content mods ship their own recipe-book blueprints, no C# required.
- **Spatial filters** (`IRecipeFilter` + `RecipeFilterRegistry`): named per-tile eligibility checks. Implemented today: `WaterEdge`, `RiverBank` (cliff-adjacent below-neighbour), `ContaminatedTile`. New filters plug in via `MultiBind<IRecipeFilter>`.

The whole system is described in DESIGN.md § Content classes and § Class A architecture; the roadmap entry exists so the cross-cutting work is at least *acknowledged* in sequence order.

### 1D — Visual + atmospheric feedback layer

✅ **Done.** Mechanism, content, and the cross-cutting primitives that enable each axis. Originally spec'd as Mod 2 work; collapsed into the base mod when the Mod 1 / Mod 2 split was retired.

**By axis:**
- **Axis 1 (static visual flourishes):** mechanism + content shipped. The Class A/B/C/D handlers and recipe-book pipeline (see 1C) drive per-biome flourish placement keyed off Maturity levels. `tools/generate-flourish-blueprints.py` produces flourish blueprints from a Python catalogue; ~68 flourish blueprints across 7 biomes (Grassland, Riparian, Wetland, River, Lake, Cesspool, Dry), plus rock-cluster geology via the Worldgen level. `KeystoneFlourish` carries the visual lifecycle (Phase × LifeStatus × health) driven by `WateredNaturalResourceSpec` / `FloodableNaturalResourceSpec`. `KeystoneRockTint` does per-tile material swap on rock clusters keyed on local water / contamination / moisture signals.
- **Axis 2 (interactive flora variety):** mechanism + content shipped. `CrossFactionCollectionProvider` + `TemplateCollectionServicePatch` pull the other faction's natural-resource collection into the active faction's `TemplateNameMapper` and strip `Plantable`/`Gatherable` so the active faction's UIs don't crash on cross-faction donors. Class D recipes reference these donors as vanilla blueprint names — no new spec authoring required.
- **Axis 6 (atmospheric particles):** mechanism + first content shipped. `KeystoneDecorationRegistry` hosts non-block-object particle GameObjects. `WetlandMistDirector` is the prototype of the **time-of-day director** pattern — a per-tick poll that schedules per-tile spawn / despawn events with deterministic `(day, x, y)` seeding and triangular fade-in / fade-out on `ParticleSystem.rateOverTime`. Wetland morning mist (Ground Fog over deep-interior wetland chunks, capped at 1 per chunk, with a per-tile water-depth gate in `(0.1, 0.5)` voxels so dry-mud pockets and open-water tiles are skipped) is the first concrete content; the director pattern is reusable for additional atmospheric content (fireflies, contamination haze) in 1E if scope allows.
- **Attrition system:** shipped (not in the original roadmap). `AttritionRecipe` + `AttritionHandler` apply `Kill` or `Destroy` actions to Class B/C entities on (biome, level) buckets, with optional channel-scaled probability (e.g. river attrition scales with `WaterFlowMagnitude`) and habitat-exclude filters (e.g. spare Dry-habitat plants from Dry-biome attrition). Three rules ship today: Dry-biome kills non-dry plants (50% per cycle), Contaminated-biome kills (50% baseline + 100% on contaminated tiles), River destroys (25%→100% flow-scaled).

**Cross-cutting primitives shipped during 1D:**
- **Trait-based decomposition of content classes** documented in `DESIGN.md` § Content composition. Class B/C semantics defined in terms of orthogonal axes (reactivity, lifecycle / interaction); A/B/C/D become convenience aliases for useful trait compositions.
- **`docs/timberborn-specs.md`** — 2041-line spec / decorator graph reference covering all 1033 `AddDecorator<...>` pairs across 194 vanilla configurators, plus per-symptom diagnostics and a case study. Future blueprint authoring is a doc lookup rather than a decompile pass.
- **Worldgen level (`RunAtStartup: true`)** — a one-shot rule pass run during the new-game warmup, distinct from the per-day rolling cycle. Generic primitive for geological / worldgen content (rock clusters today; ruins and cave dressings later).
- **Biome-agnostic recipes** — a recipe with an empty `Biome` field is internally expanded to one per `BiomeKind`. Lets geological content ship as one authoring entry rather than per-biome duplicates.
- **Time-of-day director pattern** — `WetlandMistDirector` is the prototype; reusable for any future dawn / dusk ephemera that doesn't fit the daily recipe cycle.

**Deferred to 1E / beyond MVP:**
- More atmospheric content via the time-of-day director: fireflies over healthy meadows, contamination haze, etc. Pattern is shipped; only the content authoring + tuning is missing.
- **"First sighting" notifications.** Not started; cheap to implement, parked for 1E if scope allows.

Sound (axis 5) and lighting / post-process (axis 7) are deferred to nice-to-have — see `DESIGN.md` § Layers of feedback.

### 1E — Polish, balance, MVP ship

✅ **Done in spirit.** No dedicated "MVP release" commit and no hard cutover — tuning has continued opportunistically alongside Phase 2 fauna work rather than as a separate consolidation pass. The biome simulator + visual feedback is stable enough that wildlife was promoted from "coming soon" to shipped (commit 9c0f784), which is the practical equivalent of clearing the Phase 1 bar.

Outstanding 1E items that survive into ongoing polish, *not* gating Phase 2:

- **Per-biome content review.** Dry, River, Lake remain sparser than Wetland / Grassland. Address opportunistically.
- **Performance close-out** of the open empirical questions from Phase 0 item 8 — still no formal report against the 1ms-per-game-tick budget. Fauna load adds new questions worth folding in.
- **Content gap fills.** Atmospheric content via the time-of-day director (fireflies, contamination haze). First-sighting notifications. Class C revisit (markable for beaver destruction) — parked pending the open service-registration question in `docs/timberborn-specs.md` § Case study.
- **Localization.** Add as new player-facing strings appear.

**Exit criteria for Phase 1 (met):** A player loads any vanilla Timberborn map, with either faction, and over the course of play sees biome-driven visual variety emerge from their water/land decisions — Wetlands at slow shallow water, River decoration at fast water, riparian plants along Grassland banks, open Grassland in irrigated land, Dry content where it stays parched, dead/dying flourishes where it crashes. Save and reload preserves state.

## Phase 2 — Full ecology: beavers + animals

**Goal:** Take the Phase 1 MVP and complete the ecology vertical slice — the player feels the world's health through their beavers' wellbeing, and sees and hears animals respond to biome conditions across the map. Same base mod; no separate companion. Locks down the public API so Phase 3 faction mods can take a hard dependency.

Order driven by what needs the most prototyping (high uncertainty → land first):

### 2A — Connectivity layer

✅ **Done in a different shape than originally framed.** Rather than a per-region neighbour graph, connectivity materialised as **chunk clusters** (`ChunkCluster`) — adjacent same-biome matured chunks unioned into a single capacity unit. Built event-driven on chunk biome-state changes; covered by 24 unit tests. The clusters are the consumer-facing connectivity primitive that 2B / 2C actually needed; the more general region-graph framing has been left in case a future consumer (long-range animal migration, cross-region species spread) needs it.

### 2B — Animal agent simulation (axes 3 + 4)

🟡 **Active — well past prototype.** Shipped today:

- **Class E fauna** as a content class with its own recipe shape and lifecycle (`a225914`).
- **Terrestrial agents** — KeystoneDeer (with VAT-baked animation), KeystoneBull, KeystoneCow. Region-bounded A* pathing, wander-and-idle, interior-only paths, per-species walking + animation speeds (`e60f066`), randomised spawn yaw, idle-position offset from tile centre.
- **Aquatic agents** — KeystoneFish1 / Fish2 with water-gated dev tool and pathfinding (`1f248c1`).
- **`BaseFaunaAgent`** common base for cluster-affinity self-check, stuck-despawn rule, and recipe dispatch (`99da7d9`, `9af00f8`).
- **Cluster-capacity reconciliation, decide/execute split** (`3d683c1`, `9af00f8`) — the dawn-burst handler is gone. A 6-game-hour rolling sweep walks every cluster, culls surplus immediately (frustum-gated), enqueues clusters with deficit. A per-frame drainer (`IUpdatableSingleton`, so it also ticks during pause) pops the queue, recomputes per-cluster deficit from a registry walk, instantiates one fauna off-frustum per visit. Per-frame instantiation cost is hard-capped; no game-speed gate (populations converge during fast-forward too).
- **Off-frustum spawn and cull**, plus interior-only spawn-tile validation matching the agent's own walkability predicate, so newly-spawned agents don't strand on region-edge tiles.
- **`ChunkClusterIndex.Version`** stamps every rebuild; the fauna pipeline tolerates stale ids via per-visit re-resolve (drainer) and a narrow cull-arm guard (sweep).
- **Eviction on topology change** (`a8463c2`) — fauna evict when their tile or hosting building changes.
- **Cross-faction natural-resource patching** so fauna interact cleanly with both factions.

Puppets remain unpersisted (regenerated from cluster state + RNG).

Outstanding 2B work: more species variety, audio (axis 5 — still unstarted), perf review with realistic fauna populations on large maps.

### 2C — Tiered species capacity

🟡 **Partial.** The capacity unit (cluster) and per-recipe capacity rolls ship today via the chunk-cluster system. What's *not* yet implemented from the original 2C framing: the **three-tier pioneer / established / apex** structure gated on Maturity thresholds. Today's capacity is biome × level × recipe-weight; tiering across recipes is implicit in the level table rather than an explicit ladder. Decide during 2D / 2F whether the explicit three-tier ladder buys enough beyond the level-table approximation to bother.

### 2D — Beaver wellbeing-from-ecology (axis 8)

✅ **MVP done, in a different shape than originally framed.** The original sketch was an ambient per-beaver area effect — "a beaver standing in a healthy biome chunk gets a passive modifier." During the spike, that approach hit two problems: (a) no clean public API to apply per-tick wellbeing without Harmony or virtual building-emitter proxies, and (b) the cause/effect was hard to surface to the player ("why did my wellbeing tick up?"). So the design pivoted to **visit-based satisfaction at named contemplation buildings**, which is legible, faction-symmetric (well, intentionally Folktails-only — see below), and zero-Harmony.

Shipped:
- New `KeystoneNature` need group with three biome-flavored needs (Forest, Grassland, Wetland), each contributing +1 favorable wellbeing.
- `KeystoneNatureSourceSpec` declares per-building which biomes a building can satisfy a Nature need for. Applied via `.optional.blueprint.json` overlay to vanilla Folktails ContemplationSpot (all three biomes) and Lido (Wetland only).
- `KeystoneNatureSource` runtime component picks the winning biome by gathering nearby chunks across the building's footprint, enumerating surfaces with Z ≤ building base Z (region-agnostic but vantage-height-limited so a cliff-top spot reads the meadow below it but not the plateau above), taking max Maturity per biome, scaling by `clamp01((m − 5) / (maxMaturity − 5))`. Per-tick applies a `ContinuousEffect` to every beaver inside the `Enterable`. Bypasses vanilla `AttractionSpec` so existing entertainment effects are untouched (no save-compat hit).
- `KeystoneEcologyTransparentSpec` marker keeps tagged buildings invisible to the surveyor's settled check, so a ContemplationSpot alone in a meadow doesn't freeze the chunk's biome accrual.
- `KeystoneNatureSourceDescriber` `IEntityDescriber` surfaces the affordance: build menu shows the full eligible-needs menu; placed + active shows the winning need plus a qualitative tier label ("Small / Medium / Strong bonus from undeveloped / healthy / thriving Wetlands nearby"); placed + inactive shows a placement hint with the eligible biome list.

**Folktails-only by design** (see `src/Keystone.Mod/Wellbeing/README.md` § Folktails-only by design). IT's leisure buildings are industrial-themed; the contemplation mechanic doesn't fit. The asymmetry is deliberate, not an omission — `NeedCollection.Folktails.optional` only registers our needs on Folktails, IT beavers never instantiate them.

**What didn't make this cut and remains open**:
- Passive "wellbeing debuff in a contaminated zone" — the original sketch's negative-polarity counterpart. Could ride the same Nature mechanism by adding a `KeystoneNature.Contaminated` need with negative `FavorableWellbeing`, or via a separate "exposure" mechanism. Punted to post-MVP.
- IT-side equivalent — explicitly skipped per above.

### 2E — Public API stabilization

Lock down the read API that Phase 3 faction mods and third-party content mods consume: query Suitability, Maturity, and capacities by region, react to threshold crossings, observe contracts attached to other mods' content. Versioning policy (semver). The API takes its shape through Phases 1–2 (every internal consumer above is also a candidate external consumer); 2E is when it gets *frozen* so external mods can take a hard dependency on it.

### 2F — Polish, Phase 2 ship

Tuning species tier thresholds, animal density per biome, wellbeing modifier magnitudes. Performance review including animals at maximum time speed. Localization for any animal-related player-facing strings. **Second public release** — the "complete" base mod, before faction mods exist.

**Exit criteria for Phase 2:** A player who installs the base Keystone mod and engages with the mod's nine target behaviours gets a visibly and audibly richer world that responds to their stewardship — biome-driven visuals from Phase 1 plus animals appearing and behaving, ambient richness, and beavers whose wellbeing shifts with the local biome state. Faction-agnostic. API is locked; ready for Phase 3 to extend it.

## Phase 3 — Faction integration mods

**Goal:** Ship faction-themed ecology content as separate repos that consume the base mod's public API.

- **3A — Keystone: Folktails.** Headline content: mega-tree project on preserved ruins. Reintroduces the demolish-or-preserve trade-off (vanilla pays in metal for breaking ruins; this trades metal for long-term ecological yield). Folktails-flavoured content: organic, restorative — beekeeping, gardens.
- **3B — Keystone: Iron Teeth.** Industrial-ecology content: filtration buildings, controlled wetlands, scrubbers. Asymmetric feel from Folktails — the player engineers ecology rather than tending it.
- **Third-party `Keystone-Compat<X>` integrations.** Small per-content-mod packages that register flourish recipes against the base mod's API for popular third-party plant/flora mods. Land as the community produces them; not a base-mod deliverable.

## Backlog (from player feedback)

Items raised in post-launch community feedback (r/Timberborn, Discord) that aren't tied to a specific phase. Sequencing TBD — fold into the next polish pass that makes sense.

- **Fold Riparian into Grassland.** The Riparian biome doesn't work properly in its current narrow-band form and isn't earning its complexity. Plan: remove it from the 11-biome list in `DESIGN.md`, drop the Riparian-specific scoring branch in `BiomeTargets` (and related catalogs / flourishes / fauna affinities), and let Grassland cover the near-shore area uniformly. Until this lands, the existing "don't re-widen the Riparian threshold" guidance still holds — fauna cluster capacity depends on the current Grassland reach. Defer until current release ships.
- **Decouple `KeystoneEcologyTransparentSpec` from `KeystoneNatureSourceSpec`.** Today the two are welded in `KeystoneNatureModifierProvider.BuildBuildingSpec` — every Nature-source building also flips region-finder-transparent, and there's no way to add a building as transparent-only (ignored by region finder, no Nature need) or as source-only (provides the need, still counts as settled). The three combinations are all valid. Minimum change: a flat `KeystoneEcologyTransparentBuildings` list alongside `KeystoneNatureFactions`, and the provider emits the transparency marker from the new list only. Larger version, if cross-mod extensibility becomes a goal: a drop-in convention file per mod that Keystone scans at startup, so third-party mods can declare opt-in without taking a hard Keystone dependency (spec-typed integration from their side requires the Keystone DLL loaded at deserialize time, which we want to avoid forcing). Defer until current release ships.
- **Mod Settings / performance knobs.** Surface tunables via an in-game settings UI: rolling-sweep cadences, fauna density caps, atmospheric-content density, debug overlays. Players on lower-end hardware are the immediate audience for the perf knobs. Adopts ModdableTimberborn as a manifest dependency for the settings infrastructure (see `docs/private/moddabletimberborn.md`).
- **mod.io publishing.** Currently Steam Workshop only; mod.io expands distribution to non-Steam players (and console, when Timberborn ships there). Distribution task, not a feature.
- **"Hands-off" tile marker.** Player marks tiles that Keystone must never touch (no flourishes spawned, no Class A/B/C placements, no fauna ingress). An implicit version of this exists today — Keystone treats plantable-zone markings as off-limits — but it's a misuse of a vanilla tool with a visual side effect (the marked terrain darkens). Low priority; if pursued, prefer a dedicated overlay that doesn't repaint the ground, *or* find a way to suppress the plantable-zone darkening for tiles the player is using for the Keystone hands-off purpose.
- **Keystone-owned floodable component for water flourishes** (replaces vanilla `FloodableNaturalResourceSpec`). Water flourishes (Wetland/River minis are `MinWaterHeight=MaxWaterHeight=1`; Riparian minis are `0/0`) carry vanilla `FloodableNaturalResourceSpec` + `WateredNaturalResourceSpec` with their death timers pinned to ~1e9 days so they never actually die — but vanilla still flips them to the wilted `#Dying`/`#Dead` mesh the instant the water column leaves `[Min, Max]`. The trigger is integer-quantised: `WaterObject.WaterAboveBase` is `ceil(depth)`, so *any* film of water over a Riparian (`Max=0`) flourish reads as 1 voxel = "flooded" = wilted, and a Wetland/River flourish under a 2-deep column reads as flooded too. Replace the vanilla spec with a Keystone component that decides healthy/dying off the *continuous* depth with a real tolerance band (e.g. Riparian tolerates depth < ~0.2; in-water minis tolerate up to 1.0) and drives the model state itself. This is the strong fix for both the river/wetland and riparian cases, and unlike the shipped spawn-time gate it also covers the over-time case (water rising or draining *after* spawn). Systems change touching the model-state plumbing — **explicitly deferred past v1.0**. A narrow spawn-time stopgap (only spawn river flourishes where the water column is exactly 1 voxel — the minis' `[Min, Max]` band, so neither flooded nor dry) ships in the meantime; it's cosmetic-at-spawn only and can't prevent later re-wilting.
- **Water-chargeable irrigation building.** A placeable building the player charges with water; it then dispenses irrigation (moisture) to the surrounding tiles until its charge is depleted, going inert until recharged. Raw idea — recharge source, range/falloff, charge capacity, and how the dispensed moisture interacts with Keystone's moisture/biome scoring (does it count as "natural" water for biome dominance, or only vanilla irrigation?) all TBD.

## Phase 4+ — Speculative

Ideas worth keeping on the list but not committed. Sequence depends on what Phases 1–3 reveal (player engagement, perf headroom, what factions actually want from the API).

- **Floodplain biome + controlled-flood farming.** Built as a unit; the biome's complexity is paired with the gameplay anchor of yield-boosting flood agriculture. Adds the "embrace seasonal variability" behaviour that earlier phases don't cover.
- **Hunting + wild economy.** Animals migrate toward hunted areas; predators compete with hunters as a soft cap. Lives in faction mods or as a small standalone.
- **Selective mechanical penalties.** Pollinator collapse → orchard yield drops, etc. Used surgically where cause-effect is satisfying.
- **Deferred biomes** as content space allows: riparian corridor (first-class connector), hedgerow / edge, old-growth (succession). (Cliff and cave were promoted into Phase 1A as structural region separators on the surface graph.)
- **Procedural rocky terrain** — only if visual/gameplay payoff justifies the implementation cost.

## Decisions still blocking the roadmap

These are the open design/technical questions that need answers before or during their relevant phase. Listed here so they don't get lost.

- ~~**Spatial unit for biome classification.** Per-tile, per-cluster, per-player-zone? Connectivity (1C) needs at least cluster.~~ **Resolved:** raw data per surface voxel `(X, Y, Z)`, no per-tile classification; structural region (plateau) for identity and aggregates; sub-zones within a region for distinct ecological state. See `DESIGN.md` §Spatial model.
- **Sim tick cadence.** Per game tick, per day, per season? Probably daily. Decision needed before 1B.
- **Decline math.** Exponential vs. quadratic vs. piecewise in stress duration. Affects how droughts feel. Decision needed in 1B; tunable in 1H.
- **Recovery vs. decline rate ratio.** Asymmetry is committed; the actual numbers are open. Tunable in 1H but needs a starting point in 1B.
- **Tier assignment per species.** Which fauna are tier 1 / 2 / 3 isn't obvious for all species. Content-design call in 2C.
- **Connection definition.** Direct adjacency, vegetation continuity, or wait for corridor biomes? Decision needed in 2A.
- **Public API surface.** What exactly faction mods read and write. Takes shape across Phases 1–2; locked in 2E.
