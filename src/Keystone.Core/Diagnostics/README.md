# Keystone.Core.Diagnostics

Pure-data perf measurement primitives. The Mod-side dispatcher
(`Keystone.Mod.Diagnostics.PerfTracker`) allocates one of these per
tracked scope.

## Pieces

| Type | Role |
|---|---|
| `PerfStats` | Rolling-window timing stats for a single named scope. Backing store is a fixed-capacity ring buffer of the most recent `Capacity` samples; `Add` is the hot allocation-free path, the stat properties (Average / P99 / Max / FrequencyHz) are cold paths that walk the live portion on demand. |
