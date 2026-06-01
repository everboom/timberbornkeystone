# Keystone.Core test-coverage audit (2026-05-22)

## Snapshot

- 812 tests, all green (Release config, MSTest, net10.0, 632 ms).
- Cobertura overall: **95.2% lines** (10,212 / 10,725), **89.1% branches** (2,244 / 2,519).
- Coverage XML: `TestResults\eb27c563-e5ec-4fbe-a67e-9925bafe9eeb\Admin_HUGINN_2026-05-22.09_11_20.cobertura.xml`.

## 1. Coverage breadth

**Excellent (>= 95% line):** the biome pipeline (`ChunkBiomeSampler`, `ChunkBiomeAdapter`, `BiomeTargets`, `BiomeMaturityUpdater`, `BiomeSuitabilityUpdater`, `FlourishThreshold`, `MaturityParameters`, `AttritionRecipeParser`, `AttritionTargeting`, `BiomeLevelEntryValidator`, `ChunkBiomeInputs` — all 96–100%); all spatial filters at 100%; ecology fields (`WaterContamination`, `RiparianBand`, `ScalarChannelAggregator`, `NaturalResourceRouter` 100%; `RegionEcologyFieldBuilder` 94%; `ChunkClusterIndex` 98%); `PlateauFinder` (100%), `TerrainSurveyor` (97%), `RollingSweep<TUnit>` (96.5%); **`RegionService` at 97% line / 94% branch** (cyclomatic 269, exercised by ~2,900 lines of tests across 7 files); `RegionValueStore` (100%), `SnapshotCodec` (95%); fauna `FaunaPathfinder` / `InteriorOnlyTopology` / `MaturityFilterTopology` (100%), `FaunaWanderPlanner` (94.5% line / 78% branch).

**Partial (60–90%):** `RegionEcologyField` (84% line / **72% branch**, complexity 90); `FlourishVisuals` (89% / 85%); `BiomeLevelTable` (79% / 86%); `WeightedPick` (80% / 77%); `PerfStats` (82% / 69%, diagnostics only); `Flora.FloraEntry` (71% / 50%); `KeystoneSnapshot` (64% / 50%); `RegionPersistedRecord` (62% / 100%); `ChunkValueStore` (86% / 80%).

**"Zero coverage" entries are false alarms:** `ClassARecipe`–`ClassERecipe`, `Flora.YieldInfo` are pure-data records with no executable methods; their consumers (`AttritionRecipeParser`, `BiomeLevelTable`) are tested. Truly untested files are interface, enum, and shim files only — no Core source file with substantial logic lacks tests.

## 2. Test quality

Spot-checked `BiomeMaturityUpdaterTests`, `BiomeTargetsTests`, `WaterContaminationTests`, `FlourishVisualsTests`, `PerfStatsTests`, the `Helpers/` fakes, and `PersistenceIntegrationTests`. Quality is high:

- Test names pin design constants verbatim: `Tick_FullSuitability_FromZero_AccruesAtAlphaRate`, `Threshold_IsExactlyZeroPoint05`, `BadwaterContaminationThreshold_IsExactly0p1`, `Grassland_FullDensityForest_TargetIsZero`. This is exactly the CLAUDE.md "tests pin design" rule in action.
- `WaterContaminationTests` cites the historical `>0` → `>=0.05` regression directly in docstrings and pins both the constant *and* the inclusive comparison at the exact boundary so a strict-`>` regression cannot sneak back.
- AAA structure is consistent throughout, with named tolerances and explanatory assertion messages.
- `#region` organization mirrors the source files.
- Helper fakes (`FakeBlocking`, `FakeClock`, `RecordingPerfScope` at `tests/Keystone.Core.Tests/Helpers/`) carry full docstrings.
- No tautologies, no over-mocking, no round-trip asserts. The single trivial test (`PlaceholderTests.TestProjectIsWired`) is clearly labelled as such.

A nice piece of intentional fragility worth flagging: `FlourishVisuals.ShouldDieFromBadwater` uses **strict** `>` on contamination with threshold `0.1`, while `WaterContamination.IsBadwater` uses **inclusive** `>=` with threshold `0.05`. Two thresholds, two comparison operators, both deliberate. Each axis is pinned by its own boundary test, but the cross-system invariant is not.

## 3. Gaps that matter (load-bearing, thin)

1. **`RegionEcologyField` branch coverage at 72%.** Highest-risk uncovered branches in the repo — this class feeds biome sampling, fauna gating, and cluster building downstream; a regression here is silent and far-reaching. File: `src/Keystone.Core/Ecology/Fields/RegionEcologyField.cs`.
2. **`FaunaWanderPlanner` branches at 78%.** Player-visible. File: `src/Keystone.Core/Fauna/FaunaWanderPlanner.cs`.
3. **`BiomeLevelTable` at 79%.** Level-band lookups; a misroute appears as wrong-flourish-at-wrong-progression — easy to miss in QA. File: `src/Keystone.Core/Biomes/BiomeLevelTable.cs`.
4. **`KeystoneSnapshot` direct branches at 50%.** Covered transitively via integration tests but not in isolation; save-load regressions surface late. File: `src/Keystone.Core/Persistence/KeystoneSnapshot.cs`.
5. **`ChunkValueStore` at 86% line / 80% branch.** Backbone of biome state persistence; targeted unit tests for unhit branches are cheap insurance. File: `src/Keystone.Core/Persistence/ChunkValueStore.cs`.

## 4. Port/adapter seams

**Pattern followed cleanly. No leaks.**

- Test `.csproj` references only `Keystone.Core.csproj`. No transitive path to `Keystone.Mod` or Timberborn DLLs.
- 22 of 60 test files implement port fakes inline (`IBuildingQuery`, `IContaminationQuery`, `ITerrainQuery`, `IMoistureQuery`, `IPlantingMarkQuery`, `IBlockingQuery`, `INaturalResourceEnumerator`, `IRegionTopologyQuery`, `INaturalResourceAtTileQuery`, `ICuttingMarkQuery`, `IWaterQuery`).
- Reusable fakes in `Helpers/` are properly scoped (`FakeBlocking`, `FakeClock`, `RecordingPerfScope`).
- The 14 Mod-side adapters under `src/Keystone.Mod/Adapters/` are not referenced from tests.

The only file matching `Adapter` in tests is `Biomes/ChunkBiomeAdapterTests.cs`, which tests Core's `ChunkBiomeAdapter` (a Core class), not a Mod adapter.

## 5. Recommendations (prioritized)

1. Audit `RegionEcologyField` branches; add tests for the missing 28%. Biggest risk reducer.
2. Cover `FaunaWanderPlanner`'s remaining branch arms in the existing 389-line test file.
3. Extend `BiomeLevelTableTests` to close the 21% gap.
4. Add direct `KeystoneSnapshot` unit tests for empty-snapshot, schema-version-mismatch, and value-vs-no-representative branches independent of `SnapshotCodec`.
5. Add a `FlourishVisuals.ShouldDieFromBadwater` two-axis boundary matrix test that crosses `waterDepth` and `waterContamination` simultaneously, so the strict-`>` / inclusive-`>=` divergence with `WaterContamination` is pinned across systems, not only per-system.
6. Drop or rename `PlaceholderTests.TestProjectIsWired` — the 811 other tests prove wiring.
