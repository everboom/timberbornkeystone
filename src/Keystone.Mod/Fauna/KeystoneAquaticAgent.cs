using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Fauna;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Timberborn.BaseComponentSystem;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using UnityEngine;
using Random = System.Random;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Per-entity continuous-swim agent for aquatic fauna. Parallel to
  /// <see cref="KeystoneFaunaAgent"/> but structurally simpler:
  /// <list type="bullet">
  ///   <item>No state machine — the fish is always swimming. The
  ///         <c>"Default"</c> clip loops permanently; the agent never
  ///         pauses to play an idle pose.</item>
  ///   <item>On reaching a destination, immediately rolls a new
  ///         in-region destination (no chance-to-idle gate).</item>
  ///   <item>Walkability is water-tile + biome-set membership, not
  ///         maturity-gated. Water depth varies tile-to-tile, so the
  ///         target world-Y is recomputed per waypoint and the
  ///         existing 3D <c>delta</c> lerp handles the smooth height
  ///         change as the fish swims.</item>
  /// </list>
  ///
  /// <para><b>Rotation.</b> Alignment-scaled walking on the XZ plane:
  /// translation speed scales with how well the fish is facing its
  /// next waypoint, so it visibly rotates before accelerating. Yaw-only
  /// (fish don't tilt for vertical depth changes — keeps the read
  /// consistent with the top-down camera).</para>
  ///
  /// <para><b>Target biomes.</b> Hardcoded to <see cref="BiomeKind.Wetland"/>
  /// and <see cref="BiomeKind.Lake"/> today. Promote to a spec field
  /// when a species needs different preferences.</para>
  /// </summary>
  public sealed class KeystoneAquaticAgent : BaseFaunaAgent {

    #region Tunables

    /// <summary>Biomes a fish considers walkable. Hardcoded set;
    /// promote to a spec field when species need different
    /// preferences.</summary>
    private static readonly HashSet<BiomeKind> AcceptedBiomes = new() {
        BiomeKind.Wetland,
        BiomeKind.Lake,
    };

    /// <summary>How far from the current tile to consider for the next
    /// destination, in tiles. Smaller wander than the land agent —
    /// keeps fish from launching cross-pond and overshooting visible
    /// water clusters.</summary>
    private const int WanderRadius = 6;

    /// <summary>Maximum yaw rotation per real-time second. Slower than
    /// the deer's 360 — fish turn more gracefully and the slower
    /// rotation reads as more aquatic.</summary>
    private const float RotationDegPerSec = 120f;

    /// <summary>How many candidate tiles to try when picking a
    /// destination before giving up (and re-trying next frame).</summary>
    private const int MaxDestinationAttempts = 16;

    /// <summary>Clip name played continuously while the fish lives.</summary>
    private const string SwimClipName = "Default";

    /// <summary>Playback-rate multiplier for the swim clip. Above 1.0
    /// = brisker swim cycle than the Blender bake; tuned to match
    /// in-game perception of "alive fish" rather than "lethargic
    /// fish."</summary>
    private const float SwimAnimationSpeed = 1.75f;

    #endregion

    #region Bindito-injected services

    private readonly RegionService _regions;
    private readonly IRegionTopologyQuery _interiorTopology;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly IWaterQuery _water;

    #endregion

    #region Per-species spec

    private KeystoneAquaticAgentSpec _spec = null!;

    #endregion

    #region Configured at spawn

    private KeystoneFaunaAnimator? _animator;
    /// <summary>Pathfinding traversal filter — water depth + interior,
    /// NO biome check. Lets a fish stuck in (say) a River swim out
    /// through the river to a Wetland/Lake destination.</summary>
    private IRegionTopologyQuery? _traversalTopology;
    /// <summary>Destination-pick filter — water depth + interior +
    /// dominant biome ∈ {Wetland, Lake}. Fish never *chooses* a
    /// non-home tile as a target, even if it can path through one.</summary>
    private IRegionTopologyQuery? _destinationTopology;

    #endregion

    #region Per-instance state

    private readonly Random _random = new();
    private bool _configured;
    private IReadOnlyList<TileCoord>? _path;
    private int _pathIndex;
    private Vector3 _targetWorldPos;

    #endregion

    public KeystoneAquaticAgent(
        IClock clock,
        ChunkClusterIndex clusterIndex,
        EntityService entityService,
        KeystoneFaunaRegistry registry,
        RegionService regions,
        IRegionTopologyQuery topology,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        IWaterQuery water,
        FaunaUpdateProfiler updateProfiler)
        : base(clock, clusterIndex, entityService, registry, updateProfiler) {
      _regions = regions;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _water = water;
      _interiorTopology = new InteriorOnlyTopology(topology);
    }

    public override void Awake() {
      _spec = GetComponent<KeystoneAquaticAgentSpec>();
    }

    /// <inheritdoc />
    protected override bool IsBiomeAccepted(BiomeKind biome) => AcceptedBiomes.Contains(biome);

    /// <inheritdoc />
    /// <remarks>Fish are continuous-population: they live across
    /// dusk and the dawn capacity-reconcile pass alone tops up /
    /// culls their numbers. Land grazers (the base class default)
    /// are session-bound and despawn at dusk.</remarks>
    public override bool PersistsOvernight => true;

    /// <inheritdoc />
    public override void ConfigureFromRecipe(
        KeystoneFaunaAnimator? animator,
        Region region,
        TileCoord initialTile,
        Keystone.Core.Biomes.ClassERecipe recipe,
        BiomeLevel level) {
      _ = recipe;
      _ = level;
      Configure(animator, region, initialTile);
    }

    /// <summary>Set per-instance runtime state. Called by the dev tool
    /// (or future natural-spawn handler) after the entity is
    /// instantiated. Starts the swim clip immediately and tries to
    /// pick a first destination.</summary>
    public void Configure(
        KeystoneFaunaAnimator? animator,
        Region region,
        TileCoord initialTile) {
      _animator = animator;
      Region = region;
      CurrentTile = initialTile;
      var field = _fieldQuery.FieldFor(region.Id);
      // Traversal: water + interior only. Fish can swim through any
      // deep-enough water (including River, Badwater, etc.).
      _traversalTopology = new WaterOnlyTopology(
          _interiorTopology, _water, region.Z, _spec.MinWaterDepth);
      // Destination-pick: water + interior + biome. Without an ecology
      // field we can't biome-gate, so fall back to traversal (any
      // water). Means fish on a fresh region wander anywhere; once the
      // field comes online the gate kicks in on the next destination.
      _destinationTopology = field != null
          ? new AquaticBiomeFilterTopology(
              _interiorTopology, _water, _biomeValues, field,
              region.Z, AcceptedBiomes, _spec.MinWaterDepth)
          : _traversalTopology;
      _configured = true;
      // Snap initial position so the visible fish starts exactly on its
      // own waypoint formula (matches the per-waypoint TargetForIndex).
      // Avoids first-frame stutter when the tool placed the entity at a
      // slightly different Y than the agent would compute.
      Transform.position = TileWorldPos(initialTile);
      _animator?.PlayClip(SwimClipName, loop: true, speed: SwimAnimationSpeed);
      // Schedule first cluster self-check on the next Update tick.
      ScheduleFirstClusterCheck();

      // Diagnostic: report what the spawn tile actually classifies as,
      // so a "stuck" fish reveals whether the biome gate or the depth
      // gate is the culprit.
      var spawnDepth = _water.WaterDepthAt(new SurfaceCoord(initialTile.X, initialTile.Y, region.Z));
      var spawnDom = field != null
          ? ChunkBiomeSampler.SampleDominantBiome(
              _biomeValues, region.Id,
              field.OriginX, field.OriginY, field.ChunksX, field.ChunksY,
              initialTile.X, initialTile.Y).Biome
          : null;
      KeystoneLog.Verbose(
          $"[Keystone] KeystoneAquaticAgent on '{Name}': configured in region {region.Id} " +
          $"at tile {initialTile} (z={region.Z}, biomes={{Wetland,Lake}}, " +
          $"minWaterDepth={_spec.MinWaterDepth:F2}, biome-gated={field != null}, " +
          $"spawnTileDepth={spawnDepth:F2}, spawnTileDominantBiome={(spawnDom?.ToString() ?? "<none>")}).");

      TryStartNewPath();
      if (_path == null) {
        KeystoneLog.Verbose(
            $"[Keystone] KeystoneAquaticAgent on '{Name}': no valid destination from spawn tile " +
            $"in {MaxDestinationAttempts} attempts within wander radius {WanderRadius}. " +
            "Fish will swim-in-place. Likely cause: spawn tile is outside the " +
            "{Wetland,Lake} biome cluster OR no neighbour tile meets the depth gate.");
      }
    }

    /// <summary>Per-frame update, invoked through the base's timed
    /// <c>Update</c> wrapper.</summary>
    protected override void UpdateCore() {
      if (!_configured || Region == null || _traversalTopology == null
          || _destinationTopology == null) return;
      // Base class: game-time cluster-affinity self-check.
      if (!CheckClusterAffinityAndStaysAlive()) return;
      // Per-frame water-depth gate — water sim drift can drain the
      // tile under a swimming fish at any time.
      var currentDepth = _water.WaterDepthAt(new SurfaceCoord(CurrentTile.X, CurrentTile.Y, Region.Z));
      if (currentDepth < _spec.MinWaterDepth) {
        _configured = false;
        DespawnSelf(
            $"current tile {CurrentTile} has depth {currentDepth:F2} below MinWaterDepth {_spec.MinWaterDepth:F2}",
            FaunaDespawnReason.AquaticTooShallow);
        return;
      }
      if (_path == null) {
        TryStartNewPath();
        return;
      }
      // Refresh the target waypoint's world position against the
      // current water surface every frame. X and Z don't change (the
      // target tile coord is stable mid-segment); Y picks up live
      // water-depth changes so the fish visually rides the surface
      // continuously rather than only resyncing on waypoint arrival.
      // O(1) WaterDepthAt lookup per fish per frame -- ~200 fish × 60
      // fps × 1 lookup is negligible compared to path stepping.
      _targetWorldPos = TargetForIndex(_pathIndex);
      UpdateSwimming();
    }

    #region Path lifecycle

    /// <summary>Roll a new destination (Wetland/Lake only) and
    /// pathfind to it through any deep water. On success installs
    /// <see cref="_path"/>; on failure clears it so the next frame
    /// retries.</summary>
    private void TryStartNewPath() {
      _path = null;
      var dest = TryPickDestination();
      if (dest == null) return;
      var path = FaunaPathfinder.FindPath(_traversalTopology!, Region!.Id, CurrentTile, dest.Value);
      if (path == null || path.Count < 2) return;
      _path = path;
      _pathIndex = 1;
      _targetWorldPos = TargetForIndex(_pathIndex);
    }

    /// <summary>Pick a random tile within wander radius that passes
    /// the strict destination filter (Wetland/Lake + deep enough).
    /// Pathfinding will then route through any traversable water to
    /// reach it.</summary>
    private TileCoord? TryPickDestination() {
      for (var i = 0; i < MaxDestinationAttempts; i++) {
        var dx = _random.Next(-WanderRadius, WanderRadius + 1);
        var dy = _random.Next(-WanderRadius, WanderRadius + 1);
        if (dx == 0 && dy == 0) continue;
        var candidate = new TileCoord(CurrentTile.X + dx, CurrentTile.Y + dy);
        if (_destinationTopology!.ContainsTile(Region!.Id, candidate.X, candidate.Y)) {
          return candidate;
        }
      }
      return null;
    }

    #endregion

    #region Swimming

    private void UpdateSwimming() {
      var pos = Transform.position;
      var delta = _targetWorldPos - pos;

      var flatDelta = new Vector3(delta.x, 0f, delta.z);
      var hasDirection = flatDelta.sqrMagnitude > 0.0001f;
      var desiredDir = hasDirection ? flatDelta.normalized : Vector3.zero;

      // Alignment on the XZ plane only — yaw determines how fast the
      // fish can translate. Y delta rides along on the normalized
      // 3D direction step so depth changes interpolate smoothly.
      var forward = Transform.forward;
      forward.y = 0f;
      if (forward.sqrMagnitude > 0.0001f) forward.Normalize();
      var alignment = hasDirection
          ? Mathf.Max(0f, Vector3.Dot(forward, desiredDir))
          : 1f;
      var stepLen = _spec.WorldSpeedTilesPerSec * Time.deltaTime * alignment;

      // Arrival check uses 3D distance — once within stepLen of the
      // waypoint (including the Y component), snap and advance.
      if (stepLen > 0f && delta.sqrMagnitude <= stepLen * stepLen) {
        Transform.position = _targetWorldPos;
        CurrentTile = _path![_pathIndex];
        _pathIndex++;
        if (_pathIndex >= _path.Count) {
          // Continuous-swim: immediately roll a new destination.
          TryStartNewPath();
          return;
        }
        _targetWorldPos = TargetForIndex(_pathIndex);
        return;
      }

      if (stepLen > 0f) {
        Transform.position = pos + delta.normalized * stepLen;
      }
      if (hasDirection) {
        var targetRotation = Quaternion.LookRotation(desiredDir, Vector3.up);
        Transform.rotation = Quaternion.RotateTowards(
            Transform.rotation, targetRotation, RotationDegPerSec * Time.deltaTime);
      }
    }

    /// <summary>World position for path waypoint at
    /// <paramref name="index"/>. Delegates to
    /// <see cref="TileWorldPos"/> — Y is computed per waypoint from
    /// water depth at that tile + the spec's lift, so the fish rides
    /// the water surface as it swims through depth-varying terrain.</summary>
    private Vector3 TargetForIndex(int index) => TileWorldPos(_path![index]);

    /// <summary>World position the fish should occupy when standing on
    /// <paramref name="tile"/>. Uses the centred tile floor + the
    /// tile's water depth + the spec's lift. Authoritative formula
    /// for any "where should the fish be at this tile" question —
    /// callers (initial snap in <see cref="Configure"/>, per-waypoint
    /// targets in <see cref="TargetForIndex"/>) must agree.</summary>
    private Vector3 TileWorldPos(TileCoord tile) {
      var z = Region!.Z;
      var baseWorld = CoordinateSystem.GridToWorldCentered(new Vector3Int(tile.X, tile.Y, z));
      var waterDepth = _water.WaterDepthAt(new SurfaceCoord(tile.X, tile.Y, z));
      return new Vector3(baseWorld.x, baseWorld.y + waterDepth + _spec.AboveSurfaceLift, baseWorld.z);
    }

    #endregion

    #region Topology wrappers

    /// <summary>Interior-only filter — same shape as the land agent's
    /// inner wrapper. A tile counts as in-region only if it AND all
    /// four cardinal neighbours do, which keeps the pathfinder from
    /// hugging the region boundary.</summary>
    private sealed class InteriorOnlyTopology : IRegionTopologyQuery {
      private readonly IRegionTopologyQuery _inner;
      public InteriorOnlyTopology(IRegionTopologyQuery inner) { _inner = inner; }
      public bool ContainsTile(RegionId region, int x, int y) {
        return _inner.ContainsTile(region, x, y)
            && _inner.ContainsTile(region, x + 1, y)
            && _inner.ContainsTile(region, x - 1, y)
            && _inner.ContainsTile(region, x, y + 1)
            && _inner.ContainsTile(region, x, y - 1);
      }
    }

    /// <summary>Water-only filter — tile is walkable iff the inner says
    /// so AND its water depth meets the minimum. No biome check;
    /// fallback when the region has no ecology field yet.</summary>
    private sealed class WaterOnlyTopology : IRegionTopologyQuery {
      private readonly IRegionTopologyQuery _inner;
      private readonly IWaterQuery _water;
      private readonly int _z;
      private readonly float _minDepth;
      public WaterOnlyTopology(IRegionTopologyQuery inner, IWaterQuery water, int z, float minDepth) {
        _inner = inner;
        _water = water;
        _z = z;
        _minDepth = minDepth;
      }
      public bool ContainsTile(RegionId region, int x, int y) {
        if (!_inner.ContainsTile(region, x, y)) return false;
        return _water.WaterDepthAt(new SurfaceCoord(x, y, _z)) >= _minDepth;
      }
    }

    /// <summary>Water + biome-set filter — tile is walkable iff the
    /// inner says so, its water depth meets the minimum, AND the
    /// chunk's dominant biome is in <see cref="_biomes"/>.</summary>
    private sealed class AquaticBiomeFilterTopology : IRegionTopologyQuery {
      private readonly IRegionTopologyQuery _inner;
      private readonly IWaterQuery _water;
      private readonly IChunkBiomeValues _biomeValues;
      private readonly RegionEcologyField _field;
      private readonly int _z;
      private readonly HashSet<BiomeKind> _biomes;
      private readonly float _minDepth;

      public AquaticBiomeFilterTopology(
          IRegionTopologyQuery inner,
          IWaterQuery water,
          IChunkBiomeValues biomeValues,
          RegionEcologyField field,
          int z,
          HashSet<BiomeKind> biomes,
          float minDepth) {
        _inner = inner;
        _water = water;
        _biomeValues = biomeValues;
        _field = field;
        _z = z;
        _biomes = biomes;
        _minDepth = minDepth;
      }

      public bool ContainsTile(RegionId region, int x, int y) {
        if (!_inner.ContainsTile(region, x, y)) return false;
        if (_water.WaterDepthAt(new SurfaceCoord(x, y, _z)) < _minDepth) return false;
        var (dominant, _) = ChunkBiomeSampler.SampleDominantBiome(
            _biomeValues, region,
            _field.OriginX, _field.OriginY, _field.ChunksX, _field.ChunksY,
            x, y);
        return dominant.HasValue && _biomes.Contains(dominant.Value);
      }
    }

    #endregion

  }

}
