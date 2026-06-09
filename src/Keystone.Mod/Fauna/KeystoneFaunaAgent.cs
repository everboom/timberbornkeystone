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
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using UnityEngine;
using Random = System.Random;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Per-entity wander-and-idle agent for fauna assets. Drives the
  /// <see cref="KeystoneFaunaAnimator"/> on state transitions and
  /// moves the GameObject's <see cref="Transform"/> directly along
  /// pathfinder-produced waypoints.
  ///
  /// <para><b>Not a BlockObject.</b> Fauna are pure visual decorations
  /// — they don't occupy tile space in the block service, don't
  /// participate in the entity lifecycle, don't save. The agent is
  /// added to the cloned decoration GameObject manually
  /// (<c>AddComponent</c>) by <see cref="FaunaPlacementTool"/> and
  /// configured via <see cref="Configure"/>; there's no
  /// blueprint-spec decorator chain involved.</para>
  ///
  /// <para><b>Per-frame movement via <see cref="IUpdatableComponent"/>.</b>
  /// Timberborn's <see cref="BaseComponent"/> is not a Unity
  /// MonoBehaviour, so the standard <c>Update()</c> message is not
  /// called. To get per-frame ticks we implement
  /// <see cref="IUpdatableComponent.Update"/> (public method, no
  /// parameters) — Timberborn's update manager invokes it each frame.</para>
  ///
  /// <para><b>State machine.</b> Two real states plus a terminal:
  /// <list type="bullet">
  ///   <item><c>Idle</c>: a single idle clip plays once; on completion,
  ///         the agent rolls (chance to walk; otherwise pick a new idle
  ///         clip). Animation length drives state duration, not game
  ///         ticks.</item>
  ///   <item><c>Walking</c>: agent advances along a pre-computed
  ///         smoothed path toward a random in-region destination
  ///         within <see cref="WanderRadius"/>.</item>
  ///   <item><c>Disabled</c>: terminal; entered when the agent has no
  ///         region or wasn't configured.</item>
  /// </list></para>
  /// </summary>
  public sealed class KeystoneFaunaAgent : BaseFaunaAgent {

    #region Tunables (prototype hardcoded)

    /// <summary>Probability of transitioning to Walking after any
    /// single idle clip completes. With ~3-second clips, expected
    /// value of ~1 / 0.35 ≈ 3 idle clips per cycle, the deer stays
    /// in place ~10 real seconds on average before walking off.</summary>
    private const float ChanceToWalkAfterIdleClip = 0.35f;

    /// <summary>How far from the current tile the agent considers
    /// for its next destination, in tiles. Keeps the agent in a
    /// believable local neighbourhood without forcing the pathfinder
    /// to plan long routes every cycle.</summary>
    private const int WanderRadius = 8;

    /// <summary>Maximum yaw rotation in degrees per real-time second.
    /// At 360 deg/sec a 180° turn takes half a second — fast enough
    /// to feel responsive at our walking speed, slow enough that the
    /// deer doesn't snap instantly when it picks a new waypoint.</summary>
    private const float RotationDegPerSec = 360f;

    /// <summary>How many random candidate tiles to try when picking
    /// a destination before giving up and starting a new idle
    /// period.</summary>
    private const int MaxDestinationAttempts = 16;

    /// <summary>Despawn an agent that hasn't successfully started a
    /// walk in this many game-days. 0.25 = 6 game-hours, comfortably
    /// longer than a healthy agent's natural idle-then-walk cadence
    /// (~10 real-time seconds between walks at 1× game speed, far
    /// less than one game-hour) but short enough that a genuinely
    /// stranded agent doesn't linger. Catches pathological isolation
    /// cases (current tile walkable but no destination reachable, or
    /// pathfinding can't escape a 1-tile pocket) that
    /// <see cref="GetStuckReason"/>'s walkability check on the current
    /// tile doesn't catch on its own.</summary>

    /// <summary>Minimum offset from a destination tile's centre when
    /// resolving the agent's final stopping position. Vanilla flora
    /// renders centred on its tile; the deer would clip into a tree
    /// if it idled at the same centre. 0.3 keeps the agent visibly
    /// off-centre while staying inside the tile's [-0.5, 0.5] bounds.</summary>
    private const float DestinationOffsetMin = 0.3f;

    /// <summary>Maximum offset from a destination tile's centre. Caps
    /// the offset short of the tile boundary to avoid the agent
    /// straddling two tiles visually.</summary>
    private const float DestinationOffsetMax = 0.45f;

    /// <summary>Clip name played when the agent is moving.</summary>
    private const string WalkClipName = "Walk";

    /// <summary>Idle-state clip variants the agent picks between on
    /// each Idle entry. <c>Eating</c> dominates so the deer reads as
    /// "grazing here," with occasional plain-idle poses to break up
    /// the loop.</summary>
    private static readonly (string Clip, float Weight)[] IdleClips = {
        ("Eating", 0.70f),
        ("Idle", 0.10f),
        ("Idle_2", 0.10f),
        ("Idle_Headlow", 0.10f),
    };

    #endregion

    #region Bindito-injected services (constructor)

    private readonly RegionService _regions;
    private readonly IRegionTopologyQuery _interiorTopology;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;

    #endregion

    #region Per-species spec (read at Awake, before Configure)

    /// <summary>Read at <see cref="Awake"/> from the entity's
    /// <see cref="KeystoneFaunaAgentSpec"/>. Carries the per-species
    /// world-movement speed. Awake runs at prefab build time, before
    /// <see cref="FaunaPlacementTool"/> calls <see cref="Configure"/>,
    /// so the spec is safe to consult anywhere in this component.</summary>
    private KeystoneFaunaAgentSpec _spec = null!;

    #endregion

    #region Configured at spawn (per-instance runtime state)

    private KeystoneFaunaAnimator? _animator;
    /// <summary>The agent's preferred biome, set at spawn from the
    /// originating recipe (or the dev tool). Used to compose the
    /// walkability view's maturity filter AND to gate the base
    /// class's cluster self-check via <see cref="IsBiomeAccepted"/>.</summary>
    private BiomeKind _targetBiome;
    /// <summary>Minimum target-biome Maturity (game-days) a tile
    /// must clear to be walkable. Set at spawn from the recipe's
    /// level, or 0 for dev-placed agents (no maturity gate so they
    /// can roam pre-mature terrain for visual testing).</summary>
    private float _minMaturityThreshold;
    /// <summary>Composite walkability view: interior-only filter +
    /// target-biome maturity filter. Built in <see cref="Configure"/>
    /// once the region (and therefore its ecology field) is known.
    /// Used for both <see cref="FaunaPathfinder.FindPath"/> and
    /// <see cref="TryPickDestination"/>.</summary>
    private IRegionTopologyQuery? _walkability;

    #endregion

    #region Per-instance state

    private readonly Random _random = new();

    /// <summary>State machine + path management lives in Core. The
    /// planner owns the Idle/Walking/Disabled transition, destination
    /// picking, path storage, and stuck-window timestamp; this
    /// component drives per-frame motion + animation and asks the
    /// planner "where to next?" each frame. Lazily constructed in
    /// <see cref="Configure"/> so the per-instance Random / tunables
    /// are passed in correctly.</summary>
    private FaunaWanderPlanner _planner = null!;

    /// <summary>Real-time timestamp at which the current Idle clip
    /// is expected to finish. Mod-side only — the Core planner
    /// doesn't know about clip lengths.</summary>
    private float _idleClipEndsAt;

    /// <summary>World position of the planner's current waypoint,
    /// cached on waypoint advance so the Update path doesn't recompute
    /// it every frame.</summary>
    private Vector3 _targetWorldPos;

    /// <summary>Random offset (XZ-plane only) applied to the final
    /// destination tile's world position so the agent doesn't idle
    /// at the exact tile centre -- avoids visually clipping into
    /// centre-rendered flora. Regenerated each time the agent picks
    /// a new path; intermediate waypoints still use tile centres.</summary>
    private Vector3 _destinationOffset;

    #endregion

    /// <summary>Bindito injects services through the constructor when
    /// the decorator chain builds the prefab. The per-instance runtime
    /// state (animator handle, region, tile) is set later via
    /// <see cref="Configure"/>, called by the placement tool on each
    /// spawned clone.</summary>
    public KeystoneFaunaAgent(
        IClock clock,
        ChunkClusterIndex clusterIndex,
        EntityService entityService,
        KeystoneFaunaRegistry registry,
        RegionService regions,
        IRegionTopologyQuery topology,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        FaunaUpdateProfiler updateProfiler)
        : base(clock, clusterIndex, entityService, registry, updateProfiler) {
      _regions = regions;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      // Pre-wrap the raw topology with the structural interior filter
      // (4 cardinal neighbours in-region). The full walkability view --
      // interior + biome-maturity gate -- needs the region's ecology
      // field, which we only have at Configure time.
      _interiorTopology = new InteriorOnlyTopology(topology);
    }

    /// <summary>Fetches the per-species spec at prefab-build time.
    /// Runs once per cloned entity, before any path through this
    /// component reads <see cref="_spec"/>.</summary>
    public override void Awake() {
      _spec = GetComponent<KeystoneFaunaAgentSpec>();
    }

    /// <inheritdoc />
    protected override bool IsBiomeAccepted(BiomeKind biome) => biome == _targetBiome;

    /// <inheritdoc />
    public override void ConfigureFromRecipe(
        KeystoneFaunaAnimator? animator,
        Region region,
        TileCoord initialTile,
        ClassERecipe recipe,
        BiomeLevel level) {
      Configure(animator, region, initialTile, recipe.Biome, level.LowerMaturity);
    }

    /// <summary>Set the per-instance runtime state. Called by
    /// <see cref="FaunaPlacementTool"/> after the decoration is spawned
    /// and the agent component has been located on it (via
    /// <c>GameObjectExtensions.GetComponentSlow</c>). Starts the state
    /// machine in Idle; the first idle clip plays immediately.</summary>
    public void Configure(
        KeystoneFaunaAnimator? animator,
        Region region,
        TileCoord initialTile,
        BiomeKind targetBiome,
        float minMaturityThreshold) {
      _animator = animator;
      Region = region;
      CurrentTile = initialTile;
      _targetBiome = targetBiome;
      _minMaturityThreshold = minMaturityThreshold;
      // Compose the maturity filter on top of the interior filter. If
      // the region has no ecology field yet (rare; settled regions
      // sometimes get configured before the field updater catches up),
      // fall back to interior-only so the agent at least roams within
      // the region geometry. minMaturityThreshold=0 effectively
      // disables the maturity gate while preserving the filter shape
      // (useful for dev-placed agents).
      var field = _fieldQuery.FieldFor(region.Id);
      _walkability = field != null
          ? (IRegionTopologyQuery)new MaturityFilterTopology(
              _interiorTopology, _biomeValues, field, _targetBiome, _minMaturityThreshold)
          : _interiorTopology;
      KeystoneLog.Verbose(
          $"[Keystone] KeystoneFaunaAgent on '{Name}': configured in region {region.Id} " +
          $"at tile {initialTile} (z={region.Z}, biome={_targetBiome}, " +
          $"minMaturity={_minMaturityThreshold:F2}, maturity-gated={field != null}).");
      // Base class schedules the first cluster check on next Update.
      ScheduleFirstClusterCheck();
      _planner = new FaunaWanderPlanner(
          rng: _random,
          chanceToWalkPerAttempt: ChanceToWalkAfterIdleClip,
          wanderRadius: WanderRadius,
          maxDestinationAttempts: MaxDestinationAttempts);
      _planner.EnterIdle(initialTile);
      StartIdleClip();
    }

    /// <inheritdoc />
    protected override (FaunaDespawnReason? Reason, string Message) GetStuckCategory(float nowDay) {
      if (Region == null || _walkability == null) return (null, "");
      var trigger = _planner.GetStuckTrigger(_walkability, Region.Id);
      if (!trigger.HasValue) return (null, "");
      var category = trigger.Value switch {
        FaunaWanderPlanner.StuckTrigger.CurrentTileUnwalkable
            => FaunaDespawnReason.StuckUnwalkableTile,
        FaunaWanderPlanner.StuckTrigger.NoSuccessfulWalk
            => FaunaDespawnReason.StuckNoSuccessfulWalk,
        _ => FaunaDespawnReason.Unknown,
      };
      return (category, _planner.DescribeStuck(trigger.Value));
    }

    /// <summary>Per-frame update. Cluster-affinity check first (base
    /// class); on failure the agent has already been deleted, so
    /// bail. Otherwise dispatch to the Idle / Walking state
    /// handler. Invoked through the base's timed <c>Update</c> wrapper.</summary>
    protected override void UpdateCore() {
      if (!CheckClusterAffinityAndStaysAlive()) return;
      switch (_planner.CurrentState) {
        case FaunaWanderPlanner.State.Idle: UpdateIdle(); break;
        case FaunaWanderPlanner.State.Walking: UpdateWalking(); break;
      }
    }

    #region Idle

    private void StartIdleClip() {
      var clip = PickIdleClip();
      var speed = _animator?.IdleAnimationMultiplier ?? 1f;
      if (speed < 0.01f) speed = 1f;
      _animator?.PlayClip(clip, loop: false, speed: speed);
      var length = _animator?.CurrentClipLength ?? 0f;
      if (length <= 0.05f) length = 2f;
      _idleClipEndsAt = Time.realtimeSinceStartup + length / speed;
    }

    private string PickIdleClip() {
      var totalWeight = 0f;
      for (var i = 0; i < IdleClips.Length; i++) totalWeight += IdleClips[i].Weight;
      var draw = (float)_random.NextDouble() * totalWeight;
      var cumulative = 0f;
      for (var i = 0; i < IdleClips.Length; i++) {
        cumulative += IdleClips[i].Weight;
        if (draw <= cumulative) return IdleClips[i].Clip;
      }
      return IdleClips[IdleClips.Length - 1].Clip;
    }

    private void UpdateIdle() {
      if (Time.realtimeSinceStartup < _idleClipEndsAt) return;
      if (Region != null && _walkability != null
          && _planner.TryStartWalkFromIdle(_walkability, Region.Id)) {
        CurrentTile = _planner.CurrentTile;
        _destinationOffset = RandomDestinationOffset();
        _targetWorldPos = TargetForCurrentWaypoint();
        _animator?.PlayClip(WalkClipName, loop: true, speed: _animator.WalkAnimationMultiplier);
        return;
      }
      StartIdleClip();
    }

    #endregion

    #region Walking

    private void UpdateWalking() {
      if (_planner.CurrentWaypoint == null || Region == null) {
        _planner.ReturnToIdle();
        StartIdleClip();
        return;
      }
      var pos = Transform.position;
      var delta = _targetWorldPos - pos;

      // Desired heading on the XZ plane.
      var flatDelta = new Vector3(delta.x, 0f, delta.z);
      var hasDirection = flatDelta.sqrMagnitude > 0.0001f;
      var desiredDir = hasDirection ? flatDelta.normalized : Vector3.zero;

      // Forward-facing alignment, 0..1. A deer facing perpendicular to
      // its target (or backward) gets alignment 0 and doesn't translate
      // this frame; as rotation catches up the alignment ramps up and
      // walking speed with it. Produces a natural "rotate first, then
      // accelerate forward" gait without an explicit Turning state.
      var forward = Transform.forward;
      forward.y = 0f;
      if (forward.sqrMagnitude > 0.0001f) forward.Normalize();
      var alignment = hasDirection
          ? Mathf.Max(0f, Vector3.Dot(forward, desiredDir))
          : 1f;
      var stepLen = _spec.WorldSpeedTilesPerSec * Time.deltaTime * alignment;

      if (stepLen > 0f && delta.sqrMagnitude <= stepLen * stepLen) {
        // Arrived at waypoint. Snap, ask the planner to advance.
        Transform.position = _targetWorldPos;
        var exhausted = _planner.ArriveAtCurrentWaypoint();
        CurrentTile = _planner.CurrentTile;
        if (exhausted) {
          StartIdleClip();
          return;
        }
        _targetWorldPos = TargetForCurrentWaypoint();
        return;
      }

      // Translate (alignment-scaled) and rotate (always at full rotation
      // speed, so the deer keeps turning even when it's holding still
      // waiting for alignment).
      if (stepLen > 0f) {
        Transform.position = pos + delta.normalized * stepLen;
      }
      if (hasDirection) {
        var targetRotation = Quaternion.LookRotation(desiredDir, Vector3.up);
        Transform.rotation = Quaternion.RotateTowards(
            Transform.rotation, targetRotation, RotationDegPerSec * Time.deltaTime);
      }
    }

    private Vector3 TileToWorld(TileCoord tile) =>
        CoordinateSystem.GridToWorldCentered(new Vector3Int(tile.X, tile.Y, Region!.Z));

    /// <summary>World position for the planner's current waypoint.
    /// Intermediate waypoints land on tile centres; the final waypoint
    /// adds <see cref="_destinationOffset"/> so the agent doesn't idle
    /// on top of centre-rendered flora.</summary>
    private Vector3 TargetForCurrentWaypoint() {
      var waypoint = _planner.CurrentWaypoint!.Value;
      var baseWorld = TileToWorld(waypoint);
      return _planner.IsAtFinalWaypoint ? baseWorld + _destinationOffset : baseWorld;
    }

    /// <summary>Random offset in the XZ plane with magnitude in
    /// [<see cref="DestinationOffsetMin"/>,
    /// <see cref="DestinationOffsetMax"/>]. Uniform direction
    /// (random angle); annulus magnitude — uniform in angle, not
    /// uniform in area, which is fine for this purpose.</summary>
    private Vector3 RandomDestinationOffset() {
      var angle = _random.NextDouble() * 2.0 * System.Math.PI;
      var magnitude = DestinationOffsetMin
          + (float)_random.NextDouble() * (DestinationOffsetMax - DestinationOffsetMin);
      return new Vector3(
          (float)System.Math.Cos(angle) * magnitude,
          0f,
          (float)System.Math.Sin(angle) * magnitude);
    }

    #endregion

  }

}
