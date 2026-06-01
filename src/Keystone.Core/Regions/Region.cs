using System;
using System.Collections.Generic;
using Keystone.Core.Time;

namespace Keystone.Core.Regions {

  /// <summary>
  /// A structural region (plateau): a 4-connected component of surfaces
  /// that share <c>(Z, IsCave)</c>. The unit of identity Keystone uses
  /// for everything region-shaped -- biome classification, eco-health
  /// integration, fauna spawning, inter-region connectivity.
  ///
  /// <para>Identity is structural: a region's
  /// <see cref="Z"/> + <see cref="IsCave"/> + member set is determined
  /// purely by terrain shape and roofing. Sub-zone (moisture,
  /// contamination, irrigation, plant composition) is layered on top
  /// and tracked separately, since those refresh on a different
  /// cadence.</para>
  ///
  /// <para><b>No member list lives on the region.</b> The single source
  /// of truth for "which surfaces belong to which region" is
  /// <c>RegionService._surfaceToRegion</c>. The region carries
  /// <see cref="Size"/> as a maintained counter (incremented on attach,
  /// decremented on detach) so callers don't have to scan to know how
  /// big the region is, but the actual member enumeration goes through
  /// <c>RegionService.SurfacesInRegion</c> when needed (debug UI,
  /// split-detection orphan finding).</para>
  ///
  /// <para>State that needs to follow region lifecycle (eco-health,
  /// fauna populations, etc.) will live on the region as additional
  /// fields once Chunk D defines the split/merge contract.</para>
  /// </summary>
  public sealed class Region {

    #region Properties

    /// <summary>Stable identifier, persisted across event-driven updates that don't dissolve the region.</summary>
    public RegionId Id { get; }

    /// <summary>The surface Z all members share.</summary>
    public int Z { get; }

    /// <summary>Whether all members are cave surfaces (have terrain above) or all open.</summary>
    public bool IsCave { get; }

    /// <summary>Whether all members are part of player-placed infrastructure (a building above, or a path adjacent to a building).</summary>
    public bool IsSettled { get; }

    /// <summary>
    /// Number of surfaces currently in this region. Maintained
    /// incrementally by <c>RegionService</c> via attach/detach. Does not
    /// require a member-list scan.
    /// </summary>
    public int Size { get; internal set; }

    /// <summary>
    /// Game timestamp at which this region was first observed. Setter is
    /// internal so the persistence subsystem (<c>KeystonePersistence</c>
    /// via <see cref="RegionService.RestoreCreatedAt"/>) can rehydrate
    /// the stamp after a freshly-Indexed reload reassigns the same
    /// deterministic id. Regular consumers should treat this as
    /// read-only -- writing through it outside of the load path will
    /// invalidate everything keyed on creation order.
    /// </summary>
    public GameTimestamp CreatedAt { get; internal set; }

    /// <summary>Weather phase active at <see cref="CreatedAt"/>. Setter is internal for the same reason as <see cref="CreatedAt"/>'s.</summary>
    public WeatherKind WeatherAtCreation { get; internal set; }

    /// <summary>
    /// Absolute day-count (continuous monotonic real time) at the moment
    /// this region was first observed. Captured from <see cref="IClock.TotalDaysElapsed"/>
    /// at construction; persisted alongside <see cref="CreatedAt"/> and
    /// rehydrated via <see cref="RegionService.RestoreCreatedAt"/>.
    ///
    /// <para>Useful when a derivation needs a flat float anchor rather
    /// than the structured cycle/day pair (subtractions, ratios). The
    /// score-store age accumulator does not depend on this -- it ticks
    /// independently -- but consumers that build their own time-based
    /// scores from scratch on load can subtract this from
    /// <see cref="IClock.TotalDaysElapsed"/>.</para>
    /// </summary>
    public float TotalDaysAtCreation { get; internal set; }

    /// <summary>
    /// State values attached to this region, keyed by their concrete
    /// runtime type. <see cref="RegionService"/> drives split/merge
    /// transformations through <see cref="IRegionState"/>; consumers
    /// access values via <see cref="GetState{T}"/> / <see cref="SetState{T}"/>.
    /// Internal because RegionService manages the dictionary directly
    /// during merge state-collapse.
    /// </summary>
    internal Dictionary<Type, IRegionState> States { get; } = new();

    /// <summary>
    /// Region ids this region shares a topological border with. The
    /// relation is symmetric (<c>A.Neighbors</c> contains <c>B.Id</c>
    /// iff <c>B.Neighbors</c> contains <c>A.Id</c>) and undirected.
    /// Two regions are neighbors when any pair of their member surfaces
    /// is 4-laterally adjacent at the same Z, vertically adjacent across
    /// a 1-voxel cliff in a cardinal direction, or 1-voxel-cliff
    /// diagonally adjacent <i>only</i> when both of the diagonal's
    /// constituent cardinal columns are empty in the ±1 Z window. The
    /// diagonal-fallback handles diagonal staircases without
    /// double-linking plateaus that already have richer cardinal
    /// connectivity. Wild ↔ settled boundaries are <b>not</b> linked --
    /// settled regions are walled off from the eco graph entirely.
    ///
    /// <para><b>Conservative on splits.</b> When a region splits, both
    /// pieces inherit the parent's neighbor set unchanged. This means
    /// <c>Neighbors</c> can be a superset of true neighbors (stale
    /// edges where the boundary actually fell on the other piece). Stale
    /// entries self-prune as the world evolves -- the migration-eval
    /// layer enumerates real boundary edges on demand and finds none
    /// for stale entries. If profiling shows bloat, a periodic GC pass
    /// can be added later.</para>
    /// </summary>
    public IReadOnlyCollection<RegionId> Neighbors => _neighbors;

    /// <summary>Mutable handle for <see cref="RegionService"/>; not exposed externally.</summary>
    internal HashSet<RegionId> NeighborsMutable => _neighbors;

    private readonly HashSet<RegionId> _neighbors = new();

    #endregion

    #region State access

    /// <summary>
    /// Read the state value of type <typeparamref name="T"/> on this
    /// region, or <c>null</c> if no value of that type is attached.
    /// </summary>
    public T? GetState<T>() where T : class, IRegionState =>
        States.TryGetValue(typeof(T), out var v) ? (T)v : null;

    /// <summary>
    /// Attach or replace the state value of type <typeparamref name="T"/>.
    /// Subsequent split/merge operations will route through this value's
    /// <see cref="IRegionState.ForChildOnSplit"/> and
    /// <see cref="IRegionState.Absorbing"/> implementations.
    /// </summary>
    public void SetState<T>(T value) where T : IRegionState {
      States[typeof(T)] = value;
    }

    /// <summary>True iff a state of type <typeparamref name="T"/> is attached.</summary>
    public bool HasState<T>() where T : IRegionState =>
        States.ContainsKey(typeof(T));

    #endregion

    #region Construction

    /// <summary>Construct a region. Callers (RegionService) are responsible for keeping <see cref="Size"/> in sync.</summary>
    internal Region(
        RegionId id,
        int z,
        bool isCave,
        bool isSettled,
        int size,
        GameTimestamp createdAt,
        WeatherKind weatherAtCreation,
        float totalDaysAtCreation) {
      Id = id;
      Z = z;
      IsCave = isCave;
      IsSettled = isSettled;
      Size = size;
      CreatedAt = createdAt;
      WeatherAtCreation = weatherAtCreation;
      TotalDaysAtCreation = totalDaysAtCreation;
    }

    #endregion

  }

}
