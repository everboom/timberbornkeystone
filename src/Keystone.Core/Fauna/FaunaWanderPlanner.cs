using System;
using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;

namespace Keystone.Core.Fauna {

  /// <summary>
  /// State machine + decision logic for a land-style wander/idle fauna
  /// agent. Owns the state transitions (Idle ↔ Walking ↔ Disabled),
  /// the path the agent is currently following, and the
  /// "last successful walk" timestamp used for stuck detection.
  /// Mod-side components drive per-frame motion + animation and
  /// consult the planner for what to do next.
  ///
  /// <para><b>What's in Core, what's in Mod.</b> Everything an
  /// arbitrary observer can reason about — "is this agent in Walking
  /// state?", "what tile should it head toward next?", "is it stuck?" —
  /// lives here. Anything that touches Unity (<c>Transform</c>,
  /// <c>Time.deltaTime</c>, <c>Quaternion</c>, animator) stays
  /// Mod-side. The planner doesn't know about world coordinates,
  /// real-time clip lengths, or animation; it speaks tile coordinates
  /// and game-days.</para>
  ///
  /// <para><b>Determinism.</b> Driven by a caller-supplied
  /// <see cref="System.Random"/>; tests inject a seeded RNG for
  /// reproducible behaviour. Destination picks consult an
  /// <see cref="IRegionTopologyQuery"/> the caller passes in per call
  /// (the planner doesn't capture it because the agent's walkability
  /// view is built lazily after region init).</para>
  ///
  /// <para><b>Aquatic agents.</b> Pure-continuous-swim agents like
  /// <c>KeystoneAquaticAgent</c> have a different state shape (no
  /// Idle) and use the planner's destination-pick + path-advance
  /// primitives without the Idle/Walking machinery. They consume
  /// <see cref="TryPickDestination"/> directly rather than the full
  /// state machine.</para>
  /// </summary>
  public sealed class FaunaWanderPlanner {

    #region State

    /// <summary>Tri-state lifecycle. <see cref="Disabled"/> is
    /// terminal in the sense that no transition the planner can
    /// initiate moves out of it — the Mod-side component must
    /// re-enter Idle externally (e.g. after cluster-affinity is
    /// re-established).</summary>
    public enum State {
      Idle,
      Walking,
      Disabled,
    }

    #endregion

    #region Tunables

    private readonly Random _rng;
    private readonly float _chanceToWalkPerAttempt;
    private readonly int _wanderRadius;
    private readonly int _maxDestinationAttempts;

    #endregion

    #region Mutable state

    private State _state = State.Disabled;
    private TileCoord _currentTile;
    private IReadOnlyList<TileCoord>? _path;
    private int _pathIndex;
    private bool _noWalkableNeighbors;

    #endregion

    /// <param name="rng">Source of randomness for destination picks
    /// and the per-attempt "should I walk now?" gate. Tests pass a
    /// seeded RNG; production passes <c>new Random()</c>.</param>
    /// <param name="chanceToWalkPerAttempt">Probability that
    /// <see cref="TryStartWalkFromIdle"/> rolls in favour of walking
    /// on any single call. Range <c>[0, 1]</c>.</param>
    /// <param name="wanderRadius">Maximum |dx|, |dy| from
    /// <see cref="CurrentTile"/> the destination picker considers.</param>
    /// <param name="maxDestinationAttempts">How many random
    /// candidate tiles the destination picker tries before giving
    /// up.</param>
    public FaunaWanderPlanner(
        Random rng,
        float chanceToWalkPerAttempt,
        int wanderRadius,
        int maxDestinationAttempts) {
      _rng = rng;
      _chanceToWalkPerAttempt = chanceToWalkPerAttempt;
      _wanderRadius = wanderRadius;
      _maxDestinationAttempts = maxDestinationAttempts;
    }

    #region Read-only state

    /// <summary>Current state of the agent.</summary>
    public State CurrentState => _state;

    /// <summary>The agent's current tile. Updated on waypoint arrival
    /// via <see cref="ArriveAtCurrentWaypoint"/>.</summary>
    public TileCoord CurrentTile => _currentTile;

    /// <summary>The tile the agent should head toward right now, or
    /// <c>null</c> if there's nothing to head to (Idle / Disabled, or
    /// path exhausted). Stable between calls until
    /// <see cref="ArriveAtCurrentWaypoint"/> advances the cursor.</summary>
    public TileCoord? CurrentWaypoint =>
        _state == State.Walking && _path != null && _pathIndex < _path.Count
            ? _path[_pathIndex]
            : (TileCoord?)null;

    /// <summary>True iff <see cref="CurrentWaypoint"/> points at the
    /// final tile in the path. Mod-side motion can use this to apply
    /// the destination offset only on the last segment.</summary>
    public bool IsAtFinalWaypoint =>
        _state == State.Walking && _path != null && _pathIndex == _path.Count - 1;

    /// <summary>True when <see cref="TryStartWalkFromIdle"/> found
    /// zero walkable neighbors — the agent is boxed in and should be
    /// despawned by the caller.</summary>
    public bool IsBoxedIn => _noWalkableNeighbors;

    #endregion

    #region State transitions

    /// <summary>Enter Idle. Caller-driven — the planner doesn't
    /// know when the Mod-side animator finishes its idle clip; the
    /// Mod-side calls this on initial configure and whenever a path
    /// is exhausted or fails.</summary>
    public void EnterIdle(TileCoord currentTile) {
      _state = State.Idle;
      _currentTile = currentTile;
      _path = null;
      _pathIndex = 0;
    }

    /// <summary>Enter Idle from a Walking state (path completed or
    /// became invalid).</summary>
    public void ReturnToIdle() {
      _state = State.Idle;
      _path = null;
      _pathIndex = 0;
    }

    /// <summary>Transition to Disabled. Mod-side calls this when the
    /// cluster-affinity check fails — the agent is about to be
    /// despawned, but the state machine should reflect "no further
    /// transitions" until then.</summary>
    public void Disable() {
      _state = State.Disabled;
      _path = null;
      _pathIndex = 0;
    }

    /// <summary>Roll for a walk transition and, on success, install
    /// the resulting path. Returns <c>true</c> iff the state moved to
    /// Walking. The only failure mode beyond the RNG gate is having
    /// no walkable neighbor at all (the agent is fully boxed in).
    ///
    /// <para>Path generation uses
    /// <see cref="FaunaPathfinder.RandomWalk"/>: pick a random
    /// direction, greedily walk that way for a random number of steps.
    /// Cannot fail to find a destination — any walkable neighbor
    /// suffices.</para></summary>
    public bool TryStartWalkFromIdle(
        IRegionTopologyQuery walkability,
        RegionId region) {
      if (_state != State.Idle) return false;
      if (_rng.NextDouble() >= _chanceToWalkPerAttempt) return false;
      var steps = _rng.Next(_wanderRadius / 2, _wanderRadius + 1);
      var path = FaunaPathfinder.RandomWalk(
          walkability, region, _currentTile, steps, _rng);
      if (path == null || path.Count < 2) {
        _noWalkableNeighbors = true;
        return false;
      }
      _noWalkableNeighbors = false;
      _path = path;
      _pathIndex = 1;
      _state = State.Walking;
      return true;
    }

    /// <summary>Pick a random tile within the wander radius that
    /// passes <paramref name="walkability"/>. Returns <c>null</c> if
    /// no candidate survives <see cref="_maxDestinationAttempts"/>
    /// tries. Exposed publicly so continuous-swim agents (no Idle
    /// state) can use the same picker without the full state
    /// machine.</summary>
    public TileCoord? TryPickDestination(
        IRegionTopologyQuery walkability, RegionId region) {
      for (var i = 0; i < _maxDestinationAttempts; i++) {
        var dx = _rng.Next(-_wanderRadius, _wanderRadius + 1);
        var dy = _rng.Next(-_wanderRadius, _wanderRadius + 1);
        if (dx == 0 && dy == 0) continue;
        var candidate = new TileCoord(_currentTile.X + dx, _currentTile.Y + dy);
        if (walkability.ContainsTile(region, candidate.X, candidate.Y)) {
          return candidate;
        }
      }
      return null;
    }

    #endregion

    #region Path advancement

    /// <summary>The Mod-side component reports it has reached the
    /// current waypoint. Updates <see cref="CurrentTile"/>, advances
    /// the cursor, and returns <c>true</c> iff the path is now
    /// exhausted (caller should transition to Idle). When <c>false</c>,
    /// <see cref="CurrentWaypoint"/> now points at the next tile to
    /// head toward.</summary>
    public bool ArriveAtCurrentWaypoint() {
      if (_state != State.Walking || _path == null || _pathIndex >= _path.Count) {
        return true;
      }
      _currentTile = _path[_pathIndex];
      _pathIndex++;
      if (_pathIndex >= _path.Count) {
        ReturnToIdle();
        return true;
      }
      return false;
    }

    #endregion

    #region Stuck detection

    /// <summary>Categorical stuck trigger; <c>null</c> if the agent
    /// is making progress.</summary>
    public enum StuckTrigger {
      /// <summary>Current tile no longer satisfies the walkability
      /// filter. Catches spawns placed on region-edge tiles, terrain
      /// that changed under the agent, and biome dominance shifts.</summary>
      CurrentTileUnwalkable,
      /// <summary>Agent hasn't successfully started a walk in the
      /// stuck window. Covers "every destination pick / pathfind
      /// failed" and "the chance-to-walk gate kept rolling against
      /// the agent."</summary>
      NoSuccessfulWalk,
    }

    /// <summary>Categorical stuck check. Returns the specific
    /// trigger that should despawn the agent, or <c>null</c> if it's
    /// making progress.</summary>
    public StuckTrigger? GetStuckTrigger(
        IRegionTopologyQuery walkability,
        RegionId region) {
      if (!walkability.ContainsTile(region, _currentTile.X, _currentTile.Y)) {
        return StuckTrigger.CurrentTileUnwalkable;
      }
      if (_noWalkableNeighbors) {
        return StuckTrigger.NoSuccessfulWalk;
      }
      return null;
    }

    /// <summary>Human-readable reason.</summary>
    public string DescribeStuck(StuckTrigger trigger) {
      switch (trigger) {
        case StuckTrigger.CurrentTileUnwalkable:
          return $"current tile {_currentTile} no longer satisfies walkability filter";
        case StuckTrigger.NoSuccessfulWalk:
          return $"no walkable neighbors at {_currentTile}";
        default:
          return trigger.ToString();
      }
    }

    #endregion

  }

}
