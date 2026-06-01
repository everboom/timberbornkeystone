# Wetland — development pipeline

**Status:** L1, L2, L3 shipped. Tunable.

The level-by-level cadence for the Wetland biome: shallow + low-flow
water (slow side channels, dam-stilled shallows) accumulates
Maturity, from sparse aquatic mini-flourishes to a mature
mangrove canopy. Wetland is the mod's primary productive water
biome — the positive incentive for ecology-aware water management,
"create slow side channels and life will flourish there."

## Level ladder

Per-biome override in `unity-assets/Keystone/Data/Biomes/KeystoneWetlandLevels`:

| Level | Maturity range | Density | Theme |
|---|---|---|---|
| L1 | `[0.1, 100]` | `0.10` | Aquatic mini-flourishes — wetland-flavor mini-blueprints (no filter). |
| L2 | `[0.5, 1000]` | `0.10` | Cattail + Spadderdock (vanilla aquatic plantables). |
| L3 | `[2.0, 1000]` | `0.10` | Mangrove (vanilla mangrove tree). |

L1 density is `0.10` — the same baseline as Grassland L1 — because no
spatial filter narrows the eligible set; the entire Wetland-dominant
chunk is eligible.

## Recipes per level

**L1 — Class B mini-flourishes (no spatial filter).** Ten
mesh-composed blueprints in
`unity-assets/Keystone/Data/NaturalResources/KeystoneWetlandMini{1..10}`,
composed from aquatic-flavor seedling meshes. Registered as a single
Class B `BlueprintNames` array in
`KeystoneWetlandRecipes.blueprint.json`. The same ten blueprints are
referenced by River L1 under a different filter (`RiverBank`); the
content is shared, the spatial gate is biome-specific.

**L2 — Class D aquatic plantables (no filter).**

| Recipe | Class | Weight | Vanilla blueprint |
|---|---|---|---|
| Spadderdock | D | 1.0 (default) | `Spadderdock` |
| Cattail     | D | 1.0 (default) | `Cattail` |

**L3 — Class D Mangrove (no filter).**

| Recipe | Class | Weight | Vanilla blueprint |
|---|---|---|---|
| Mangrove | D | 1.0 (default) | `Mangrove` |

Single-recipe L3 bucket — every L3 activation lands a Mangrove. The
mangrove is the wetland's canopy species; once a chunk has
accumulated ~2 game-days of Maturity, mangroves start appearing
as the "mature" tier of the biome.

## Visual cadence

1. **Pre-L1** (Maturity < 0.1). Plain shallow water, no
   Keystone-spawned content.
2. **L1 fires.** Mini-flourishes appear across ~10% of the
   Wetland-dominant water tiles. Reads as a marshy, life-bearing
   side channel.
3. **L2 fires** (~0.5 game-days). Cattails and spadderdocks start
   appearing on remaining tiles. These are fully vanilla plantables
   — gatherable, with vanilla `ReproducibleSpec` driving further
   spread.
4. **L3 fires** (~2 game-days). Mangrove seedlings appear. Grow via
   vanilla `GrowableSpec` at vanilla rate.
5. **Mature wetland.** Mix of mini-flourishes (L1) where they
   weren't replaced, cattail/spadderdock thicket (L2), and emerging
   mangroves (L3). Class D's replacement rule means L3 mangroves
   *can* succeed L1 mini-flourishes on the same tile.

## Depth × flow gating

A chunk reads as Wetland only when its water is **shallow + low-flow**:

- **Shallow:** depth `≤ 1`. Hard threshold -- water deeper than 1
  reads as Lake instead.
- **Low-flow:** flow magnitude below `HighFlowThreshold` (currently
  `0.10`). Higher flow flows into River instead.

The depth factor is soft on purpose: a chunk with mixed-depth water
gets a partial Wetland and partial Lake Suitability, both rising in
parallel rather than a hard switch at the threshold.

## Monoculture interaction

Wetland's positive target does not carry a `(1 - Monoculture)`
multiplier — the player doesn't typically plant managed monocultures
on water. Per-tile mark check in `ChunkRulesApplier.ProcessUnit`
still keeps handler spawns off any planter-marked tile.

## Open questions

1. **L2 species balance.** Cattail and spadderdock fire at equal
   weight today. If one reads better than the other in mass we may
   want to weight them.
2. **L3 mangrove pacing.** Single-Mangrove L3 with density `0.10`
   produces a relatively sparse canopy over time. Worth eyeballing
   in-game once a wetland chunk has run for ~5 game-days to see if
   the canopy fills in faster, slower, or right.
3. **Cesspool retirement.** An earlier design split contaminated
   shallow water into its own biome (`Cesspool`). The current model
   collapses all contaminated water into `Badwater` regardless of
   depth/flow — the contamination palette dominates anyway. If we
   want a visual distinction between stagnant and flowing badwater
   later, it can come back as a sub-biome or a recipe-level filter
   without revisiting the biome enum.
