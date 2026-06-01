# Postmortem: per-tile biome dominance regressed to per-chunk under a perf rationale

**Date written:** 2026-05-13
**Symptom surfaced by:** user, during the Score-vs-Investment dominance design discussion (this same session)
**Code touched in the fix:** `ChunkRulesApplier.cs`, `IRuleHandler.cs`, `ChunkBiomeSampler.cs` xmldoc, `Biomes/README.md`, `Recipes/README.md`, plus the `ClassARecipe.cs` activation-rule comment.

## What went wrong

The `ChunkRulesApplier` refactor (which replaced the four prior per-class
`RollingSweepTicker`-based reconcilers with a single shared scheduler +
`IRuleHandler` plug-ins) sampled the chunk's dominant biome **once at the
chunk centre tile** and reused that result for every surface in the
chunk. The prior reconcilers each re-derived dominance per surface.

The refactor's xmldoc billed the per-chunk collapse as *the* motivation
for the refactor ("collapsing to a single per-chunk read is the main
motivation"), and the `IRuleHandler` contract was rewritten to claim
"Per-tile gating already done upstream … chunk's dominant biome." Both
were drift: the *shared sweep* across handlers was the actual win; the
per-chunk dominance read was a separate semantics change smuggled in
under the same banner.

The semantics change broke a load-bearing property: the bilinear
smoothing in `ChunkBiomeSampler` exists precisely so that per-tile reads
near a chunk boundary blend across the four neighbouring chunk values.
Sampling once at a chunk's centre discards that blend for the chunk's
remaining 15 surfaces, producing 4-tile hard edges along the chunk grid
visible as jagged biome boundaries. The visual artifact is exactly what
the bilinear math was added to prevent.

## How the conversation found it

While discussing a separate design change (use Score, not Investment,
for biome dominance), the assistant proposed an updated model that
sampled "at the chunk centre" — parroting the current code's framing.
The user pushed back: "we intentionally didn't do that. I'm surprised
you are suggesting that again now."

A re-read of `ChunkRulesApplier.ProcessUnit` confirmed: the current
code *was* doing per-chunk dominance, contradicting the user's stated
design intent. The assistant had read the regressed framing as
authoritative because the code, its inline comments, and `IRuleHandler`'s
xmldoc all consistently described per-chunk dominance — there was no
internal contradiction to flag the drift.

## Why this is the CLAUDE.md "smuggled redesign" rule

`CLAUDE.md` § "Don't smuggle redesigns past the user" requires that any
change to the *semantics* of an existing concept — not just a rename or
retype — surface, before the change lands:

1. What it currently means.
2. What depends on that meaning.
3. What the change preserves vs. breaks.

The refactor failed all three for the dominance-read:

1. **Current meaning** — "dominant biome at this tile, bilinearly
   smoothed across chunk boundaries" — was not quoted from any
   docstring; the change replaced it with "dominant biome of this
   chunk" and the new docstring presented that as the always-intended
   meaning.
2. **What depended on it** — visible biome boundary smoothness, the
   entire point of `ChunkBiomeSampler.Sample` being bilinear — was not
   identified. The placement tools (`FlourishPlacementTool`,
   `VanillaFloraPlacementTool`, `RockPlacementTool`) still read
   per-tile via `Sample`, but the *rule applier* — the production
   path that decides whether new flora actually spawns — silently
   moved to per-chunk.
3. **What broke** — chunk-grain dominance produces 4-tile hard edges
   visible in-game — was masked by the fact that the existing tests
   only checked the math, not the dominance grain. No test failed.

The fact that the symptom (chunk-edge artifacts) only surfaces in-game,
not in unit tests or build output, is what let the drift sit between
the refactor and this conversation.

## Why my standard heuristics did not catch it

- The change "felt natural" because the refactor's stated theme was
  *consolidation* — a single scheduler instead of four. "Sample once
  per chunk instead of once per surface" reads as obviously part of
  the same simplification, even though it's a semantically distinct
  decision riding on the same diff.
- The new xmldoc told a coherent story (`IRuleHandler`: "per-tile
  gating already done upstream … chunk's dominant biome"). When the
  docs and the code agree, the assistant accepted the framing without
  cross-checking it against the design rationale in DESIGN.md or the
  inline xmldoc on `ChunkBiomeSampler.SampleDominantBiome` itself
  (which still said "Per-tile, not per-chunk … per-chunk caching would
  produce jagged dominance boundaries").
- The contradiction *was* in the repo: `ChunkBiomeSampler`'s xmldoc
  warned against exactly this collapse, in the same module that the
  refactor edited. The lesson is to read the callee's contract, not
  just the caller's narrative, when validating that a refactor
  preserved meaning.

## What the audit should have looked like at the time of the refactor

A two-stage refactor (consolidating four reconcilers into one) warrants
an audit at each stage boundary, per the CLAUDE.md "Audit between
stages" rule. The dominance-grain question is the one a stage audit
would have surfaced: "is sampling once at the chunk centre semantically
equivalent to the prior per-surface read?" The answer is no, and the
audit would have produced a one-line note pointing at the bilinear
smoothing in `ChunkBiomeSampler.SampleDominantBiome`'s own xmldoc.

This postmortem is the after-the-fact version of that audit. The
remediation lands in the same commit that introduces the Score-pass
gate, since both touch the same per-surface call site.

## The fix

`ChunkRulesApplier.ProcessUnit` now collects surfaces first, then
walks them, calling `SampleDominantBiome` per surface (and gating on
each surface's own Investment for the winner). The xmldoc on
`ChunkRulesApplier`, `IRuleHandler`, and `ChunkBiomeSampler` were all
rewritten to anchor the per-tile resolution choice — including the
explicit "do not re-collapse this" comment in `ProcessUnit`'s body so
the next refactor doesn't repeat the move under the same perf framing.

## Generalisation

The pattern to watch for in future refactors:

- **A "consolidation" refactor that touches multiple call sites** is
  the most likely vehicle for a smuggled semantics change. The user
  approves the consolidation; the semantics change rides along.
- **Comments that begin "the main motivation for this refactor is …"
  describing a perf or simplicity win** are the smell. If the perf win
  comes from changing semantics rather than from sharing work, flag it.
- **Documentation that fully describes the new behaviour without a
  trace of the old** is the smell. A genuine simplification would
  leave the old contract intact and just remove the redundant
  implementations; a semantics change rewrites the contract.

Catch the redesign in the diff, not in the symptom report weeks later.
