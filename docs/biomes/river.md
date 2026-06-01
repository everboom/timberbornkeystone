# River — development pipeline

**Status:** L1 shipped. River is a decorative-only water biome (the main
channel current is destructive to aquatic flora) so the level ladder is
shallow on purpose.

The level-by-level cadence for the River biome: a River-dominant chunk
is high-flow water of any depth. There is no productive content on the
river itself; what L1 ships is **bank decoration** — saturated-soil
tiles at the river wall pick up wetland-flavor mini-flourishes via the
`RiverBank` spatial filter, dressing the river's margins without
trying to colonise the current.

## Level ladder

Per-biome override in `unity-assets/Keystone/Data/Biomes/KeystoneRiverLevels`:

| Level | Maturity range | Density | Theme |
|---|---|---|---|
| L1 | `[0.1, 100]` | `0.25` | Wetland-flavor mini-flourishes along the river bank (saturated-soil tiles against the river wall). |

L1's density is `0.25` rather than `0.10` because the `RiverBank`
filter narrows the eligible set sharply — only step-up wall tiles
qualify, so a higher per-eligible-tile density produces the desired
visual density without blanketing the biome.

## Recipes per level

**L1 — Class B mini-flourishes with `RiverBank` filter.** Reuses the
ten wetland mini-blueprints
(`KeystoneWetlandMini{1..10}`); content is identical to Wetland L1,
only the eligibility predicate differs. Registered as a single
Class B `BlueprintNames` array in
`KeystoneRiverRecipes.blueprint.json` with `Filter: "RiverBank"`.

## Spatial filter (`RiverBank`)

`Filter: "RiverBank"` routes through `RecipeFilterRegistry` to
`Keystone.Core.Spatial.RiverBankRecipeFilter`. The predicate is

```
CliffProximity.IsBelowNeighbor(surface) && !CliffProximity.IsAboveNeighbor(surface)
```

— the tile sits at the bottom of a step-up (a Manhattan-neighbour
column has natural terrain reaching into `surface.Z`) and is not
itself a cliff top (no Manhattan-neighbour has empty space at
`surface.Z - 1`). The combination admits the wall under a river bank
and **rejects waterfall edges**, where the player has dropped water
over a cliff: those tiles are both above the basin below and below
the bank wall above, which isn't the visual the bank-decoration
wants.

The filter is per-voxel rather than per-column because Timberborn
columns can host overhangs and floating geometry; surface-Z
comparison would mis-report those cases. See
`src/Keystone.Core/Spatial/README.md` for the underlying
`CliffProximity` helper.

## Visual cadence

1. **Pre-L1** (Maturity < 0.1). The river runs but the banks are
   plain.
2. **L1 fires.** Mini-flourishes (cattail / sunflower-as-marsh-flower
   / cassava-as-reed mixes) appear on `RiverBank`-eligible saturated-
   soil tiles along the river wall. Density `0.25` of eligible tiles.
   Waterfall edges stay clean — the filter skips them so the player
   gets a crisp visual contrast between "managed river crossing the
   bank" and "wild bank".

## Monoculture interaction

Same as the other natural water-edge biomes: monoculture on the
banks suppresses the Riparian target via the
`(1 - Monoculture)` multiplier, but River itself isn't monoculture-
aware (the `RiverBank` filter applies to River-dominant chunks,
which exist only at high-flow water — not at planted bank tiles).
Player-drawn marks still gate per-tile spawning via
`ChunkRulesApplier.ProcessUnit`.

## Open questions

1. **Same content as Wetland.** L1 reuses the wetland mini-blueprints
   verbatim. The visual identity of a river bank vs. a slow wetland
   margin reads the same today; if we want them to feel distinct, a
   river-specific blueprint set (e.g. driftwood, smoother stones,
   wind-flattened reeds) would do it.
2. **L2 candidates.** Decorative-only is the design; if a deeper L2
   makes sense it would be more bank decoration rather than channel
   content. Saving for a later content pass.
3. **`HighFlowThreshold` value.** The flow magnitude that splits
   Wetland from River sits at `0.10` in `ChunkBiomeAdapter`. Tunable
   from in-game observation -- raise if too many slow side-channels
   classify as River, lower if too many fast channels classify as
   Wetland.
