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
  for trees/bushes). Holds the set of enabled species, an `AllowGaps` flag
  (the "leave some tiles empty" outcome), and `Choose(float pickHash)`, which
  picks one outcome per tile at equal weight. `null` means "leave the tile
  empty" (gap, or nothing enabled). Reuses `Keystone.Core.Biomes.WeightedPick`
  for the draw so boundary/rounding behavior matches the rest of Core.

## Design notes

- **Equal weights, deliberately.** The brush is a pure manual mixer; it is
  *not* biome-aware. Every enabled species (and the gap, when allowed) draws
  with the same weight. If a biome-weighted variant is ever wanted, it slots
  in behind the same `Choose` seam without reshaping callers.
- **No statics, injected draw.** The pick value is supplied by the caller, so
  the policy is unit-tested with explicit hashes (see
  `tests/Keystone.Core.Tests/Planting/`). Selection happens on a player click,
  never inside a simulation tick.

## Where the rest lives

- Mod-side tool + panel + menu injection: `src/Keystone.Mod/Planting/`.
- The feature originated from a study of the Forest Tool mod
  (`Cordial.ForestTool`); see `docs/private/foresttool.md` for the reference
  decompile and the API surface it mapped.
