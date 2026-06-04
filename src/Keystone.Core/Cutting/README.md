# Keystone.Core.Cutting

Pure simulation-side policy for Keystone's **thinning-cut brush** — the
player-facing tool that drag-selects an area and marks a *fraction* of the
trees in it for cutting (e.g. "thin 30% of the pines here"). Conceptually the
cut-side mirror of the planting brush (`Keystone.Core.Planting`), and a leaner
take on Cordial's third-party Cutter Tool (`docs/private/cuttertool.md`): a
percentage knob instead of fixed checkered/line patterns. The Timberborn-facing
plumbing (tool, drag selection, species resolution, cutting-area writes, options
panel, menu-button injection) lives in `Keystone.Mod/`; this folder holds only
the testable selection policy.

## Key type

- **`ThinningSelector`** — a static, pure per-tile predicate. `ShouldMark(x, y,
  z, fraction, seed)` decides whether one already-eligible tile is in the marked
  ~`fraction`, deterministically from the tile's coordinate and a per-drag
  `seed`. `Sample(x, y, z, seed)` exposes the underlying uniform `[0, 1)` value
  for tests. Reuses the FNV-1a + Murmur3 hash idiom of
  `Keystone.Core.Biomes.FlourishThreshold`.

## Design notes

- **Per-tile, not per-area.** Each tile's verdict depends only on its own
  coordinate and the seed — never on the drag-rectangle size or other tiles.
  Growing the drag only adds verdicts; it never disturbs a tile already shown,
  so the preview highlight is flicker-free and preview == commit. A per-area
  "pick round(N·fraction) of the candidates" scheme was rejected because it
  reshuffles as N changes mid-drag.
- **Seed = reroll.** The seed is held by the tool and bumped after each
  completed drag. Fixed within a drag (preview/commit agree); changed between
  drags so redrawing the same area yields a different ~X% subset.
- **Expected fraction, not exact.** Independent per-tile coin flips at
  probability `fraction`; marked count ≈ `fraction·N` in expectation, converging
  with area. No "mark at least one" floor — that needs the area count this
  policy never sees, so a low fraction over a tiny patch can mark nothing. By
  design.
- **Species filtering lives in the Mod layer, not here.** Resolving a tile's
  species (the tree at it, or its planting mark as fallback) needs game state,
  so it can't be in Core. The Mod builds the candidate list and filters out
  left-alone species *before* calling `ShouldMark`; this policy only ever sees
  eligible tiles. Because the threshold is uniform per tile, the fraction
  applies evenly across whichever species are active (you thin each by ~X%,
  not X% of a pooled total).
- **Determinism.** Same inputs → same output across sessions and .NET versions
  (FNV-1a/Murmur3, never `GetHashCode`). The bit-mixing finalizer is
  load-bearing: a naive linear coordinate combo dithers into visible diagonal
  stripes rather than looking random.
- **Player action, not a tick.** Selection fires on a player drag, never inside
  a simulation tick, so the seed is not part of tick-replay determinism. The
  *result* — coordinates written to the host's cutting-area registry — is
  persisted by the host and is deterministic from there on.

## Where the rest lives

- The write seam: `Keystone.Core.Ports.ICuttingAreaWriter`
  (`MarkForCutting` / `UnmarkForCutting`), a Mod adapter over
  `Timberborn.Forestry.TreeCuttingArea`.
- Mod-side tool + panel + menu injection: `Keystone.Mod/` (pending).
- Reference for the original tool this leans on: `docs/private/cuttertool.md`.
