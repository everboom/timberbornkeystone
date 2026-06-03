# Keystone.Core.Planting

Pure simulation-side policy for Keystone's **mixed-planting brush** — the
player-facing tool that drag-selects an area and queues a *random mix* of
plantable species (the way the third-party "Forest Tool" mod does, but
extended to crops and built without Forest Tool's static-global / baked-in-RNG
smells). The Timberborn-facing plumbing (tool, drag selection, planting-mark
writes, options panel, menu-button injection) lives in
`Keystone.Mod/Planting/`; this folder holds only the testable selection
policy.

## Key types

- **`PlantingPalette`** — one instance per planting tool (one for crops, one
  for trees/bushes). Holds a per-species integer **weight** (`0` = excluded,
  default `1`, capped at `9`), a `GapWeight` for the "clearings" (leave-empty)
  outcome that competes in the draw like a species, and `Choose(float pickHash)`,
  which picks one outcome per tile *proportional to weight*. `null` means "leave
  the tile empty" (a clearings result, or nothing with a positive weight).
  Reuses `Keystone.Core.Biomes.WeightedPick` for the draw so boundary/rounding
  behavior matches the rest of Core.

## Design notes

- **Manual weighted mixer, not biome-aware.** The brush is a pure manual
  mixer: the player sets each species' weight directly (panel `−`/`+`
  steppers, plus Select all / Clear all). A species at weight 3 is drawn three
  times as often as one at weight 1; weight 0 drops it. "Clearings" (the
  leave-empty outcome) is `GapWeight`, a peer in the same draw the player dials
  independently — Select all leaves it as-is, Clear all zeroes it (the panel
  wires this asymmetry; `SetAllWeights` itself never touches `GapWeight`). If a biome-weighted variant is
  ever wanted, it slots in behind the same `Choose` seam without reshaping
  callers.
- **No statics, injected draw.** The pick value is supplied by the caller, so
  the policy is unit-tested with explicit hashes (see
  `tests/Keystone.Core.Tests/Planting/`). Selection happens on a player click,
  never inside a simulation tick.

## Where the rest lives

- Mod-side tool + panel + menu injection: `src/Keystone.Mod/Planting/`.
- The feature originated from a study of the Forest Tool mod
  (`Cordial.ForestTool`); see `docs/private/foresttool.md` for the reference
  decompile and the API surface it mapped.
