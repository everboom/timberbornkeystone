# Keystone.Mod.Overgrowth

Augmentation layer that drapes **existing** entities (currently vanilla
trees) in flourishes — undergrowth now, trunk ivy later — **without
modifying the host's specs or behaviour**. This is the implementation of
GitHub issue #33 ("Dead trees overgrow, then regrow").

It is *not* a new spawn class. The host tree keeps every spec it's
defined with (Growable, Cuttable, Reproducible, colliders); overgrowth
only adds a non-blocking visual layer on top. So it's framed as
"Class D + data", and the per-chunk driver (a later slice) will be a
rule that operates on existing entities — a sibling of `AttritionHandler`,
not a fifth spawn class.

## Why an overlay, not a model swap

Vanilla flora renders from a custom per-instance matrix, **not** the
Unity Transform (see `docs/timberborn-api.md` § "Visual position is
custom-rendered"). So we can't repaint or rescale the trunk at runtime,
and we don't try to. Instead we keep the tree untouched and layer
separate decoration GameObjects (which *do* honour their own transform)
around it via `KeystoneDecorationRegistry`.

## Pieces

| Type | Role |
|---|---|
| `KeystoneOvergrowth` | Per-instance component, attached to **every tree** via `AddDecorator<TreeComponentSpec, KeystoneOvergrowth>` (in `KeystoneConfigurator`) — vanilla and any faction's, no hardcoded list. `Apply()` spawns one flourish-composition decoration (with a lifecycle controller); `Clear()` removes it; cleans up on entity delete. `CanOvergrow` self-filters water-based trees (`FloodableNaturalResourceSpec.MinWaterHeight > 0`). |
| `OvergrowthReseeder` | The reseed mechanism: removes a mature overgrown **dead** tree, plants a weighted Class D seedling at the same tile (via `ClassDSpawnHandler.TrySpawnClassD`), carries the overgrowth onto it, and drops the felled tree's wood onto the **new seedling's own `GoodStack`** + registers it for lumberjack hauling. Mimics a real cut (`Cuttable.Cut`) without a free-standing log-pile entity (vanilla has none). Eligibility gates live in `OvergrowthHandler`; this is the pure mechanism. |
| `OvergrowthTestTool` | Dev tool: click cycles barren → overgrown → **reseeded**. The reseed click bypasses the natural gates so the delete + spawn + wood-drop is reachable on demand (Grassland L4 species, a `KeystoneOvergrowthMini` overlay). |

## How it attaches (no marker spec)

Attachment rides the universal vanilla `TreeComponentSpec`, the same way
`KeystoneGrowthBonus` decorates `GrowableSpec`: every tree in the game
gets a `KeystoneOvergrowth` and self-filters at runtime. This is why
there's **no** Keystone marker spec or blueprint-modifier provider — they
were an earlier hardcoded-per-tree approach, replaced because it missed
faction trees and required maintaining a list. Reintroduce a marker spec
only if per-blueprint overgrowth *config* is ever needed.

## Status / roadmap

- **Done:**
  - Overlay mechanism + persistence (is-decorated, composition, re-spawn
    on load); save-portable (removal strips orphan data, trees intact).
  - Biome-driven `OvergrowthHandler` on the chunk pass — Live recipes on
    Deterministic levels (capped), Dead on Stochastic (accumulate); all
    land trees (water trees self-filter); Grassland content authored.
  - **Reclamation maturity** — the reseed clock. A **dead** host accrues
    `Maturity` (`+1`/day moist, `−2`/day dry, floored, persisted) **whether
    or not it's overgrown**, so the graphical overgrowth layer and the
    gameplay replacement speed are decoupled. Living trees have no clock.
    Tile-panel readout. (Persistence keys `Decorated` explicitly now, so a
    dead barren tree's maturity survives reload.)
  - **Terminal death** (`Kill()` → persisted dead-*visual* state; controller
    pins `#Dead`) + **decay cleanup** (`KeystoneOvergrowthDecayTicker`,
    ~10%/day, `Clear()`s the dead overlay → tree barren, can re-overgrow;
    `Clear()` leaves maturity intact — a still-dead tree keeps reclaiming).
  - **Death triggers:** (1) per-tick **badwater self-kill** — same predicate
    + threshold as Class B (`FlourishVisuals.ShouldDieFromBadwater`); (2)
    **Dry-biome attrition** kills it via the `"Overgrowth"` token in an
    attrition rule's `Classes` (Dry L1 uses `["B","C","Overgrowth"]`), on
    the same cadence it kills irrigated flourishes.
  - **Reseed** (C3): a new `Reseed` target on the overgrowth recipe family.
    When a **matured** (`Maturity ≥ MaturityThreshold`) **dead** tree is hit
    on a recovering-biome level, `OvergrowthReseeder` removes the dead tree,
    plants a weighted Class D seedling from the recipe's `SourceLevel` table,
    carries an overgrowth overlay onto it, and drops the felled wood onto
    the new tree's own `GoodStack`. The host need **not** be overgrown — the
    reclamation clock runs on barren dead trees too; bad conditions erode
    maturity and stall reseed naturally. Grassland content authored
    (`Reseed` level + entry — **needs a Unity Mod Builder rebuild** to reach
    the game; the dev tool exercises the mechanism without it).
  - **Dedicated overgrowth compositions**: 10 `KeystoneOvergrowthMini1..10`
    (undergrowth — mature CoffeeBush/BlueberryBush ≤70% + seedling Corn/
    Sunflower + pebbles — kept off-centre via the generator's `--clear-center`
    so they ring the host trunk). Wired via the recipe family's
    `Compositions` list (each `OvergrowthEntry` expands 1:1 into recipes; the
    handler weighted-picks one per tree, so Dead/Live/Reseed all draw a
    random mini). Replaces the old `KeystoneGrasslandMini1` stand-in.
  - **Forest content**: Grassland's overgrow + reseed levels/entries mirrored
    into `KeystoneForest` (reseed `SourceLevel` = Forest's `L1` tree table).
  - **Player settings** (`KeystoneOvergrowthSettings`, "Overgrowth" panel
    section): two independent 0–200% sliders, via `GetDensityMultiplier` —
    an **overgrowth rate** (graphics: scales Dead/Live overgrow density; 0%
    = no visuals) and a **replacement rate** (gameplay: scales the Reseed
    level density; 0% = trees never replaced; 200% = double). Independent
    because the reclamation clock accrues on every dead tree regardless of
    the visual.
- **Next:**
  - The trunk **ivy**, and in-game tuning of the maturity bands / densities /
    `MaturityThreshold` (now adjustable live via the Overgrowth settings).
- **Decisions settled** (see #33): **persist** the state — terminal dead
  state needs it; verified save-portable (removing Keystone strips the
  orphan data, trees load intact).

## Performance rule

Activation must be driven by the low-frequency chunk pass, **never** a
per-tree per-frame tick. The cost to watch is the number of overlays
rendered simultaneously, not the bookkeeping.
