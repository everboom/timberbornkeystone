using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Shared base for Keystone fauna agent components. Owns:
  /// <list type="bullet">
  ///   <item>Position state — <see cref="Region"/>,
  ///         <see cref="CurrentTile"/> — and exposure of both via
  ///         <see cref="IFaunaPositioning"/> for the registry /
  ///         dawn-reconciler.</item>
  ///   <item>The periodic cluster self-check at game-hour cadence:
  ///         every <see cref="ClusterCheckIntervalDays"/> in-game
  ///         days the agent re-resolves its current chunk → cluster
  ///         and despawns itself when the cluster's biome no longer
  ///         matches what <see cref="IsBiomeAccepted"/> calls home.</item>
  ///   <item>The recipe-driven configure entry point
  ///         (<see cref="ConfigureFromRecipe"/>) the dawn handler
  ///         calls without knowing the concrete agent type.</item>
  /// </list>
  ///
  /// <para>Concrete agents (<see cref="KeystoneFaunaAgent"/>,
  /// <see cref="KeystoneAquaticAgent"/>) implement movement / state
  /// machine / animation specifics and override the abstract hooks.
  /// They invoke <see cref="CheckClusterAffinityAndStaysAlive"/> at
  /// the top of their <see cref="IUpdatableComponent.Update"/>
  /// implementations; on a check failure the agent has already been
  /// deleted via <see cref="EntityService"/> and the caller should
  /// bail before touching any other state.</para>
  ///
  /// <para><b>Game-time, not real-time.</b> The cluster check uses
  /// <see cref="IClock.TotalDaysElapsed"/>, so the cadence stays at
  /// one in-game hour regardless of game-speed slider position.</para>
  /// </summary>
  public abstract class BaseFaunaAgent : BaseComponent,
                                         IAwakableComponent,
                                         IUpdatableComponent,
                                         IFaunaPositioning {

    /// <summary>Cluster self-check cadence, in in-game days.
    /// <c>1/24</c> = once per game-hour, the cadence the user
    /// specified when this check was introduced. Anchored to
    /// <see cref="IClock.TotalDaysElapsed"/> so 3× game-speed still
    /// fires three in-game hours' worth of checks in the same
    /// real-time window.</summary>
    protected const float ClusterCheckIntervalDays = 1f / 24f;

    #region Bindito-injected

    private readonly IClock _clock;
    private readonly ChunkClusterIndex _clusterIndex;
    private readonly EntityService _entityService;
    private readonly KeystoneFaunaRegistry _registry;

    #endregion

    #region Per-instance state

    /// <summary>The region the agent was configured into. Treated as
    /// constant for the agent's lifetime (fauna don't traverse
    /// regions; they get culled if they end up off-region).</summary>
    protected Region? Region { get; set; }

    /// <summary>The agent's current tile. Concrete subclasses update
    /// this as they advance through path waypoints.</summary>
    protected TileCoord CurrentTile { get; set; }

    /// <summary><see cref="IClock.TotalDaysElapsed"/> at which the
    /// next cluster check fires. Initialised to <c>0</c> in
    /// <see cref="ScheduleFirstClusterCheck"/> so a freshly-
    /// configured agent gets checked on the next Update tick.</summary>
    private float _nextClusterCheckAtDay;

    /// <summary>Once the agent self-despawns we mark it disabled so
    /// any subsequent Update calls (in-flight before Unity tears the
    /// entity down) bail out cleanly.</summary>
    protected bool Disabled { get; private set; }

    #endregion

    protected BaseFaunaAgent(
        IClock clock,
        ChunkClusterIndex clusterIndex,
        EntityService entityService,
        KeystoneFaunaRegistry registry) {
      _clock = clock;
      _clusterIndex = clusterIndex;
      _entityService = entityService;
      _registry = registry;
    }

    public abstract void Awake();
    public abstract void Update();

    Region? IFaunaPositioning.Region => Region;
    TileCoord IFaunaPositioning.CurrentTile => CurrentTile;

    /// <summary>Whether agents of this species survive
    /// <see cref="KeystoneFaunaRegistry.DespawnAll"/> at dusk. Defaults
    /// false: most fauna (land grazers in v1) spawn at dawn and
    /// despawn at dusk -- the ephemeral lifecycle inherited from the
    /// pre-cluster design. Aquatic species override to <c>true</c>;
    /// fish are continuous-population, with the dawn capacity-reconcile
    /// pass alone managing their numbers.
    /// <para>Save/load still wipes the registry regardless. Persistent
    /// species re-populate via the next dawn's capacity reconcile (the
    /// pass spawns up to <c>capacity − existing</c>, and on a fresh load
    /// <c>existing == 0</c>).</para></summary>
    public virtual bool PersistsOvernight => false;

    /// <summary>Recipe-driven configure entry point. The dawn
    /// handler calls this on the base type without branching on the
    /// concrete agent class; subclasses dispatch to their own
    /// concrete <c>Configure</c> with whichever fields they
    /// need.</summary>
    /// <param name="animator">Animator component on the same entity,
    /// or <c>null</c> when the entity has no
    /// <see cref="KeystoneFaunaAnimator"/>.</param>
    /// <param name="region">Region the spawn tile belongs to.</param>
    /// <param name="initialTile">Spawn tile.</param>
    /// <param name="recipe">The Class E recipe that produced this
    /// spawn. Carries the home biome (used by
    /// <see cref="IsBiomeAccepted"/> on land agents).</param>
    /// <param name="level">The biome level the recipe is registered
    /// against; used by land agents to set the per-instance Maturity
    /// gate on the walkability filter.</param>
    public abstract void ConfigureFromRecipe(
        KeystoneFaunaAnimator? animator,
        Region region,
        TileCoord initialTile,
        Keystone.Core.Biomes.ClassERecipe recipe,
        BiomeLevel level);

    /// <summary>Called by concrete <see cref="Configure"/>
    /// implementations after they've set <see cref="Region"/> and
    /// <see cref="CurrentTile"/>; schedules the first cluster check
    /// to fire on the next Update tick (catches mis-placed dev-
    /// spawned agents immediately).</summary>
    protected void ScheduleFirstClusterCheck() {
      _nextClusterCheckAtDay = 0f;
    }

    /// <summary>Run the periodic cluster-affinity check if due.
    /// Returns <c>true</c> if the agent should keep running (either
    /// the interval hasn't elapsed or the check passed); returns
    /// <c>false</c> after self-despawning, in which case the calling
    /// <see cref="IUpdatableComponent.Update"/> must bail out
    /// immediately.</summary>
    protected bool CheckClusterAffinityAndStaysAlive() {
      if (Disabled) return false;
      var nowDay = _clock.TotalDaysElapsed;
      if (nowDay < _nextClusterCheckAtDay) return true;
      _nextClusterCheckAtDay = nowDay + ClusterCheckIntervalDays;
      var clusterFailure = ClassifyClusterAffinity();
      if (clusterFailure.Reason.HasValue) {
        DespawnSelf(clusterFailure.Message, clusterFailure.Reason.Value);
        return false;
      }
      var stuckCategory = GetStuckCategory(nowDay);
      if (stuckCategory.Reason.HasValue) {
        DespawnSelf(stuckCategory.Message, stuckCategory.Reason.Value);
        return false;
      }
      return true;
    }

    /// <summary>Categorical cluster-affinity classification. Returns
    /// a (reason, message) pair when the agent should despawn —
    /// distinguishing "region null" from "chunk has no cluster" from
    /// "cluster's biome not accepted" so the panel histogram tells us
    /// which path is firing. Reason is null iff the agent is home.</summary>
    private (FaunaDespawnReason? Reason, string Message) ClassifyClusterAffinity() {
      if (Region == null) {
        return (FaunaDespawnReason.ClusterRegionNull,
            $"hourly check: Region is null (agent never configured or post-region-invalidation)");
      }
      var chunkX = CurrentTile.X / RegionEcologyField.ChunkSize;
      var chunkY = CurrentTile.Y / RegionEcologyField.ChunkSize;
      var id = _clusterIndex.ClusterFor(Region.Id, chunkX, chunkY);
      if (id == null) {
        return (FaunaDespawnReason.ClusterUnknown,
            $"hourly check: tile {CurrentTile} (chunk {chunkX},{chunkY}) in region {Region.Id} not in any cluster");
      }
      var biome = _clusterIndex.BiomeFor(id.Value);
      if (!biome.HasValue || !IsBiomeAccepted(biome.Value)) {
        return (FaunaDespawnReason.ClusterBiomeRejected,
            $"hourly check: tile {CurrentTile} in cluster with biome {biome?.ToString() ?? "<none>"}, not in accepted set");
      }
      return (null, "");
    }

    /// <summary>Subclass hook for the hourly stuck check. Returns a
    /// (category, message) pair when the agent should despawn for a
    /// positional stuck condition; <c>(null, "")</c> to stay alive.
    /// Concrete agents delegate to their planner / state machine —
    /// see <c>KeystoneFaunaAgent.GetStuckCategory</c>.</summary>
    protected virtual (FaunaDespawnReason? Reason, string Message) GetStuckCategory(float nowDay)
        => (null, "");

    /// <summary>Current game-day from the injected
    /// <see cref="IClock"/>. Exposed to subclasses so they can stamp
    /// "last successful X at Y day" timers for use in
    /// <see cref="GetStuckReason"/>.</summary>
    protected float NowDay => _clock.TotalDaysElapsed;

    /// <summary>Delete this entity via <see cref="EntityService"/>
    /// and mark the agent <see cref="Disabled"/> so any subsequent
    /// Update tick bails out cleanly. Logs the supplied reason.
    /// Concrete agents call this from their own per-frame checks
    /// (water depth on aquatic, etc.) on top of the base-class
    /// cluster-affinity check.</summary>
    protected void DespawnSelf(string reason)
        => DespawnSelf(reason, FaunaDespawnReason.Unknown);

    protected void DespawnSelf(string reason, FaunaDespawnReason category) {
      if (Disabled) return;
      KeystoneLog.Verbose($"[Keystone] {GetType().Name} on '{Name}': despawning — {reason}.");
      Disabled = true;
      // Route through the registry so the despawn-reason tally
      // increments. Despawn handles both the registry removal and
      // the EntityService.Delete call; the entity-deleted event
      // fires next, but Forget no-ops since the entry is already
      // gone — no double-count.
      _registry.Despawn(GetComponent<EntityComponent>(), category);
    }

    /// <summary>True iff the agent's current chunk is in a cluster
    /// whose biome the subclass calls home. False on missing region,
    /// missing cluster, or biome rejection.</summary>
    private bool IsInHomeCluster() {
      if (Region == null) return false;
      var chunkX = CurrentTile.X / RegionEcologyField.ChunkSize;
      var chunkY = CurrentTile.Y / RegionEcologyField.ChunkSize;
      var id = _clusterIndex.ClusterFor(Region.Id, chunkX, chunkY);
      if (id == null) return false;
      var biome = _clusterIndex.BiomeFor(id.Value);
      return biome.HasValue && IsBiomeAccepted(biome.Value);
    }

    /// <summary>Subclass-specific predicate: does this agent
    /// consider <paramref name="biome"/> a valid home? Land fauna
    /// match a single configured biome; aquatic fauna accept a
    /// set.</summary>
    protected abstract bool IsBiomeAccepted(BiomeKind biome);

  }

}
