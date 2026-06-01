# Keystone.Mod.Decoration

Class A content: non-`BlockObject` decorations. Atmospheric
particles + biome-Suitability-driven ambient flora, both spawned through
the same registry. Decorations claim no tile, are not entity-system
participants, are not persisted, and are not selectable / demolishable.

Reactivity is orthogonal: a decoration may carry an
`IDecorationController` that polls ecology services to update its
visual state, or it may be inert (zero per-tick cost). Same registry,
same lifecycle.

## Pieces

| Type | Role |
|---|---|
| `KeystoneDecorationRegistry` | `IUpdatableSingleton`. Owns the live set of decorations. `Spawn(donorBlueprintName, tile, controller)` clones a vanilla prefab via `IPrefabOptimizationChain` and `TemplateCollectionService.AllTemplates`. `RegisterExisting(go, tile, controller)` takes ownership of a caller-built GameObject (e.g. a code-built `ParticleSystem`). Ticks reactive controllers on a throttled cadence (`TicksBetweenSweeps`); inert decorations cost nothing after spawn. `Despawn` destroys the GameObject and removes the entry. |
| `KeystoneDecoration` | One live decoration: owns a `GameObject`, knows its tile + `SurfaceCoord`, optionally carries an `IDecorationController`. Pure data + a ref. |
| `IDecorationController` | Per-decoration reactivity contract. `Tick(decoration, moisture, contamination, water)` is called by the registry on each throttled sweep. Implementations read Core ports and mutate the decoration's GameObject. |
| `MoistureFadeController` | Sample reactive controller: scales the decoration based on `IMoistureQuery.IsMoistAt`. Crude but obvious for prototype validation. |
| `FloraLifecycleMoistureController` | Reactive controller for decorations cloned from vanilla flora prefabs. Walks the standard `#Models/<stage>/#<state>` hierarchy on first tick, disables Seedling, and toggles Mature `#Alive` / `#Dying` based on per-voxel moisture. Compensates for the fact that decorations skip the entity system, so vanilla `Growable` + `NaturalResourceLifecycleModel` aren't picking exactly one variant to show. |
| `DecorationPlacementTool` | Dev tool: left-click to spawn a Dandelion decoration with a `FloraLifecycleMoistureController`. Validates the reactive flora-clone path end-to-end. |
| `ParticlePlacementTool` | Dev tool: left-click to spawn a code-built Unity `ParticleSystem` decoration via `RegisterExisting`. Validates that the registry isn't tied to vanilla prefab cloning -- any GameObject works. |

Class B content (`BlockObject`-claiming flourishes -- the
`KeystoneFlourishTest` pattern) lives in `Flourish/`. Class D
(harvestable vanilla flora, both active-faction and cross-faction)
lives in `Flora/`. The per-class spawn handlers
(`ClassASpawnHandler`, `ClassBSpawnHandler`, `ClassCSpawnHandler`,
`ClassDSpawnHandler`) live in `Recipes/` and are invoked by
`ChunkRulesApplier` during the single per-chunk pass.
