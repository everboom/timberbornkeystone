# Keystone.Core.Biomes

Per-chunk biome scoring (two channels: short-term Suitability with drift-
toward-target dynamics, long-term Maturity integrated from Suitability),
the bilinear sampler, the per-biome level table, and the Class A /
Class B recipe types. Recipe activation is two-stage at every tile:
Suitability must clear a pass threshold and the biome must win dominance
among passers (Suitability-axis), then Maturity must clear the level's
LowerMaturity (timeline-axis).

## Why this exists

Each chunk in the world has a *character* -- a label like Forest,
Wetland, or Contaminated -- that drives what visuals and ecology
behaviour Keystone applies to it. A naive classifier (recompute the
label every tick from current state) would oscillate visibly on small
state changes and would miss the design's core asymmetry: healthy
biomes should be hard-earned and easy to lose, contamination should
be sudden and lingering.

So a chunk holds a per-biome *Suitability* in `[0, 1]` that drifts toward a
*target* computed from current state. Drift rates are asymmetric, and
stress encodes catastrophic decay as a deeply negative target.

## Pieces

| Type | Role |
|---|---|
| `BiomeKind` | The 10 biomes Keystone classifies into (Forest, Grassland, Monoculture, River, Lake, Wetland, Cave, Dry, Contaminated, Badwater). |
| `ChunkBiomeInputs` | Pure struct: snapshot of a chunk's current state. Land-state fractions partition; contamination overlays. Inputs to the target functions. |
| `BiomeTargets` | Per-biome positive functions returning `[0, 1]` plus `Compute(...)`, which multiplies each by a contamination cancellation factor so contaminated chunks correctly read as Contaminated/Badwater rather than erroneously also reading as Forest etc. Drought and inundation are handled implicitly by the positive predicates (IrrigatedFraction/WaterFraction fall to 0 under those conditions). |
| `BiomeSuitabilityUpdater` | Writes Suitability into `ChunkValueStore` for every biome each tick. Stateless: each call recomputes from current `ChunkBiomeInputs` via `BiomeTargets.Compute`; previously-stored values are overwritten without being read. No drift, no rise/drop rates -- Maturity owns all the temporal dynamics. |
| `BiomeMaturityUpdater` | Ticks per-chunk Maturity values forward using a **hybrid model: exponential accrue, linear decay**. Each tick first computes the chunk's dominant biome (`ChunkBiomeSampler.DominantAtChunk`) and the scar-gate state (`Badwater > 0.1` OR `Contaminated > 0.5` toxic Maturity). Per biome: accrue mode (M ≤ asymptote) integrates `dM/dt = α·Suitability − βAccrue·M` exponentially; decay mode (M > asymptote) integrates `dM/dt = −rate` linearly, clamped at the asymptote so partial-Suitability support halts decay at the new sustainable level. Decay rate comes from `MaturityParameters.DecayRate(decaying, dominant)` -- the matrix -- unless the decaying biome is itself dominant or no biome dominates, in which case the polarity-driven `FallbackDecayDays` applies. Under Dry dominance the matrix rate is then multiplied by a drought-intensity scalar `floor + (1 - floor) * min(1, M_dry / DroughtSaturationMaturity)` so water-family biomes (`DroughtFloor=0.1`) take a small immediate hit while Grassland/Forest (`floor=0`) buffer until Dry has built up. Co-present cells (Contaminated decaying under Badwater dominant) zero the rate. Scar gate blocks accrue (only) for every `!IsNegative` biome. Floored at 0. |
| `MaturityParameters` | Per-biome ceiling table, (decaying, dominant) decay-clear-time matrix, scar-gate thresholds, polarity fallbacks, drought-intensity inputs. `Alpha=1` universal. `BetaAccrue = 1/Ceiling(biome)`: Forest/Grassland/Wetland/River/Lake/Cave = 30d, Badwater = 7d, Dry = 10d, Contaminated/Monoculture = 3.5d. `DecayClearTimeDays(decaying, dominant)` returns clear-time in days (linear rate at integration is `ceiling/clearTimeDays`): column defaults by dominant tier (BW 0.5d, Con 1d, water-family 5d, land-family 7d, Cave 14d) with row overrides (Badwater scar 14d, Contaminated scar 3.5d, Dry low-ceiling fast clear, Monoculture player-replaceable) and cell overrides (Wetland/Lake under dominant River = 3d). The Dry column is per-biome rather than a uniform default: River/Lake 0.7d, Wetland 1.8d, Grassland 2.1d, Forest 4.1d (saturated); Cave + Monoculture fall through to the 3d column default. One co-present cell: Contaminated decaying under Badwater dominant. `DecayRate` wraps the clear time as `(coPresent, ceiling/clearTimeDays)`. The integration layer further scales the rate under Dry dominance by `floor + (1 - floor) * min(1, M_dry / DroughtSaturationMaturity)` — `DroughtFloor(biome)` is 0.1 for water-family, 0 elsewhere; `DroughtSaturationMaturity = 3.33` is decoupled from `Ceiling(Dry)=10` so the ramp saturates around day 4 of a fresh drought rather than day 20+. Polarity fallbacks `PositiveFallbackDecayDays=7` / `NegativeFallbackDecayDays=70`. Scar-gate constants: `BadwaterScarGateThreshold=0.1`, `ContaminatedScarGateThreshold=0.5`. See DESIGN.md § Maturity for the full table. |
| `ChunkBiomeSampler` | Bilinear-interpolated per-tile read from `ChunkValueStore` -- same edge-clamp + invalid-corner-renormalise pattern as `RegionEcologyField.Sample`. Surface methods: `SampleSuitability` and `SampleMaturity` for a single biome's channel value; `SampleDominantBiome` picks per-tile identity as the max-Suitability biome (tiebreak by aggressor tier so Badwater wins over Contaminated on stacked chunks); `DominantAtChunk` is the chunk-resolution counterpart (no bilinear) used by `BiomeMaturityUpdater` to pick the dominant biome the (decaying, dominant) decay matrix indexes into. Per-tile resolution for `SampleDominantBiome` is by design -- per-chunk caching would carve hard edges along the chunk grid that the bilinear smoothing exists to prevent. |
| `BiomeValueKinds` | Maps `BiomeKind` to / from the `ChunkValueStore` string keys for both biome channels: `"keystone.chunk.suitability.<biome>"` (short-term Suitability, via `ForSuitability` / `TryParseSuitability`) and `"keystone.chunk.maturity.<biome>"` (long-term Maturity, via `ForMaturity` / `TryParseMaturity`). Both lookup tables cached at static-ctor time so the hot ticker paths don't allocate. |
| `BiomeLevel` | Pure data: `(Biome, LevelId, LowerMaturity, UpperMaturity, Density)`. A level entry on a biome's progression ladder. `Density` is the fraction of (eligible) tiles at which a recipe in this bucket fires when the level is active. |
| `BiomeLevelTable` | Per-biome ladder of level entries. Populated at PostLoad by the Mod-side `BiomeLevelCatalog` from `KeystoneBiomeLevelsSpec` instances (default ladder + per-biome overrides). Lookups: `LevelsFor(biome)`, `Find(biome, levelId)`, `ProgressIn(biome, levelId, maturity)`. The handlers gate level activity on `maturity >= LowerMaturity`; `UpperMaturity` is retained for forward compatibility but isn't consulted by the current activation math. |
| `ClassARecipe` / `ClassBRecipe` / `ClassCRecipe` / `ClassDRecipe` | Recipe records for the four content classes. All share `Biome`, `LevelId`, `BlueprintName` (or `DonorBlueprintName` for Class A), `Filter`, `Weight`. Activation rule: tile must have `Biome` as its dominant biome AND its level active AND the per-tile activation gate hits. The gate is `ComputeActivation(...) < level.Density` for Class A/B/C (deterministic per tile, reproducible across reloads) and `rng.NextDouble() < level.Density` for Class D (stochastic, accumulates over real time). Density is owned by the level, not the recipe — multiple recipes share the bucket and the handler picks one via weighted-random sampling on `Weight`. See `src/Keystone.Mod/Recipes/README.md` for the full dispatch shape. |
| `FlourishThreshold` | Two pure deterministic per-tile hashes in `[0, 1)`. `ComputeActivation(tileX, tileY, biome, levelId)` drives the bucket-activation gate (`hash < level.Density`); `ComputePick(tileX, tileY, biome, levelId)` drives weighted-random recipe selection within the bucket. The two use different salts so adding density doesn't shift which recipe gets picked. FNV-1a + Murmur3 finaliser; deterministic across sessions and .NET versions (does not use `string.GetHashCode`). |
| `WeightedPick` | Pure helper: picks an index from a list of weights using a uniform `[0, 1)` sample. Used by the handlers to choose one recipe from a bucket on activation. |

## Suitability is stateless

Each tick, `BiomeSuitabilityUpdater.Tick` writes
`BiomeTargets.Compute(biome, inputs)` clamped to `[0, 1]` for every
biome. No drift, no rise/drop rates, no history. Persistence is for
cross-tick consumer reads only; if the store is empty, the next tick
recomputes the same values.

## Cancellation lattice

`BiomeTargets.Compute(biome, inputs)` =
`ComputePositive(biome, inputs) × ContaminationFactor(biome, inputs)`.

The contamination factor is the only explicit cancellation in the
system. Drought and inundation are encoded implicitly by the per-biome
positive predicates: `IrrigatedFraction` falls to 0 under both, killing
Forest/Grassland/Monoculture; `WaterFraction` falls to 0 under drought,
killing River/Lake/Wetland. The partition
(`IrrigatedFraction + DryLandFraction +
WaterFraction + CaveFraction = 1`) does the work.

Contamination needs the explicit factor because a contaminated land
tile is still irrigated from the moisture channel's perspective. The
factor:

- Land biomes (Forest, Grassland, Monoculture, Cave, Dry):
  `(1 − clamp01(ContaminatedFraction × 20))`. 5% contamination fully
  cancels.
- Water biomes (River, Lake, Wetland): same shape on
  `ContaminatedWaterFraction`. Land contamination on the shore doesn't
  cancel them (the water itself is still clean).
- Contaminated and Badwater: factor is always 1 (they *are* the
  contamination state).

Lattice priority falls out naturally: Badwater contributes to
`ContaminatedFraction`, so it cancels every land biome through the
same factor; aggressor tiebreak in `ChunkBiomeSampler` resolves
dominance when Contaminated and Badwater tie at the same Suitability.

## Cross-biome yield (Grassland)

Grassland is the positive predicate that yields multiplicatively to
two competitors: it's
`irrigated * (1 - mature_canopy) * (1 - Monoculture)`.
A chunk with *established* (mature) trees yields Grassland to Forest; a
player-managed chunk yields Grassland to Monoculture; a chunk that has
both gives a smaller residual Grassland alongside the others.
Multiplicative rather than gated so partial-overlap cases produce smooth
accumulation rather than chunk-edge cliffs. Forest also carries
the `(1 - Monoculture)` multiplier so managed areas suppress every
natural healthy-land biome at once.

The canopy term keys off `MatureTreeCount`, the same mature signal
Forest's mature-canopy gate uses (see above) — not raw `TreeCount`. So a
chunk freshly planted with seedlings reads as ~0 Forest (gated) *and*
full Grassland (no mature canopy to yield to), handing off cleanly to
Forest as the trees establish. Keying off raw tree count instead would
strand a seedling field in a low-everything limbo (not-Forest,
not-Grassland).

Near-water tiles within a Grassland chunk spawn riparian-style
decorations via the `WaterEdge` recipe filter; the biome scoring itself
is water-distance-agnostic.

## Forest mature-canopy gate

Forest carries one extra multiplicative factor beyond irrigation,
diversity, density, and `(1 - Monoculture)`: a **mature-canopy gate**,
`saturate(MatureTreeFraction / 0.25)`. `MatureTreeFraction` is the share
of the chunk's trees that are fully grown (`MatureTreeCount / TreeCount`,
0 when treeless). The gate ramps linearly from 0 (no mature trees) to 1
(>= 25% of the chunk's trees mature). A chunk freshly carpeted with
dense, diverse *seedlings* therefore reads as 0 Forest until roughly a
quarter of them mature — the biome rewards genuinely established
woodland and can't be gamed by mass-planting saplings. Smooth ramp, not
a hard threshold, for the same chunk-edge-cliff-avoidance reason as the
Grassland yields above.

The mature count is collected upstream: `EcologyFieldUpdater` keeps a
synthetic `(mature-trees)` aggregate channel (a live tree counts toward
it only when its `Growable.IsGrown`), the adapter reads it into
`ChunkBiomeInputs.MatureTreeCount`, and `BiomeTargets.Forest` divides by
`TreeCount` for the fraction.

## Monoculture as a distribution-sensitive signal

`Monoculture` reads Simpson's diversity index
(`PlantableDominance = Σ (count_i / total)²`) plus a saturation
factor over the chunk's total plantable count (entities + player-
drawn planting marks). Distinguishes `14:2` (lopsided, high
monoculture) from `8:8` (balanced two-species mix, mild
monoculture) even though both have two species. A perfectly-even
three-species mix lands at zero; pure single-species saturates to
one. Hard floor at three total plantables so single-tile chunks
never trigger.

## Water biomes (depth × flow split)

Water is partitioned across depth (hard threshold at 1)
and flow (binary at `HighFlowThreshold`):

- **Wetland** = shallow + low-flow. Slow side channels, dam-stilled
  shallows. The mod's primary productive water biome (cattail,
  spadderdock, mangrove).
- **River** = high-flow at any depth (shallow + deep, summed).
  Main-channel current. Decorative only.
- **Lake** = deep + low-flow. Dam reservoirs and natural ponds.
  Passive, no Keystone content.

`ChunkBiomeInputs` exposes the four depth × flow sub-fractions
plus the `HighFlowWaterFraction` aggregate. Cesspool is not a
biome in this taxonomy — contaminated water of any depth/flow
collapses into `Badwater`.

## Usage shape (downstream code)

```csharp
var suitabilityUpdater = new BiomeSuitabilityUpdater();
var maturityUpdater = new BiomeMaturityUpdater();

// Per chunk, per Keystone tick (Mod-side ChunkBiomeTicker does this):
var inputs = adapter.Build(field, cx, cy);
// Suitability first -- short-term [0, 1] drift toward stress-aware target.
suitabilityUpdater.Tick(chunkStore, regionId, globalCx, globalCy,
    in inputs, deltaDays);
// Maturity second -- long-term integration of the just-updated Suitability.
maturityUpdater.Tick(chunkStore, regionId, globalCx, globalCy,
    deltaDays);

// Per-tile reads for downstream spawn drivers. Three flavours:
var localSuitability = ChunkBiomeSampler.SampleSuitability(
    chunkStore, regionId, BiomeKind.Forest,
    field.OriginX, field.OriginY, field.ChunksX, field.ChunksY,
    tileX, tileY);
var localMaturity = ChunkBiomeSampler.SampleMaturity(
    chunkStore, regionId, BiomeKind.Forest,
    field.OriginX, field.OriginY, field.ChunksX, field.ChunksY,
    tileX, tileY);
// SampleDominantBiome layers two gates: Suitability-pass + max-Suitability
// among passers. Returns (null, 0) when no biome's Suitability clears
// the gate, otherwise (winner, winner's Maturity).
var (dominant, dominantMaturity) = ChunkBiomeSampler.SampleDominantBiome(
    chunkStore, regionId,
    field.OriginX, field.OriginY, field.ChunksX, field.ChunksY,
    tileX, tileY);
if (dominant == null) return;  // tile is in no biome right now

// Handlers gate bucket activation per (tile, biome, level) on
// hash < level.Density, then pick one recipe from the bucket via
// a separate per-tile weighted-random pick.
foreach (var level in levels.LevelsFor(dominant.Value)) {
  if (dominantMaturity < level.LowerMaturity) continue;
  var activationHash = FlourishThreshold.ComputeActivation(
      tileX, tileY, dominant.Value, level.LevelId);
  if (activationHash >= level.Density) continue;
  var pickHash = FlourishThreshold.ComputePick(
      tileX, tileY, dominant.Value, level.LevelId);
  // ... pick one recipe from catalog.ClassAFor(dominant.Value, level.LevelId)
  //     via WeightedPick.Pick(weights, pickHash)
}
```

Both Suitability and Maturity values live in the same `ChunkValueStore`
under namespaced kinds (`keystone.chunk.suitability.<biome>` /
`keystone.chunk.maturity.<biome>`). No separate in-memory mirror;
the persistent store *is* the working set.

## Calibration

Suitability constants live on `BiomeSuitabilityUpdater` and `BiomeTargets`;
Maturity constants on `MaturityParameters`. Tweak the magnitudes
and re-run the updater tests; they pin the most-load-bearing edge
cases (rise cap, drop no-overshoot, stress-driven crash, parallel
accumulation across biomes; Maturity asymptote, fast positive
decay, slow negative scar fade, mode-switch boundary).
