# Keystone.Mod.Visualization

Mod-side visual feedback hooks. Smoke-test surfaces today; full
ecology-driven feedback (spawn handlers, particle drivers) lives
in dedicated subsystems (`Recipes/`, `Decoration/`).

## Pieces

| Type | Role |
|---|---|
| `PlateauHighlighter` | Per-frame visualizer that highlights the `Region` containing the cursor surface plus the single ecology-field chunk the cursor sits in. Uses Timberborn's `AreaHighlightingService` (per-frame commit pattern: `UnhighlightAll` -> `DrawTile` per surface -> `Highlight`). Region surfaces draw cyan; the cursor's chunk overrides red so the chunk lattice is visible. Region lookup is O(1) via the precomputed `RegionService.Containing` map. Despite the name, this is no longer plateau-finder driven. |
| `KeystonePanelStyle` | Shared visual treatment for Keystone's standalone right-edge panels (the biome-overlay legend and the mixed-planting options panel) so they read as one family: the dark-green `square-large--green` nine-slice frame (`FrameBgClass`) and the game's own header label style (`AddHeader` → TimberUi `AddLabelHeader` / the `text--header` USS class). |
