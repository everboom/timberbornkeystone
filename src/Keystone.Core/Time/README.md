# Keystone.Core.Time

Game-cycle-aware time primitives. Used wherever Keystone needs to
record when something happened (region creation timestamps, eco-health
history, sub-zone change events) in a form that survives save/reload.

## Why not raw ticks or wall-clock time?

The DESIGN principle is that anything time-stamped must replay
deterministically across save/load. Wall-clock time fails by definition
(it advances when the game is paused). A locally-incremented "tick
counter" would fail unless we manually saved and restored it.

Timberborn's `GameCycleService` already persists `Cycle`, `CycleDay`,
and `PartialCycleDay` as part of the save format. Tying our timestamp
to those fields means save/reload round-trips for free.

## Types

- `GameTimestamp(Cycle, CycleDay, PartialCycleDay)` — the canonical
  timestamp.
- `WeatherKind` — `Temperate` / `Drought` / `Badtide`. Useful as
  ecology context (e.g., "this region was first observed during a
  drought").
- `IClock` — port over `GameTimestamp Now` + `WeatherKind CurrentWeather`
  + `float TotalDaysElapsed`. The `TotalDaysElapsed` reading forwards to
  Timberborn's `IDayNightCycle.PartialDayNumber` and gives consumers a
  flat monotonic real-time anchor for math that the structured cycle/day
  pair makes awkward (dt computation, age accumulators). Adapter lives in
  `Keystone.Mod.Adapters.GameClockAdapter`.
