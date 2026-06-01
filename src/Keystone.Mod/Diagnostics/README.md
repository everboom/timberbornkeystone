# Keystone.Mod.Diagnostics

Mod-side instrumentation. Companion to `Keystone.Core.Diagnostics`
(which holds the pure-data `PerfStats` ring buffer).

## Pieces

| Type | Role |
|---|---|
| `PerfTracker` | Central dispatcher for per-scope timing. Subsystems inject this and wrap work-doing methods with `using var _ = perf.Track("name")`; the returned struct-typed scope records elapsed time on `Dispose`. Allocation-free in steady state -- only on first encounter of a new name (lazy `PerfStats` allocation). Single-threaded by design (Bindito singletons run on the Unity main thread). Also exposes `RecordOnce(label, ms)` for load-time costs that fire exactly once per session; those are surfaced separately from the rolling per-scope stats. For non-time samples (unit counts — "chunks drained this tick", "classify calls this flush") use `RecordCount(name, count)`: it tags the scope's `PerfStats.Kind` as `Counter` so the window renders it in a separate count-labeled table and keeps it out of the millisecond cost aggregates. Mixing a count into `Record`/`Track` would mislabel it as ms and inflate the headline total. |
| `KeystonePerfWindow` | Non-modal floating overlay (Alt+Shift+K toggles). Renders the rolling-sweep per-scope table (samples, avg, P99, max, frequency, headline ms/sec total) plus the one-shot startup section from `PerfTracker.OneShots`. Mounted via `RootVisualElementProvider.CreateEmpty` rather than TimberUi's dialog stack so it doesn't gate game input. Draggable by its header bar. |
