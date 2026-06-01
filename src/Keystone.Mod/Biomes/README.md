# Keystone.Mod.Biomes

Mod-side drivers for the Core biome scoring system. Translates ecology
field state into `ChunkBiomeInputs` and ticks each chunk's per-biome
Suitability values forward against `ChunkValueStore` once per game-hour.

## Pieces

| Type | Role |
|---|---|
| `ChunkBiomeAdapter` | Translates per-chunk `RegionEcologyField` channel values into `ChunkBiomeInputs`. Plumbs the scalar channels (Moisture, Contamination, WaterDepth, WaterFlowMagnitude) into the five-way land partition plus the depth × flow water sub-fractions: `ShallowSlowWaterFraction`, `ShallowHighFlowWaterFraction`, `DeepSlowWaterFraction`, `DeepHighFlowWaterFraction`. Depth uses a hard threshold at 1 (≤ 1 → Wetland/Lake-shallow, > 1 → Lake-deep); flow uses a binary `HighFlowThreshold` (0.10). Folds the chunk's Tree-kind entity counts plus the union of plantable entities and player-drawn planting marks (via `IPlantingMarkQuery`) into the plantable count / species count / Simpson's-D dominance. Cave fraction is still a placeholder (see code comments). |
| `ChunkBiomeTicker` | Rolling-sweep ticker on a 1-game-hour cycle. Walks every live region's valid chunks, builds inputs via the adapter, then runs two passes per chunk: `BiomeSuitabilityUpdater.Tick` advances Suitability under `keystone.chunk.suitability.<biome>` kinds, and `BiomeMaturityUpdater.Tick` integrates Maturity under `keystone.chunk.maturity.<biome>` kinds against the just-updated Suitability. No separate in-memory mirror -- `ChunkValueStore` is both the working set and the persisted form. At cycle end the ticker prunes `ChunkValueStore` of any chunk that left its region's bbox during the cycle (the only ticker doing this prune; consolidating it here keeps the per-chunk schedule and cleanup symmetric). |
