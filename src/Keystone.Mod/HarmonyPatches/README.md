# Keystone.Mod.HarmonyPatches

Harmony patches applied at `IModStarter.StartMod` (see
`KeystoneModStarter`). Used narrowly: cross-faction template mutation
plus three patches that make Keystone-ambient entities invisible to
player interaction. Anything more invasive should go through Bindito ->
adapter -> port instead.

The expected patched-method count is asserted at startup
(`KeystoneModStarter.ExpectedPatchedMethodCount`); a mismatch logs a
warning so a Timberborn rename / removal surfaces immediately rather
than silently breaking gameplay.

## Pieces

| Type | Role |
|---|---|
| `AmbientNaming` | Single source of truth for "is this entity Keystone-ambient" -- the predicate the three interaction-suppression patches share. Two contracts: a `KeystoneFlourish` decorator (the spec-driven path for shipped content) OR a `Keystone.Stripped.` GameObject-name prefix (the legacy path for `StrippedEntityProbe`'s code-built blueprints). |
| `TemplateCollectionServicePatch` | Postfix on `TemplateCollectionService.Load`. Two passes: **(1) Dedupe-first-wins** by `Blueprint.Name`, required because expansion factions (Emberpelts) reference vanilla blueprint file paths from their own `NaturalResources.<Faction>` collection -- once we also ask for the vanilla collections via `CrossFactionCollectionProvider`, the same file gets parsed twice and `TemplateNameMapper.TryAddTemplate` throws. Logs a Warning when a dropped duplicate has a different `ComponentSpec` composition than the first-seen entry (real divergence; dedupe would be hiding it). **(2) Capability-aware Plantable/Gatherable strip on cross-faction blueprints**: computes three sets -- *nativeBlueprintNames* (active faction's `FactionSpec.TemplateCollectionIds` → `TemplateCollectionSpec.Blueprints` basenames), *supportedPlantableGroups* (`PlanterBuildingSpec.PlantableResourceGroup` across loaded planters), *supportedGoodIds* (loaded `GoodSpec.Id`). Native blueprints are kept untouched. Non-native (Keystone-injected) blueprints have `PlantableSpec` stripped if its `ResourceGroup` isn't supported or its sibling `GatherableSpec`'s yield Good isn't loaded; `GatherableSpec` stripped if its yield Good isn't loaded. Result under any active faction: vanilla shared flora (Pine/Birch/Oak/BlueberryBush) stays plantable because the active faction has a Forester-class planter and Logs is universal; foreign-only flora (Folktails Dandelion / Carrot / Sunflower under IronTeeth or Emberpelts) gets stripped because either the planter group or the harvest good is absent. No per-faction hardcoded lists; faction author's own naturals are trusted. Reads `FactionSpec` from `FactionIdAccessor.CurrentSpec` and the spec service from `SpecServiceAccessor.Specs` (collection specs aren't in `AllTemplates`; they're metadata read by `TemplateCollectionService` from `ISpecService`). Replicates the ModdableTimberborn pattern without dragging in the dep. |
| `NaturalResourceModelShowCurrentModelPatch` | Skips the body of `NaturalResourceModel.ShowCurrentModel` when the entity has no `Growable`. Vanilla's else-branch unconditionally calls `_growable.ShowMatureModel()` with no null check; flourishes opt out of `GrowableSpec`, so without this patch they NRE at `PostInitializeEntity`. Skip is safe: without `Growable` there's no `NaturalResourceLifecycleModel` either, so there's nothing to show / hide regardless. |
| `SelectableObjectRetrieverPatch` | Intercepts `SelectableObjectRetriever.TryGetSelectableObject(GameObject, out)` and returns false for ambient entities. Suppresses cursor hover highlight. The less-invasive alternatives (spec strip, `DisableComponent`, collider tricks, cache-removal reflection) all failed; this patch is the cleanest cut. |
| `EntitySelectionServicePatch` | Companion to the retriever patch. Cursor *click* goes through `EntitySelectionService.Select` -> the assertion-style `GetSelectableObject(BaseComponent)` overload, which would throw on the suppressed entities. We skip `Select` entirely for ambients. |
| `DemolishableSelectionToolPatch` | Filters ambient entities out of the demolish tool's `ActionCallback` (commit) and `PreviewCallback` (drag preview) lists. `Demolishable` itself is wired to a foundational spec we keep, so spec strip / `DisableComponent` aren't viable -- the cleanest cut is at the tool's input path. |
| `BuildingDeconstructionClassBPatch` | The inverse direction: *injects* Class B entities into the **building** bulk-demolish tool's `PreviewCallback` and `ActionCallback` lists so the player can drag-select an area and have Class Bs deleted instantly alongside actual buildings. Required because the picker filters by `GetComponent<BuildingSpec>() != null` at the LINQ stage and Class B blueprints don't carry `BuildingSpec` (would force a migration off `WateredNaturalResourceSpec`). Reads from `ClassBAreaQuery` (a small `IBlockService` wrapper exposed via static singleton because Harmony patches can't take constructor injection). The companion suppression patches stay: Class B remains unselectable on hover/click; the only way to remove one is via the bulk-area tool. |

Per the project convention in `CLAUDE.md`, the standard Harmony mod is
listed as a manifest dependency rather than bundled as a NuGet ref --
avoids version drift across the Timberborn modding ecosystem.
