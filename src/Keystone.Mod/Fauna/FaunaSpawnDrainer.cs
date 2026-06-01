using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Fauna;
using Keystone.Core.Persistence;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Keystone.Mod.Settings;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateCollectionSystem;
using UnityEngine;
using Random = System.Random;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Per-frame drainer for the fauna spawn queue. Pops a small number
  /// of clusters per frame, recomputes each one's per-bucket deficit
  /// from a live registry walk, and instantiates one fauna into the
  /// most-deficient bucket — provided a qualifying tile exists outside
  /// the camera's viewport. Camera-blocked clusters get re-enqueued and
  /// retried next frame; clusters whose deficit has dropped to zero
  /// fall out of the queue.
  ///
  /// <para><b>Why frame-cadence and not the sweep ticker?</b> The
  /// sweep runs every 6 game-hours. Spawning at the sweep would put
  /// every cluster's full deficit through Instantiate in a single tick
  /// (potentially many instantiates per frame). The drainer spreads
  /// work out — at <see cref="MaxSpawnsPerFrame"/> the per-frame
  /// allocation budget is bounded regardless of how many clusters need
  /// fauna. Also runs during pause (IUpdatableSingleton ticks while
  /// the game is paused, ITickableSingleton doesn't), so a long
  /// fast-forward followed by a pause produces visible population
  /// convergence right as the player slows down.</para>
  ///
  /// <para><b>Bucket counting: walk the registry per visit.</b> The
  /// drainer doesn't maintain its own per-cluster live count. Each
  /// visit it iterates <see cref="KeystoneFaunaRegistry.Entries"/>,
  /// filters to entries whose current tile resolves to this cluster,
  /// and groups them by <c>(biome, levelId)</c>. Registry sizes in
  /// practice run to a couple hundred entries; at a per-frame cap of a
  /// handful of visits this is negligible work. Avoids the staleness
  /// failure mode where a cached count drifts from reality because a
  /// fauna died through a path the cache wasn't watching (terrain
  /// edits, region invalidation, vanilla cleanup) — see the global
  /// "don't cache derived state whose source changes outside your
  /// observation" rule.</para>
  ///
  /// <para><b>Frustum gating.</b>
  /// <see cref="FaunaFrustumFilter.IsInFrustum"/> rejects spawn tiles
  /// inside the camera's viewport (with a small margin). When every
  /// qualifying chunk in a cluster is on-screen, the drainer
  /// re-enqueues the cluster instead of forcing a visible pop —
  /// natural pacing while the player isn't looking, and a hard pause
  /// while they are.</para>
  /// </summary>
  public sealed class FaunaSpawnDrainer : IUpdatableSingleton {

    #region Constants

    /// <summary>Hard cap on spawns per frame across all clusters.
    /// Bounded to keep per-frame allocation cost tiny even when the
    /// queue is large. At 60fps a cap of 2 drains 120 fauna/sec — the
    /// system typically has nowhere near that to spawn at steady
    /// state, so this is the recovery budget for when a big chunk of
    /// terrain newly qualifies and clusters fill up post-load /
    /// post-fast-forward.</summary>
    private const int MaxSpawnsPerFrame = 2;

    /// <summary>Maximum cluster visits per frame, separate from spawns.
    /// A visit that finds the cluster camera-blocked or finds no
    /// deficit costs the registry walk + bucket grouping but doesn't
    /// instantiate. Capping visits prevents the drainer from churning
    /// through a queue full of camera-blocked clusters in one frame
    /// when the player has the camera parked on a fauna-rich area.</summary>
    private const int MaxVisitsPerFrame = 6;

    #endregion

    #region Dependencies

    private readonly RegionService _regions;
    private readonly ChunkValueStore _chunkValues;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly ChunkClusterIndex _clusterIndex;
    private readonly FlourishCatalog _catalog;
    private readonly BiomeLevelTable _levelTable;
    private readonly EntityService _entityService;
    private readonly TemplateCollectionService _templates;
    private readonly KeystoneFaunaRegistry _registry;
    private readonly FaunaSpawnQueue _queue;
    private readonly IRegionTopologyQuery _topology;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly KeystoneFaunaSettings _settings;
    private readonly PerfTracker _perf;

    #endregion

    #region Per-frame state

    /// <summary>Reusable per-visit bucket map. Cleared at the top of
    /// every visit. Maps <c>levelId</c> → list of registry entries
    /// whose current tile resolves to this cluster's chunks at that
    /// level's bucket. Kept as a field, not a local, to avoid the
    /// per-frame dictionary allocation.</summary>
    private readonly Dictionary<string, List<KeystoneFaunaRegistry.Entry>> _liveByLevel = new();

    /// <summary>Reused chunk-eligibility scratch list, matching the
    /// sweep ticker's pattern. Filled per visit with chunks whose
    /// Maturity clears the level's placement floor AND whose tile centre
    /// would land off-frustum.</summary>
    private readonly List<ChunkCoord> _qualifyingChunks = new();

    /// <summary>Non-deterministic per-frame RNG, matching the sweep's
    /// stance: fauna placement isn't on the save-replay path so a
    /// time-seeded RNG is fine.</summary>
    private readonly Random _random = new();

    /// <summary>One-shot-warn dedupe for the per-spawn warnings below
    /// (missing blueprint / Instantiate null / orphan agent). Without
    /// this gate, a Class E recipe whose blueprint never shipped would
    /// fire a warning every spawn attempt — many per cluster per cycle.
    /// Mirrors <c>RecipeFilterRegistry._warnedNames</c> and
    /// <c>GrowableTimeTriggerAccessor._warnedMissing</c>. Keyed by
    /// blueprint name (all three warning sites are about the same
    /// blueprint).</summary>
    private readonly HashSet<string> _warnedBlueprints = new();

    #endregion

    #region Construction

    public FaunaSpawnDrainer(
        RegionService regions,
        ChunkValueStore chunkValues,
        IChunkBiomeValues biomeValues,
        ChunkClusterIndex clusterIndex,
        FlourishCatalog catalog,
        BiomeLevelTable levelTable,
        EntityService entityService,
        TemplateCollectionService templates,
        KeystoneFaunaRegistry registry,
        FaunaSpawnQueue queue,
        IRegionTopologyQuery topology,
        IEcologyFieldQuery fieldQuery,
        KeystoneFaunaSettings settings,
        PerfTracker perf) {
      _regions = regions;
      _chunkValues = chunkValues;
      _biomeValues = biomeValues;
      _clusterIndex = clusterIndex;
      _catalog = catalog;
      _levelTable = levelTable;
      _entityService = entityService;
      _templates = templates;
      _registry = registry;
      _queue = queue;
      _topology = topology;
      _fieldQuery = fieldQuery;
      _settings = settings;
      _perf = perf;
    }

    #endregion

    #region IUpdatableSingleton

    /// <inheritdoc />
    public void UpdateSingleton() {
      if (_queue.Count == 0) return;
      // Outermost try/catch: a throw inside VisitCluster (or the loop
      // bookkeeping) would let Bindito drop us from the update list and
      // the wildlife system would silently stall for the rest of the
      // session. Catch + record once per session so the dialog can
      // surface it; subsequent frames retry the queue.
      try {
        // Stale ids in the queue (from a rebuild between sweep enqueue
        // and now) are safe: VisitCluster re-resolves biome / member
        // chunks / live entries from the *current* cluster index, so a
        // stale id either drops out (BiomeFor returns null, no recipes
        // for the new biome) or processes against the new cluster's real
        // state (re-counts liveness, recomputes deficit). We deliberately
        // do not clear the queue on version drift because the biome
        // ticker rebuilds every game-hour while the fauna sweep cycle is
        // 6 game-hours -- clearing on every drift would throw out
        // legitimately-queued work most of the time and starve
        // population recovery.
        var spawnsThisFrame = 0;
        var visitsThisFrame = 0;
        // Off-tick per-frame work (IUpdatableSingleton, not in
        // Engine.TickWork): instrument so the registry-walk-per-visit
        // cost shows in the perf window. Wrapped after the queue-empty
        // guard above so idle frames don't dilute the average to zero.
        using (_perf.Track("Fauna.Drain")) {
          while (spawnsThisFrame < MaxSpawnsPerFrame
                 && visitsThisFrame < MaxVisitsPerFrame
                 && _queue.TryDequeue(out var clusterId)) {
            visitsThisFrame++;
            var outcome = VisitCluster(clusterId);
            if (outcome == VisitOutcome.Spawned) {
              spawnsThisFrame++;
              _queue.Enqueue(clusterId);
            } else if (outcome == VisitOutcome.CameraBlocked) {
              _queue.Enqueue(clusterId);
            }
          }
        }
        // Per-frame counters (unit counts, not ms): how many cluster
        // visits and actual spawns this frame. High visit counts with
        // few spawns = the queue is churning camera-blocked / filled
        // clusters (registry walk cost without payoff).
        _perf.RecordCount("Fauna.Drain.Visits", visitsThisFrame);
        _perf.RecordCount("Fauna.Drain.Spawns", spawnsThisFrame);
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorOnce(
            "FaunaSpawnDrainer.UpdateSingleton", "Subsystem failed", ex, ref _updateFailureLogged);
      }
    }

    /// <summary>Rate-limit per session so a persistent VisitCluster
    /// failure doesn't spam Player.log every frame. One Error line
    /// + one aggregator record is enough.</summary>
    private bool _updateFailureLogged;

    #endregion

    #region Visit

    private enum VisitOutcome {
      /// <summary>Cluster id is stale or its biome has no fauna
      /// recipes. Dropped from the queue.</summary>
      Invalid,
      /// <summary>Cluster has no deficit in any bucket. Dropped from
      /// the queue; sweep will re-enqueue if it grows.</summary>
      Filled,
      /// <summary>Deficit exists but every qualifying tile is in the
      /// camera frustum. Re-enqueue and try next frame.</summary>
      CameraBlocked,
      /// <summary>Deficit exists but the chosen chunks have no
      /// interior (1-tile-from-edge) tile we could pick — region
      /// fragmentation or chunk-vs-region edge mismatch. Dropped from
      /// the queue (re-enqueue would keep failing the same way); next
      /// sweep cycle re-enqueues if it still wants to fill.</summary>
      NoEligibleTile,
      /// <summary>One fauna spawned. Re-enqueue in case residual
      /// deficit remains.</summary>
      Spawned,
    }

    private VisitOutcome VisitCluster(ChunkClusterId clusterId) {
      var biome = _clusterIndex.BiomeFor(clusterId);
      if (biome == null) return VisitOutcome.Invalid;
      var chunks = _clusterIndex.ChunksIn(clusterId);
      if (chunks.Count == 0) return VisitOutcome.Invalid;

      // Group recipes by levelId for this biome.
      var bucketsByLevel = new Dictionary<string, List<Keystone.Core.Biomes.ClassERecipe>>();
      foreach (var recipe in _catalog.ClassEForBiome(biome.Value)) {
        if (!bucketsByLevel.TryGetValue(recipe.LevelId, out var bucket)) {
          bucket = new List<Keystone.Core.Biomes.ClassERecipe>();
          bucketsByLevel[recipe.LevelId] = bucket;
        }
        bucket.Add(recipe);
      }
      if (bucketsByLevel.Count == 0) return VisitOutcome.Invalid;

      // Walk the registry once, bucket by levelId, filter to this
      // cluster only. O(registry size) per visit — small.
      //
      // Per-entry isolation: a single registry entry with a degenerate
      // positioning handle (Region accessor throws, CurrentTile
      // accessor throws on a corrupted agent) shouldn't skip the whole
      // visit. Log per-entry; continue.
      _liveByLevel.Clear();
      foreach (var entry in _registry.Entries) {
        try {
          var ec = ClusterForEntry(entry);
          if (ec == null || !ec.Value.Equals(clusterId)) continue;
          var recipe = FindRecipeForBlueprint(entry.BlueprintName);
          if (recipe == null) continue;
          if (!_liveByLevel.TryGetValue(recipe.LevelId, out var list)) {
            list = new List<KeystoneFaunaRegistry.Entry>();
            _liveByLevel[recipe.LevelId] = list;
          }
          list.Add(entry);
        } catch (System.Exception ex) {
          KeystoneLog.Warn(
              $"[Keystone] FaunaSpawnDrainer.VisitCluster: registry entry " +
              $"'{entry.BlueprintName}' threw {ex.GetType().Name}: {ex.Message}. " +
              "Excluding this entry from the visit; the cluster's other entries continue.");
          Keystone.Mod.Diagnostics.KeystoneIntegrationHealth.TryRecord(
              "Per-entity tick errors",
              $"FaunaSpawnDrainer: {entry.BlueprintName}");
        }
      }

      var clusterScore = _clusterIndex.Score(clusterId);
      var anyDeficit = false;
      var anyCameraBlocked = false;

      // Pick the bucket with the largest absolute deficit and try to
      // spawn one. Round-robin across visits — each visit picks the
      // current largest, spawns one, returns; sibling buckets get
      // their turn the next time the cluster comes off the queue.
      string? bestLevelId = null;
      List<Keystone.Core.Biomes.ClassERecipe>? bestBucket = null;
      BiomeLevel? bestLevel = null;
      var bestDeficit = 0;

      foreach (var (levelId, bucket) in bucketsByLevel) {
        var level = _levelTable.Find(biome.Value, levelId);
        if (level == null) continue;

        // Per-category abundance multiplier from the player setting.
        // FlourishCatalog asserts at load that all recipes in a
        // (biome, levelId) bucket share the same Category, so reading
        // the first recipe's category is well-defined. When the master
        // toggle is off the multiplier is 0 → capacity is 0 → drainer
        // bails on this bucket and the sweep ticker's surplus arm
        // handles cleanup.
        var multiplier = _settings.MultiplierFor(bucket[0].Category);
        var capacity = clusterScore >= level.FaunaMinScore
            ? (int)System.Math.Floor(clusterScore * level.FaunaCapacityAtSaturation * multiplier)
            : 0;
        if (capacity <= 0) continue;

        _liveByLevel.TryGetValue(levelId, out var live);
        var liveCount = live?.Count ?? 0;
        var deficit = capacity - liveCount;
        if (deficit <= 0) continue;
        anyDeficit = true;

        if (deficit > bestDeficit) {
          bestDeficit = deficit;
          bestLevelId = levelId;
          bestBucket = bucket;
          bestLevel = level;
        }
      }

      if (!anyDeficit) return VisitOutcome.Filled;

      // Build the off-frustum qualifying chunk list for the chosen
      // bucket. If we filter out every chunk to the camera, the
      // cluster is camera-blocked — re-enqueue for next frame.
      _qualifyingChunks.Clear();
      var maturityKind = BiomeValueKinds.ForMaturity(biome!.Value);
      for (var j = 0; j < chunks.Count; j++) {
        var c = chunks[j];
        var m = _chunkValues.Get(c.RegionId, c.GlobalChunkX, c.GlobalChunkY, maturityKind) ?? 0f;
        if (m < bestLevel!.LowerMaturity) continue;
        // Frustum filter at chunk centre. Cheaper than per-tile and
        // close enough — chunks are 4×4 tiles, so the centre is at
        // most ~2 tiles from any tile in the chunk.
        var centreX = c.GlobalChunkX * RegionEcologyField.ChunkSize + RegionEcologyField.ChunkSize / 2;
        var centreY = c.GlobalChunkY * RegionEcologyField.ChunkSize + RegionEcologyField.ChunkSize / 2;
        var region = _regions.Get(c.RegionId);
        if (region == null) continue;
        if (FaunaFrustumFilter.IsInFrustum(new TileCoord(centreX, centreY), region.Z)) {
          anyCameraBlocked = true;
          continue;
        }
        _qualifyingChunks.Add(c);
      }

      if (_qualifyingChunks.Count == 0) {
        return anyCameraBlocked ? VisitOutcome.CameraBlocked : VisitOutcome.Filled;
      }

      // Pick a recipe weighted within the bucket, an off-frustum
      // chunk, and a random tile within it that passes the agent's
      // exact walkability predicate.
      var totalWeight = 0f;
      for (var i = 0; i < bestBucket!.Count; i++) totalWeight += bestBucket[i].Weight;
      if (totalWeight <= 0f) return VisitOutcome.Filled;

      var chosenRecipe = PickWeighted(bestBucket, totalWeight);
      var chosenChunk = _qualifyingChunks[_random.Next(_qualifyingChunks.Count)];
      var chosenRegion = _regions.Get(chosenChunk.RegionId);
      if (chosenRegion == null) return VisitOutcome.Invalid;

      // Build the agent's walkability predicate for this recipe so the
      // spawn tile passes the exact same test the agent will run on
      // its first hourly stuck-check. Without this, the drainer's
      // coarser chunk-centre maturity check accepts tiles whose
      // per-tile bilinear sample fails the agent's filter (different
      // dominant biome, lower bilinear maturity), and the agent
      // self-despawns on first Update — a spawn/despawn loop with no
      // progress.
      var field = _fieldQuery.FieldFor(chosenRegion.Id);
      var walkability = field != null
          ? (IRegionTopologyQuery)new MaturityFilterTopology(
              new InteriorOnlyTopology(_topology),
              _biomeValues, field, biome.Value, bestLevel!.LowerMaturity)
          : new InteriorOnlyTopology(_topology);

      // Higher attempt budget than the pre-walkability code because
      // most random picks in a chunk near a biome / maturity boundary
      // fail the per-tile bilinear check.
      const int spawnAttempts = 16;
      TileCoord? spawnTile = null;
      for (var attempt = 0; attempt < spawnAttempts; attempt++) {
        var dx = _random.Next(RegionEcologyField.ChunkSize);
        var dy = _random.Next(RegionEcologyField.ChunkSize);
        var tx = chosenChunk.GlobalChunkX * RegionEcologyField.ChunkSize + dx;
        var ty = chosenChunk.GlobalChunkY * RegionEcologyField.ChunkSize + dy;
        if (walkability.ContainsTile(chosenRegion.Id, tx, ty)) {
          spawnTile = new TileCoord(tx, ty);
          break;
        }
      }
      if (spawnTile == null) return VisitOutcome.NoEligibleTile;

      return Spawn(chosenRegion, chosenRecipe, bestLevel!, spawnTile.Value)
          ? VisitOutcome.Spawned
          : VisitOutcome.Invalid;
    }

    #endregion

    #region Helpers

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

    private Keystone.Core.Biomes.ClassERecipe? FindRecipeForBlueprint(string blueprintName) {
      foreach (var r in _catalog.AllClassE) {
        if (r.BlueprintName == blueprintName) return r;
      }
      return null;
    }

    private Keystone.Core.Biomes.ClassERecipe PickWeighted(
        IReadOnlyList<Keystone.Core.Biomes.ClassERecipe> bucket, float totalWeight) {
      var roll = (float)_random.NextDouble() * totalWeight;
      var accum = 0f;
      for (var i = 0; i < bucket.Count; i++) {
        accum += bucket[i].Weight;
        if (roll < accum) return bucket[i];
      }
      return bucket[bucket.Count - 1];
    }

    /// <summary>Instantiate one fauna at the chosen tile. Mirrors
    /// <see cref="FaunaCycleTicker"/>'s previous Spawn helper, moved
    /// here so the cycle ticker only enqueues.</summary>
    private bool Spawn(Region region, Keystone.Core.Biomes.ClassERecipe recipe,
        BiomeLevel level, TileCoord tile) {
      var blueprint = _templates.AllTemplates
          .FirstOrDefault(b => b.Name == recipe.BlueprintName);
      if (blueprint == null) {
        if (_warnedBlueprints.Add(recipe.BlueprintName)) {
          KeystoneLog.Warn(
              $"[Keystone] FaunaSpawnDrainer: blueprint '{recipe.BlueprintName}' not in " +
              "TemplateCollectionService.AllTemplates. Recipe inert until the JSON ships.");
        }
        return false;
      }

      var entity = _entityService.Instantiate(blueprint);
      if (entity == null) {
        if (_warnedBlueprints.Add(recipe.BlueprintName)) {
          KeystoneLog.Warn(
              $"[Keystone] FaunaSpawnDrainer: EntityService.Instantiate returned " +
              $"null for '{recipe.BlueprintName}'.");
        }
        return false;
      }
      entity.Transform.position = CoordinateSystem.GridToWorldCentered(
          new Vector3Int(tile.X, tile.Y, region.Z));
      entity.Transform.rotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 360f, 0f);

      var animator = entity.GetComponent<KeystoneFaunaAnimator>();
      var agent = entity.GetComponent<BaseFaunaAgent>();
      if (agent == null) {
        if (_warnedBlueprints.Add(recipe.BlueprintName)) {
          KeystoneLog.Warn(
              $"[Keystone] FaunaSpawnDrainer: spawned entity '{recipe.BlueprintName}' has " +
              "no BaseFaunaAgent " +
              "(neither KeystoneFaunaAgent nor KeystoneAquaticAgent on this blueprint). " +
              "Deleting orphaned entity.");
        }
        _entityService.Delete(entity);
        return false;
      }
      agent.ConfigureFromRecipe(animator, region, tile, recipe, level);
      _registry.Add(entity, agent, recipe.BlueprintName, agent.PersistsOvernight);
      return true;
    }

    #endregion

  }

}
