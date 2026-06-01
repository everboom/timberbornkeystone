# Keystone.Mod.Atmosphere

Time-of-day-driven ambient effects layered on top of the biome state.
Distinct from biome flourish (`Keystone.Mod.Flourish`), which spawns
biome-keyed content on a per-day cycle: this folder is where the
*clock*, not the biome score, drives the spawn -- morning mist, dusk
fireflies, etc. The pattern is per-tick polling of `IClock`, with a
day-roll gate that fires once per in-game day.

## Pieces

| Type | Role |
|---|---|
| `WetlandMistDirector` | `ITickableSingleton` that scatters Ground Fog particle instances across deep-interior Wetland chunks at the start of each in-game morning. Three-gate eligibility per tile: chunk's dominant biome is Wetland (Suitability winner), chunk's Wetland Maturity ≥ 2.5 days, and the topmost surface's water depth sits in the `(0.1, 0.5)` voxel band (shallow standing water -- excludes dry mud and open river/lake channels). Tiles also need all four cardinal neighbours in Wetland-dominant chunks (deep-interior, not zone rim). Passing tiles get a deterministic `(day, x, y)`-seeded 25% RNG roll, capped at one mist per chunk. Spawn time is uniform in the 18.5–19h window, despawn in the 0–1h window of the next in-game day; emission ramps in and out over one in-game hour either side. No persistence -- Class A, not a `BlockObject`; reloads past the spawn window mark the day as rolled and skip today's mist (deliberate). The class is named non-time-qualified to leave room for later directors at different windows. |
