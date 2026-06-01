# Keystone.Core.Tiles

Engine-agnostic coordinate primitives.

## Pieces

| Type | Role |
|---|---|
| `TileCoord` | `(X, Y)` value-typed key for a column. |
| `SurfaceCoord` | `(X, Y, Z)` value-typed key for one voxel-surface within a column. Stacked surfaces in the same column have distinct Z values; the `Column` accessor projects to `TileCoord`. Total order is `X` -> `Y` -> `Z`. |
| `FlowVector` | `(X, Y)` 2D horizontal water-flow vector at a surface. Engine-agnostic counterpart of Timberborn's `UnityEngine.Vector2`. |
| `TileMap<TKey, TValue>` | Sparse dictionary keyed by `TileCoord` or `SurfaceCoord` (or anything else). Used to attach Keystone-side annotations without mutating game state. |

Nothing in this folder may reference Unity, Timberborn, or Bindito.
