# Keystone.Mod.Flourish

Class B content: `BlockObject`-claiming flourishes. Persisted,
displaceable by player builds, non-interactive (selection /
demolish suppressed via the Harmony patches in `HarmonyPatches/`).
Custom three-axis lifecycle (phase / life-status / health) with
optional auto-wiring from vanilla `WateredNaturalResourceSpec` /
`FloodableNaturalResourceSpec`.

## Pieces

| Type | Role |
|---|---|
| `KeystoneFlourishSpec` | Marker `ComponentSpec`. Empty record; presence on a blueprint is what the template chain uses to attach `KeystoneFlourish` and what the Harmony ambient-filter patches read (`AmbientNaming.IsAmbient`). |
| `KeystoneFlourish` | Per-entity decorator. Three orthogonal state axes: `FlourishPhase` (Seedling / Mature / Stump, manual), `FlourishLifeStatus` (Alive / Dead, manual -- vanilla `Died` deliberately not subscribed; Keystone owns death decisions), `FlourishHealth` (Healthy / Dry, auto-driven by `DyingNaturalResource.StartedDying` / `StoppedDying`). Resolves leaf GameObjects under the standard `#Models/(Seedling|Mature)/#(Alive|Dying|Dead)` + `#Models/Stump` hierarchy at `InitializeEntity`. `UpdateVisuals` hides every leaf and activates the one matching `(phase, life-status, health)`. Replaces vanilla's `NaturalResourceModel` pipeline (which NREs without `Growable`; see `NaturalResourceModelShowCurrentModelPatch`). |
| `FlourishPlacementTool` | Dev tool: left-click force-places a Class B recipe at the cursor tile, bypassing the level-activation gate. Resolves the cursor's dominant biome by **Suitability** (not Maturity, so freshly-built terrain works before Maturity has accrued; see `feedback_dev_tool_uses_score`), then picks a random recipe from `FlourishCatalog.ClassBForBiome` for that biome and instantiates it. Stamps `KeystoneVariant.Class = "B"` post-spawn so force-placed and handler-spawned entities are indistinguishable. |
