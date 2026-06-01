# Keystone.Mod.Survey

Mod-side drivers that keep the Core surveyor + region graph in sync
with live game state.

## Pieces

| Type | Role |
|---|---|
| `KeystoneSurveyor` | `IPostLoadableSingleton`. Owns the one-shot full-map sweep at PostLoad: runs `TerrainSurveyor.Survey()`, then `RegionService.Index()`. Idempotent via `EnsurePostLoaded()` so `KeystonePersistence.PostLoad` can force ordering ahead of stamp rehydration without depending on Timberborn's `OrderingAttribute`. |
| `RegionUpdater` | `ILoadableSingleton + ITickableSingleton`. Subscribes to `ITerrainService.TerrainHeightChanged` and `BlockObjectSetEvent` / `BlockObjectUnsetEvent`, accumulates dirty columns, and flushes on a debounced cadence (quiet-period or max-latency). Per-event work is a HashSet add; per-flush work resurveys affected columns and applies incremental region updates. |

`KeystoneSurveyor.Core` exposes the underlying `TerrainSurveyor` for
other Mod-side singletons that need to read its surface map directly
(notably the field updater and the debug panel).

PostLoad rather than Load because `ITerrainService.MaxTerrainHeight`
is updated by lazy events and isn't valid during the Load phase.
