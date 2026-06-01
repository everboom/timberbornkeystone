using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Keystone.Mod.Settings;
using Keystone.Mod.Sweep;
using Timberborn.EntitySystem;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Per-cluster capacity reconciliation sweep. Decides which clusters
  /// need fauna and which have too many; <i>decision only</i> — the
  /// actual Instantiate calls live in <see cref="FaunaSpawnDrainer"/>.
  /// Splitting decision and execution lets the sweep keep its slow
  /// "match populations to capacity" cadence while spawning is paced
  /// per-frame and gated on the camera frustum, hiding the visible
  /// pop and capping per-frame allocation cost.
  ///
  /// <para><b>Cycle: 6 game-hours (¼ game-day).</b> Each cluster is
  /// visited four times per game-day, smoothing capacity adjustments
  /// against weather / contamination drift without making spawns feel
  /// laggy. Saves and loads no longer carry the "no fauna until next
  /// dawn" stutter — within 6 game-hours of load the sweep has touched
  /// every cluster at least once.</para>
  ///
  /// <para><b>Per visit</b> (<see cref="ProcessUnit"/>): for each
  /// Class E recipe bucket in the cluster's biome, compute capacity
  /// from <c>floor(cluster.Score · FaunaCapacityAtSaturation)</c>
  /// (gated by <c>FaunaMinScore</c>) and compare to the live count
  /// from the cycle-start classification. Surplus → cull immediately
  /// (frustum-gated; on-screen entries skipped, picked up next cycle).
  /// Deficit → enqueue the cluster on <see cref="FaunaSpawnQueue"/>;
  /// the per-frame drainer picks it up, recomputes from current state,
  /// and spawns one off-frustum.</para>
  ///
  /// <para><b>No game-speed gate.</b> Earlier revisions skipped the
  /// sweep at speed ≥ 3 to avoid per-tick Instantiate spikes during
  /// fast-forward. With the drainer now owning instantiation at a
  /// hard per-frame cap, the spike vector is closed structurally and
  /// the gate is gone — populations now converge during fast-forward
  /// too, just at the drainer's controlled rate.</para>
  ///
  /// <para><b>Entity-deletion sync.</b> Subscribes to
  /// <c>EntityDeletedEvent</c> to keep <see cref="KeystoneFaunaRegistry"/>
  /// in step with the world; an entity destroyed via paths Keystone
  /// didn't initiate (region invalidation, vanilla cleanup) gets
  /// dropped from the registry so it's not classified or culled
  /// later as a stale reference.</para>
  /// </summary>
  public sealed class FaunaCycleTicker : RollingSweepTicker<ChunkClusterId>,
                                         ILoadableSingleton,
                                         IUpdatableSingleton {

    #region Constants

    /// <summary>Cycle duration in game-days. 0.25 = 6 game-hours —
    /// four cluster passes per game-day. Lower = faster reaction to
    /// capacity changes (e.g. a freshly matured biome seeds fauna
    /// sooner) but slightly more per-tick noise; higher = smoother
    /// but laggier convergence.</summary>
    private const float CycleDays = 0.25f;

    #endregion

    #region Dependencies

    private readonly EventBus _eventBus;
    private readonly ChunkValueStore _chunkValues;
    private readonly ChunkClusterIndex _clusterIndex;
    private readonly FlourishCatalog _catalog;
    private readonly BiomeLevelTable _levelTable;
    private readonly KeystoneFaunaRegistry _registry;
    private readonly FaunaSpawnQueue _spawnQueue;
    private readonly KeystoneFaunaSettings _settings;

    #endregion

    #region Per-cycle state

    /// <summary>Cycle-start classification: every live registered
    /// fauna assigned to its <c>(cluster, biome, levelId)</c> bucket
    /// based on its current tile. Built fresh at the start of each
    /// cycle; per-visit reads its own cluster's row to compute the
    /// delta. Stale entries (a fauna that wandered into a different
    /// cluster mid-cycle) self-correct next cycle.</summary>
    private Dictionary<(ChunkClusterId Cluster, BiomeKind Biome, string LevelId),
                       List<KeystoneFaunaRegistry.Entry>>? _liveByBucket;

    /// <summary>Fauna whose cycle-start cluster resolution returned
    /// <c>null</c> (no current region, off-cluster chunk, blueprint
    /// not in the catalog). Culled at <see cref="OnCycleComplete"/> —
    /// they have no home.</summary>
    private List<KeystoneFaunaRegistry.Entry>? _homelessAtCycleStart;

    /// <summary>Edge-detection state for the master-fauna-toggle
    /// watcher in <see cref="UpdateSingleton"/>. Starts <c>false</c>;
    /// the first tick where <c>EnableFauna</c> is observed true latches
    /// it to true, and the first tick where it flips back to false
    /// triggers an immediate full cull. Initial-value choice means a
    /// game loaded with the toggle already off won't trigger a spurious
    /// edge on the first tick (the registry would be empty anyway, but
    /// keeping the edge clean simplifies reasoning).</summary>
    private bool _faunaWasEnabledLastTick;

    /// <summary><see cref="ChunkClusterIndex.Version"/> at the moment
    /// <see cref="BuildSchedule"/> captured the cluster ids and the
    /// per-bucket live snapshot. If a rebuild fires mid-cycle (the
    /// schedule is drained over many ticks; biome ticker's own cycle
    /// can fire its rebuild between two of those ticks), every queued
    /// id and every bucket key becomes meaningless — the same numeric
    /// cluster id may now refer to a different cluster with a
    /// different biome and different member entries. <see cref="ProcessUnit"/>
    /// and <see cref="OnCycleComplete"/> check this against the live
    /// version and bail to avoid culling entries that no longer belong
    /// to the cluster the schedule says they were in.</summary>
    private int _scheduleClusterVersion;

    #endregion

    #region Construction

    public FaunaCycleTicker(
        IClock clock,
        PerfTracker perfTracker,
        EventBus eventBus,
        ChunkValueStore chunkValues,
        ChunkClusterIndex clusterIndex,
        FlourishCatalog catalog,
        BiomeLevelTable levelTable,
        KeystoneFaunaRegistry registry,
        FaunaSpawnQueue spawnQueue,
        KeystoneFaunaSettings settings)
        : base(clock, perfTracker, CycleDays) {
      _eventBus = eventBus;
      _chunkValues = chunkValues;
      _clusterIndex = clusterIndex;
      _catalog = catalog;
      _levelTable = levelTable;
      _registry = registry;
      _spawnQueue = spawnQueue;
      _settings = settings;
    }

    /// <inheritdoc />
    public void Load() {
      _eventBus.Register(this);
    }

    #endregion

    #region IUpdatableSingleton

    /// <summary>Per-frame poll for the master-fauna-toggle edge. The
    /// per-cycle sweep already handles "spawn less" via the multiplier
    /// path, but the player's expectation when flipping the master
    /// toggle off is "now," not "after the next 6-game-hour reconcile."
    /// On a true→false edge, despawn every live fauna immediately,
    /// bypassing the surplus-cull's frustum gating — the toggle is a
    /// deliberate user action, so visible pop is the intended
    /// affordance.
    ///
    /// <para>Abundance-slider changes (per-category percentages) are
    /// deliberately NOT instant — those are tuning knobs, and jarring
    /// disappearances on every slider drag would be worse UX than
    /// waiting one in-game cycle for the per-cluster reconcile to
    /// converge.</para>
    ///
    /// <para>Runs every Unity frame regardless of pause state, so the
    /// cull fires immediately even when the game is paused.</para></summary>
    public void UpdateSingleton() {
      var enabledNow = _settings.IsEnabled;
      if (_faunaWasEnabledLastTick && !enabledNow) {
        CullAllOnMasterToggleOff();
      }
      _faunaWasEnabledLastTick = enabledNow;
    }

    /// <summary>Despawn every tracked fauna entry, including aquatic
    /// PersistsOvernight entries, attributed to
    /// <see cref="FaunaDespawnReason.MasterToggleOff"/>. Iterates a
    /// snapshot because <see cref="KeystoneFaunaRegistry.Despawn"/>
    /// mutates the underlying list; iterating in reverse would also
    /// work, but a snapshot is clearer at the cost of one allocation
    /// per toggle event (rare).</summary>
    private void CullAllOnMasterToggleOff() {
      var entries = _registry.Entries;
      if (entries.Count == 0) return;
      var snapshot = new List<KeystoneFaunaRegistry.Entry>(entries);
      for (var i = 0; i < snapshot.Count; i++) {
        _registry.Despawn(snapshot[i].Entity, FaunaDespawnReason.MasterToggleOff);
      }
    }

    #endregion

    #region EventBus

    /// <summary>Keep the registry in sync with the world. Fauna
    /// destroyed by paths Keystone didn't initiate (region
    /// invalidation, vanilla cleanup, scene unload) get forgotten
    /// here so we don't try to <see cref="EntityService.Delete"/>
    /// dangling references later.</summary>
    [OnEvent]
    public void OnEntityDeleted(EntityDeletedEvent e) {
      _registry.Forget(e.Entity);
    }

    #endregion

    #region Sweep hooks

    /// <summary>Skip the sweep entirely when fauna is off and nothing
    /// is left to clean up. Called every frame by the base ticker; we
    /// stay quiet until either the master toggle flips back on or the
    /// registry repopulates (which it can't without our help, but
    /// we check anyway for symmetry).
    ///
    /// <para>When fauna is off but the registry still has entries, the
    /// sweep MUST run — the surplus-cull arm is what tears them down
    /// (frustum-gated, so gradually as the player isn't looking). The
    /// short-circuit only fires once everything's gone.</para>
    ///
    /// <para>Re-enabling: the next frame after the toggle flips back on,
    /// <see cref="ShouldRun"/> returns true and the base ticker fires a
    /// cycle build immediately (cycle anchor is stale by more than the
    /// cycle duration). Populations start filling within seconds of
    /// game-time without waiting for a fresh 6-hour cadence anchor.</para></summary>
    protected override bool ShouldRun() {
      if (!_settings.IsEnabled && _registry.Entries.Count == 0) return false;
      return true;
    }

    /// <inheritdoc />
    protected override void BuildSchedule(List<ChunkClusterId> schedule) {
      _scheduleClusterVersion = _clusterIndex.Version;

      // Defensive shortcut: no fauna recipes registered means no work
      // for any cluster in any biome.
      if (_catalog.AllClassE.Count == 0) {
        _liveByBucket = null;
        _homelessAtCycleStart = null;
        return;
      }

      // Classify all live fauna by (cluster, biome, levelId).
      // BlueprintName → (biome, levelId) is resolved via the catalog's
      // first matching recipe; fauna whose blueprint has no recipe
      // are treated as homeless and culled at cycle end.
      _liveByBucket = new Dictionary<(ChunkClusterId, BiomeKind, string),
                                     List<KeystoneFaunaRegistry.Entry>>();
      _homelessAtCycleStart = new List<KeystoneFaunaRegistry.Entry>();
      foreach (var entry in _registry.Entries) {
        var clusterId = ClusterForEntry(entry);
        if (clusterId == null) {
          _homelessAtCycleStart.Add(entry);
          continue;
        }
        var recipe = FindRecipeForBlueprint(entry.BlueprintName);
        if (recipe == null) {
          _homelessAtCycleStart.Add(entry);
          continue;
        }
        var key = (clusterId.Value, recipe.Biome, recipe.LevelId);
        if (!_liveByBucket.TryGetValue(key, out var list)) {
          list = new List<KeystoneFaunaRegistry.Entry>();
          _liveByBucket[key] = list;
        }
        list.Add(entry);
      }

      // Schedule every cluster. Clusters with no Class E recipes for
      // their biome process near-instantly (the per-biome catalog
      // lookup returns empty); we keep them in the schedule so the
      // base class's drain accounting reflects total cluster count
      // rather than only fauna-eligible clusters.
      for (var i = 0; i < _clusterIndex.ClusterCount; i++) {
        schedule.Add(new ChunkClusterId(i));
      }
    }

    /// <inheritdoc />
    protected override void ProcessUnit(ChunkClusterId clusterId) {
      if (_liveByBucket == null) return;

      var biome = _clusterIndex.BiomeFor(clusterId);
      if (biome == null) return;
      var chunks = _clusterIndex.ChunksIn(clusterId);
      if (chunks.Count == 0) return;
      var clusterScore = _clusterIndex.Score(clusterId);

      // Group eligible recipes by levelId for this cluster's biome.
      // Recipe counts are tiny (one bucket per species per biome) so
      // a per-visit grouping is cheaper than caching it across the
      // cycle and dealing with cluster-index reshapes.
      var bucketsByLevel = new Dictionary<string, List<ClassERecipe>>();
      foreach (var recipe in _catalog.ClassEForBiome(biome.Value)) {
        if (!bucketsByLevel.TryGetValue(recipe.LevelId, out var bucket)) {
          bucket = new List<ClassERecipe>();
          bucketsByLevel[recipe.LevelId] = bucket;
        }
        bucket.Add(recipe);
      }
      if (bucketsByLevel.Count == 0) return;

      // Per-bucket: compute capacity, compare to live count, decide.
      // Surplus → cull immediately (frustum-gated). Deficit → enqueue
      // the cluster for the drainer to handle. Mixed buckets within
      // one cluster (one over, one under) are normal — each is acted
      // on independently.
      var anyDeficit = false;
      foreach (var (levelId, bucketRecipes) in bucketsByLevel) {
        var level = _levelTable.Find(biome.Value, levelId);
        if (level == null) continue;

        // Per-category abundance multiplier from the player setting.
        // FlourishCatalog asserts at load that all recipes in a
        // (biome, levelId) bucket share the same Category, so reading
        // the first recipe's category is well-defined. Master toggle
        // off → multiplier returns 0 → capacity = 0 → surplus cull
        // arm sweeps everything; unknown category → 1.0 (no effect)
        // so future fauna mods spawn at their recipe-defined density
        // until Keystone exposes a slider for them.
        var multiplier = _settings.MultiplierFor(bucketRecipes[0].Category);

        var capacity = clusterScore >= level.FaunaMinScore
            ? (int)System.Math.Floor(clusterScore * level.FaunaCapacityAtSaturation * multiplier)
            : 0;

        // Placement filter: per-recipe LowerMaturity still constrains
        // which chunks agents can land on, even when whole-cluster
        // score qualifies the bucket. A cluster with mostly low-
        // maturity chunks may have non-zero capacity but few or zero
        // placement-eligible chunks; capacity collapses to zero in
        // that case so we don't enqueue a cluster the drainer can't
        // serve anyway.
        if (capacity > 0) {
          var hasQualifyingChunk = false;
          for (var j = 0; j < chunks.Count; j++) {
            var c = chunks[j];
            var m = _chunkValues.Get(c.RegionId, c.GlobalChunkX, c.GlobalChunkY,
                BiomeValueKinds.ForMaturity(biome.Value)) ?? 0f;
            if (m >= level.LowerMaturity) { hasQualifyingChunk = true; break; }
          }
          if (!hasQualifyingChunk) capacity = 0;
        }

        _liveByBucket.TryGetValue((clusterId, biome.Value, levelId), out var live);
        var liveCount = live?.Count ?? 0;

        if (liveCount > capacity) {
          // Cull arm is gated on version-match because _liveByBucket
          // was built at BuildSchedule and a rebuild could have
          // reassigned ids — the entries under (clusterId, biome,
          // levelId) might no longer be in this cluster. Enqueue arm
          // doesn't need the guard: the drainer re-resolves per-visit
          // before spawning, so stale enqueues are safe.
          if (_clusterIndex.Version == _scheduleClusterVersion) {
            CullOldestOffscreen(live!, liveCount - capacity);
          }
        } else if (liveCount < capacity) {
          anyDeficit = true;
        }
      }

      if (anyDeficit) {
        _spawnQueue.Enqueue(clusterId);
      }
    }

    /// <inheritdoc />
    protected override void OnCycleComplete() {
      // Cull every fauna whose cycle-start classification said
      // homeless — frustum-gated AND with a re-resolve check.
      //
      // The re-resolve handles a rebuild that fired between BuildSchedule
      // and now: an entry classified as homeless under the old cluster
      // index may now resolve to a valid cluster under the new one
      // (rebuild restructured chunks into a cluster the entry happens
      // to sit in). Skip those — they've found a home in the interim.
      //
      // Surviving "still homeless" entries are frustum-gated as before:
      // homeless are by definition the entries the player is most likely
      // watching, so despawning on-screen here would produce the exact
      // visible pop the rest of the pipeline is built to avoid.
      if (_homelessAtCycleStart != null) {
        for (var i = 0; i < _homelessAtCycleStart.Count; i++) {
          var entry = _homelessAtCycleStart[i];
          if (ClusterForEntry(entry) != null) continue;
          if (entry.Position?.Region is { } region
              && FaunaFrustumFilter.IsInFrustum(entry.Position.CurrentTile, region.Z)) {
            continue;
          }
          _registry.Despawn(entry.Entity, FaunaDespawnReason.HomelessAfterCycle);
        }
      }

      LogTopFaunaCandidates();

      _liveByBucket = null;
      _homelessAtCycleStart = null;
    }

    #endregion

    #region Helpers

    /// <summary>First recipe in the catalog whose
    /// <c>BlueprintName</c> matches; resolves a live fauna's bucket
    /// (Biome, LevelId) from its blueprint name. Linear scan — recipe
    /// counts are small (one per fauna species).</summary>
    private ClassERecipe? FindRecipeForBlueprint(string blueprintName) {
      foreach (var r in _catalog.AllClassE) {
        if (r.BlueprintName == blueprintName) return r;
      }
      return null;
    }

    /// <summary>Resolve which cluster a registered fauna currently
    /// belongs to via its <see cref="IFaunaPositioning"/> handle.
    /// Returns <c>null</c> when the agent has no positioning, no
    /// region, or its current chunk isn't in any cluster.</summary>
    private ChunkClusterId? ClusterForEntry(KeystoneFaunaRegistry.Entry entry) {
      if (entry.Position == null) return null;
      var region = entry.Position.Region;
      if (region == null) return null;
      var tile = entry.Position.CurrentTile;
      var chunkX = tile.X / RegionEcologyField.ChunkSize;
      var chunkY = tile.Y / RegionEcologyField.ChunkSize;
      return _clusterIndex.ClusterFor(region.Id, chunkX, chunkY);
    }

    /// <summary>Cull up to <paramref name="count"/> oldest entries by
    /// <see cref="KeystoneFaunaRegistry.Entry.Sequence"/>, skipping
    /// those whose current tile is inside the camera frustum. A culled
    /// fauna disappearing on-screen reads as a bug; deferring on-screen
    /// despawns to the next sweep cycle (6 game-hours later) gives the
    /// player time to look away or for the fauna to wander off
    /// screen.</summary>
    /// <returns>Number actually culled this visit.</returns>
    private int CullOldestOffscreen(List<KeystoneFaunaRegistry.Entry> entries, int count) {
      if (count <= 0 || entries.Count == 0) return 0;
      entries.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
      var culled = 0;
      for (var i = 0; i < entries.Count && culled < count; i++) {
        var entry = entries[i];
        if (entry.Position == null) {
          // No positioning handle — never on-screen by definition,
          // safe to despawn.
          _registry.Despawn(entry.Entity, FaunaDespawnReason.SurplusCull);
          culled++;
          continue;
        }
        var region = entry.Position.Region;
        if (region == null) {
          _registry.Despawn(entry.Entity, FaunaDespawnReason.SurplusCull);
          culled++;
          continue;
        }
        if (FaunaFrustumFilter.IsInFrustum(entry.Position.CurrentTile, region.Z)) continue;
        _registry.Despawn(entry.Entity, FaunaDespawnReason.SurplusCull);
        culled++;
      }
      return culled;
    }

    #endregion

    #region Diagnostic

    /// <summary>Dev-only end-of-cycle diagnostic. For each (biome,
    /// levelId) bucket with at least one Class E recipe, walks every
    /// matching cluster and reports the top 3 by Score with a verdict
    /// explaining what's holding each back. Answers
    /// "why aren't my deer / cows / fish spawning?" at a glance.</summary>
    private void LogTopFaunaCandidates() {
      if (!KeystoneLog.IsVerbose) return;
      if (_catalog.AllClassE.Count == 0) return;

      var buckets = new Dictionary<(BiomeKind Biome, string LevelId), List<ClassERecipe>>();
      foreach (var recipe in _catalog.AllClassE) {
        var key = (recipe.Biome, recipe.LevelId);
        if (!buckets.TryGetValue(key, out var list)) {
          list = new List<ClassERecipe>();
          buckets[key] = list;
        }
        list.Add(recipe);
      }

      var candidates = new List<(float Score, float RawScore, int ClusterTiles, int QualifyingChunks, float MaxMaturity, int LiveCount, ChunkClusterId Id)>();

      foreach (var ((bucketBiome, bucketLevelId), bucketRecipes) in buckets) {
        var level = _levelTable.Find(bucketBiome, bucketLevelId);
        if (level == null) continue;
        var maturityKind = BiomeValueKinds.ForMaturity(bucketBiome);
        var multiplier = _settings.MultiplierFor(bucketRecipes[0].Category);

        var memberList = string.Join("+", bucketRecipes.Select(r =>
            $"{r.BlueprintName}(w={r.Weight.ToString("F2", CultureInfo.InvariantCulture)})"));

        candidates.Clear();
        for (var i = 0; i < _clusterIndex.ClusterCount; i++) {
          var clusterId = new ChunkClusterId(i);
          var biome = _clusterIndex.BiomeFor(clusterId);
          if (biome != bucketBiome) continue;
          var chunks = _clusterIndex.ChunksIn(clusterId);
          var maxMat = 0f;
          var qualifyingChunks = 0;
          for (var j = 0; j < chunks.Count; j++) {
            var c = chunks[j];
            var m = _chunkValues.Get(c.RegionId, c.GlobalChunkX, c.GlobalChunkY, maturityKind) ?? 0f;
            if (m > maxMat) maxMat = m;
            if (m >= level.LowerMaturity) qualifyingChunks++;
          }
          var clusterTiles = _clusterIndex.TileCount(clusterId);
          var score = _clusterIndex.Score(clusterId);
          var rawScore = _clusterIndex.RawScore(clusterId);
          var liveCount = 0;
          if (_liveByBucket != null
              && _liveByBucket.TryGetValue((clusterId, bucketBiome, bucketLevelId), out var live)) {
            liveCount = live.Count;
          }
          candidates.Add((score, rawScore, clusterTiles, qualifyingChunks, maxMat, liveCount, clusterId));
        }

        if (candidates.Count == 0) {
          KeystoneLog.Verbose(
              $"[Keystone] Cycle fauna candidates ({memberList} / {bucketBiome} {bucketLevelId}): " +
              $"no clusters of biome {bucketBiome} matured enough to register.");
          continue;
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var sb = new StringBuilder();
        sb.Append("[Keystone] Cycle fauna candidates (")
          .Append(memberList).Append(" / ")
          .Append(bucketBiome).Append(' ').Append(bucketLevelId)
          .Append("; cap@sat=").Append(level.FaunaCapacityAtSaturation);
        if (level.FaunaMinScore > 0f) {
          sb.Append(", min score=").Append(level.FaunaMinScore.ToString("F2", CultureInfo.InvariantCulture));
        }
        sb.Append(", placement maturity >=").Append(level.LowerMaturity.ToString("F1", CultureInfo.InvariantCulture))
          .Append("):");

        var top = System.Math.Min(3, candidates.Count);
        for (var k = 0; k < top; k++) {
          var cand = candidates[k];
          sb.Append("\n  cluster #").Append(cand.Id.Value)
            .Append(": ").Append(cand.ClusterTiles).Append(" tiles, score=")
            .Append(cand.Score.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" (raw=").Append(cand.RawScore.ToString("F0", CultureInfo.InvariantCulture)).Append(") -- ");

          var aboveFloor = cand.Score >= level.FaunaMinScore;
          var capacity = aboveFloor
              ? (int)System.Math.Floor(cand.Score * level.FaunaCapacityAtSaturation * multiplier)
              : 0;
          if (!aboveFloor) {
            sb.Append("below score floor (")
              .Append(cand.Score.ToString("F2", CultureInfo.InvariantCulture)).Append(" < ")
              .Append(level.FaunaMinScore.ToString("F2", CultureInfo.InvariantCulture)).Append(")");
          } else if (capacity == 0 && level.FaunaCapacityAtSaturation > 0) {
            sb.Append("score too low for any capacity yet (max maturity=")
              .Append(cand.MaxMaturity.ToString("F1", CultureInfo.InvariantCulture)).Append(')');
          } else if (level.FaunaCapacityAtSaturation == 0) {
            sb.Append("cap@sat=0, level spawns no fauna");
          } else if (cand.QualifyingChunks == 0) {
            sb.Append("no chunk meets placement floor (max maturity=")
              .Append(cand.MaxMaturity.ToString("F1", CultureInfo.InvariantCulture))
              .Append(", need ").Append(level.LowerMaturity.ToString("F1", CultureInfo.InvariantCulture)).Append(')');
          } else if (cand.LiveCount >= capacity) {
            sb.Append("at capacity (").Append(cand.LiveCount).Append('/').Append(capacity).Append(')');
          } else {
            sb.Append("would spawn ").Append(capacity - cand.LiveCount)
              .Append(" (").Append(cand.LiveCount).Append('/').Append(capacity).Append(" live)");
          }
        }

        KeystoneLog.Verbose(sb.ToString());
      }
    }

    #endregion

  }

}
