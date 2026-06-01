# Fauna

Animal entities (Class E in the recipe taxonomy). Population is driven by
the per-cluster ecological score: each Class E recipe carries a per-level
`FaunaCapacityAtSaturation` and `FaunaMinScore`, and the system maintains
`floor(cluster.Score · FaunaCapacityAtSaturation)` live agents in every
cluster whose biome and level matches.

## Spawn pipeline: decide vs. execute

The spawn pipeline is split across two singletons so that **decisions**
(cluster-level capacity reconciliation) run on a slow cadence while
**execution** (Instantiate calls) runs per-frame with frustum gating.

```
                       FaunaCycleTicker (sweep, 6 game-hours/cycle)
                         |
                         |  surplus -> cull immediately (frustum-gated)
                         |  deficit -> enqueue cluster
                         v
                       FaunaSpawnQueue (HashSet+Queue, dedup by ClusterId)
                         |
                         |  pop, recompute deficit via registry walk
                         |  pick off-frustum tile, Instantiate
                         v
                       FaunaSpawnDrainer (IUpdatableSingleton, per-frame)
```

The drainer is `IUpdatableSingleton` rather than `ITickableSingleton`,
which means it continues to fire while the game is paused. This is
deliberate: a long fast-forward followed by a pause produces visible
population convergence right as the player slows down. There is no
game-speed gate; the per-frame spawn cap is the rate limiter.

### Off-frustum gating

Both sides of the pipeline reject entities whose tile is inside the
camera viewport (with a small edge margin). On the spawn side this
manifests as "cluster stays in the queue until the camera moves." On
the cull side it manifests as "on-screen surplus survives until the
next sweep cycle 6 game-hours later." Pops are hidden, queue/cluster
state self-heals when the camera moves.

### Per-cluster recount on every drain visit

The drainer does **not** maintain incremental per-cluster live counts.
On every visit it walks `KeystoneFaunaRegistry.Entries` once and filters
to the visited cluster. Registry sizes run to a couple hundred at most,
the per-frame visit cap is small, and the live count is therefore always
fresh — no risk of drift between a cached count and reality after a
fauna dies via a path the cache wasn't watching (terrain edit, region
invalidation, vanilla cleanup). See the global "don't cache derived
state whose source changes outside your observation" rule.

## Files

### Spawn pipeline

- `FaunaCycleTicker.cs` — `RollingSweepTicker<ChunkClusterId>` that
  visits every cluster every 6 game-hours. Per visit: builds buckets by
  `levelId`, computes capacity from the cluster's hyperbolic score,
  culls surplus off-screen entries via `CullOldestOffscreen`, enqueues
  clusters with deficit. Also subscribes to `EntityDeletedEvent` to
  keep the registry in step with the world.
- `FaunaSpawnQueue.cs` — FIFO worklist of cluster ids, dedup'd by
  membership set. Stale ids (cluster index rebuilt between enqueue and
  drain) are tolerated; the drainer filters them at visit time.
- `FaunaSpawnDrainer.cs` — `IUpdatableSingleton` that pops a few
  clusters per frame, recomputes per-bucket deficit from a fresh
  registry walk, and instantiates one fauna into the largest-deficit
  bucket on an off-frustum qualifying chunk. Camera-blocked clusters
  re-enter the queue; filled clusters are dropped (sweep re-enqueues
  if their deficit grows again).
- `FaunaFrustumFilter.cs` — shared `IsInFrustum(TileCoord, z)` helper
  used by both ticker (cull side) and drainer (spawn side). Wraps
  `Camera.main.WorldToViewportPoint` with a small edge margin so pops
  at the screen border are also avoided. Defensive "in-frustum" when
  no main camera is available — defer rather than risk an unhidden pop.

### Registry and per-agent components

- `KeystoneFaunaRegistry.cs` — singleton tracking live Keystone fauna.
  Flat list of `Entry { Entity, Position, BlueprintName, Sequence,
  PersistsOvernight }`. Sequence is monotonic across the session so
  cull-by-oldest is a simple sort. Position is read live via the
  agent's `IFaunaPositioning` — the registry doesn't re-sync as fauna
  walk. No save/load; loaded games start empty and the sweep refills
  within one cycle of game time.
- `IFaunaPositioning.cs` — read-only `(Region, CurrentTile)` handle
  that both land and aquatic agents expose, so cluster resolution
  doesn't branch on agent type.
- `BaseFaunaAgent.cs` — shared base for both agent kinds. Owns
  position state, the periodic cluster-affinity self-check
  (`CheckClusterAffinityAndStaysAlive`), and the `ConfigureFromRecipe`
  entry point the drainer calls without knowing the concrete type.
- `KeystoneFaunaAgent.cs` / `KeystoneFaunaAgentSpec.cs` — land-fauna
  wander/idle state machine. Walks waypoints via Timberborn's `Walker`.
- `KeystoneAquaticAgent.cs` / `KeystoneAquaticAgentSpec.cs` — aquatic
  continuous-swim agent (no idle state). `PersistsOvernight` so fish
  keep swimming through the night.
- `KeystoneFaunaAnimator.cs` / `KeystoneFaunaAnimatorSpec.cs` —
  VAT-driven `IAnimator` implementation; keeps animator Speed in sync
  with the game-speed slider via `CurrentSpeedChangedEvent`.
- `FaunaTopologyChangeWatcher.cs` — subscribes to `BlockObjectSetEvent`
  and `ITerrainService.TerrainHeightChanged`; precautionarily evicts
  fauna from columns where the terrain just changed via
  `KeystoneFaunaRegistry.DespawnAnyAtColumn`.

### Dev tools

- `FaunaPlacementTool.cs` — dev placement tool: drops a single fauna
  at the cursor via the active fauna recipe.
- `FishSmokeTestTool.cs` — aquatic-agent equivalent for fish.

## Adding a new fauna species

1. Author the mesh: Quaternius `.blend` → Timbermesh export → bake
   VAT clips → drop into `unity-assets/Keystone/AssetBundles/`. See
   `docs/timberborn-api.md` § "Custom mesh authoring." Material name
   must be unique — a collision overwrites the original's VAT param
   bindings at load time.
2. Add the `.mat` to `MaterialCollection.Keystone.blueprint.json` —
   bundling alone is not enough; `GetMaterial` returns null otherwise.
3. Drop the blueprint JSON next to the existing fauna blueprints.
4. Add a Class E recipe entry to whichever level/biome it belongs to
   in the biome-level table. The recipe carries `Weight`,
   `LowerMaturity`, and the bucket's `FaunaCapacityAtSaturation` /
   `FaunaMinScore` come from the level.

The spawn pipeline picks it up automatically on the next sweep cycle.
