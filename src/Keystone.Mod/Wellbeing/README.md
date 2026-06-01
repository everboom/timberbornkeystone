# Wellbeing

Keystone's per-beaver wellbeing layer — biome-keyed Nature needs satisfied by visiting contemplation-style buildings.

## Pieces

| Type | Role |
|---|---|
| `KeystoneEcologyTransparentSpec` | Marker spec attached to buildings whose voxels should not count as "settled" for Keystone's surveyor. Read by `Adapters.BuildingQueryAdapter` — a tagged building reports as `BuildingKind.None` so its chunk keeps accruing biome state. |
| `KeystoneNatureSourceSpec` + `KeystoneNatureSourceEntry` | Data spec listing the (biome, need-id, points-per-hour) sources a building can offer. |
| `KeystoneNatureSource` | `TickableComponent` decorator-bound to any blueprint carrying `KeystoneNatureSourceSpec`. Each refresh: builds the 2×2 chunk neighbourhood across the footprint, runs a per-surface Z scan to emit `(region, chunkX, chunkY)` samples for surfaces at or below the building's base Z (the vantage rule), resolves each sample to a `ChunkClusterIndex` cluster id (de-duping), then for each eligible biome sums threshold-weighted tile counts across all touched same-biome clusters: `raw = Σ_clusters Σ_t ThresholdWeights[t] * cluster.TileCountsAbove[t]`, then `score = clamp01(raw / Saturation)`. Highest-rate source wins; per tick applies its `ContinuousEffect` to every beaver in the `Enterable`. Bypasses vanilla `AttractionSpec` so entertainment satisfaction is untouched. |
| `KeystoneNatureSourceDescriber` | `IEntityDescriber` decorator that surfaces the building's Nature-need affordance in the build-menu tooltip and the placed entity panel. Build menu lists all eligible sources (compare-before-place). Placed + active shows the winning source plus a two-axis description: a tier headline (Minor / Medium / Major) and independent adjectives for size (small / medium / large, by chunkCount) and maturity (immature / healthy / mature, by averageMaturity), so the player can see which axis is weak. Placed + inactive emits a placement hint listing the eligible biomes. |
| `KeystoneNatureFactions` | Static table — single source of truth for which factions get the Nature need set and which of their buildings emit `KeystoneNatureSourceSpec`. Edit this to change faction coverage. |
| `KeystoneNatureModifierProvider` | `IBlueprintModifierProvider` that fans `KeystoneNatureFactions.Entries` out into `SpecService`-time JSON modifiers — one `NeedCollectionSpec.Needs#append` per opted-in faction, one `KeystoneNatureSourceSpec` (+ `KeystoneEcologyTransparentSpec`) per listed building. Bound via concrete + `MultiBind<IBlueprintModifierProvider>().ToExisting<…>` in `KeystoneConfigurator`. |

## Data side

- `unity-assets/Keystone/Data/NeedGroups/NeedGroup.KeystoneNature.blueprint.json` — own group header "Nature".
- `unity-assets/Keystone/Data/Needs/Need.Beaver.KeystoneNature.{Forest,Grassland,Wetland}.blueprint.json` — three need flavors. Forest and Wetland contribute **+3 favorable wellbeing** when filled (tier-3 vanilla — Agora, MudBath); Grassland contributes **+1** because it forms with minimal player effort (idle terrain matures into grassland naturally) and shouldn't compete with the harder-earned biomes. Decay **−0.2/day** uniform (matches vanilla tier-1 leisure decay; biome state moves on a similar timescale). All three filled = +7 wellbeing total, weighted toward the effortful biomes.

The per-faction `NeedCollection` overlays and per-building Nature-source specs are **not** authored as JSON files — they're emitted programmatically by `KeystoneNatureModifierProvider` from the C# table in `KeystoneNatureFactions`.

## Faction coverage

The Nature mechanism is attached to factions that ship semantically appropriate contemplation buildings. Each building's biome list is chosen by its **theme**, not the faction:

| Theme | Biomes | Buildings |
|---|---|---|
| Dry (ground-level, non-water) | Forest, Grassland | ContemplationSpot (vanilla & LeafCoats), Campfire, Garden.LeafCoats |
| Sky (elevated, overlooking) | Forest, Grassland, Wetland | RooftopTerrace, ContemplationSpot.Branch.LeafCoats, ObservationTerrace.LeafCoats |
| Water (water-themed leisure) | Wetland | Lido.Folktails, MudPit.LeafCoats |

**Per-faction overrides** are layered on top of the theme buckets. The only one currently in play: **Emberpelts dislike water** (lore-driven), so every Emberpelts building drops Wetland — including the otherwise-sky RooftopTerrace.Emberpelts, which lists only Forest + Grassland. The NeedCollection append is then driven by the per-faction biome union, so the Wetland need never instantiates on Emberpelts beavers' NeedManager at all.

**Iron Teeth** has no entry, deliberately:

1. **No thematic fit.** IT's leisure buildings (Motivatorium, ExercisePlaza, Mud Bath, etc.) are industrial / utilitarian by faction design. A "contemplation in nature" mechanic on a Motivatorium would read as a category mistake.
2. **Already correctly excluded by construction.** A faction with no entry in `KeystoneNatureFactions.Entries` has no `Needs#append` modifier emitted against its `NeedCollection`, so IT beavers never instantiate the Nature needs in their `NeedManager`. The NeedSpec blueprints load globally (just data sitting in the spec service) but nothing references them on IT side.

Don't add an Iron Teeth entry "for symmetry" — the asymmetry is the design.

### Adding a third-party faction

1. Confirm the target mod ships a `NeedCollection.<FactionId>.blueprint` (the `CollectionId` matches the faction id) and at least one contemplation-spot-analogue building.
2. Append a `NatureFactionEntry` to `KeystoneNatureFactions.Entries` with that faction id and the list of buildings to overlay. Use `AllBiomes` for general contemplation buildings, `WetlandOnly` (or a new subset) for thematic exceptions.

The modifier provider matches by lowercased blueprint-filename suffix on the loaded blueprint `Path`, so **the target mod's directory layout does not matter** — Emberpelts shipping its NeedCollection under `Collections/` and vanilla shipping its under `NeedCollections/` both work without any path-mirroring.

## Tuning knobs

Score formula, in `KeystoneNatureSource`:

- `ThresholdWeights` (`{0.625, 1.25, 2.5, 3.75, 5.0}`) — per-threshold tile-count multipliers, parallel to `ChunkClusterIndex.Thresholds` (2.5 / 5 / 10 / 15 / 20). Linear schedule `weight = threshold / 4`: a fully-mature tile (≥ 20) clears all five buckets and contributes ~13.1 to the raw score; a barely-qualifying one (≥ 2.5) contributes 0.625.
- `Saturation` (1000) — raw score at which the clamped score saturates to 1.0. A vast pristine biome (~8 fully-mature chunks ≈ 128 tiles) saturates; a small fully-mature biome (~2 chunks ≈ 32 tiles, raw ≈ 420) lands in Medium territory. Lower = easier Major; higher = vast-biome-required.
- `RefreshIntervalTicks` (600) — how often to recompute the winning source. Maturity is day-scale so generous intervals are fine.

Per-building default, in `KeystoneNatureFactions`:

- `DefaultPointsPerHour` (4.0) — the cap that `score` multiplies. At score=1.0 a beaver fills a need in ~15 game-minutes of visit, and the −0.2/day depletion keeps it favorable for ~5 game-days after.

Qualitative buckets (size adjective at chunkCount ≤ 2 / ≤ 5 / >5; maturity adjective at avg < 5 / < 10 / ≥ 10; tier cutoff at score 0.35 / 0.70) and tier wording live in `KeystoneNatureSourceDescriber.AppendQualitativeStatus`. The aggregate `chunkCount` and `averageMaturity` reported in the tooltip are summed across all contributing same-biome clusters, so a building between two small forests reads "large" even though no single cluster is large.
