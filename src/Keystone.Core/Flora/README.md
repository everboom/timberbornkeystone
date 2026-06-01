# Keystone.Core.Flora

Distilled per-blueprint registry of the flora present in the running
game -- vanilla plus whatever mods are loaded. Built once at mod load
by the Mod-side `FloraCatalogLoader` walking
`ISpecService.GetSpecs<NaturalResourceSpec>()`; consumed thereafter by
ecology rules that need to reason about what's available
(eco-pressure, biome classification, biotic spawning).

## Types

- `FloraKind` (enum): `Tree` / `Bush` / `Crop` / `GroundCover` --
  coarse stratum bucket derived from `TreeComponentSpec` / `BushSpec`
  / `CropSpec` markers, with `GroundCover` as the fallback when none
  apply (mushrooms, dandelions, decorative naturals).
- `FloraEntry` (sealed class): per-blueprint signature -- growth
  time, water tolerance, yield, kind. Most fields are nullable
  because not every flora carries every spec; `null` means "this
  blueprint doesn't expose that signal," which is meaningfully
  different from "value is zero."
- `FloraCatalog` (sealed class): name -> entry lookup, populated
  once via `Populate(...)`.

## Why a runtime catalog

Other mods can rename, rebalance, or add flora freely. Hard-coding
"Pine grows in 8 days" would lock Keystone to vanilla and break
silently when a mod ships a "PineFast" with growth=2. The runtime
catalog gives ecology rules the world as it actually is. Consumers
should reason on relative scales (within the catalog's distribution)
rather than absolute thresholds, so the mod adapts to whatever
flora population happens to be loaded.

## Discriminator

Membership is "blueprint has `NaturalResourceSpec`" -- the broadest
"map-spawned biotic element" signal in Timberborn. That includes
trees, bushes, mushrooms, ground cover, cattails, and crops (crops
carry `NaturalResourceSpec` even though they spawn into farmhouses
rather than onto the map; their alive-counts in the catalog typically
read 0).
