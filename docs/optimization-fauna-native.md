# Keystone native fauna performance optimizations

Port of the proven *intent* of the TB Optimizer Keystone adapters into Keystone itself. Keeping this patch separate from the optional Miners of Mountains compatibility change allows review and rollback independently.

## FaunaSpawnDrainer.cs

1. **Bound actual fauna spawn work to one per update.** `MaxSpawnsPerFrame` is reduced from 2 to 1. This is a native equivalent to TB Optimizer's `FaunaSpawnBudgetPatcher`, without the external Harmony skip-and-pretend-success flow. The existing drainer handles real re-enqueuing after successful spawns. **This changes the replenishment rate by design.** No logging or benchmark probes are introduced.
2. **Cache successful Class E recipe lookups.** `FindRecipeForBlueprint` builds a lazy per-drainer dictionary indexed by blueprint name rather than scanning `_catalog.AllClassE` for every registered fauna during every cluster visit. The dictionary retains the first duplicate, preserving the original loop's behavior. It is scoped to the drainer and only caches non-null blueprint names. Keystone's catalog is loaded before this updater executes and treated as immutable during a session. Should future hot-reload support be added, invalidate/rebuild the lookup then.

## Separate external optimization not migrated

TB Optimizer also includes `KeystoneFaunaTemplatePrewarmer` (Cow/Bull/Deer). It uses global Harmony patches into Timberborn's template-instantiation startup path. **Do not copy it blindly** into Keystone's native lifecycle: it must be reworked against Keystone's current startup order and verified not to trigger heavy synchronous prewarming during loading or duplicate template caches. The current PR focuses on the bounded drainer and verified native recipe path. This is a documented follow-up, not a claimed implemented change.

## Regression matrix

- Benchmark peak frame time and long-tail spikes before and after on the same save at x1 and x3 game speeds; record FPS alongside speed.
- Verify real instantiations never exceed one during a single `FaunaSpawnDrainer.UpdateSingleton` invocation.
- Verify deficits remain queued/replenish, no starvation after save/load, camera blocked clusters behave as before.
- Check fish/cattle/deer and all Class E names resolve; benchmark against full catalog on duplicate-blueprint recipes.
- Test with TB Optimizer external Keystone patches **disabled** to avoid double instrumentation/behavior changes.
- Check against Keystone and Timberborn 1.1 with a real compilation and gameplay test before releasing.

**Status:** source changes committed; no compiled binary or fresh in-game verification in this PR.
