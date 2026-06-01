# Forest — development pipeline

**Status:** L1 shipped. Single-level for now; deeper progression a future
content pass.

The level-by-level cadence for the Forest biome: what the player sees as a
Forest-dominant tile accumulates Maturity, from a young grove to a
mature canopy.

A Forest chunk typically arrives via succession from Grassland: as
Grassland's L3 tree spawns elevate `TreeCount`, the chunk's
`BiomeTargets.Forest` Suitability climbs past `BiomeTargets.Grassland`'s Suitability, and
Maturity in Forest eventually overtakes Maturity in Grassland. From
that moment Forest's recipes take over.

## Level ladder

Per-biome override in `unity-assets/Keystone/Data/Biomes/KeystoneForestLevels`:

| Level | Maturity range | Density | Theme |
|---|---|---|---|
| L1 | `[0.1, 1000000]` | `0.005` | Mixed tree canopy — Birch-dominant with occasional Maple, Chestnut, Oak. |

L1 fires early (Maturity 0.1 ≈ a couple of game-hours after the chunk
became Forest-dominant). Density 0.005 (Stochastic) means each Forest-
dominant tile has ~0.5% chance per cycle of receiving a tree spawn —
slower than Grassland L4 (0.03) so a chunk that just flipped from
Grassland fills in gradually rather than instantly carpeting itself.

## Recipes per level

**L1 — Class D tree canopy (no filter).** Same 4-species pool as
Grassland L3 but with **birch demoted to a rare straggler**. Birch is the
pioneer species in Grassland L3 (weighted 6/11 there); once a chunk has
matured into Forest, birch backs off and the slower-growing canopy
species take over.

| Recipe | Class | Weight | Vanilla blueprint |
|---|---|---|---|
| Birch | D | 0.5 | `Birch` |
| Maple | D | 2.0 | `Maple` |
| ChestnutTree | D | 2.0 | `ChestnutTree` |
| Oak | D | 1.0 | `Oak` |

Weights sum to 5.5. Maple and Chestnut fire ~36% each, Oak ~18%, Birch
~9%. The Grassland L3 birches that already exist on the chunk persist —
this only shifts what *new* spawns look like. Over many game-days the
forest's composition drifts from "young birch-dominated grove" toward
"mature mixed canopy with the bigger trees", with occasional birches
mixed in as natural-feeling stragglers.

Class D uses RNG-driven activation: each cycle rolls one `level.Density`
gate per eligible tile, then weighted-picks one recipe.

## Visual cadence

1. **Pre-Forest** — chunk is Grassland-dominant. Grassland recipes run
   (L1 mini-flourishes, L2 bushes, L3 trees). Trees accumulate; once
   `TreeCount ≥ 3` with `TreeSpeciesCount ≥ 2`, Forest's target exceeds
   Grassland's. Maturity migrates over the following game-days.
2. **Forest takes over.** Once Forest is the dominant biome, Grassland
   recipes stop firing in the chunk; Forest L1 starts firing on
   remaining empty tiles. The L1 mini-flourishes (Class B) that
   Grassland left behind remain in place — Forest doesn't author
   replacements for them yet, and they don't conflict with the L1 tree
   spawns (Class D's `TryClearForReplacement` rule allows Forest L1 trees
   to succeed Grassland minis on the same tile).
3. **Mature forest.** Tree population approaches the chunk's tile
   capacity (16 tiles minus any non-plantable surfaces — water, paths,
   etc.). Vanilla `ReproducibleSpec` on the spawned trees continues to
   produce seedlings nearby, supplementing Keystone's spawning. Cut
   trees re-emerge via vanilla reproduction (Keystone doesn't re-spawn
   cut Class D entities — they're memo'd per `(tile, levelId)`).

## Monoculture interaction

A managed forest — Forester area marked for a single tree species —
registers as Monoculture. The chunk's `BiomeTargets.Forest` is suppressed
via `(1 − Monoculture)`. Player-drawn marks contribute to the plantable
count immediately; once a chunk reads as monoculture, Forest scoring
yields to the player's management and Keystone backs off (per-tile mark
check in `ChunkRulesApplier.ProcessUnit` also skips marked tiles
even on non-monoculture chunks).

A natural forest with diverse species reads as low Monoculture, so
Forest's target stays high and Keystone continues filling in the canopy.
The dominance signal is Simpson's D, so 3+ species reads as fully
diverse regardless of distribution.

## Open questions

1. **L2 and beyond.** A mature canopy is a clear stopping point for
   density-driven content, but deeper progression could express in other
   ways:
   - **Ambient fauna** (Class A) — birds, deer, butterflies. No
     occupancy implications; pure visual richness layer.
   - **Mushrooms and undergrowth** (Class B or new Class C) — small
     ground-flora under the canopy. Would need a spatial filter (e.g.
     `"UnderCanopy"`) to spawn only on tiles adjacent to trees.
   - **Mature-tree variants** — currently the L1 trees grow via vanilla
     `GrowableSpec` from seedlings; could a deeper level introduce
     specifically mature-stage trees, or trees of additional species
     (e.g. ancient oaks, pines)?
2. **L1 spawn rate calibration.** Density was dropped from 0.10 to
   0.005 after playtest showed the 0.10 rate carpeted a freshly-flipped
   Forest chunk far too quickly (especially compounded with the
   Grassland L4 trees that persist past the biome flip). 0.005 is now
   well below Grassland L4's 0.03, so the canopy fills in gradually.
   Revisit if the new rate reads as too sparse — the Grassland-trees-
   persist effect alone may carry most of the canopy work.
