# Grassland — development pipeline

**Status:** L1, L2, L3 shipped. Tunable; no major design gaps known.

The level-by-level cadence for the Grassland biome: what the player sees as
a Grassland-dominant tile accumulates Maturity, from sparse pioneer cover
to a mature mixed forest.

## Level ladder

Per-biome override in `unity-assets/Keystone/Data/Biomes/KeystoneGrasslandLevels`:

| Level | Maturity range | Density | Mode | Theme |
|---|---|---|---|---|
| L1 | `[0.5, 2.5]` | `0.08` | Deterministic (Class B) | Mini-flourishes — 25 mesh-composed blueprints from a seedling palette. |
| L2 | `[2.5, 30.0]` | `0.10` | Deterministic (Class B) | Same mini-flourish pool, second band over higher maturity. |
| L3 | `[5.0, 1000000]` | `0.005` | Stochastic (Class D) | Bushes — vanilla `BlueberryBush` and `Dandelion`. |
| L4 | `[10.0, 1000000]` | `0.005` | Stochastic (Class D) | Mixed tree canopy — Birch dominates with occasional Maple, Chestnut, Oak. |

Density semantics differ by mode. **Deterministic levels** (L1, L2): a
per-tile hash gate compared to `Density × progress`; once a tile activates
it stays activated. At full progress, ~8% (L1) and ~10% (L2) of tiles are
permanently activated, with their hashes independent so the combined
coverage from the two levels is ~17%.

**Stochastic levels** (L3, L4): each cycle is a per-tile RNG roll at the
full `Density` rate (1 cycle = 1 game-day). The `(tile, levelId)` memo in
Class D's spawn handler prevents a successful tile from re-rolling, so the
chunk's tree/bush count is asymptotic to its plantable tile count, not
unbounded. At `0.005`, expected days to first spawn on a given tile is
~200; expected per-chunk spawn rate is ~0.08 spawns/day across 16 tiles.

## Recipes per level

**L1 — Class B mini-flourishes (no spatial filter).** Twenty-five
mesh-composed blueprints in
`unity-assets/Keystone/Data/NaturalResources/KeystoneGrasslandMini{1..25}`,
each composing 2–3 vanilla seedling meshes per `TransformSpec` from the
palette `BlueberryBush + Dandelion + Sunflower + Eggplant + Corn`. Generated
deterministically via:

```bash
python tools/generate-flourish-blueprints.py \
    --prefix KeystoneGrasslandMini \
    --plants BlueberryBush Dandelion Sunflower Eggplant Corn \
    --count 25 --plants-per-blueprint 2-3 --seed 1 --force
```

Registered as a single Class B `BlueprintNames` array in
`KeystoneGrasslandRecipes.blueprint.json` — the handler weighted-picks
uniformly across the 25 variants per activated tile.

**L2 — Class D bushes (no filter).**

| Recipe | Class | Weight | Vanilla blueprint |
|---|---|---|---|
| BlueberryBush | D | 1.0 (default) | `BlueberryBush` |
| Dandelion | D | 1.0 (default) | `Dandelion` |

Class D uses RNG-driven activation: each cycle rolls one `level.Density`
gate per eligible tile, then weighted-picks one recipe from the pool. The
spawned entity is fully vanilla — grows from seedling, gatherable, governed
by `ReproducibleSpec`. Keystone doesn't re-spawn cut entities (memo'd per
`(tile, levelId)` until reload).

**L3 — Class D tree canopy (no filter).**

| Recipe | Class | Weight | Vanilla blueprint |
|---|---|---|---|
| Birch | D | 6.0 | `Birch` |
| Maple | D | 2.0 | `Maple` |
| ChestnutTree | D | 2.0 | `ChestnutTree` |
| Oak | D | 1.0 | `Oak` |

Weights sum to 11. So Birch fires ~55% of the time, Maple and Chestnut ~18%
each, Oak ~9%. With a 4-species pool firing stochastically, expect
substantial species diversity over time — once a chunk has accumulated a
handful of L3 spawns, its `PlantableSpeciesCount` typically climbs past 2,
and the Monoculture suppression on Forest/Grassland softens, allowing
Forest's natural progression to take over.

## Visual cadence

1. **Pre-L1** (Maturity < 0.1, roughly the first ~2 game-hours). Plain
   grass terrain, no Keystone-spawned content.
2. **L1 fires.** Mini-flourishes start appearing across ~10% of grass
   tiles. Mix of vanilla seedling meshes — visually reads as a moderately
   wild meadow with diverse small plants.
3. **L2 fires** (~0.5 game-days). Stochastic bushes appear on previously
   empty tiles. The mini-flourishes from L1 stay where they were; bushes
   coexist on different tiles. (A Class D spawn aborts on a tile that
   already holds a live L1 mini — as of 2026-06-06 live Class B is no
   longer displaced by Class D. L1 minis occupy only a bounded,
   hash-deterministic subset of tiles, so most tiles stay free for L2/L3.)
4. **L3 fires** (~2 game-days). Tree seedlings (Birch-weighted) begin
   appearing on remaining empty tiles. Trees grow from seedlings at
   vanilla rate. Class D no longer displaces live Class B, so tree spawns
   establish on tiles not held by a live mini rather than bulldozing the
   mini layer — see "Succession" below.
5. **Natural succession** (longer-term). As `TreeCount` rises, the chunk's
   `ChunkBiomeInputs.TreeCount` and `TreeSpeciesCount` shift the
   `BiomeTargets.Forest` formula above `BiomeTargets.Grassland`. The
   chunk's dominant biome eventually flips from Grassland to Forest. From
   that moment Keystone goes dormant on the chunk (no Forest recipes
   defined yet) and vanilla `ReproducibleSpec` on the trees handles
   further expansion.

## Succession to Forest

By design, an unmanaged Grassland chunk progresses toward Forest over the
course of multiple game-weeks. The trajectory:

- L3's stochastic Class D spawns elevate `TreeCount`.
- `Forest` target grows with tree density and species diversity.
- `Grassland` target shrinks via `(1 − tree/5)` and `(1 − Monoculture)`.
- At ~3+ trees of ≥2 species, Forest Suitability crosses Grassland's. Maturity
  in Forest accumulates faster than Grassland decays, so dominant-biome
  eventually flips.
- After flip, Grassland recipes stop firing (dispatcher gates on dominant
  biome). Forest recipes don't exist yet, so further evolution comes only
  from vanilla reproduction.

**Trees do not displace live minis** (retracted 2026-06-06). A Class D
spawn aborts on any tile holding a live Class B mini; the prior
"succession override" that demolished a live mini to seat a tree is gone.
This barely slows the Forest flip: Class B spawns deterministically on a
bounded subset of tiles (~10% at L1), so the majority stay open for trees
to seat on, and `TreeCount` still climbs on the free tiles. A mini's tile
is only reclaimed for a tree once the mini has *died* (the dead-flourish
recovery path still clears dead husks). Net effect: the meadow's mini
layer persists alongside the growing trees instead of being bulldozed.

Player intervention reverses succession: cutting trees lowers `TreeCount`,
Grassland regains dominance, and Class B minis re-emerge in their original
hash-deterministic positions.

## Monoculture interaction

A player-managed Grassland farm or tree plantation (3+ plantables, low
species diversity) registers as `BiomeTargets.Monoculture`. Monoculture
multiplicatively suppresses Forest, Grassland, and Riparian targets via
`(1 − Monoculture)`. Player-drawn Forester or Farmhouse marks contribute
to the plantable count immediately, so the chunk reads as monoculture from
the moment the player commits to the area — Keystone backs off from the
chunk before any sapling grows.

The Class A/B/C/D handlers also skip individual marked tiles via the
`IPlantingMarkQuery` check in `ChunkRulesApplier.ProcessUnit`, so
even on a Grassland-dominant chunk that hasn't yet crossed the
monoculture threshold, marked tiles get a per-tile pass.

## Open questions

- **L3 / L4 spawn rate.** Dropped to 0.005 each from earlier values
  (0.01 / 0.03) after playtest showed the cumulative per-chunk rate
  filled chunks too fast. At 0.005 across 16 tiles a chunk accumulates
  ~0.08 spawns/day; the Grassland → Forest succession (requiring ≥5
  trees with ≥2 species) will take on the order of ~70-100 game-days
  rather than the ~20 days the prior 0.03 rate produced. Revisit if
  the succession arc reads as too slow.
- **GroundCover content.** L1's mini-flourishes are visually rich but
  there's no Class A particle / ambient-effect layer for Grassland yet.
  Could add wind-blown petals, butterflies, or similar atmospheric
  effects per-tile via the existing `KeystoneDecorationRegistry`.
- **L4 / mature forest.** When a Grassland chunk has flipped to Forest,
  what's the next stage of evolution? Forest's own recipe ladder is the
  natural answer; not yet authored. (See `docs/biomes/forest.md`.)
