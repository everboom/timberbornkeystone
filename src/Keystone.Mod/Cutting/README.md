# Keystone.Mod.Cutting

Player-facing **logging brush** — drag-select an area and mark a
player-set *fraction* of the trees in it for cutting ("thin 30% of the pines
here"). The cut-side mirror of the mixed-planting brush
(`Keystone.Mod.Planting`), and a leaner take on Cordial's third-party Cutter
Tool (`docs/private/cuttertool.md`): a percentage knob instead of fixed
checkered/line patterns, built on the vanilla cutting-area services.

It only writes the host's tree-cutting **area** (beavers fell the trees through
the normal forester pipeline); nothing is force-removed. **Player-gated by its
own `KeystoneUiSettings.CuttingPlannerTool` toggle** (default on; the toggle
lets a player running the upstream Cutter Tool turn ours off — issue #30),
enforced live by `Keystone.Mod.Toolbar.KeystoneToolDisabler` the same way the
planting brushes are: the button is always wired into the vanilla TreeCutting
group and its visibility re-checks on each tool-group open, so a toggle takes
effect with no reload.

The options panel lists the **tree-type filter first** (one toggle per species,
plus Select all / Clear all), then below a divider the other options: a
percentage slider (default **100%**) with a live `X%` readout to its right, and
an "Override existing marks" toggle. Geometry, padding, and bulk-button look are
the shared `Visualization/KeystonePanelStyle` treatment used by every Keystone
right-edge tool panel.

## Key types

- **`KeystoneLoggingTool`** — the tool: `ITool` driving a
  `SelectionToolProcessor` (drag rectangle). Per tile it resolves the species
  (tree, or planting mark as fallback), drops deselected species, then asks the
  Core `Keystone.Core.Cutting.LoggingSelector` whether this tile is in the
  marked fraction under the current per-drag seed. Writes through
  `ICuttingAreaWriter`; injected into the vanilla `"TreeCutting"` menu.
- **`KeystoneLoggingPanel`** — the options window: the per-species filter
  first (one toggle per tree species + "Select all" / "Clear all"), then a
  percentage slider (`AddSliderInt` + `AddEndLabel` for the `X%` readout) and an
  "Override existing marks" toggle (default on). Built on the shared
  `Visualization/KeystonePanelStyle` frame/geometry/bulk-button helpers, so it
  matches the planting panel and biome legend. Holds no policy state — every
  control pushes straight through to the tool.
- **`KeystoneLoggingMenuInitializer`** — `IPostLoadableSingleton` that
  appends the tool button into the vanilla `"TreeCutting"` group, located via
  the base-game `TreeCuttingAreaSelectionTool` (public `ToolButtonService`
  path, iterated defensively rather than `GetToolButton<T>()` which throws when
  absent).

## Design notes

- **Selection policy lives in Core** (`Keystone.Core.Cutting.LoggingSelector`),
  unit-tested. This Mod layer is only Timberborn plumbing + species resolution.
- **Per-tile, seeded selection.** Each tile's include/exclude is a pure
  function of its coordinate and a per-drag seed, so the preview doesn't flicker
  as the rectangle is sized and preview == commit. The seed bumps after each
  commit, so re-dragging rerolls. See the Core README for the full rationale.
- **Override existing (default on) is scoped to the active species.** On commit
  the tool unmarks the eligible (active-species) tiles in the area, then marks
  the selected subset — so a drag *sets* the area to ~X% (and lowering the
  slider removes marks) rather than only ever adding. Other species' marks are
  never touched.
- **Species match is by stripped GameObject name.** A placed tree's species is
  `Name` with `(Clone)` + spaces removed, which equals the `PlantableSpec`
  template name the filter is keyed on. Same approach as Cordial's Cutter Tool;
  brittle if a faction's tree prefab name diverges from its template name, but
  it's the only handle a placed tree exposes. Planting marks give the resource
  name directly (`PlantingService.GetResourceAt`).
- **No Harmony.** Like the planting brush, the tool owns its own selection and
  writes the vanilla cutting-area registry — no patching needed.

## Where the rest lives

- Core selection policy + the write port: `src/Keystone.Core/Cutting/` and
  `Keystone.Core.Ports.ICuttingAreaWriter`.
- The write adapter: `Keystone.Mod/Adapters/CuttingAreaWriter.cs` (over
  `Timberborn.Forestry.TreeCuttingArea`).
- Reference for the original tool this leans on: `docs/private/cuttertool.md`.
