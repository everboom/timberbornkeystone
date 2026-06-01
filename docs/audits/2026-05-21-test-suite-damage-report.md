# Audit: Test-suite damage report and coverage assessment

**Date:** 2026-05-21
**Scope:** `tests/Keystone.Core.Tests/` — 537 [TestMethod] declarations across 35 files.
**Motivation:** CLAUDE.md "Tests pin design — don't rewrite them on failure" rule, with a worry that tests have been weakened to make failures go away. **Damage report sought.**
**Method:** Read-only audit; no code changes. Git history sweep for test-weakening patterns; sampled ~35 test methods for assertion strength; coverage-shape walk against `src/Keystone.Core/` folders; regression-catch verdict against known past bugs.

---

## Headline

**The suite is healthier than the CLAUDE.md cautionary memory implies.**

- 537 test methods, 35 files, 11 subfolders mirroring `src/Keystone.Core/`.
- No tautological asserts beyond one intentional sentinel (`PlaceholderTests.TestProjectIsWired`).
- No silent `catch {}`.
- No act-without-assert tests.
- Naming convention is *exemplary* — design-pinning specifics encoded everywhere.

---

## Damage report

### The named CLAUDE.md incident doesn't match shipped code

CLAUDE.md cites: `DecayClearTime_DryDecaying_BadwaterDominant_ColumnDefault0p5` and `_HealthyDominant_LowCeilingFastClear1d` "were merged into a single `_AnyDominant_DerivedFromBaselineRate` test."

**Reality:** Both tests still exist in `tests/Keystone.Core.Tests/Biomes/BiomeMaturityUpdaterTests.cs` at lines ~353/411. No `_AnyDominant_DerivedFromBaselineRate` anywhere in the repo. Either caught and reverted pre-merge, or the prose described an earlier draft of the change.

The commit closest to that description (`2d31052`, "Toxic scar baseline decay 1/day") *did* rename:
- `_ScarFade14dLinear` → `_ScarFadeAt1PerDay`
- `_ClearsScarGateInFourteenDays` → `_ClearsScarGateInSevenDays`

Those are **honest renames** — the design constant genuinely changed from 14d to 7d.

### One real flattening found

**Commit `c700c16`** ("Linear Maturity decay + scar gate, Stage E") in `MaturityParametersTests.cs` merged two design-pinning tests into one:

- `DecayHalfLife_BadwaterDecaying_ContaminatedDominant_Toxic_v_Toxic_14d` (pinned: toxic-vs-toxic = 14d)
- `DecayHalfLife_BadwaterDecaying_NonToxicDominant_ScarFade3p5d` (pinned: under Forest/Dry/Cave = 3.5d, three distinct asserts)

→ collapsed to `DecayClearTime_BadwaterDecaying_AnyDominant_14d_ScarFade` (single uniform row).

The implementation genuinely did unify the row, so this is *defensible*, but the "toxic-vs-toxic was conceptually distinct" design memory is now lost. The commit message doesn't surface this as a deliberate flattening. **Worth deciding whether to re-split.**

Also in `c700c16`: `DecayHalfLife_ContaminatedDecaying_NonToxicDominant_ScarFade1d` → `_3p5d` — value really changed (1d→3.5d), honest rename.

### Suspect commit cleared on inspection

**`edd0d74`** ("Stateless Suitability") — largest test-deletion commit (474 deletions in `BiomeSuitabilityUpdaterTests.cs`). On inspection: deleted tests of the *drift-toward-target* subsystem that was itself being removed. The semantic behaviours those tests cared about (Grassland yielding to Riparian on saturated soil, contamination cancelling healthy biome targets, drought degrading Grassland) were **re-pinned in `BiomeTargetsTests.cs`**. Not weakening.

### Other large-delete commits cleared

- `3250c17` — removed `ContaminatedRiparian_RequiresBadwaterAdjacency`; the biome itself was deleted by the same commit. Honest.
- `4664ea3` — pure Score→Suitability / Investment→Maturity rename. 338 lines changed both directions.
- `5b334be` — removed `Survey_PerVoxelMoistureCanDifferFromColumnFloat`; the commit also stripped those volatile fields from `SurfaceSurvey`. Honest.

**Net damage report: one questionable flattening (`c700c16`, Badwater row). The named incident appears not to have shipped.**

---

## Assertion strength

Sampled ~35 tests across `Biomes/`, `Ecology/Clusters/`, `Persistence/`, `Regions/`, `Spatial/`, `Flora/`.

**Examples of good practice:**
- `BiomeTargetsTests.cs:128 Grassland_FullySaturatedSoil_TargetIsZero` — full A/A/A, single precise numeric assert with documented tolerance, docstring quotes the design rule it pins.
- `ChunkClusterIndexTests.cs:27 Rebuild_SingleQualifyingChunk_NotEmitted` — docstring distinguishes which of two filters is exercised; sibling test explicitly distinguishes itself. Exemplary.
- `BiomeMaturityUpdaterTests.cs:353 _LowCeilingFastClear1d` — derives expected value in comment, asserts to 1e-3 tolerance.

No tautological asserts (one intentional sentinel, labelled). No silent `catch {}`. No act-without-assert. Tolerances reasonable (1e-4 typical; 0.1 for time-integration where justified inline).

---

## Coverage gaps

Per-folder `src/Keystone.Core/` vs `tests/Keystone.Core.Tests/`:

| Folder | src files | test files | Status |
|---|---|---|---|
| Biomes | 19 | 10 | well-covered |
| Buildings | 5 | 2 | OK |
| Compatibility | 1 | 0 | trivial (IsExternalInit) |
| Diagnostics | 1 | 1 | OK |
| Ecology | 1 + 2 subdirs | 0 + 2 subdirs | subdirs covered; root `SurfaceSurvey` untested |
| Fauna | 1 | 1 | OK |
| Flora | 4 | 1 | thin (`FloraEntry`/`Kind`/`YieldInfo` untested directly) |
| Persistence | 9 | 4 | OK |
| **Ports** | **9** | **0** | interface-only, expected |
| Regions | 4 | 6 | well-covered |
| Spatial | 6 | 4 | `ContaminatedTileRecipeFilter` and `IRecipeFilter` untested |
| Survey | 3 | 2 | OK |
| **Tiles** | **5** | **0** | `TileCoord/SurfaceCoord/ChunkCoord/TileMap/FlowVector` untested |
| **Time** | **3** | **0** | `GameTimestamp/IClock/WeatherKind` untested |

**Tiles and Time gaps are the most concerning structurally.** `TileMap` is a substrate that everything else builds on; bugs there surface as confusing failures in dependent tests rather than localised ones.

---

## Regression-catch verdict

| Past bug | Test exists? |
|---|---|
| Water contamination must use `>= 0.05` threshold (not strict `> 0`), or pools paint as badwater | **No.** Every fake `IWaterQuery` in the suite returns `WaterContaminationAt(..) => 0f`. No test exercises the 0.05 threshold boundary. **Exactly the kind of regression CLAUDE.md memory warns about.** |
| Riparian intentionally narrow water-hugging band (don't re-widen) | **No.** No test mentions "narrow", "IrrigationDistance", or "RiparianThreshold". The width predicate lives in the Mod adapter, but the Core-side threshold constant isn't pinned. |
| Biome Maturity drift (Grassland-yields-to-Riparian agreed-on factor) | **Yes.** Three tests in `BiomeTargetsTests.cs` (Fully/Partial/StackMultiplicatively) lock the formula tight. |
| Cluster index over-running per-region (recent "skip lone-chunk regions and singletons" fix) | **Yes.** `ChunkClusterIndexTests.cs:27/49/72` pins both region-level and chunk-level filters, distinguished by docstring. |
| Toxic scar baseline decay 1/day | **Yes.** `BiomeMaturityUpdaterTests.cs:287/303/321/336` all pin the new rate. |

**Two of five — water-contamination threshold and Riparian band width — have no Core-side regression test.** Both happen to be Mod-side adapter values rather than Core constants, which is consistent with the architecture, but the safety net relies on the live game catching it.

---

## Naming hygiene

Strong adherence to "design-pinning" convention. Sample:

- `Tick_BadwaterDecayingUnderForestDominant_ScarFadeAt1PerDay`
- `Tick_DryDecayingUnderForestDominant_LowCeilingFastClear1d`
- `DecayClearTime_ForestDecaying_GrasslandDominant_LandFamily7d`
- `Grassland_SaturatedSoilAndTrees_StackMultiplicatively`
- `Rebuild_LoneQualifyingChunkAmidDifferentBiomes_NotEmitted`
- `Monoculture_BelowMinCount_TargetIsZero`

Numeric thresholds (`7d`, `0p5`, `1PerDay`, `3p5d`) and design conditions (`UnderForestDominant`, `_LandFamily`, `_LowCeiling`) consistently encoded. Generic names (`Tick_Works`, `_DoesNotThrow`) essentially absent. Docstrings frequently quote the design rule (e.g. `BiomeTargetsTests.cs:122-126` quotes "Guards the agreed-on but historically dropped `(1 - SaturatedSoilFraction)` multiplier"). **Exemplary on this axis.**

---

## Recommendations (ranked)

1. **Add a Core-side test for the water-contamination 0.05 threshold.** The memory note (`project_water_contamination_threshold.md`) and the listed past-bugs both call this out, and there's no test for it. A `WaterEdgeRecipeFilter` or `WaterProximity` test that varies `WaterContaminationAt` across {0, 0.04, 0.05, 0.1} and asserts boundary handling would fit existing patterns.
2. **Audit `MaturityParametersTests.DecayClearTime_BadwaterDecaying_AnyDominant_14d_ScarFade` for the lost 3.5d-vs-14d design memory.** If the row really is uniform 14d by design, leave it; if "toxic-vs-toxic 14d" and "non-toxic 3.5d" are still conceptually distinct, split the test back into two with separate docstrings.
3. **Add minimal coverage to `Tiles/` and `Time/`.** `TileMap` especially is a substrate primitive; one round-trip + bounds-check test per type is cheap insurance.
4. **Pin the Riparian narrow-band threshold somewhere testable** (likely a Core constant referenced by the Mod adapter) so the "don't re-widen" memory note has a test enforcing it.
5. **Update the CLAUDE.md cautionary example.** The `_DerivedFromBaselineRate` flattening doesn't appear in shipped code. Either cite the real `c700c16` Badwater-row flattening, or note that the danger was caught pre-merge.
