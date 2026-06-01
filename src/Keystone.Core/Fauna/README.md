# Fauna (Core)

Pure-logic fauna primitives. No Unity / Bindito / Timberborn refs.
Mod-side adapters wire the production data sources in.

## Files

- `FaunaPathfinder.cs` — static A* + line-of-sight smoothing over a
  single region, queried via `IRegionTopologyQuery`
  (`Core/Ports/`). 4-connected grid, region-bounded, no path
  caching. Used by `Keystone.Mod.Fauna.KeystoneFaunaAgent`.

The agent state machine itself, the spawner, and the recipe-class
plumbing for fauna recipes (Class E) live in `Keystone.Mod` for
now — Core only owns logic that doesn't need a game-loop heartbeat.
