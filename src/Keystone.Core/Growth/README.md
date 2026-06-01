# Growth

Pure calculation for the biome-driven plant growth bonus. Plants in
qualifying biomes grow faster: trees benefit from Forest, water plants
from Wetland.

## Key types

| Type | Purpose |
|---|---|
| `GrowthBonusCalculator` | Static helper. `ComputeBonus` blends chunk Suitability (50%) and Maturity fraction (50%) into a bonus in [0, maxBonus]. `TargetBiome` maps plant characteristics (aquatic / tree) to the biome whose health drives the bonus. |

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
