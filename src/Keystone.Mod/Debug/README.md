# Keystone.Mod.Debug

In-game debug surfaces and Phase 1 prototype probes. Strictly
developer-facing -- no production UX should depend on anything here.

## Pieces

| Type | Role |
|---|---|
| `KeystoneTileDebugPanel` | `IDebuggingPanel` "Keystone (tile)" — cursor coordinates, cliff-adjacency status, every `BlockObject` at the voxel with catalog classification + per-entity lifecycle detail (`KeystoneFlourish`: plant-type tier dry/water/irrigated + healthy/unhealthy/dead + phase + variant class; `KeystoneRockTint`: current tint variant + variant class), survey staleness, and the per-surface region + irrigation readout. |
| `KeystoneChunkDebugPanel` | `IDebuggingPanel` "Keystone (chunk)" — bilinear ecology-field sample (entity densities) and per-chunk `ChunkValueStore` entries (biome Suitability + Maturity) at the cursor's chunk. |
| `PanelActivity` (abstract) + `KeystoneTilePanelActivity` / `KeystoneChunkPanelActivity` | Per-panel "was this section queried recently" signal. Each debug panel touches its own activity in `GetText()`; `PlateauHighlighter` reads each independently so its tile-side overlay (Nature-source samples) only fires when the tile panel is expanded and its chunk-side overlays (region / cluster / chunk) only fire when the chunk panel is expanded. Side-channel HACK that lets us avoid a hotkey or `DebugModeController` gate; see the type docs for the failure-mode discussion. |
| `CrossFactionProviderBase` | Shared base for the cross-faction collection / material providers. Captures the active faction id once, exposes it via the static `ActiveFactionId` for the Harmony patch's static context, and yields the non-active-faction collection ids out of a narrow candidate list. |
| `CrossFactionCollectionProvider` | Multi-bound `ITemplateCollectionIdProvider`. Asks the collection service to load the OTHER faction's `NaturalResources.<faction>` collection on top of the active one. Pairs with `TemplateCollectionServicePatch` to keep the active faction's UIs from binding cross-faction plantables. |
| `CrossFactionMaterialProvider` | Multi-bound `IMaterialCollectionIdsProvider`. Companion to the collection provider: loads the OTHER faction's material collection so cross-faction prefabs can resolve their meshes/materials at instantiation time. |
| `KeystoneSpawnProbe` | Prototype #1: dumps every loaded `TemplateCollectionSpec` / `TemplateSpec`, then spawns one row of every Folktails-only and IronTeeth-only natural resource near the district center to verify the cross-faction unlock works in both directions. |
| `PassiveObjectProbe` | Prototype #2: a Unity GameObject with just a renderer at a Timberborn tile -- no entity-system, no save/load, no UI. The shape that became `KeystoneDecorationRegistry.RegisterExisting`. |
| `ParticleProbe` | Prototype #3: code-built placeholder ParticleSystem rigs (mist, flying critters, ground critters, fish) west of the district center. Validates construction + positioning + lifecycle ahead of authoring real assets. |
| `DerivedBlueprintProbe` | Prototype P2 (validation): tests whether the second `Blueprint(Blueprint, ComponentSpec, ComponentSpec)` constructor preserves the donor's prefab association. Backstory for the eventual `KeystoneFlourishSpec` blueprint authoring path. |
| `StrippedEntityProbe` | Prototype P2: lifts a vanilla Maple, strips interaction specs (gathering / planting / cutting / yielding), renames it `Keystone.Stripped.Maple`, and spawns several diagnostic placements. The "Keystone.Stripped." prefix it injects is the legacy half of `AmbientNaming.IsAmbient`. |
