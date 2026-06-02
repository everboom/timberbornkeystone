# Keystone.Mod.Planting

Player-facing **mixed-planting brush** — drag-select an area and each tile is
queued for planting with a random pick from a configurable set of species.
Keystone's own take on the third-party "Forest Tool" mod
(`Cordial.ForestTool`, see `docs/private/foresttool.md`), extended to crops and
built on the clean vanilla planting services instead of Forest Tool's static
global state.

Unlike the dev tools in `Keystone.Mod.Toolbar` / the various `*PlacementTool`s,
this is **not** dev-gated — it's intended for end users, and it only writes
planting *marks* (beavers fulfil them through the normal pipeline); nothing is
force-spawned.

## Key types

- **`KeystonePlantingToolBase`** — the shared tool: `ITool` driving a
  `SelectionToolProcessor` (drag rectangle), per-tile `Choose` → `CanPlant` →
  `PlantingService.SetPlantingCoordinates`/`UnsetPlantingCoordinates`. Owns a
  Core `PlantingPalette` and a `KeystonePlantingPanel`. Subclasses supply the
  species category and loc keys.
- **`KeystoneCropPlantingTool`** — crops (`CropSpec`), injected into the vanilla
  `"Fields"` menu. **Live.**
- **`KeystoneForestPlantingTool`** — trees + bushes (`TreeComponentSpec ||
  BushSpec`), targets the vanilla `"Forestry"` menu. **Held back** — built and
  DI-bound, but its button is not registered (see below).
- **`KeystonePlantingPanel`** — the options window: per-species on/off toggles +
  "All" + "Allow gaps". Styled like `Visualization/BiomeOverlayLegend`
  (`square-large--brown` nine-slice, right edge, shown while the tool is active).
- **`KeystonePlantingMenuInitializer`** — `IPostLoadableSingleton` that appends
  the tool buttons into the existing vanilla group buttons (the Forest Tool
  approach: find `ToolGroupButton` by id, `ToolButtonFactory.Create`, `AddTool`).

## Why the trees/bushes variant is held back

The crops brush is net-new; the trees/bushes brush directly overlaps Forest
Tool. We're reimplementing rather than depending (Forest Tool's author is
inactive, and we want crops + adjustments he can't make), but out of respect
for upstream the overlapping variant stays dark until we've talked to him. It's
fully built so lighting it up is a one-line change:
`KeystonePlantingMenuInitializer.EnableForestVariant = true`.

## Design notes

- **Selection policy lives in Core** (`Keystone.Core.Planting.PlantingPalette`),
  unit-tested. This Mod layer is only Timberborn plumbing.
- **Not biome-aware** — a pure manual mixer, by decision. Equal weights.
- **Placeholder icon.** Both buttons currently share the dev
  `"KeystoneFlourishPlacement"` sprite; per-tool icons need the Unity asset
  pipeline.
- **`ToolGroupButton` field access.** The menu initializer reads
  `ToolButtonService._toolGroupButtons` and `ToolGroupButton._toolGroup.Id` —
  the same (public, underscore-named) fields Forest Tool uses. If a future game
  version hides them, switch to the `ToolButtonService.ToolButtons` +
  `GetToolGroupButton` public path.
