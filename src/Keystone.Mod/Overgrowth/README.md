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
| `OvergrowthTestTool` | Dev tool: click cycles barren → overgrown → **reseeded**. The reseed click bypasses the natural gates so the delete + spawn + wood-drop is reachable on demand (Grassland L4 species, placeholder composition). |

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
  - **Maturity points** (`+1`/day alive, `−2`/day drying, floored, persisted)
    + tile-panel readout.
  - **Terminal death** (`Kill()` → persisted dead state; controller pins
    `#Dead`, maturity stops) + **decay cleanup** (`KeystoneOvergrowthDecayTicker`,
    ~10%/day, `Clear()`s dead overgrowth → tree barren, can re-overgrow).
  - **Death triggers:** (1) per-tick **badwater self-kill** — same predicate
    + threshold as Class B (`FlourishVisuals.ShouldDieFromBadwater`); (2)
    **Dry-biome attrition** kills it via the `"Overgrowth"` token in an
    attrition rule's `Classes` (Dry L1 uses `["B","C","Overgrowth"]`), on
    the same cadence it kills irrigated flourishes.
  - **Reseed** (C3): a new `Reseed` target on the overgrowth recipe family.
    When an **overgrown, alive-overgrowth, matured** (`Maturity ≥
    MaturityThreshold`) tree on a **dead host** is hit on a recovering-biome
    level, `OvergrowthReseeder` removes the dead tree, plants a weighted
    Class D seedling from the recipe's `SourceLevel` table, carries the
    overgrowth onto it, and drops the felled wood onto the new tree's own
    `GoodStack`. A *killed* overgrowth (recovery stalled) decays to barren
    instead. Grassland content authored (`Reseed` level + entry — **needs a
    Unity Mod Builder rebuild** to reach the game; the dev tool exercises
    the mechanism without it).
- **Next:**
  - Forest content (mirror Grassland's overgrow + reseed levels/entries),
    the trunk ivy, a dedicated overgrowth composition (replace the
    `KeystoneGrasslandMini1` placeholder), and in-game tuning of the
    maturity bands / densities / `MaturityThreshold`.
- **Decisions settled** (see #33): **persist** the state — terminal dead
  state needs it; verified save-portable (removing Keystone strips the
  orphan data, trees load intact).

## Performance rule

Activation must be driven by the low-frequency chunk pass, **never** a
per-tree per-frame tick. The cost to watch is the number of overlays
rendered simultaneously, not the bookkeeping.
