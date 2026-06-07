# Growth

Pure calculation for the biome-driven plant growth bonus. Plants in
qualifying biomes grow faster: trees benefit from Forest, water plants
from Wetland.

## Key types

| Type | Purpose |
|---|---|
| `GrowthBonusCalculator` | Static helper. `ComputeBonus` blends chunk Suitability (50%) and Maturity fraction (50%) into a bonus in [0, maxBonus]. `TargetBiome` maps plant characteristics (aquatic / tree) to the biome whose health drives the bonus. |
| `GrowthDiagnostics` | Pure classification for the entity-panel readout. `Classify(GrowthSignals)` returns a `GrowthVerdict` (Thriving / Benefiting / Hostile / Establishing / WrongBiome / Dormant) via a priority cascade over the two axes (current suitability, established maturity) plus the dominant-by-maturity and dominant-by-suitability biomes. Also exposes the display tiers (`MaturityTierOf`, `SuitabilityTierOf`), the hostility predicates (`IsToxic`, `IsMoistureMismatch`), and the named thresholds (`EstablishedMinFraction`, `SuitabilityFavorable`, `BonusMarginFraction`, …) shared with the UI. |
| `GrowthSignals` | Immutable signal bundle (primitives + `BiomeKind` only) assembled by `KeystoneGrowthBonus.ComputeSignals` and consumed by `Classify` + the tooltip. Carries suitability, maturity fraction, cluster maturity fraction, bonus fraction, the two dominant-biome reads, and (Forest) the mature-canopy gate + un-gated-vs-monoculture flag. |

## Verdict cascade

`Classify` resolves the first matching rule: toxic-obstacle → Hostile;
established **and** favorable → Thriving; bonus ≥ `BonusMarginFraction` →
Benefiting; moisture-mismatch obstacle → Hostile; on-track (Forest canopy
young, or favorable-but-immature) → **Establishing** if there is real
current suitability (≥ `SuitabilityWeak`) or **Potential** if suitability
is ~0 (canopy gated — will establish but hasn't started); other biome
established here → WrongBiome; else Dormant. The bonus-margin rule stops a
negligible suitability/maturity blend from claiming a positive verdict;
the Establishing/Potential split stops a zero-suitability seedling stand
from claiming it is establishing *now*.

## Bonus formula

```
bonus = maxBonus * (0.5 * suitability + 0.5 * clamp01(maturity / ceiling))
```

- `suitability` — chunk-level biome suitability at the plant's tile, [0, 1].
- `maturity` — max of chunk-local maturity and cluster average maturity.
- `ceiling` — per-biome maturity cap from `MaturityParameters.Ceiling`.
- `maxBonus` — player-configurable via the "Max biome growth bonus" slider
  (default 20%).

## Biome targeting

| Plant characteristic | Target biome |
|---|---|
| `FloodableNaturalResourceSpec.MinWaterHeight > 0` (aquatic) | Wetland |
| `TreeComponentSpec` (non-aquatic tree) | Forest |
| Neither | No bonus (component inert) |

Aquatic takes priority over tree (e.g. Mangrove = aquatic tree -> Wetland).
