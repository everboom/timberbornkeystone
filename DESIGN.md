# Keystone — Design

A working document. Captures the design as it solidifies; open questions are marked explicitly. Not a spec — expect things to change.

## Name

**Keystone.** The name carries two intentional meanings.

In ecology, a *keystone species* is one whose impact on its ecosystem is disproportionately larger than its abundance suggests — typically because it engineers habitat that other species depend on. Beavers are the textbook example, which makes the name a natural fit. More importantly, the name describes the mod's design thesis: the player's choices about water and land *create the conditions* under which an ecosystem can exist, and everything else depends on those conditions. Keystone names the mechanism, not the outcome.

The second meaning is architectural. The mod is structured as a platform — a base ecology framework with separate per-faction expansion mods built on top. The base mod is literally the keystone of that arch.

Faction mods will follow a "Keystone: <faction>" or themed-extension pattern (e.g. "Keystone: Folktails" / "Keystone: Iron Teeth", or evocative names like "Keystone Bloom" / "Keystone Forge"). Naming convention to be settled when faction mods are scoped.

## Mod thesis

Timberborn already lets the player bring a dead map back to life through irrigation and planting. The base game does not reward this beyond the utility of the resources produced, and the resulting world stays static — flora grows in predictable patterns, no fauna, no sense of ecosystem.

This mod's job is to make the player's ecological choices **feel meaningful and visible**. As the player improves the ecosystem, the world becomes richer: more diverse flora, animals appearing and behaving, ambient sounds, flourishes. As the player neglects or harms it, the world feels poorer — silent, decayed, ugly in places it shouldn't be.

The mod is built in phases. **Phase 1 is the cosmetic / feedback layer.** It does not introduce mechanical penalties or hard gameplay incentives. The player chooses to engage with ecology because the world they get is more alive — not because the game punishes them otherwise. Phase 2 will add direct gameplay integration (resources, hunting, etc.) on top of the same simulation substrate.

## Mod architecture (base mod + faction extensions)

The project ships as a **single base mod** (this repo) covering the complete faction-agnostic ecology experience, with **faction integration mods** (separate repos, deferred to Phase 3) layered on top.

- **Keystone (base mod, Phases 1–2).** Observes game state, detects regions, computes per-chunk Suitability and Maturity per biome, spawns visual flourishes that respond to biome state, applies wellbeing modifiers to beavers in healthy/contaminated zones, manages animal agents proportional to per-cluster species capacity. Persists everything that can't be re-derived. Faction-agnostic. Exposes a public read API + contracts for Phase 3 consumers.
- **Faction integration mods (Phase 3+, separate repos).** Folktails-flavoured content (organic, restorative — mega-tree project on preserved ruins, beekeeping, gardens) and Iron-Teeth-flavoured content (industrial ecology — filtration buildings, controlled wetlands, scrubbers) live in their own repos. Third-party content-mod integration via small per-mod `Keystone-Compat<X>` packages. All consume the base mod via the same public API and contracts.

**Earlier framing retired.** An earlier version of this design split the work into a Mod 1 (observatory: reads + scores + persists, modifies nothing) and a Mod 2 (content companion: spawns puppets + visuals on top of Mod 1's API). The split was retired during Phase 1 iteration: the "two installable mods" boundary added coordination cost without buying a corresponding capability — the analytics overlay survives as a debug UI inside the base mod rather than a standalone deliverable, and animal/visual content was always going to ship alongside the simulator that drives it.

**Integration with faction mods stays declarative, not imperative.** The boundary between the base mod and Phase 3 consumers runs through:

1. **Public read API** — query Suitability and Maturity, region state, biome classification, species capacity. Stable, versioned (semver), the thing faction mods take a hard dependency on. Locked at the end of Phase 2 (sub-milestone 2E).
2. **Contracts** — components and specs faction mods attach to their entities so the base mod picks them up via the same scan-everything pattern used for flora and building catalogs. Works across vanilla and any installed mod.

There is **no contribution API and no shared storage layer.** Faction mods don't call the base mod to "add an animal count" — the base mod derives counts from observing the world (including modded content discovered through contracts). This decouples the base mod from any specific consumer; it works the same whether zero, one, or twenty faction/content mods are installed.

**Plants vs. animals asymmetry.** Plants and trees use the game's existing specs (`NaturalResourceSpec`, `GrowableSpec`, `WateredNaturalResourceSpec`, `PlantableSpec`, `FloodableNaturalResourceSpec`). Keystone reads them as-is and does not define a parallel plant contract — vanilla and modded plants are picked up by `FloraCatalog` automatically. Animals get their own contract (`KeystoneSpeciesSpec` or similar — design lands in Phase 2 alongside the agent simulation) because vanilla has no fauna abstraction; beavers and bots are characters tied to factions, and there is no "wildlife" system to extend. Other ecological signals (water sources, ruins, blockages, brambles, sinks) are observed without a custom contract — they're inert for ecology purposes.

**Why the base-mod-plus-faction-mods shape:**
- The base mod is a complete experience by itself — a player who installs Keystone and nothing else gets the full ecology simulator + animals + wellbeing on any map with either faction.
- Faction mods stay sized to their content. Some might add five buildings, some might add fifty; asymmetry between factions stops being a balancing problem because each ships its own scope.
- Third-party integration scales — any plant/flora/animal mod can extend Keystone's view by attaching contracts, with optional `Keystone-Compat<X>` companion packages registering richer recipes against the public API.

Naming convention for faction/content mods follows a "Keystone: <theme>" pattern (e.g. "Keystone: Folktails", "Keystone: Iron Teeth"). Third-party compat packages are "Keystone-Compat<ModName>".

## Phase 1 scope

Phase 1 is the **MVP**: core simulator + visual feedback. Phase 2 fleshes it out with beavers + animals (same base mod). Phase 3 layers faction-themed integration mods on top. See **§ Layers of feedback** for the full effect-axis breakdown that drives this scoping.

What's in Phase 1:
- A simulation layer that tracks ecological state (biodiversity, biome composition, water quality, etc.) per region and per chunk. Per-biome Suitability and Maturity, biome classification, persistence.
- **Static visual flourishes** (axis 1) — passive ground variety per biome, via placed decoration with sub-tile composition.
- **Interactive flora variety** (axis 2) — primarily through biome-driven natural spread of cross-faction flora that's already loaded but normally invisible to the current faction; spec tweaks to existing flora; new species only if a specific gap demands it.
- **Atmospheric / particle effects** (axis 6) — mist, fireflies, contamination haze, etc., emitted from chunk state.

What's in **Phase 2** (was previously in Phase 1):
- **Beaver wellbeing area effects** (axis 8) — passive wellbeing modifiers driven by the chunk a beaver is standing in. Code-only; hooks the existing vanilla wellbeing system.
- **Animal agents and decorative moving flourishes** (axes 3, 4) — ephemeral animal puppets proportional to per-cluster species capacity, driven by Phase 2's connectivity + capacity work.
- **Public API stabilization** — locked at the end of Phase 2 so Phase 3 faction mods can take a hard dependency.

What's **not** in phase 1 or 2 yet:
- Sound / ambience (axis 5) and lighting / post-process (axis 7) — deferred to nice-to-have.
- Hard mechanical penalties (yield drops, raids, disease).
- Direct economic rewards (hunting, foraging, special resources).
- Procedurally generated new terrain types (e.g. true rocky biome).
- New buildings or player tools beyond what's needed for feedback (overlays).

## Target player behaviors

The mod is designed to reward these specific behaviors with visible/audible ecosystem responses. The reward is the ecosystem itself — no resource drops, no efficiency multipliers, in phase 1.

1. **Bring water purposefully across the map.** Irrigate land that doesn't strictly need to be productive. The reward scales with how much of the map sustains life — but unwaterable terrain (cliffs) is also valuable in its own way, so this is "use water deliberately" rather than "irrigate every tile."
2. **Remove badwater sources from the map.** Badwater zones are visually degraded; cleaning them transforms ugliness into health.
3. **Clean badwater creep from contaminated ground.** Even after a source is removed, contaminated terrain stays barren until reclaimed.
4. **Keep beavers and animals away from badwater creep.** Spatial planning — fences, terrain, distance, routing — to protect populations from contamination.
5. **Plant mixed compositions, not monocultures.** Species variety in a region drives biodiversity rewards more than density of a single species.
6. **Create distinct biomes intentionally.** Different planting + water compositions produce different biomes, each with its own fauna and visual identity.
7. **Maintain flowing water alongside reservoirs.** Stagnant reservoirs and flowing streams support different ecosystems. A landscape with both is richer than one with either alone.
8. **Remove ruins and metal towers from the map.** They are industrial blight that suppresses local ecological richness. The player is already incentivized to demolish them in vanilla for resources; the mod adds an ecological reason on top.
9. **Connect biomes together.** A landscape of isolated wild patches is species-poor; a landscape of connected ones supports more life. The player is rewarded for designing their map so wild zones can reach each other — through direct adjacency, continuous vegetation, or (when those biomes land) dedicated corridors like hedgerows and riparian strips.
10. **Maintain conditions across drought cycles.** Ecological health accumulates slowly over time when conditions are sustained and decays when they aren't. Brief lapses are absorbable; prolonged mismanagement compounds into real loss. This makes drought management an ecological act, not just a survival one — and it's how the mod fuses with Timberborn's signature mechanic instead of running parallel to it.

## Biomes

Biomes are **composition-emergent**, not placed. The player doesn't designate a "swamp here" — a region becomes a swamp because the player created the conditions for one (shallow slow water + saturated ground + appropriate plantings). This makes biome variety a natural product of varied play rather than a separate placement minigame.

Recent versions of Timberborn let the player directly control water flow speed and water height. The water-defined biomes (wetland, river, lake) are therefore not just *detectable* but **engineerable** — the player gives the simulation explicit signals about what they're trying to build, which makes biome classification much cleaner.

### Biome list (phase 1)

Eleven biomes, evaluated independently per chunk. Every chunk accumulates a Suitability value per biome from its current state; **multiple biomes can have positive Suitability on the same chunk** (a partially-treed irrigated chunk has both Forest and Grassland Suitabilities climbing; a partially-contaminated lake has both Lake and Badwater Suitabilities climbing). Cross-biome influence — like Badwater killing nearby healthy biomes — lives in cross-chunk passes that read neighbour state, not inside per-target functions.

| Class | Biomes |
|---|---|
| Land — irrigated | Forest, Grassland, Monoculture |
| Water — clean | River, Lake, Wetland |
| Edge — clean | Riparian |
| Structural | Cave |
| Negative — moisture | Dry |
| Negative — contamination | Contaminated, Badwater |

Per-biome target predicates (target = Suitability the chunk would have under sustained matching conditions):

- **Forest**: `irrigated × diversity × density × (1 − Monoculture)`. Diversity saturates at 2 species; density saturates at 5 trees.
- **Grassland**: `irrigated × (1 − saturated_soil) × (1 − tree_density) × (1 − Monoculture)`. Yields multiplicatively to Riparian (saturated soil), Forest (trees), and Monoculture (managed cultivation).
- **Monoculture**: `irrigated × saturation × dominance` where saturation = `count / 16` and dominance derives from Simpson's diversity index. Player-drawn planting marks count toward the plantable totals immediately.
- **Water biomes (the three-way split):**
  - **Wetland**: shallow + low-flow water. L1 = aquatic mini-flourishes (Class B); L2 = cattail + spadderdock (Class D); L3 = mangrove (Class D). The mod's main positive incentive for ecology-aware water management — "create slow side channels and life will flourish there."
  - **River**: high-flow water of any depth. L1 = bank decoration (Class B mini-flourishes, gated by the `RiverBank` filter so the visuals hug step-up tiles against the river wall and skip waterfall edges). Rivers themselves are destructive enough to sweep wetland plants away — keep flow low if you want a productive water biome.
  - **Lake**: deep + low-flow water (dam reservoirs, natural ponds). Passive — no Keystone content.
- **Riparian**: `saturated_soil × (1 − Monoculture)`. The shoreline strip — hosts mini-flourishes (L1) and Class D birches (L2).
- **Cave**: `cave_fraction`. Region splitter at the structural level.
- **Dry**: `dry_land_fraction`. No contamination gate.
- **Contaminated**: total `contaminated_fraction` (land + water). Stacks with Badwater rather than competing — both target the same chunk-area when badwater fluid is present, and Badwater wins the dominance argmax via the aggressor tiebreak in `ChunkBiomeSampler`. Maturity-side consequence: Contaminated Maturity stays in accrue mode under Badwater dominance (asymptote remains at its ceiling), so the matrix's co-present cell is structurally unreachable in practice.
- **Badwater**: `contaminated_water_fraction`. Any depth/flow — the stagnant/flow distinction we apply for clean water doesn't carry, since the contamination palette dominates anyway. Treated as "worse Contaminated" — wherever Badwater Suitability is high, Contaminated Suitability is held at the same value (see Maturity section: Badwater stacks on Contaminated rather than replacing it).

Cave is a region separator: a roofed surface is its own ecological context regardless of what its lateral neighbors are doing. Region detection treats cave and non-cave surfaces as distinct regions even when they share Z and are laterally adjacent.

**Cliff is not a biome.** An earlier iteration tagged each rim tile as `Cliff`, but cliffs aren't really *places* — they're *transitions between places*. A rim tile still has its own primary ecology (the cliff edge of a wet forest is a forest tile), and the tag-based representation didn't say *what* the tile is a cliff against. Height-differential data lives where it belongs: as a derived property of the **inter-region boundary**, not a per-tile classification. Each region tracks its neighbor regions and per-edge stats (shared boundary length, Z-delta distribution, etc.). The plateau / region detection itself splits on `(Z, IsCave)` adjacency — cliffs are an emergent consequence of plateaus being at different heights, not a labeled category.

### Deferred biomes

Interesting but explicitly not in phase 1, in roughly descending order of likelihood to land later:

- **Floodplain.** Land that floods in wet season and dries during drought, hosting plants and migratory fauna adapted to the cycle. Conceptually strong because it would directly reinforce Timberborn's signature drought mechanic — a biome whose ecology *depends on* the wet/drought cycle is something only this game can do. However, the implementation cost (per-tile flood history, migratory fauna behavior, cycle-aware flora) is significant, and the biome's value as wild content alone doesn't clearly justify it. Best paired with the phase 2 controlled-flood farming mechanic (see parking lot) — build them together as a unit when there's a gameplay anchor.
- **Riparian corridor.** The narrow strip of vegetation along watercourses. Easy to detect, real ecological significance, and a natural first-class *connector* between biomes (see behavior 9 — connect biomes together). Deferred because edge-style mechanics complicate the simulation model rather than just adding content, but its connectivity role makes it more justified than the original "just another biome" framing suggested.
- **Cave / overhang.** Bats, certain birds. Visually striking but rare on maps and technically harder (occlusion detection).
- **Old-growth forest** (succession state, not strictly a biome). Forest tier that emerges after long undisturbed maturity. Adds a *time* dimension to ecology — discourages clearcutting mature forest. Promising but conceptually different from the area-biome model.
- **Edges / hedgerows** (transition zones, not strictly biomes). Forest/grassland boundaries hosting bonus biodiversity, and — more importantly given behavior 9 — first-class *corridors* the player can build to connect distant wild patches across managed land. The connectivity role gives them a much stronger justification than the original "edge bonus" framing alone.

## Temporal model: per-chunk per-biome channels

Each `(chunk, biome)` carries two persistent values: a short-term **Suitability** in `[0, 1]` and a long-term **Maturity** in game-days. Suitability is stateless (recomputed each tick from current chunk state); Maturity is integrated over time and carries all of the channel's temporal dynamics. The Suitability/Maturity split is what lets the two roles coexist cleanly: Suitability answers "is this chunk acting like X right now," Maturity answers "how long has it been doing that."

### Suitability: stateless per-tick read

Each tick, every biome's Suitability is recomputed directly from the chunk's current `ChunkBiomeInputs` and clamped to `[0, 1]`. There's no drift, no rise/drop rates, no history. Persistence is for cross-tick consumer reads only; if the store is empty, the next tick recomputes the same value.

The formula per biome is `Suitability = positive × contamination_factor`:

- **Positive predicate** answers "how strongly does this chunk look like that biome from the inputs alone." Forest is `irrigated × diversity × density × (1 − Monoculture)`. Grassland is the same shape with the additional `(1 − saturated_soil) × (1 − tree_density)` multipliers. Water biomes (River/Lake/Wetland) read their respective depth × flow sub-fractions. Etc. See the per-biome list under "Biomes" above.
- **Contamination factor** in `[0, 1]` cancels the biome on contaminated chunks. Land biomes (Forest/Grassland/Monoculture/Riparian/Cave/Dry) use `ContaminatedFraction` (total contamination, land + water); water biomes (River/Lake/Wetland) use `ContaminatedWaterFraction` (water contamination only — land contamination on the shoreline doesn't make the water dirty). With cancellation scale `k=20`, 5% contamination fully cancels the affected biome; below that the factor ramps linearly. Contaminated and Badwater receive no cancellation — they *are* the contamination state.

### Cancellation lattice

The ordering of which conditions cancel which biomes is the design principle "badwater overrides everything, contamination overrides everything below, drought knocks out water/irrigated, water knocks out irrigated":

1. **Badwater** cancels all non-toxic biomes — enforced via the contamination factor, since badwater contributes to `ContaminatedFraction` (it's contaminated water).
2. **Contamination** cancels all biomes except Contaminated and Badwater themselves — same contamination factor.
3. **Drought** cancels irrigation- and water-dependent biomes — enforced naturally by their positive predicates (IrrigatedFraction and WaterFraction both fall to 0 under drought), no explicit cancellation factor needed.
4. **Inundation** cancels irrigation-dependent land biomes — same mechanism (IrrigatedFraction falls to 0 when standing water replaces irrigated land).

Drought and inundation don't need explicit multipliers because the per-biome positive predicates already encode them through the partition: `IrrigatedFraction + DryLandFraction + SaturatedSoilFraction + WaterFraction + CaveFraction = 1` (the land-state fractions are mutually exclusive). Contamination is the odd one out: a contaminated land tile is still irrigated (the moisture channel doesn't care), so without the explicit factor the positive predicates would erroneously read Forest on contaminated ground.

### Maturity: long-term integration of Suitability

Suitability gives the immediate "is this chunk acting like a Forest right now" reading; it rises and crashes in minutes. **Maturity** is the long-term integral of Suitability — "how long has this chunk been acting like a Forest" — and is what gates the level ladder for visual content (Class A flourishes, Class B/C/D recipes). It runs on the same per-chunk per-biome storage (`keystone.chunk.maturity.<biome>`) and is updated each game-hour alongside Suitability.

The model is hybrid — **exponential accrue, linear decay**:

- **Accrue mode** (`M ≤ α·S / β_accrue`): `dM/dt = α·S − β_accrue·M`. Exponential approach to the asymptote `α·S / β_accrue`, which equals the ceiling at `S = 1`. The rise slows as Maturity nears its ceiling — that's the "growth keeps feeling ongoing" feel.
- **Decay mode** (`M > α·S / β_accrue`): `dM/dt = −rate`, linear, with `rate = ceiling / clearTimeDays`. Clamped at the asymptote so partial-Suitability support halts decay at the new sustainable level instead of overshooting to 0. `clearTimeDays` comes from the matrix below.

Why the asymmetry: exponential accrue gives the design's "every day of upkeep still buys you a bit more" feel because the rate never quite hits zero. Exponential decay would do the same on the way down, leaving a long tail that's not useful — once a biome is destroyed, the player wants a predictable cleanup window, not "Forest is at 0.04 forever." Linear with a ceiling-based clear time delivers that.

Two design intents drive the parameters:

- **Asymptote (ceiling) is per-biome.** A biome's `α / β_accrue` ratio sets how mature a chunk can ever become for that biome at sustained `S = 1`. Healthy biomes are deep wells (ceiling 30 days — the player has to commit time); the toxic scars are *also* deep (Contaminated 12.5, Badwater 15) so an entrenched scar is a real multi-week reclamation rather than a trivially-cleared blemish; Dry (10) and Monoculture (3.5) sit lower.
- **Decay rate is a function of the dominant biome.** When a chunk's Maturity for biome A is decaying, the rate depends on *which* biome is dominant on that chunk, not just on biome A. Badwater overwriting a Forest is hours; Grassland yielding to a Forest is a week. The same Forest Maturity that's "fragile under contamination" is "robust under peer healthy biomes."

#### Per-biome ceilings (Maturity asymptote at `S = 1`, in game-days)

| Biome | Ceiling |
|---|---|
| Forest, Grassland, Wetland, River, Lake, Riparian, Cave | 30 |
| Badwater | 15 |
| Dry | 10 |
| Contaminated | 12.5 |
| Monoculture | 3.5 |

With `α = 1` and `β_accrue = 1 / ceiling`, a biome's accrue time constant equals its ceiling (~ceiling days to reach equilibrium). For the toxic scars this is deliberate and exposure-proportional: early accrual is ~`α·S` ≈ 1/day regardless of ceiling, so a fresh scar still crosses its gate threshold (Badwater 0.1, Contaminated 0.5) within hours — damage *registers* immediately. What scales with the deep ceiling is the *reservoir*: a scar only reaches full depth after ~ceiling days of sustained input, and since it drains at the same flat 1/day, clearing it takes about as long as it was allowed to fester (capped at the ceiling). A one-day spill is cheap to fix; an ignored one is a multi-week cleanup. If accrue time ever needs to be decoupled from ceiling, scale `α` per biome instead.

#### Decay-rate matrix (clear time in game-days)

Rows = decaying biome, columns = dominant biome. Cell value is the time for Maturity to drop from ceiling to 0 under linear decay at the implied rate `ceiling / clearTimeDays`. `--` = same biome (accrue mode by definition). `acc` = co-present: the decaying biome's Suitability is held high by the dominant biome's presence, so Maturity stays in accrue mode and never enters decay.

| decaying \ dominant | BW  | Con | Dry | Riv | Wet | Lak | Rip | Gra | For | Mon | Cav |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **Badwater**     | --  | 15  | 15  | 15  | 15  | 15  | 15  | 15  | 15  | 15  | 15  |
| **Contaminated** | acc | --  | 12.5| 12.5| 12.5| 12.5| 12.5| 12.5| 12.5| 12.5| 12.5|
| **Dry**          | 0.5 | 1   | --  | 1   | 1   | 1   | 1   | 1   | 1   | 1   | 1   |
| **River**        | 0.5 | 1   | 0.7 | --  | 30  | 5   | 5   | 7   | 7   | 7   | 14  |
| **Wetland**      | 0.5 | 1   | 1.8 | 3   | --  | 5   | 5   | 7   | 7   | 7   | 14  |
| **Lake**         | 0.5 | 1   | 0.7 | 3   | 30  | --  | 5   | 7   | 7   | 7   | 14  |
| **Riparian**     | 0.5 | 1   | 2.0 | 5   | 5   | 5   | --  | 7   | 7   | 7   | 14  |
| **Grassland**    | 0.5 | 1   | 2.1 | 5   | 5   | 5   | 5   | --  | 30  | 7   | 14  |
| **Forest**       | 0.5 | 1   | 4.1 | 5   | 5   | 5   | 5   | 7   | --  | 7   | 14  |
| **Monoculture**  | 0.5 | 1   | 3   | 5   | 5   | 5   | 5   | 3   | 3   | --  | 14  |
| **Cave**         | 0.5 | 1   | 3   | 5   | 5   | 5   | 5   | 7   | 7   | 7   | --  |

Dry-column values are *saturated* clear times (drought-intensity factor = 1). Fresh-drought clear times are stretched by the drought-intensity ramp described below — roughly 2× the saturated value for water-family biomes, less for Grassland/Forest. The Cave and Monoculture rows fall through to the 3 d column default; the negative biomes (Badwater, Contaminated, Dry) use their row overrides and don't see the per-biome Dry-column treatment.

**Structural pattern.** The matrix mostly factors:

- *Column default* — most cells in a column share the dominant biome's aggressor tier: Badwater 0.5 d, Contaminated 1 d, water-family (River / Wetland / Lake / Riparian) 5 d, land-family (Grassland / Forest / Monoculture) 7 d, Cave 14 d. The Dry column is the exception — it carries a per-biome *kill order* rather than a uniform default (see below).
- *Row overrides* — a decaying biome can differ from its column default: Dry collapses to 1 d under any non-toxic dominant (low ceiling, easily cleared); Monoculture goes to 3 d under healthy land dominants (replaceable with effort); Badwater and Contaminated decay at a flat `BaselineDecayRatePerDay = 1/day` once they're no longer the chunk's dominant signal — clear time follows from the biome's ceiling (`Badwater` 15 d, `Contaminated` 12.5 d). The rate is the design constant; the clear times are derived. While Badwater or Contaminated is itself the chunk's dominant biome (its Suitability is the highest, i.e. the stress hasn't gone) the scar Maturity holds at its current value — implemented as a short-circuit in `BiomeMaturityUpdater.Tick` before the matrix is consulted.
- *Cell overrides* — Wetland and Lake both yield to a dominant River in 3 d (flow erosion is faster than the water-family default). Three **succession-free peer drift** cells encode asymmetric succession: Grassland decaying under Forest, and River / Lake decaying under Wetland, drift at the baseline 1/day rate (30 d clear) rather than the column default. The matrix's reverse direction stays at the faster matrix rate (Forest under Grassland 7 d, Wetland under River 3 d) — the "successor kills predecessor" semantic. Grassland succeeds Forest; Lake and River succeed Wetland; the reverse pairs are no-aggression and drift slowly.
- *Dry-column kill order* — under Dry dominance, healthy biomes don't share a single column default. River/Lake clear in 0.7 d, Wetland 1.8 d, Riparian 2.0 d, Grassland 2.1 d, Forest 4.1 d. Cave and Monoculture fall through to the 3 d column default (placeholders pending the "unhealthy biome" pass). Water-family biomes go fastest because the input that defines them (water) is the thing the drought removes; root systems let Grassland and Forest hold on longer. These are the *saturated* clear times; combined with the drought-intensity ramp below, fresh-drought clear times are roughly 2 d / 3.5 d / 3.7 d / 4 d / 6 d for the same five biomes.

#### Drought-intensity ramp

The Dry column rates are multiplied at integration time by a per-biome intensity scalar so a fresh drought doesn't bite at full strength on day one. Under Dry dominance, the effective decay rate for a non-Dry biome is

`rate_effective = (ceiling / clearTimeDays) × (floor + (1 − floor) × droughtDepth)`

where `droughtDepth = min(1, M_dry / 3.33)` and the per-biome `floor` is:

- **River / Wetland / Lake / Riparian** — `0.1`. Water-family biomes are defined by the water input; the moment the water leaves they're already in trouble, so they take a small immediate hit even at `droughtDepth ≈ 0`.
- **Grassland, Forest** — `0`. Root systems and soil banking buffer transient dry spells; decay only meaningfully kicks in once Dry Maturity has actually built up.
- **Cave, Monoculture** — `0` (placeholders pending real treatment).

The saturation threshold `DroughtSaturationMaturity = 3.33` is deliberately **decoupled** from `Ceiling(Dry) = 10`. Drought "feels saturated" at roughly 33 % of Dry's ceiling — reached at ~day 4 of a fresh drought (since `M_dry(t) = 10·(1 − e^{−t/10})` hits 3.33 at t ≈ 4.05 d). Tying the ramp to Dry's own slow time constant would balloon fresh-drought clear times to 2–3× the matrix nominals. Any UI surface that wants to show "drought intensity at this chunk" should read `min(1, M_dry / 3.33)`, not `M_dry / Ceiling(Dry)`.

The ramp fires only under Dry dominance; decay under other dominants uses the raw matrix rate. Dry-decaying-under-other rows use the matrix as-is too (no self-application of the scalar).

**Co-present cell (`acc`): Badwater stacks on Contaminated.** Badwater is functionally "Contaminated plus open badwater fluid." Whenever Badwater Suitability is high, Contaminated Suitability is held at the same value (mechanism lives in `BiomeTargets`), so Contaminated Maturity never enters decay mode while Badwater is dominant. The chunk just carries both Maturities side by side. Removing the badwater fluid drops Badwater Suitability; Badwater Maturity then decays linearly at 1/day (ceiling 15 → 15 d to clear), and Contaminated Maturity persists as long as the underlying contamination does.

**Implication.** A chunk can have multiple non-zero toxic Maturities simultaneously. Downstream consumers (level-table gates, debug UI, future Phase 2 wellbeing modifiers) should not assume "only the dominant biome has non-zero Maturity."

#### Scar gate

Every biome except the toxic scars themselves — so the eight healthy biomes (Forest, Grassland, Monoculture, Wetland, River, Lake, Riparian, Cave) plus Dry — cannot accrue Maturity on a chunk while either toxic biome's Maturity is above its gate threshold:

- **Badwater Maturity > 0.1** (clears ~15 game-days after badwater fluid is removed, for a fully-entrenched scar)
- **Contaminated Maturity > 0.5** (clears ~12 game-days after contamination is removed, for a fully-entrenched scar)

Dry is gated alongside healthy biomes because the input-side `ContaminationFactor` cancellation in `BiomeTargets` only suppresses Dry while the contamination *input* is present. Without this gate, Dry Maturity would spring back during the cleanup tail (after fluid removal but before the scar Maturity drains), out of pace with the toxic biomes that still own the chunk.

The gate only blocks the *accrue* branch — decay is unaffected (the matrix handles peer Maturity being killed when a toxic biome is actively dominant). The point is the cleanup tail: even after the player has cleared the badwater fluid and the chunk's input-side Suitability has shifted to Forest or Dry, the chunk stays uninhabitable to non-toxic-scar accrual until the residual Badwater (and/or Contaminated) scar finishes draining.

Gate state is computed at the start of each chunk tick from pre-update toxic Maturity values, so the gate doesn't flicker on the tick the toxic Maturity drops below threshold.

**All values are first-cut tuning, not final.** The matrix structure — column tiers, per-biome ceilings, the stacking rule for Badwater-on-Contaminated, the scar gate thresholds — is the committed design; individual times will move as in-game pacing tells us where the friction is.

### Tile-level reads via bilinear interpolation

Per-chunk Suitability and Maturity are coarse (4×4 chunks). For consumers that need a per-tile value (decoration placement, debug overlays), interpolate from the 4 nearest chunk centers. Smooths boundaries automatically — no chunk-grid seams visible to the player.

### Species presence is a function of current eco health

Tier 1 species (small, common: songbirds, butterflies, frogs) appear at low health. Tier 2 (medium: deer, foxes, ducks, otters) at higher health. Tier 3 (apex/rare: wolves, bears, eagles, herons) only near the ceiling. **Visible richness reflects current state, not just past investment** — the deer disappear in the third week of a botched drought, and the player *sees* their mismanagement.

Tier 2 and 3 species also gate on structural requirements (minimum biome size, connectivity to other suitable biomes, maturity). These structural gates determine the *ceiling*; current eco health determines how much of the available palette is realized.

### Save / load determinism

Per-chunk biome Suitability persists via the existing `ChunkValueStore` (each biome → a named kind, e.g. `keystone.chunk.suitability.forest`); Maturity persists alongside under a parallel kind (`keystone.chunk.maturity.<biome>`). These two channels per biome are the only persistent state; everything visible in the world is a *projection* of them, not independent state. Visuals regenerate on load via the same per-surface evaluation that runs at game time (see "Class B architecture"). The drift dynamics above run from whatever Suitability is loaded, so if a save captures a chunk mid-crash it'll continue crashing on resume — and an undisturbed chunk picks up exactly where it left off.

## Performance

Perf is mostly architectural. Profiling reveals problems but doesn't fix bad designs — these commitments are what keep us out of the hole.

**Architectural commitments:**

- **Tile state is the ground truth; region aggregates are derived caches.** This is honest framing, not optimism. Producing a region's metadata (biodiversity, composition, eco-health inputs) is fundamentally per-tile work — we cannot compute "this forest is mature and mixed" without reading the tiles. Where region-level work *is* cheap is on the *consumption* side: once the cache is built, downstream code (animal spawning, faction-mod queries, debug UI) reads a few dozen objects.
- **Dirty-tracking + incremental aggregation is the real lever.** Most tiles don't change between updates. If a region had no tile changes, we skip re-aggregation entirely — steady-state cost approaches zero. When changes happen, prefer delta updates (one tile flipped from young to mature → shift the count by one) over full re-aggregation.
- **Slow tick cadence for most metadata.** Daily ticks are the default for eco-health, biome detection, connectivity, fauna spawning. Per-game-tick map-wide recomputation is forbidden. Faster updates are exceptions and must justify their budget.
- **Polling with frame-skipping is the baseline, not events.** Subscribing to change events across 65k tiles has bad failure modes: event storms during bulk changes, subscription overhead, determinism risk, save/load complexity. Polling is predictable, throttleable, and deterministic. The pattern: process N tiles or chunks per frame in a rolling sweep, with total cycle time = total / N. Daily-cadence metadata tolerates a multi-second sweep cycle without issue. Events may still be useful as *hints* ("this area changed — prioritize it next sweep") but are not the primary mechanism.
- **Capped entity counts on animal puppets.** Hard ceiling regardless of biodiversity. Spawn logic decides which to show, not one-per-metadata-unit.
- **Spread heavy work across ticks.** Initial map scan (one-time at load) and large rebuilds (connectivity graph after major terrain change) split across multiple ticks rather than landing in one frame. Must remain deterministic.

**Cost profile is bursty, not flat.** A tick where the player demolished a forest is expensive — a tick with no changes is nearly free. Budget enforcement should look at sustained averages over a few seconds, not flag every spike, otherwise legitimate player-action bursts produce false positives.

### Region membership: the harder problem

Tiles don't carry their region identity intrinsically. Region membership is computed from spatial connectivity of compatible tiles, and every change can expand, shrink, split, or merge regions. Splits and merges are expensive: naive recomputation is O(map); union-find data structures handle merges cheaply but don't support efficient splits, and we definitely have splits (player carves a strip through a forest).

#### Architectural principle: separate structure from state

Region *identity* is defined by **structure** — slow-changing, player-driven, event-trackable attributes (terrain height, buildings, dams, walls). Region *biome and eco-health* are **state** — derived from dynamic, polling-driven attributes (water level, flow, badwater extent, irrigation status, vegetation composition).

The same topological region can be a wetland today, a grassland during a drought, and a wetland again next cycle — without its identity ever changing. Biome classification is a function applied to a region's current state, not a property defining it. This collapses a category of expensive recomputation (water shifts triggering region restructuring) into cheap state attribute updates.

Connectivity becomes two graphs:

- **Structural connectivity** — which regions are physically adjacent given terrain + buildings. Slow-changing, cheap.
- **Effective connectivity at this moment** — structural minus state-blocked edges (badwater barriers, drained channels). Recomputed cheaply on top of the structural graph.

Animal/eco logic queries effective connectivity; region membership uses structural.

**Where plants fit:** plants are state, not structure. Aggregated at chunk level (composition fractions: % forest, % grass, % water plants). Composition affects biome classification but doesn't trigger structural restructuring per planting. A player who mass-plants a forest pays the same polling cost as one who plants gradually. (Alternative: plants as structure, treating each planting as an event. Rejected because mass plantings cause event storms — same failure mode as water tracking.)

**Strategy: structural pass + polling sweep, with tiles rolling up directly to regions.**

Two complementary mechanisms, no intermediate layer needed:

**1. Structural pass (event-driven, rare).** Listens for building, terraform, dam, and wall events. On change, recomputes affected regions via connected-components on the structural graph (terrain + structures), and maintains a tile → region lookup map. Owns region *identity* and *structural connectivity*. Cost is bounded by player input rate — usually negligible, occasionally bursty when the player makes a large structural edit.

**2. Polling sweep (continuous, bounded cost).** Iterates tiles on a rolling schedule (N tiles per frame, full cycle in some seconds). For each tile:
- Read its current state (water, plants, contamination).
- Look up its region (O(1) via the cached structural map).
- Contribute the tile's stats to the region's running totals.

At cycle completion, each region has fresh aggregated stats: vegetation composition, water summary, contamination level. Biome classification is recomputed from those stats; eco-health updates per the temporal model; animal counts adjust per tier rules.

**Cost profile.** This model has a *constant floor cost* — we pay the full polling sweep every cycle whether anything changed or not, because the only way to know what changed is to look. On a typical map with a multi-second sweep cycle, this is on the order of a thousand tile-evaluations per frame: manageable and predictable, but not zero. Perf budget must account for the floor cost; idle frames aren't free.

**Possible refinements (defer until profiling demands them):**

- **Coarser dirty-tracking layer (chunked clustering).** If structural changes prove expensive to handle event-by-event, an intermediate chunk layer between tiles and regions can localize the cost. Adds complexity and granularity loss; only justified if structural updates show up as the bottleneck.
- **Adaptive sampling.** Tiles in regions that recently changed get more poll attention; long-stable regions get sampled less frequently. Reduces floor cost but adds bookkeeping.
- **Linear-feature pass.** When deferred linear biomes (riparian, hedgerow) land, they need a separate mechanism — the area-biome region model won't capture them. Different problem, layered on top of this model.

What this buys:

- Per-chunk aggregation is bounded (constant tile count per chunk, simple loops).
- Region clustering operates on ~1000 chunks instead of ~65k tiles. Re-clustering becomes cheap.
- "What region is tile T in?" is O(1) (chunk hash → chunk's region pointer).
- Splits and merges only re-traverse affected chunks and their neighbors — local work, not map-wide.
- Naturally aligns with the rolling polling sweep.

What it costs:

- Chunk granularity loses tile-level boundary precision. Small features (1-tile-wide riparian strips, single-tile ponds) don't fit cleanly. For phase 1 area biomes this is acceptable; deferred linear biomes (riparian, hedgerow) will need a different mechanism layered on top.
- Chunk size is a tuning parameter. Smaller chunks = better precision and more aggregation cost; larger = the reverse.

Recommended: **chunked hierarchical with periodic full re-clustering as a safety net.** Incremental updates handle common-case small edits; periodic rebuild prevents accumulated state errors. Phase 0 validates this pattern empirically before committing.

**Perf budget (starting target — revisable as we learn):**

Total mod CPU under **1ms per game tick at 250 days/sec time speed on a typical map**, P99 under 3ms. Subsystem slices (provisional):

- Biome detection / region clustering: 0.3ms
- Eco-health updates: 0.3ms
- Connectivity graph: 0.2ms
- Fauna spawn/despawn logic + everything else: 0.2ms

Important constraint: Timberborn supports time speeds up to 10x. **Budget at the highest time speed, not the lowest.** A subsystem that takes 0.5ms per game tick at 1x runs 5ms per real-world frame at 10x — tolerable. 5ms at 1x becomes 50ms at 10x — game is unplayable. The budget above is at the *highest* time speed.

When a subsystem exceeds its slice, that's the signal to optimize before adding features to it.

**Tracking infrastructure (built in phase 0):**

A `PerfTracker` service wraps each subsystem's update with `System.Diagnostics.Stopwatch` and exposes timings to a debug panel. Per subsystem:

- Average time per update (baseline cost)
- P99 / max time (worst-case is what the player feels as a hitch)
- Update frequency (so total budget consumption = avg × frequency)

Plus map-wide:

- Total mod CPU per game tick (sum across subsystems)
- Frame-time correlation (flag ticks where our work exceeded budget AND overall frame time spiked — distinguishes "we're slow" from "vanilla is slow")

The perf data lives as a panel in the high-level mod debug window. Without continuous in-game visibility, regressions go unnoticed until they're entrenched.

**Empirical questions phase 0 should answer:**

- What sweep cycle length balances aggregation freshness against per-frame cost on typical maps? Larger maps need more tiles processed per frame to keep the cycle bounded.
- What is the cost of region re-computation when the player makes a typical structural edit (a few buildings, a wall)? On a worst-case edit (demolishing a large structure that splits or merges regions)?
- How does the polling sweep scale on the largest typical Timberborn maps?
- Is the constant floor cost (full sweep every cycle) tolerable, or do we need adaptive sampling / chunked dirty-tracking from the start?

Don't lock the strategy until phase 0 has answered these.

### Open questions on the core five

- How sharp are biome boundaries? (Smooth gradient between regions, or discrete classification?) **Partially answered:** plateau boundaries are crisp because regions split on `(Z, IsCave)` differences; remaining question is whether a region's *interior* ecology is gradient or discrete (this is what the eventual sub-zone concept will answer).
- ~~What's the spatial unit for biome classification — per tile, per cluster, per player-defined zone?~~ **Resolved:** raw data is per *surface voxel* `(X, Y, Z)`. Classification is per *region* (a 4-connected component of surfaces sharing `(Z, IsCave)`). Sub-zones within a region capture finer ecological variation (irrigated patch within a dry plateau, contaminated lobe within a wet one). Column-level data (moisture/contamination float) has its uses but is not the unit of classification.
- How long does it take for a "young" forest to count as a forest, vs. just a planting?
- What counts as a "connection" between two biome regions — direct adjacency, contiguous vegetation, dedicated corridor biomes, distance with tolerance, or some mix? The choice shapes how connectivity feels in play.

### Spatial model

Two coordinate types in play, plus a region identifier (introduced in Chunk B of Phase 1A):

- **`TileCoord(X, Y)`** — a column. Used for column-level lookups (the fractional moisture/contamination value, neighbor enumeration).
- **`SurfaceCoord(X, Y, Z)`** — a single buildable surface voxel. One column produces N surfaces where N ≥ 1 (typically 1 for natural terrain, more wherever the player has dug, stacked, or built overhangs).
- **`RegionId`** — opaque ID of the plateau a surface belongs to. Plateau = 4-connected component of surfaces sharing `(Z, IsCave)`. Each region tracks its members, identity, age, and (eventually) inter-region edge data.

### Surface data shape

Surfaces are pure raw data — no derived classification:

| Field | Source |
|---|---|
| `Moisture` (float) | column-level: `ISoilMoistureService.SoilMoisture(2D index)` |
| `Contamination` (float) | column-level: `ISoilContaminationService.Contamination(2D index)` |
| `IsMoist` (bool) | per-voxel: `ISoilMoistureService.SoilIsMoist(Vector3Int)` |
| `IsContaminated` (bool) | per-voxel: `ISoilContaminationService.SoilIsContaminated(Vector3Int)` |
| `IsCave` (bool) | per-voxel: `ITerrainService.TryGetDistanceToTerrainAbove(Vector3Int)` |
| `WaterDepth` (float) | per-voxel: `IThreadSafeWaterMap.WaterDepth(Vector3Int)` |
| `Flow` (FlowVector{X, Y}) | per-voxel: `IThreadSafeWaterMap.WaterFlowDirection(Vector3Int)` |

Soil services expose a two-axis API: column-level fractional values, plus per-voxel boolean predicates. Stacked surfaces in a single column share the floats but can disagree on the booleans because the per-voxel predicate depends on Z relative to the water table. Water depth and flow are per-voxel so they too vary across stacked surfaces (a buried platform with no water above vs. the river running on top).

There is **no per-surface tag enum**. Earlier iterations had an `EcologyTag` that priority-collapsed these axes into one bucket; this lost information and forced an arbitrary ordering. Region-level classification (forest, wetland, etc.) and inter-region transition stats (height delta, shared boundary length) are computed from these raw fields and live on the region, not the surface.

## Negative feedback (badwater zones)

Vanilla already makes badwater visually distinct, but lightly. The mod amplifies this:

- Contaminated ground gets ugly flourishes — discolored soil, dead vegetation, perhaps unsettling ambient audio cues.
- Animal corpses or signs of death where creep has spread into formerly healthy zones.
- Absence of fauna — silence — in places that should sound alive.
- Ruins and metal towers function as a milder, localized form of blight. While they stand, they suppress ecological richness in their immediate vicinity — fauna avoids the area and flora variety stays muted even with full irrigation. Removing them releases the surrounding land to develop ecologically.

Soft punishment, by design. The player isn't told "your beavers are sick." They feel something is wrong with the place.

## Positive feedback (healthy zones)

- Animals visibly present and behaving (foraging, drinking, fleeing, herding).
- Ambient audio: birdsong, frogs at dusk, insects, wind through varied flora.
- Flora variety beyond vanilla — wildflowers, undergrowth, different tree shapes.
- "First sighting" notifications for new species reaching a region. Cheap to implement, very satisfying.

## Layers of feedback (effect axes)

The negative/positive lists above describe *what* the player should feel; this section describes *how* — the technical axes along which we apply effects to the world. Each axis has its own technology stack, content cost, and persistence semantics. Designing per-axis lets us ship feedback incrementally rather than waiting for the whole stack to be ready.

Nine axes, ordered roughly by ascending complexity:

1. **Static visual flourishes.** Passive non-interactive ground decoration: textures, decals, ground variety, color tints. Beavers don't path around them. Three implementation routes: (a) tint/swap existing materials at runtime (`MaterialPatcher`-style, code-only); (b) author small flourish prefabs in the Unity SDK with mesh vertices baked at sub-tile positions (water lilies offset to edge of water tile, etc.) and spawn them via the ambient-flora blueprint pipeline (Phase 1 P2); (c) place existing vanilla decorative props at runtime. Route (b) is what the Keystone aesthetic actually wants — vanilla flora rendering bypasses Unity Transform (see `docs/timberborn-api.md` § Visual position is custom-rendered) so runtime sub-tile positioning of vanilla content is *not* a viable route. **Cross-mod story:** because per-flora visual placement requires curating per-content meshes/recipes, integration with third-party plant mods is delivered via small **`Keystone-Compat<X>`** companion mods — one per content mod we want to integrate with, each registering its own flourish recipes against Keystone Core's public API. The curated-per-content pattern is more compelling than a generic placement engine would have been: water lilies positioned where the water is deep enough, cattails along shorelines, daisies in dry meadows, etc.
2. **Interactive flora.** New plants/trees/bushes that behave as full game objects — beavers can plant, cut, harvest. Three tiers of effort: (a) modify existing flora behavior via `ITemplateModifier` (code-only, low payoff); (b) **make cross-faction flora available** to the current faction via biome-driven natural spread (code-only, surprising amount of payoff because cross-faction flora is loaded but normally invisible to the player); (c) author new flora species (Unity required, biggest payoff per item). (b) is the high-leverage middle path — it ships "new flora" without authoring a single new prefab.
3. **Decorative moving flourishes.** Bats, butterflies, frogs that flit through the world driven by chunk data. Visual mobiles, no agency, no persistence (regenerated from chunk state + RNG seed each session). Phase 2 territory.
4. **Animal agents.** Tracked entities with simple behaviors (wander, flee, herd, drink). Backed by per-region capacity counts from the simulation. Phase 2.
5. **Sound / ambience.** *Deferred to nice-to-have.* Emotional impact is enormous; technical lift and asset workflow are nontrivial. Slot left open in case we revisit.
6. **Atmospheric / particle effects.** Mist over wetlands, fireflies over healthy meadows, pollen in sunbeams, dust haze on contaminated ground. Different from #1 (static) because they emit and animate, different from #3 (mobiles) because they're field effects rather than placed entities. Phase 1 if we keep visuals modest; phase 2 if we want richer particle art.
7. **Lighting / post-process / color grading.** *Deferred to nice-to-have.* Per-biome warmth, fog falloff, saturation shift on contamination. Powerful but a separate technology track.
8. **Beaver wellbeing area effects.** Hooks ecology directly into the most consequential vanilla system: a beaver standing in a healthy biome gets a passive wellbeing modifier, a beaver standing in a contaminated zone gets a debuff. No new buildings required (#2-style buildings come Phase 2). This is the one axis where the player feels the effect even when not looking at the world. Phase 1, code-only.
9. **Tile shader overlays.** A data-driven shader pass over ground tiles, mirroring how vanilla shows soil moisture and contamination as visible color/pattern on the terrain. Different from #1 (static decoration) because it's a per-tile shader-driven render layer, not placed assets or material patches; different from #7 (post-process) because it's tile-local rather than global. Useful for surfacing simulation state directly: biome classification, eco-health, region boundaries, biodiversity score, etc. Implementation route is unknown — vanilla likely uses a custom shader/render pass we'd need to either compose with or replace. **Reference:** [Ustice/Timberborn-Wildfire](https://github.com/Ustice/Timberborn-Wildfire) — the author is implementing this technique for a wildfire visualization (per Discord); not yet in the repo at time of writing, worth re-checking later. Backlog; not Phase 1.

**Persistence vs regeneration per axis.** For decorative content (#1, #3, #6) we regenerate placements each session from chunk state plus a stable RNG seed — no need to persist individual placements. For full game objects (#2 interactive flora) we use the game's own placement persistence. For #4 agents, per-individual state is intentionally ephemeral (per Animal representation model resolution above). For #8 wellbeing modifiers, the modifier *value* is derived from current chunk state — no separate persistence layer.

**Phase split (subject to revision):**
- **Phase 1 (MVP):** axes 1, 2, 6. Passive visual variety, interactive flora using the cross-faction trick + light spec mods, atmospheric particles. Visual feedback driven by the per-chunk biome Suitability and Maturity; no per-beaver effects yet, no animals.
- **Phase 2 (full ecology):** axes 3, 4, 8. Decorative mobiles + tracked animal agents (prefabs, behaviours) + per-beaver wellbeing modifiers driven by chunk state.
- **Nice-to-have / deferred:** axes 5 (sound), 7 (lighting), 9 (tile shader overlays). Deliberately punted.

**Unity is allowed in Phase 1.** An earlier framing tried to keep Phase 1 strictly code-only. That's a self-imposed limit, not a technical one — the SDK is sitting there ready and a small amount of Unity work for axes that genuinely benefit (custom particle textures for #6, eventually new flora species for #2 tier (c)) is reasonable. We don't have to white-knuckle it.

## Content composition (traits + presets)

The feedback axes describe *what* feedback we deliver; this section describes *how* — the trait primitives Keystone composes into player-visible content. Content is described along **two orthogonal axes**, plus an "escape-hatch" tier for cases where entity-ification would be wrong.

The A / B / C / D labels survive as **convenience aliases for four useful trait compositions**. They are not the primitives — composition is. New content opts into individual traits via component specs; "Class B" just names a common combination of those opt-ins.

### Axis 1 — Reactivity (composable, opt-in)

Independent traits the blueprint opts into via dedicated component specs. An entity can carry zero, one, or both:

| Trait | Reads | Example |
|---|---|---|
| **Environment-sensitive** | Live game data per tile (irrigation, contamination, water depth, flow) | Rock cluster re-tints when the tile floods (`KeystoneRockTintSpec` + `KeystoneRockTint` tickable) |
| **Ecosystem-sensitive** | Keystone chunk biome data (Suitability, Maturity, active level) | Flourish dies when its chunk's biome Suitability crashes (`KeystoneFlourishSpec` lifecycle phases) |

Static (neither trait) is the default — the entity just sits there.

### Axis 2 — Lifecycle / interaction (three mutually exclusive tiers)

| Tier | Removal model | Player interaction |
|---|---|---|
| **Mod-managed visual** | Mod registry adds / removes; not a `BlockObject`, claims no tile | None — invisible to player tools |
| **Yielding** | Removed when the player builds over the tile, or via the destroy tool (instant) | Not selectable; not markable |
| **Blocking + markable** | Cannot be built over. Player marks for destruction; a beaver removes it on a job. (Also removable instantly via the destroy tool.) | Selectable; markable for clearing |

### Trait → vanilla integration

Design rule: use vanilla machinery when the pattern clearly matches; build a Keystone variant when only half of the vanilla pattern fits and we'd need to hack the rest. Selectability is the deliberate exception — it's core enough that we accept hooking into it.

| Trait | Mechanism | Pattern fit | What composes on the blueprint |
|---|---|---|---|
| Yielding to construction | `BlockObjectSpec.Overridable: true` | Clean adopt | Set the flag on the blueprint's `BlockObjectSpec` |
| Markable for destruction | `DemolishableSpec` + runtime `Demolishing` (vanilla pipeline; no yield required) | Clean adopt | Compose `DemolishableSpec`; vanilla drives marking, beaver job, removal |
| Instant destroy-tool removal | `BlockObject` default; `BuildingDeconstructionClassBPatch` widens the building tool's filter to admit Class B (non-`BuildingSpec`) entities | Default / patch | Inherited; Class B is explicitly injected into the building-tool filter |
| Selection suppression | Harmony patches keyed on `KeystoneVariant.Class == "B"` | **Core, must hook** — no clean blueprint-level opt-out exists | Compose `KeystoneVariantSpec`; set Class string at spawn |
| Environment-sensitive reactivity | `TickableComponent` polling port queries (`IWaterQuery` / `IMoistureQuery` / `IContaminationQuery`) | Keystone-native | One ComponentSpec per reactive behaviour |
| Ecosystem-sensitive reactivity | Recipe layer + lifecycle decorators (`KeystoneFlourish`) drive spawn / death decisions | Keystone-native | Blueprint participates in recipes; lifecycle specs handle phase transitions |

The non-Harmony traits compose declaratively at the blueprint level with no Keystone-side runtime work — that's the bar for "clean adopt." The Harmony patches around selection suppression and Class B's destroy-tool widening are accepted because the vanilla seam doesn't allow declarative opt-in / opt-out at those points.

### Presets

| Preset | Axis 2 (lifecycle) | Typical Axis 1 | Source | Real example |
|---|---|---|---|---|
| **Class A** | Mod-managed visual | Optional (rare) | Keystone-authored visual; no entity registration | Atmospheric mist; sub-tile clover patches |
| **Class B** | Yielding | Any combination | Keystone-authored blueprint | `KeystoneFlourishTest` Pine + Sunflower (Phase 0/P2 prototype) |
| **Class C** | Blocking + markable | Any combination | Keystone-authored blueprint | `KeystoneRockCluster1..8` (currently instant-demolishable; markable is the refactor target) |
| **Class D** | Blocking + markable (vanilla mechanics) | Vanilla growth pipeline | Existing vanilla blueprint, faction-irrelevant bits stripped | Pine, Maple, CoffeeBush |
| **Class E** *(Phase 2)* | Own taxonomy | n/a | New entity layer with `KeystoneSpeciesSpec` | Deer, songbirds, foxes |

Class D differs from "Class C with a vanilla blueprint" because vanilla blueprints carry their full growth / reproduction / harvest pipeline (`GrowableSpec`, `ReproducibleSpec`, `CuttableSpec`, `PlantableSpec`). Keystone preserves these as-is, only removing the faction-specific Plantable / Gatherable handles when the active faction can't use them (via `CrossFactionCollectionProvider` + `TemplateCollectionServicePatch`). Authoring a Class D recipe is a one-line reference to a vanilla blueprint name; everything else is vanilla's responsibility.

### Class A is an escape hatch

A "mod-managed visual" decoration carries no persistence, no save state, no selection affordance — the mod owns its full lifecycle via `KeystoneDecorationRegistry`. This duplicates work the entity system does for free (persistence, lifecycle, tool integration) and forces the registry to reconcile its set against changing world state every cycle.

Reserve Class A for content that genuinely doesn't fit the entity model: ambient particles (mist, fireflies), sub-tile visual flourishes too small to entity-ify, decorative mobiles. **Default to entity-based tiers (B / C / D) wherever the content claims a tile.** If the player could plausibly want to inspect or remove it, it's not Class A.

### Decision tree when adding new content

1. **Does it claim a tile?** No → Class A (and revisit step 1 — usually the answer is "no" because nothing's actually there). Yes → continue.
2. **Is it vanilla flora?** Yes → Class D, reference the vanilla blueprint by name. No → continue.
3. **Should construction sweep it away?** Yes → Class B. No → Class C.
4. **What's it reactive to?** Compose reactivity specs (`KeystoneRockTintSpec`, `KeystoneFlourishSpec`, …) orthogonally — the choice doesn't depend on the lifecycle tier.

### Level affinity for class choice

A heuristic, not a hard rule: **L1 is almost always Class A or B.** L1 represents the biome hinting at itself — flavor and ambient texture, no harvestable content yet. Class C / D reads as commitment ("here is a thing the player can work with") and belongs at later levels when the biome has matured.

L1 recipes typically use *multiple smaller-version variants* of plants the biome uses at higher levels (mini-cattails firing together rather than one full-size blueprint), giving visual continuity with L2 / L3 in a "flavor without officially occupying space" register. Class A coexists by not being a `BlockObject` at all; Class B is a `BlockObject` that yields under build placement, so the player never has to demolish it before building.

The choice between A and B at L1 is about whether the content fits the entity model: Class B carries a real entity (heavier but spawn placements survive across save / load via Timberborn's machinery and the player can interact with it via the destroy tool); Class A regenerates deterministically from `ChunkValueStore` on each load (lighter but adds reconcile-and-despawn machinery and is invisible to player tools). **Default to Class B when the content can be entity-ified at all.** Reserve A for content that genuinely cannot — particle systems, sub-tile decoration too small for a per-instance entity.

### Mapping back to feedback axes

| Feedback axis | Likely preset | Notes |
|---|---|---|
| 1. Static visual flourishes | Class B | Class A for sub-tile / non-blocking ambient that can't be entity-ified. |
| 2. Interactive flora variety | Class D | Vanilla pipeline including cross-faction donors. |
| 3. Decorative moving flourishes | Class A | Particle systems / animated mobiles, ephemeral. |
| 4. Animal agents | Class E (Phase 2) | Own taxonomy. |
| 6. Atmospheric / particle effects | Class A | The escape-hatch fit. |
| 8. Beaver wellbeing | (no spawned content) | Code-only modifier on existing beaver entities. |

### Implementation status (refactor in progress as of 2026-05-12)

The codebase is mid-migration toward the trait model. Concrete divergences from target, in roughly the order they should land:

- **Class C is directly demolishable (instant), not markable.** Current rock clusters destroy on click. Target: compose `DemolishableSpec` on Class C blueprints, drop the instant path, beavers do the work on a job triggered by the player marking the cluster.
- **Class B carries `DemolishableSpec`** with `DemolishableSelectionToolPatch` Harmony-suppressing the mark UI. Target: drop `DemolishableSpec` from Class B blueprints and remove the patch — declarative composition replaces the hack.
- **`BlockObjectSpec.Overridable: true` isn't uniformly set on Class B.** Older flourishes (pre-rock-cluster era) may lack the flag, defeating the "yielding" trait. Audit and set uniformly.
- **Spawn handlers are one-per-class** (`ClassASpawnHandler` / `ClassBSpawnHandler` / …) rather than dispatching from a single applier over composed trait sets. The recipe layer (`ChunkRulesApplier` + per-class `IRuleHandler` implementations) consolidates as trait-spec composition stabilises.
- **`KeystoneVariant.Class` stamps stay** — they're the integration point for the one trait (selection suppression) where the core system is hooked via Harmony. The stamp is the input the patches key off; removing it would require an alternative selectability seam, which vanilla doesn't provide.

The deeper sections below (recipe pipeline, Class A handler details, Class B prototype) describe current implementation. The spawn / lifecycle machinery they document is largely orthogonal to the per-entity trait composition refactor and remains accurate.

### Class A architecture (Phase 1F)

Class A spans two design tiers (atmospheric and ambient) but one code class. Both go through `KeystoneDecorationRegistry`, which clones a donor prefab (or accepts a runtime-built `GameObject`) and tracks the live set without entity registration. Reactivity is opt-in per decoration via `IDecorationController` — purely passive Class A entries cost zero per tick after spawn.

The eco-sensitive ambient tier (presence driven by per-tile biome **Maturity** values, in game-days) is automated by the handler:

- **Level table:** `BiomeLevelTable` (Core) holds the per-biome progression ladder, populated at PostLoad by `BiomeLevelCatalog` (Mod) from `KeystoneBiomeLevelsSpec` instances. A default-ladder spec applies its level entries to every biome; per-biome override specs redefine specific levels' ranges. The ladder is the single source of truth for "what's an L1 / L2 / L3 range in this biome"; recipes only carry the level id.
- **Recipe data:** `KeystoneRecipeBookSpec` on a Keystone-authored "recipe book" blueprint carries a list of `(Class, BlueprintName(s), Biome, Level, Filter, Weight)` entries. Recipes are decoupled from the blueprints they reference -- the same blueprint asset can appear in multiple recipes with different classes, mods extend by shipping their own recipe books. `FlourishCatalog` walks every book at PostLoad and indexes by `(Class, Biome, LevelId)`. Density lives on the *level* (`KeystoneBiomeLevelsSpec`), not the recipe — recipes in the same `(biome, level)` bucket share the level's density and compete via `Weight` for the activated tile (default 0.10 per level).
- **Handlers (one per class).** `ClassASpawnHandler` (per-cycle reconcile-and-despawn decoration regeneration), `ClassBSpawnHandler` (one-shot persistent inert flourish; stamps `KeystoneVariant.Class = "B"` for selection-suppression patches), `ClassCSpawnHandler` (one-shot persistent but selectable + demolishable; no `_attempted` memo, so demolished tiles re-evaluate next cycle), `ClassDSpawnHandler` (vanilla flora donor placement; no variant stamp, no growth fast-forward; vanilla `ReproducibleSpec` handles natural regrowth; carries a `(tile, levelId)` memo so a cut tree doesn't re-fire from the same level). All four go through `SpawnHandlerBase.EvaluateLevel`, which dispatches on the level's `Mode` field (deterministic hash gate or stochastic RNG roll — see below); the difference between handlers is what gets spawned and how the lifecycle behaves after.
- **Per-entity class persists across save/load.** `KeystoneVariantSpec` on every Keystone-spawn-eligible blueprint triggers a `KeystoneVariant` component that records which class spawned the entity. Saved via `IPersistentEntity` so the designation survives reload. Required because Timberborn's entity persistence rebuilds entities from blueprint specs; runtime-added Unity components don't survive.
- **Activation gate.** Tile fires a recipe iff `Biome` is the tile's dominant biome AND maturity ≥ the level's `LowerMaturity` AND the per-level `Mode` gate passes. Two modes, chosen per-level in the levels blueprint (`"Mode": "Deterministic"` or `"Mode": "Stochastic"`; default deterministic):
  - **Deterministic.** `FlourishThreshold.ComputeActivation(tile, biome, levelId) < level.Density * progress`, where `progress = clamp01((maturity - LowerMaturity) / (UpperMaturity - LowerMaturity))`. Density ramps in linearly across the level's maturity range rather than snapping to full strength at `LowerMaturity`. The same tiles always activate at the same maturity, so the population is reproducible across save/load. Per-level keying in the hash means independent draws across levels — if L1 and L2 are both active at the same biome, their coverage stacks additively (with overlap).
  - **Stochastic.** Each cycle, every eligible tile rolls an independent RNG check against `level.Density`. No ramp — the per-day chance is the full `Density` from the moment the level activates (so `UpperMaturity` is informational only for stochastic levels). Population accumulates over real time; reload reproducibility is not preserved (the design is "trickles in," not "regenerates from a seed"). Typical use: Class D vanilla flora, where the entity has its own lifecycle (vanilla `ReproducibleSpec` for regrowth) and "10% chance per day of a tree" reads more naturally than a deterministic band.

  Mode is class-agnostic. A Class B level can be stochastic (one-shot inert flourishes trickling in) or a Class D level can be deterministic; the choice is driven by player-feel, not by what the recipe spawns.
- **No save state per instance.** State continuity across save/load comes for free: Maturity persists, the handler regenerates the same flora from the same Maturity values, hashes are deterministic. Class B/C/D entities also persist via Timberborn's vanilla machinery.

Atmospheric Class A (mist, particles, fly-through fauna) is authored separately — particle systems and ambient effects spawned at region or setting scope, not driven by per-tile biome state. Same registry, no recipe spec, no handler.

**Future optimisation (not built yet):** when the level-driven action system arrives (per-biome level checkpoints triggering per-tile actions), each tile's "what biome is dominant here" question will sample all 11 biomes' bilinearly-interpolated Maturity values and pick the max. At ~44 store reads per tile, the rolling sweep amortises this comfortably across game-ticks at any reasonable speed, but the cost can be cut to ≤4 candidates per tile (often fewer) by **caching the top-K dominant biomes per chunk** when the Maturity updater writes. A tile's candidate set then becomes the union of top-K from its 4 surrounding chunks. With K = 1 (just the single dominant biome per chunk) this is incorrect at three-biome junctions where a non-corner-dominant biome wins on bilinear average — covers ~99% of cases but loses correctness in narrow boundary stripes. K = 3-5 catches the boundary cases. Skip until profiling demands it; layer on with the trade-off in mind.

### Class B architecture (validated as of Phase 0/P2 prototype)

The Phase 0 prototype (`KeystoneFlourishTest`) proved Class B end-to-end. Pattern reference:

- **Spec set:** `TemplateSpec`, `BlockObjectSpec`, `BlockObjectModelSpec`, `KeystoneFlourishSpec` (marker), `NaturalResourceSpec`, `WateredNaturalResourceSpec`, `FloodableNaturalResourceSpec`, `DemolishableSpec`. **No** `GrowableSpec`, `ReproducibleSpec`, `CuttableSpec`, `GatherableSpec`, `PlantableSpec` — vanilla's growth/reproduction/harvest pipelines stay out, which is what makes it non-interactive.
- **Children hierarchy:** `#Models → #Alive / #Dying / #Dead`, each containing per-child mesh entries (`TimbermeshSpec` + `TransformSpec`) for sub-tile composition. Hierarchy details and the `TransformSpec` finding are documented in `docs/timberborn-api.md` § "Sub-tile composition via TransformSpec" and § "Custom mesh authoring".
- **Required Harmony patch:** one Prefix on `NaturalResourceModel.ShowCurrentModel` to skip its body when no `Growable` is attached (vanilla's else-branch hard-derefs). `~30` LoC.
- **Custom lifecycle:** `KeystoneFlourish` decorator subscribes to `LivingNaturalResource` / `DyingNaturalResource` events, toggles `#Alive` / `#Dying` / `#Dead` children via `SetActive`. Replaces vanilla's automatic visual switching.

Class B requires affordance care: a normal-sized tree as Class B reads as "broken cuttable" to the player. Viable Class B "trees" are sapling-sized (clearly young; could eventually convert to Class C or D), or overlapping clusters where the silhouette doesn't read as a single discrete tree (mangrove pneumatophores at the water edge mixed with cattail clumps reading as "wetland thicket").

### Class A handler details

Detail-level walk-through of the deterministic Class A pipeline summarised above.

#### Per-surface placement

For each unblocked surface tile, derive: "what biome should I display, and at what density?" The answer is a pure function of:

1. **Tile-level Suitability via bilinear interpolation.** Read the 4 nearest chunk centers' Suitability for each biome, bilinearly weight by tile position, get a per-tile per-biome Suitability value. This is what kills chunk-grid seams: the 4×4 chunks are coarse, but the tile-level reads are smooth.
2. **Per-surface per-biome thresholds.** Hash from `(world_x, world_y, biome_name, recipe_key)` into a per-tile per-recipe activation threshold in `[0, 1]`. The hash gives a stable but spatially-varying threshold — some tiles activate Forest at Suitability=0.3, others not until 0.7. Thresholds are deterministic so reload reproduces the same pattern; spatial variation gives the visual a "sometimes there, sometimes not" organic feel rather than a sharp on/off zone.
3. **Recipe lookup.** For each biome with `Suitability > threshold` on a tile, pull the per-biome recipe (which flora/flourish to spawn at what density). Multiple biomes can fire on the same tile — moss for damp Forest, a wildflower for the Grassland reading on the same tile, no conflict.

Each Class A recipe is independent, and the per-surface threshold gives spatially varied activation without any global tuning lever.

#### Reconciliation cadence

Daily-ish (currently every ~5% of a game day, ~1.2 game-hours). Each cycle, walk surveyed surfaces, compute the desired flourish state from current Suitability, and add/remove flourishes to match. No per-frame work — the sweep amortises across the day. Smooth biome transitions are produced naturally: as a chunk's Forest Suitability drifts up, more and more tiles cross their per-tile threshold, so flora fades in tile-by-tile rather than as a wall.

#### Save / load

`ChunkValueStore` is the only persistent state. On load, the placement function regenerates the visible decoration from the loaded Suitability — same pure function, same input, same output. A save mid-collapse resumes with the same decoration in the same state of disappearance.

#### Spawn surface

- **Singleton `KeystoneDecorationRegistry`** owns the live decoration set in memory (no entity registration). Spawn API for programmatic creation; cleanup tracks `GameObject + tile`.
- **Class A is passive by definition** — no controller, no per-tick reactivity. Class B decorations sharing the registry have a controller; the registry handles both via the same lifecycle.
- **Spawn source:** `IPrefabOptimizationChain.Process(blueprint)` + `UnityEngine.Object.Instantiate` for vanilla geometry; future custom assets via the Blender plugin pipeline.

## Open design questions

These are deferred but will need answers before/during implementation:

- ~~**Animal representation model.** Discrete entities (every animal tracked), population abstraction (per-region counts that tick), or hybrid (population sim drives spawning of a few visible representatives). Affects performance and feel.~~ **Resolved:** hybrid. The base mod derives **per-cluster capacity** from cluster Score × the recipe's `FaunaCapacityAtSaturation` (gated by `FaunaMinScore`); a 6-game-hour rolling sweep enqueues clusters with deficit and a per-frame drainer instantiates one fauna at a time off-frustum until the deficit closes. Per-individual animal state (behaviour tree, target waypoint) is never persisted and not save-replay-deterministic; entities are ephemeral simulation puppets. They are not despawned when the camera moves away — frustum-gating is only on the moment of spawn/cull, so existing fauna roam freely regardless of where the player is looking; stranded agents (current tile fails walkability, or no successful walk in 6 game-hours) self-despawn on their hourly self-check.
- **Spatial granularity.** Per-tile, per-region, per-biome. Likely per-region for tractability, but TBD.
- **Sim tick cadence.** Does ecology evolve per game tick, per day, per season? Probably daily, but TBD.
- **Save/load.** All new state must serialize and replay deterministically — Timberborn is tick-deterministic. Constrains the simulation design.
- **Phase 0 vertical slice.** Worth doing a thin proof-of-concept (one species, one biome, minimal feedback) before committing to full phase 1, to validate the modding stack and visual feel.

## Phase 2+ parking lot

Ideas raised but explicitly deferred. Captured here so they aren't lost.

- **Mega-tree / monument project from preserved ruins** (likely Folktails faction mod). Instead of demolishing a ruin for metal (vanilla incentive), the player can convert the ruin itself into the foundation of a long-term project — a mega-tree or similar landmark — that actively boosts surrounding ecological richness. This preserves a meaningful "demolish for metal vs. keep for ecology" tension, with the cost side being a genuinely scarce vanilla resource. Phase 1 keeps the ruin-as-blight behavior clean; phase 2 reintroduces the trade-off at a higher level by making the ruin worth more *unbroken* than broken — but only if you commit to the project.
- **Hunting and the wild economy.** Wild zones produce things farms can't (hunting yields, foraged goods). Animals migrate toward hunted areas, so hunters profit from large rewilded hinterlands. Predators compete with hunters, providing a soft cap.
- **Controlled-flood farming + floodplain biome.** Player engineers basins that flood seasonally; flooded crops get a yield boost from soil moisture during the dry phase. Pairs naturally with the wet/drought cycle — surplus wet-season water banks moisture for drought-season harvest. The same engineering verb produces a wild floodplain (migratory birds, wildflowers) if no crops are planted, or a productive flood-farm if they are. Should be built together with the floodplain biome as a single unit, since the biome's complexity is hard to justify without this gameplay anchor.
- **Mechanical penalties.** Pollinator collapse → orchard yields drop, etc. Used selectively where the cause-and-effect is satisfying, not as a blanket tax.
- **Procedurally generated rocky terrain** as a true new biome, if the visual and gameplay payoff justifies the implementation cost.
