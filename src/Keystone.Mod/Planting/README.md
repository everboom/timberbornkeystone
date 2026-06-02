# Keystone.Mod.Planting

Player-facing **mixed-planting brush** — drag-select an area and each tile is
queued for planting with a random pick from a configurable set of species.
Keystone's own take on the third-party "Forest Tool" mod
(`Cordial.ForestTool`, see `docs/private/foresttool.md`), extended to crops and
built on the clean vanilla planting services instead of Forest Tool's static
global state.

It only writes planting *marks* (beavers fulfil them through the normal
pipeline); nothing is force-spawned. **Currently dev-mode only** — gated behind
`KeystoneDevMode` like the `Keystone.Mod.Toolbar` dev tools — while the design
is still in flux (issue #30) and the trees/bushes overlap with the upstream
Forest Tool mod is unresolved.

## Key types

- **`KeystonePlantingToolBase`** — the shared tool: `ITool` driving a
  `SelectionToolProcessor` (drag rectangle), per-tile `Choose` → `CanPlant` →
  `PlantingService.SetPlantingCoordinates`/`UnsetPlantingCoordinates`. Owns a
  Core `PlantingPalette` and a `KeystonePlantingPanel`. Subclasses supply the
  species category and loc keys.
- **`KeystoneCropPlantingTool`** — crops (`CropSpec`), injected into the vanilla
  `"Fields"` menu.
- **`KeystoneForestPlantingTool`** — trees + bushes (`TreeComponentSpec ||
  BushSpec`), injected into the vanilla `"Forestry"` menu.
- **`KeystonePlantingPanel`** — the options window: per-species on/off toggles +
  "All" + "Allow gaps". Styled like `Visualization/BiomeOverlayLegend`
  (`square-large--brown` nine-slice, right edge, shown while the tool is active).
- **`KeystonePlantingMenuInitializer`** — `IPostLoadableSingleton` that appends
  the tool buttons into the existing vanilla group buttons (the Forest Tool
  approach: find `ToolGroupButton` by id, `ToolButtonFactory.Create`, `AddTool`).

## Dev-mode only, for now

Both brushes are gated behind `KeystoneDevMode` in
`KeystonePlantingMenuInitializer.PostLoad` — they appear only when dev mode is
enabled, never in a clean release build. Two reasons: the design is still in
flux (issue #30 — e.g. count-tiered "smart planting" modes), and the
trees/bushes brush directly overlaps the upstream Forest Tool mod, which we
don't want in players' hands until that's squared with its author. When these
go player-facing, drop the dev-mode gate in `PostLoad` and decide per-tool
exposure there (crops are net-new; the trees/bushes overlap is the sensitive
one).

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
