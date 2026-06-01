# Keystone.Core.Regions

Structural regions (plateaus) and the service that builds them. Regions
are Keystone's primary unit of identity: anything that aggregates
across surfaces (biome classification, eco-health, fauna spawning,
inter-region connectivity) hangs off a region.

## Identity rule

A region is a 4-connected component of surfaces sharing
`(Z, IsCave, IsSettled)`. Three structural splitting axes: surfaces at
different Z, surfaces with different cave status, or surfaces with
different settled status (player-placed infrastructure vs natural)
never end up in the same region. All three are event-trackable
(terrain edits, overhang changes, building placement / demolition) and
belong on the structural side of the structure-vs-state line in
`DESIGN.md`'s performance section.

## What lives on a Region

Right now: id, Z, IsCave, IsSettled, member size counter, creation
timestamp, weather at creation. That's enough to answer "where does
this surface belong", "how big is this plateau", "how old is it",
"is this player infrastructure or natural ground".

Eventually (1A-C / 1A-D): inter-region edge data (shared boundary
length, Z-delta distribution per edge), sub-zone caches (irrigated /
contaminated / planted breakdowns within the region).

## Indexing model

`RegionService.Index()` runs a full-map flood-fill, assigning a
monotonic `RegionId` per discovered region. The Mod-side `RegionUpdater`
then keeps the index live via incremental updates: terrain edits and
block-object set/unset events feed `ApplyColumnDiff` / per-surface
attach + detach calls that grow regions, shrink them, split them, and
merge them without re-flooding the whole map. `RegionSplit`,
`RegionMerged`, and `RegionRemoved` events fire so `RegionValueStore` can
carry its per-region accumulated state through topology changes (via
`RegionValueLifecycleHandler`); the per-chunk stores follow topology
spatially through `ChunkReconciler` instead, not via these events.

## IRegionState

The `IRegionState` interface gives consumers a place to attach
extensive (counts; split-by-size, merge-sum) or intensive (qualities;
inherit on split, weighted-average on merge) data that needs to follow
region lifecycle. Today's per-region stores skip the interface and
handle inheritance / merge directly via `Inherit` / `MergeFrom`
methods; `IRegionState` is sketched for richer Phase 2 use.
