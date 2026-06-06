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
| `OvergrowthTestTool` | Dev tool: click a tree to toggle its overgrowth. Manual trigger until the biome driver exists. |

## How it attaches (no marker spec)

Attachment rides the universal vanilla `TreeComponentSpec`, the same way
`KeystoneGrowthBonus` decorates `GrowableSpec`: every tree in the game
gets a `KeystoneOvergrowth` and self-filters at runtime. This is why
there's **no** Keystone marker spec or blueprint-modifier provider — they
were an earlier hardcoded-per-tree approach, replaced because it missed
faction trees and required maintaining a list. Reintroduce a marker spec
only if per-blueprint overgrowth *config* is ever needed.

## Status / roadmap

- **Done: mechanism + persistence**, dev-triggered, undergrowth-only,
  all land trees (every `TreeComponentSpec`, water trees excluded). The
  lifecycle-managed decoration overlay attaches, persists (is-decorated +
  which composition), and re-spawns the same composition on load.
- **Next: kill + replace slice.** Terminal `#Dead` state (persisted),
  time-alive / time-dead counters, a rolling-sweep that randomly kills
  (after a min age) and, after a further min-dead age, replaces the
  overgrowth — random, never a deterministic timer. Then: biome-driven
  activation (which trees overgrow), the trunk ivy, the regrow half of #33.
- **Decisions settled** (see #33): **persist** the state (chosen over
  re-derive) — terminal/sticky dead state needs it. **⚠ Gate:** because
  this persists Keystone data onto *vanilla* trees, the
  remove-Keystone-then-load test must pass before ship (orphan-component
  data on removal — unverified).

## Performance rule

Activation must be driven by the low-frequency chunk pass, **never** a
per-tree per-frame tick. The cost to watch is the number of overlays
rendered simultaneously, not the bookkeeping.
