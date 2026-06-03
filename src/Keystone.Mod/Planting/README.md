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
- **`KeystonePlantingPanel`** — the options window. One row per entry,
  `[name] [−] [weight] [+]  [proportion bar] [NN%]`, where the bar + percent
  show that entry's share of the total weight and rescale whenever any weight
  changes; entries at weight 0 gray out. Above the rows are "Select all" /
  "Clear all" bulk buttons; the last weight row is **Clearings** (weights how
  much bare ground the brush leaves — a peer in the blend that replaced the old
  "Allow gaps" toggle, not swept by the bulk buttons). Below a divider sit the
  tool-behavior toggles: **Overwrite existing plants** and its indented, opt-in
  **Destroy existing plants** sub-toggle (disabled + grayed while overwrite is
  off). "Destroy" not "cut" deliberately — it's the demolish path (the plant is
  removed, nothing harvested), distinct from the forester's harvest-for-logs cut.
  Toggle state lives on the tool, not the palette (it's planting *behavior*,
  not selection policy); the panel pushes changes back via `OptionToggle`
  setters. The steppers/bulk
  buttons are native `NineSliceButton`s with the game's button USS classes
  (orange "game" button + green hover, square `+`/`−` glyphs) — reachable
  because the mod compiles against the publicized `Timberborn.CoreUI` (see
  `Keystone.Mod.csproj`). Shares the dark-green nine-slice frame + title header
  with `Visualization/BiomeOverlayLegend` via `Visualization/KeystonePanelStyle`;
  right edge, shown while the tool is active.
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
- **Not biome-aware** — a pure manual mixer, by decision. The player sets
  per-species weights directly; the draw is proportional to them.
- **Overwrite is tool-side — no Harmony.** Vanilla `PlantingService` never
  rejects an occupied tile; it's the `PlantingAreaValidator.CanPlant` guard the
  tool applies that "respects existing plants." Since we own that call, the
  overwrite toggle just bypasses the guard for tiles blocked by *another plant*
  (buildings/paths stay protected — `CanPlant` is false and there's no
  `PlantableSpec`, so neither plant branch fires), and "Destroy existing" marks
  that plant's `Demolishable` in our own `ActionCallback` (the demolish path is
  pure removal — the Demolishing system has no yield/recovery, only an optional
  science reward for ruins — so it destroys rather than harvests). The mark is fulfilled when
  the tile clears (the spot re-evaluates on `OnBlockObjectUnset`). The
  `Calloatti.NaturalResourcesTweaks` mod (`dump/naturalresourcestweaks-decompile.md`)
  does the same effect with three global Harmony patches because it retrofits
  the *vanilla* tool; owning our own tool lets us skip all three. We're also
  stricter than NRT, which marks *any* `Demolishable` (it can bulldoze
  buildings) — we only ever mark plants.
- **Placeholder icon.** Both buttons currently share the dev
  `"KeystoneFlourishPlacement"` sprite; per-tool icons need the Unity asset
  pipeline.
- **`ToolGroupButton` field access.** The menu initializer reads
  `ToolButtonService._toolGroupButtons` and `ToolGroupButton._toolGroup.Id` —
  the same (public, underscore-named) fields Forest Tool uses. If a future game
  version hides them, switch to the `ToolButtonService.ToolButtons` +
  `GetToolGroupButton` public path.
