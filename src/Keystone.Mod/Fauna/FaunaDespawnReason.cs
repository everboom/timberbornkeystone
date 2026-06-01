namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Categorical reason for a fauna entry leaving the registry,
  /// used by the activity panel's despawn-reason histogram to
  /// distinguish "homeless after cycle" from "stuck on a tile" from
  /// "vanilla cleanup destroyed the entity."
  ///
  /// <para><b>Diagnostic-only.</b> No simulation logic branches on
  /// this value — it exists so the panel can answer "why are fauna
  /// disappearing" at a glance. Add new categories liberally; the
  /// recorder just tallies whatever the registry stamps and the
  /// panel renders the distribution.</para>
  /// </summary>
  public enum FaunaDespawnReason {

    /// <summary>Caller passed no reason. Catch-all; spotting many
    /// of these means a despawn path is missing instrumentation.</summary>
    Unknown,

    /// <summary>Hourly self-check: agent's chunk resolved to no
    /// cluster at all (<c>ChunkClusterIndex.ClusterFor</c> returned
    /// null). Either the cluster lost the chunk (Maturity dipped,
    /// region re-keyed) or the rebuild excluded this region.</summary>
    ClusterUnknown,

    /// <summary>Hourly self-check: agent's chunk resolved to a
    /// cluster, but that cluster's dominant biome is not in the
    /// agent's accepted set. The agent has either wandered into a
    /// different biome's cluster, or its home chunk's dominance
    /// flipped under it.</summary>
    ClusterBiomeRejected,

    /// <summary>Hourly self-check: the agent's stored
    /// <c>Region</c> was null. Shouldn't happen during normal play —
    /// flag if seen.</summary>
    ClusterRegionNull,

    /// <summary>Hourly stuck check: the agent's current tile no
    /// longer satisfies its walkability filter (biome maturity
    /// dropped, terrain edited under the agent, etc.).</summary>
    StuckUnwalkableTile,

    /// <summary>Hourly stuck check: the agent has not successfully
    /// started a walk in the stuck window. Either the chance-to-walk
    /// gate kept rolling against it, or every destination pick / path
    /// search failed.</summary>
    StuckNoSuccessfulWalk,

    /// <summary>Aquatic-only: per-frame water-depth gate found the
    /// fauna's tile below the spec's MinWaterDepth (water drained
    /// out from under it).</summary>
    AquaticTooShallow,

    /// <summary>End-of-cycle homeless cull in
    /// <see cref="FaunaCycleTicker"/>. Cluster resolution returned
    /// null for the entry at both cycle start and cycle end, and
    /// the entry was off-frustum. Should be rare on a stable map;
    /// a steady stream here points at the cluster-index seam.</summary>
    HomelessAfterCycle,

    /// <summary>Per-cluster surplus cull in
    /// <see cref="FaunaCycleTicker"/>: live count exceeded capacity
    /// for a bucket, oldest entries culled until inside the cap.</summary>
    SurplusCull,

    /// <summary>Terrain edit at the fauna's column — player dug or
    /// filled. See <c>FaunaTopologyChangeWatcher.OnTerrainHeightChanged</c>.</summary>
    TerrainEdited,

    /// <summary>Building placed on the fauna's column. See
    /// <c>FaunaTopologyChangeWatcher.OnBlockObjectSet</c>.</summary>
    BlockObjectPlaced,

    /// <summary>Entity was destroyed outside Keystone's despawn paths
    /// (vanilla cleanup, region invalidation, scene unload). Came in
    /// via the <c>EntityDeleted</c> event handler that calls
    /// <see cref="KeystoneFaunaRegistry.Forget"/>. Distinct from the
    /// Keystone-initiated paths above because the registry-side
    /// Despawn / DespawnAnyAtColumn paths remove the entry first,
    /// so the subsequent EntityDeleted → Forget call no-ops without
    /// double-counting.</summary>
    ExternalEntityDeleted,

    /// <summary>Player flipped the master fauna toggle off mid-game.
    /// Bypasses the surplus-cull's frustum gating — visible pop is
    /// the intended affordance ("turn it off and see it stop"). Fires
    /// once on the true→false edge of
    /// <c>KeystoneFaunaSettings.EnableFauna</c>.</summary>
    MasterToggleOff,

  }

}
