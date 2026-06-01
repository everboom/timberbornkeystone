# Audit: `Keystone.Core` testability — unit vs integration

**Date:** 2026-05-21
**Scope:** `src/Keystone.Core/` and `tests/Keystone.Core.Tests/` (686 tests).
**Method:** Walked all 105 Core source files and all 54 test files. Cross-referenced Core types against test-file presence; spot-read the largest test files plus the largest behavior-bearing untested types.

**Two questions answered:**
1. Core code that currently requires an integration test to exercise meaningfully but doesn't have one.
2. Code currently tested only through integration scaffolding where a unit test would give equivalent or better coverage at lower cost.

---

## Question 1: Untested integration-shaped code

### Tier 1 — Clear gaps

#### 1.1 `RegionService.ComputeCanonicalIdMap` / `ComputeRepresentativeSurfaces` tested only inside `PersistenceIntegrationTests`

- **Where:** `PersistenceIntegrationTests.cs:240, 333, 372` exercise these `RegionService` methods only as part of snapshot encode/decode round-trips.
- **Problem:** The assertions are on pure `RegionService` properties — `{0->1, 1->0}` after `ProcessChanges` + a Z-step is a canonical-id contract — but the test class is titled "PersistenceIntegrationTests" and wires up a snapshot codec, encode/decode, and chunk-value round-trips that have nothing to do with what's being verified.
- **Strategy:** Move `ComputeCanonicalIdMap_AfterProcessChanges_RemapsToIndexOutput` and `ComputeRepresentativeSurfaces_PicksMinSortedMemberPerRegion` into `tests/Keystone.Core.Tests/Regions/RegionServiceDeterminismTests.cs`. Drop the snapshot/codec scaffolding; assert on `regions.ComputeCanonicalIdMap()` / `regions.ComputeRepresentativeSurfaces()` directly. The existing terrain setup (`FakeTerrain` + `Setup` helper) is already in scope.
- **Effort: S.**

#### 1.2 `RegionService` lifecycle events (`RegionSplit`, `RegionMerged`, `RegionRemoved`) tested only transitively

- **Where:** `PersistenceIntegrationTests.cs:502, 543`.
- **Problem:** The only callsites subscribing to these events in tests are wiring them as `chunkStore.Inherit(parent, orphan)` to verify chunk-score propagation. **No test asserts "when `ProcessChanges` produces a split, `RegionSplit` fires once with `(parent, orphan)`" as a contract.** That fact is smuggled through whether the chunk-store inheritance happens to land.
- **Strategy:** Add `tests/Keystone.Core.Tests/Regions/RegionServiceEventTests.cs` with subscriber probes that record `(parent, orphan)` tuples and assert event firing order and arguments for representative split/merge/remove sequences. Reuse `FakeTerrain` + `Setup`.
- **Effort: S.**

#### 1.3 `TerrainSurveyor.ResurveyColumn` has no direct test

- **Where:** `src/Keystone.Core/Survey/TerrainSurveyor.cs`. Diff-emission contract exercised only transitively through `RegionServiceIncrementalTests`.
- **Problem:** If the surveyor's diff ever drifts, the regions tests fail with confusing symptoms (a region-split test fails because the surveyor mis-classified a diff) and **no test localises the bug to the surveyor**.
- **Strategy:** Add `tests/Keystone.Core.Tests/Survey/TerrainSurveyorResurveyTests.cs` driving `ResurveyColumn` directly with `FakeTerrain` mutations and asserting the `ColumnDiff` contents. Five-to-eight focused tests cover the matrix (appear, disappear, cave flip, settled flip, blocked flip, no-op resurvey).
- **Effort: S.**

### Tier 2 — Worth fixing, harder

#### 2.1 `BiomeMaturityUpdater.Tick`'s drought-floor scaling path is coupled to `ChunkBiomeSampler`

- **Where:** `src/Keystone.Core/Biomes/BiomeMaturityUpdater.cs:185-188`.
- **Problem:** The path depends on (a) Dry being the dominant biome (decided inside `ChunkBiomeSampler.DominantAtChunk` from the store), (b) Dry's own Maturity having been written previously, and (c) `MaturityParameters.DroughtFloor` returning the right per-biome floor. Today the test surface (`BiomeMaturityUpdaterTests.cs`) gets there by populating `ChunkValueStore` with the right Suitability and Maturity for Dry and the test biome — which works, but couples the test to `ChunkBiomeSampler`'s argmax. If the sampler ever changes tiebreak semantics, these tests fail in ways that look like maturity-updater bugs.
- **Strategy:** Introduce a one-method `IDominantBiomeQuery` port ("what's the dominant biome at this chunk?") defaulting to a `ChunkBiomeSampler`-backed implementation. Tests pass a stub returning the dominant biome they want. Existing tests stay; new isolated ones get clearer failure modes.
- **Effort: M.** Adds a port + one binding line + an adapter; tests get a small fake.

### Tier 3 — Judgment calls

#### 3.1 `BiomeMaturityUpdater`'s scar-gate pre-pass and dominant-biome lookup are inlined inside `Tick`

- **Where:** `BiomeMaturityUpdater.cs:105-115`.
- **Problem:** Both are private; they can be tested only through `Tick`'s observed Maturity output, which means a scar-gate regression masked by another change in the same tick (accrue branch coincidentally ends at the same value) wouldn't surface. Not currently broken; flagging as a candidate for the same `IDominantBiomeQuery` seam as 2.1.
- **Strategy:** Extract `ScarGateClosed(store, region, cx, cy)` and `ComputeDroughtDepth(store, region, cx, cy)` as `internal static` helpers; give them their own focused tests. Bundle with 2.1 if that lands.
- **Effort:** S (after 2.1), L stand-alone.

---

## Question 2: Integration tests that could be unit tests

### Tier 1 — Clear wins

#### 2.Q.1 RegionService methods inside `PersistenceIntegrationTests`

Same as finding 1.1. Each test in question has 20-30 lines of terrain construction + `Index` + `ResurveyColumn` + `ProcessChanges` scaffolding to verify a pure `RegionService` property. The setup is reasonable for the *role* (exercising RegionService) but the *file* implies it's a persistence-snapshot test. Moving them to a Region-focused file makes the surface smaller and the failure messages more directed.

- **Effort: S.**

### Tier 2 — Worth fixing, harder

#### 2.Q.2 `ChunkClusterIndexTests` cluster-aggregate tests (lines 1098-1294)

- **Where:** ~6 tests using `FakeFieldQuery` + `RegionEcologyField.MarkValidWithSampleCount` per chunk + per-chunk Suitability/Maturity in the `ChunkValueStore` (4-line-per-chunk Arrange × N chunks).
- **Problem:** The actual assertion is on aggregate score values (`RawScore`, `Score`, capacity asymptote). The cluster-build pipeline is integration-shaped by necessity (it consumes chunk biome state + field validity), but several of these tests ultimately check things that live in pure helpers: the `SaturatedScore` hyperbolic, the per-bucket weight ladder, the `RawScore = Σ weight × tileCount` formula. Those formulas would test more cheaply against extracted helpers.
- **Strategy:** Expose the raw-score-per-chunk weight ladder as a public/internal static helper (e.g. `WeightForMaturity(float maturity)`). Several aggregate-shaped tests collapse to one-line formula assertions; the whole-pipeline tests stay but shrink to the smaller set that genuinely needs union-find scaffolding.
- **Effort: M.**

### Tier 3 — Judgment calls

#### 2.Q.3 `PersistenceIntegrationTests.Save_Load_RoundTripsRegionClockStamps` (lines 38-82)

- **Problem:** Uses a 5-tile terrain, two `ResurveyAndIndex` passes, encode, fresh world, decode, restore — ~40 lines of setup — to verify three fields (`CreatedAt`, `WeatherAtCreation`, `TotalDaysAtCreation`) survive a save/load cycle. The "do these fields round-trip" property is testable on `SnapshotCodec` alone (already covered in `SnapshotCodecTests.cs`). The extra value is the rebind through `RestoreCreatedAt` after a fresh `Index`, which is real, but the test could be split: one tiny codec test, one tiny `RestoreCreatedAt` test (does it overwrite the live region's stamps when ids match? warn when they don't?). The combined test makes failures muddier than necessary.
- **Strategy:** Add a focused `RestoreCreatedAt_LiveIdsMatch_OverwritesStamps` test against `RegionService` directly with a hand-built `IReadOnlyDictionary<RegionId, RegionPersistedRecord>`. Keep the end-to-end integration test as the boundary contract; the unit test catches regressions earlier.
- **Effort: S.**

---

## Summary

**Tier-1 total: 4 findings, all S, ~3-4 hours of work.**

The Core test surface is otherwise in good shape — most heavy-setup tests genuinely need their scaffolding (region split/merge, cluster rebuild, persistence round-trip). The biggest single recurring smell is **`PersistenceIntegrationTests.cs` housing assertions on non-persistence Core seams** (RegionService canonical ids, RegionService events, RestoreCreatedAt); pulling those into per-subsystem files would tighten failure messages and give those Core methods first-class test homes without losing the end-to-end coverage.

The most structural item is **2.1 (introducing an `IDominantBiomeQuery` seam so `BiomeMaturityUpdater` can be unit-tested without going through `ChunkBiomeSampler`'s argmax).** That's the only finding involving a code change in Core proper rather than test reshuffling.
