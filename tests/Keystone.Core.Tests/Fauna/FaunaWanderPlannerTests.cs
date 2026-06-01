using System;
using System.Collections.Generic;
using Keystone.Core.Fauna;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Fauna {

  /// <summary>
  /// Pins the state machine + decision logic of
  /// <see cref="FaunaWanderPlanner"/>. Before this extraction the
  /// behaviour was buried inside <c>KeystoneFaunaAgent</c> and only
  /// exercisable by running the game; now it's testable with a fake
  /// topology and a seeded RNG.
  /// </summary>
  [TestClass]
  public class FaunaWanderPlannerTests {

    #region Helpers

    private sealed class FakeTopology : IRegionTopologyQuery {

      private readonly HashSet<(RegionId, int, int)> _walkable = new();

      public void AddRectAround(RegionId region, int cx, int cy, int radius) {
        for (var x = cx - radius; x <= cx + radius; x++) {
          for (var y = cy - radius; y <= cy + radius; y++) {
            _walkable.Add((region, x, y));
          }
        }
      }

      public void Add(RegionId region, int x, int y) {
        _walkable.Add((region, x, y));
      }

      public void Remove(RegionId region, int x, int y) {
        _walkable.Remove((region, x, y));
      }

      public bool ContainsTile(RegionId region, int x, int y) {
        return _walkable.Contains((region, x, y));
      }

    }

    private static readonly RegionId Region = new(1);

    private static FaunaWanderPlanner MakePlanner(
        Random? rng = null,
        float chanceToWalkPerAttempt = 1f,
        int wanderRadius = 4,
        int maxDestinationAttempts = 16) {
      return new FaunaWanderPlanner(
          rng ?? new Random(42),
          chanceToWalkPerAttempt,
          wanderRadius,
          maxDestinationAttempts);
    }

    /// <summary>Helper: enter idle + attempt walk, returning whether
    /// the walk started. Avoids repeating the two-call pattern in
    /// every test that needs a walking planner.</summary>
    private static bool IdleAndWalk(
        FaunaWanderPlanner planner, TileCoord tile,
        FakeTopology topo, RegionId region) {
      planner.EnterIdle(tile);
      return planner.TryStartWalkFromIdle(topo, region);
    }

    #endregion

    #region Initial state + EnterIdle

    [TestMethod]
    public void NewPlanner_StartsInDisabledState() {
      var planner = MakePlanner();
      Assert.AreEqual(FaunaWanderPlanner.State.Disabled, planner.CurrentState);
      Assert.IsNull(planner.CurrentWaypoint);
    }

    [TestMethod]
    public void EnterIdle_TransitionsToIdleAndRecordsCurrentTile() {
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(3, 5));

      Assert.AreEqual(FaunaWanderPlanner.State.Idle, planner.CurrentState);
      Assert.AreEqual(new TileCoord(3, 5), planner.CurrentTile);
      Assert.IsNull(planner.CurrentWaypoint, "No waypoint while idle.");
    }


    #endregion

    #region TryStartWalkFromIdle

    [TestMethod]
    public void TryStartWalkFromIdle_DisabledState_ReturnsFalseDoesNotChangeState() {
      // Planner starts Disabled; even with everything else available,
      // the gate must reject the call.
      var planner = MakePlanner();
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 4);

      var started = planner.TryStartWalkFromIdle(topo, Region);

      Assert.IsFalse(started);
      Assert.AreEqual(FaunaWanderPlanner.State.Disabled, planner.CurrentState);
    }

    [TestMethod]
    public void TryStartWalkFromIdle_ChanceRollBelowGate_ReturnsFalseStaysIdle() {
      // chanceToWalkPerAttempt = 0 â€" the RNG roll always fails.
      var planner = MakePlanner(chanceToWalkPerAttempt: 0f);
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);

      var started = planner.TryStartWalkFromIdle(topo, Region);

      Assert.IsFalse(started);
      Assert.AreEqual(FaunaWanderPlanner.State.Idle, planner.CurrentState);
    }

    [TestMethod]
    public void TryStartWalkFromIdle_NoReachableDestination_ReturnsFalseStaysIdle() {
      // Single-tile region (only the agent's current tile is walkable).
      // No destination within wander radius passes â€" destination
      // picker exhausts attempts; TryStartWalk returns false.
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.Add(Region, 0, 0);  // only the current tile is walkable

      var started = planner.TryStartWalkFromIdle(topo, Region);

      Assert.IsFalse(started);
      Assert.AreEqual(FaunaWanderPlanner.State.Idle, planner.CurrentState);
    }

    [TestMethod]
    public void TryStartWalkFromIdle_HappyPath_TransitionsToWalkingWithWaypoint() {
      var planner = MakePlanner(rng: new Random(1));
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);

      var started = planner.TryStartWalkFromIdle(topo, Region);

      Assert.IsTrue(started);
      Assert.AreEqual(FaunaWanderPlanner.State.Walking, planner.CurrentState);
      Assert.IsNotNull(planner.CurrentWaypoint, "First waypoint should be available.");
      Assert.IsFalse(planner.IsBoxedIn,
          "Successful walk clears the boxed-in flag.");
    }

    [TestMethod]
    public void TryStartWalkFromIdle_NoReachableNeighbor_SetsIsBoxedIn() {
      // Single-tile region — RandomWalk finds zero walkable neighbors.
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.Add(Region, 0, 0);

      planner.TryStartWalkFromIdle(topo, Region);

      Assert.IsTrue(planner.IsBoxedIn,
          "When RandomWalk finds no walkable neighbors, IsBoxedIn must be set.");
    }

    [TestMethod]
    public void TryStartWalkFromIdle_IsBoxedInResetsOnSuccess() {
      // After a failed walk sets IsBoxedIn, a successful walk must
      // clear it — the agent is no longer stuck.
      var planner = MakePlanner(rng: new Random(1));
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.Add(Region, 0, 0);
      planner.TryStartWalkFromIdle(topo, Region);
      Assert.IsTrue(planner.IsBoxedIn, "Precondition: boxed in after single-tile walk.");

      // Open up the terrain and walk again.
      topo.AddRectAround(Region, 0, 0, radius: 5);
      planner.EnterIdle(new TileCoord(0, 0));
      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));
      Assert.IsFalse(planner.IsBoxedIn,
          "Successful walk must clear IsBoxedIn.");
    }

    [TestMethod]
    public void TryStartWalkFromIdle_WhileAlreadyWalking_ReturnsFalse() {
      // The state-transition gate is strict: can only walk from Idle.
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);
      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));
      Assert.AreEqual(FaunaWanderPlanner.State.Walking, planner.CurrentState);

      // A second call while already walking should no-op.
      var startedAgain = planner.TryStartWalkFromIdle(topo, Region);

      Assert.IsFalse(startedAgain);
      Assert.AreEqual(FaunaWanderPlanner.State.Walking, planner.CurrentState);
    }

    #endregion

    #region Waypoint advancement

    [TestMethod]
    public void ArriveAtCurrentWaypoint_AdvancesAlongPath_NonExhaustedSegmentReturnsFalse() {
      var planner = MakePlanner(rng: new Random(3));
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 8);

      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));
      var firstWaypoint = planner.CurrentWaypoint!.Value;

      // Caller "arrives" at the first waypoint. If there's a second
      // waypoint, the planner stays in Walking and advances the cursor.
      var exhausted = planner.ArriveAtCurrentWaypoint();

      Assert.AreEqual(firstWaypoint, planner.CurrentTile,
          "CurrentTile updates to the arrived waypoint.");
      if (!exhausted) {
        // Multi-waypoint path: still walking, next waypoint exists.
        Assert.AreEqual(FaunaWanderPlanner.State.Walking, planner.CurrentState);
        Assert.IsNotNull(planner.CurrentWaypoint);
        Assert.AreNotEqual(firstWaypoint, planner.CurrentWaypoint!.Value);
      } else {
        // Single-segment path (rare with seed=3 + large topo): planner
        // should have returned to Idle.
        Assert.AreEqual(FaunaWanderPlanner.State.Idle, planner.CurrentState);
      }
    }

    [TestMethod]
    public void ArriveAtCurrentWaypoint_PathExhaustion_TransitionsToIdle() {
      // Drain the path to exhaustion by repeatedly arriving.
      var planner = MakePlanner(rng: new Random(7));
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 8);

      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));

      // Walk to the end of the path.
      bool exhausted;
      var safetyMax = 100;
      do {
        exhausted = planner.ArriveAtCurrentWaypoint();
        safetyMax--;
      } while (!exhausted && safetyMax > 0);

      Assert.IsTrue(exhausted, "Path must exhaust within finite steps.");
      Assert.AreEqual(FaunaWanderPlanner.State.Idle, planner.CurrentState);
      Assert.IsNull(planner.CurrentWaypoint);
    }

    #endregion

    #region GetStuckTrigger

    [TestMethod]
    public void GetStuckTrigger_WalkableWithNeighbors_ReturnsNull() {
      // Current tile walkable + neighbors reachable → not stuck.
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);

      Assert.IsNull(planner.GetStuckTrigger(topo, Region));
    }

    [TestMethod]
    public void GetStuckTrigger_CurrentTileNoLongerWalkable_ReportsCurrentTileUnwalkable() {
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(3, 3));
      // Walkable everywhere EXCEPT the current tile.
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 3, 3, radius: 5);
      topo.Remove(Region, 3, 3);

      var trigger = planner.GetStuckTrigger(topo, Region);

      Assert.AreEqual(FaunaWanderPlanner.StuckTrigger.CurrentTileUnwalkable, trigger);
    }

    [TestMethod]
    public void GetStuckTrigger_IsBoxedIn_ReportsNoSuccessfulWalk() {
      // Agent on a single walkable tile — RandomWalk sets IsBoxedIn,
      // and GetStuckTrigger must report NoSuccessfulWalk.
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.Add(Region, 0, 0);

      planner.TryStartWalkFromIdle(topo, Region);
      Assert.IsTrue(planner.IsBoxedIn, "Precondition: boxed in.");

      var trigger = planner.GetStuckTrigger(topo, Region);
      Assert.AreEqual(FaunaWanderPlanner.StuckTrigger.NoSuccessfulWalk, trigger);
    }

    #endregion

    #region Disable / ReturnToIdle

    [TestMethod]
    public void Disable_ClearsPathAndTransitionsToDisabled() {
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);
      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));
      Assert.IsNotNull(planner.CurrentWaypoint);

      planner.Disable();

      Assert.AreEqual(FaunaWanderPlanner.State.Disabled, planner.CurrentState);
      Assert.IsNull(planner.CurrentWaypoint);
    }

    [TestMethod]
    public void ReturnToIdle_DropsPathAndTransitionsToIdle() {
      // ReturnToIdle is for "path failed or completed mid-walk."
      // It drops the path and transitions to Idle without clearing
      // the boxed-in flag (that only resets on a successful walk).
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);
      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));

      planner.ReturnToIdle();

      Assert.AreEqual(FaunaWanderPlanner.State.Idle, planner.CurrentState);
      Assert.IsNull(planner.CurrentWaypoint,
          "ReturnToIdle must drop the path.");
    }

    #endregion

    #region TryPickDestination

    [TestMethod]
    public void TryPickDestination_AllNeighboursWalkable_ReturnsSomeTile() {
      var planner = MakePlanner(rng: new Random(11));
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);

      var dest = planner.TryPickDestination(topo, Region);

      Assert.IsNotNull(dest);
      Assert.IsTrue(topo.ContainsTile(Region, dest.Value.X, dest.Value.Y));
    }

    [TestMethod]
    public void TryPickDestination_NoNeighboursWalkable_ReturnsNull() {
      var planner = MakePlanner(maxDestinationAttempts: 32);
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.Add(Region, 0, 0);  // only the current tile is walkable

      Assert.IsNull(planner.TryPickDestination(topo, Region));
    }

    [TestMethod]
    public void TryPickDestination_NeverReturnsCurrentTile() {
      // The picker explicitly skips dx=dy=0 (otherwise pathfinding
      // would never start a walk).
      var planner = MakePlanner(
          rng: new Random(19), wanderRadius: 1,
          maxDestinationAttempts: 200);
      planner.EnterIdle(new TileCoord(5, 5));
      var topo = new FakeTopology();
      // Only current tile + (5,6) walkable; with wanderRadius=1 the
      // picker can only hit those two.
      topo.Add(Region, 5, 5);
      topo.Add(Region, 5, 6);

      var dest = planner.TryPickDestination(topo, Region);

      Assert.AreEqual(new TileCoord(5, 6), dest,
          "Picker must skip the current tile and only return walkable distinct neighbours.");
    }

    #endregion

    #region TryStartWalkFromIdle — pathfinder failure

    /// <summary>
    /// Pins the second arm of the <c>path == null || path.Count &lt; 2</c>
    /// guard in <see cref="FaunaWanderPlanner.TryStartWalkFromIdle"/>:
    /// when the destination picker returns a candidate that's inside the
    /// topology's walkable set but disconnected from the agent's current
    /// tile, <see cref="FaunaPathfinder.FindPath"/> returns <c>null</c>
    /// and the planner must stay Idle rather than crash or transition to
    /// Walking with a null path. The peer test
    /// <c>TryStartWalkFromIdle_NoReachableDestination_ReturnsFalseStaysIdle</c>
    /// only exercises the destination-picker-returns-null arm because it
    /// makes only the current tile walkable; this test exercises the
    /// distinct case of a walkable-but-unreachable destination.
    /// </summary>
    [TestMethod]
    public void TryStartWalkFromIdle_PathfinderReturnsNullDueToDisconnect_StaysIdle() {
      // Arrange — start at (0,0). The destination picker can hit (3,3)
      // because it's in the walkable set, but A* can never reach it: the
      // walkability set is a disjoint union {start tile} ∪ {far tile}, no
      // bridge.
      var planner = MakePlanner(rng: new Random(1), wanderRadius: 4);
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.Add(Region, 0, 0);
      // Add one disconnected island the picker can reach; A* cannot
      // bridge to it because the 4-neighbours of (0,0) are all unwalkable.
      topo.Add(Region, 3, 3);

      // Act
      var started = planner.TryStartWalkFromIdle(topo, Region);

      // Assert
      Assert.IsFalse(started);
      Assert.AreEqual(FaunaWanderPlanner.State.Idle, planner.CurrentState);
      Assert.IsNull(planner.CurrentWaypoint);
    }

    #endregion

    #region IsAtFinalWaypoint

    /// <summary>
    /// Pins <see cref="FaunaWanderPlanner.IsAtFinalWaypoint"/>'s default
    /// false return when the planner is not in Walking state — Mod-side
    /// motion uses this to know when to apply the destination tile-
    /// offset (only on the last segment), and an Idle/Disabled planner
    /// must not report being on a final segment.
    /// </summary>
    [TestMethod]
    public void IsAtFinalWaypoint_IdleState_ReturnsFalse() {
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(0, 0));

      Assert.IsFalse(planner.IsAtFinalWaypoint);
    }

    /// <summary>
    /// Pins that <see cref="FaunaWanderPlanner.IsAtFinalWaypoint"/>
    /// returns false while still earlier than the final segment of an
    /// active walk. With LOS-smoothing the pathfinder typically collapses
    /// open regions to <c>[start, goal]</c>, in which case the very first
    /// waypoint is also the final one. To force at least one mid-path
    /// waypoint we pathfind through a single-row corridor.
    /// </summary>
    [TestMethod]
    public void IsAtFinalWaypoint_BeforeLastSegment_ReturnsFalse() {
      // Arrange — a 1xN corridor. After smoothing A* still produces
      // [(0,0), (1,0), ..., (N,0)] because each step is a unique row
      // change; with a single-row corridor LOS doesn't collapse since
      // every Bresenham step is already on the line.
      // To guarantee an L-shape (which smoothing cannot collapse to a
      // single segment), use a corridor that bends.
      var planner = MakePlanner(rng: new Random(1), wanderRadius: 6,
          maxDestinationAttempts: 200);
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      // Horizontal arm from (0,0) to (3,0).
      for (var x = 0; x <= 3; x++) topo.Add(Region, x, 0);
      // Vertical arm from (3,0) to (3,3).
      for (var y = 0; y <= 3; y++) topo.Add(Region, 3, y);

      // Loop until the destination picker selects (3,3) (or another
      // tile beyond the bend that forces a multi-waypoint smoothed path).
      // RNG is seeded; we'll roll a few times if needed.
      var startedMultiSegment = false;
      for (var attempt = 0; attempt < 20 && !startedMultiSegment; attempt++) {
        planner = MakePlanner(rng: new Random(attempt), wanderRadius: 6,
            maxDestinationAttempts: 200);
        planner.EnterIdle(new TileCoord(0, 0));
        if (planner.TryStartWalkFromIdle(topo, Region)) {
          // Multi-segment iff there's a waypoint *after* the current one.
          // We need at least one ArriveAtCurrentWaypoint that doesn't
          // exhaust the path AND at that point IsAtFinalWaypoint is false
          // for the segment before the last.
          if (!planner.IsAtFinalWaypoint) {
            startedMultiSegment = true;
          }
        }
      }

      // Assert — found a walk where, on the first waypoint, we're NOT
      // yet at the final one. This pins the "_pathIndex == Count-1" arm
      // returning false when pathIndex < Count-1.
      Assert.IsTrue(startedMultiSegment,
          "Expected at least one walk with an interior waypoint to pin "
          + "IsAtFinalWaypoint=false on a pre-final segment.");
    }

    /// <summary>
    /// Pins that <see cref="FaunaWanderPlanner.IsAtFinalWaypoint"/>
    /// returns true exactly on the final waypoint of the path — the
    /// signal Mod-side motion uses to apply the within-tile destination
    /// offset before the agent's animation transitions back to idle.
    /// </summary>
    [TestMethod]
    public void IsAtFinalWaypoint_OnFinalSegment_ReturnsTrue() {
      // Arrange — open-area path. After smoothing every open-region
      // path collapses to [start, goal], so the first (and only)
      // waypoint is the final one.
      var planner = MakePlanner(rng: new Random(1));
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);

      // Act
      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));

      // Assert — single-segment path: first waypoint = final waypoint.
      Assert.IsTrue(planner.IsAtFinalWaypoint,
          "Smoothed path in an open region is a single segment whose "
          + "only waypoint is the final waypoint.");
    }

    /// <summary>
    /// Pins that <see cref="FaunaWanderPlanner.IsAtFinalWaypoint"/>
    /// returns false once the path has been exhausted and the planner
    /// has returned to Idle — _path is null at that point, and the
    /// getter's path-null guard must catch it.
    /// </summary>
    [TestMethod]
    public void IsAtFinalWaypoint_AfterPathExhaustion_ReturnsFalse() {
      var planner = MakePlanner(rng: new Random(1));
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);
      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));

      // Drain.
      var safety = 100;
      while (!planner.ArriveAtCurrentWaypoint() && safety-- > 0) {
      }

      Assert.AreEqual(FaunaWanderPlanner.State.Idle, planner.CurrentState);
      Assert.IsFalse(planner.IsAtFinalWaypoint);
    }

    #endregion

    #region ArriveAtCurrentWaypoint — guard arms

    /// <summary>
    /// Pins <see cref="FaunaWanderPlanner.ArriveAtCurrentWaypoint"/>'s
    /// non-Walking guard: a caller that wakes a Disabled or freshly-Idle
    /// planner with an Arrive call must get the "exhausted" signal
    /// (<c>true</c>) so the Mod-side motion code transitions to Idle
    /// rather than dereferencing a null path.
    /// </summary>
    [TestMethod]
    public void ArriveAtCurrentWaypoint_NotWalking_ReturnsTrueWithoutMutation() {
      var planner = MakePlanner();
      planner.EnterIdle(new TileCoord(5, 5));

      var exhausted = planner.ArriveAtCurrentWaypoint();

      Assert.IsTrue(exhausted,
          "Arrive on a non-Walking planner short-circuits to the "
          + "exhausted-signal so the caller transitions to Idle.");
      Assert.AreEqual(new TileCoord(5, 5), planner.CurrentTile,
          "Guarded short-circuit must not mutate CurrentTile.");
    }

    /// <summary>
    /// Pins the Disabled-state arm of the guard in
    /// <see cref="FaunaWanderPlanner.ArriveAtCurrentWaypoint"/>: even
    /// though Disabled is terminal, a stale Arrive call from a
    /// Mod-side motion-loop draining after Disable() must not crash;
    /// it returns <c>true</c> without state mutation.
    /// </summary>
    [TestMethod]
    public void ArriveAtCurrentWaypoint_Disabled_ReturnsTrueWithoutMutation() {
      var planner = MakePlanner();
      // Reach Walking first so _path is non-null; then Disable.
      planner.EnterIdle(new TileCoord(0, 0));
      var topo = new FakeTopology();
      topo.AddRectAround(Region, 0, 0, radius: 5);
      Assert.IsTrue(planner.TryStartWalkFromIdle(topo, Region));
      planner.Disable();

      var exhausted = planner.ArriveAtCurrentWaypoint();

      Assert.IsTrue(exhausted);
      Assert.AreEqual(FaunaWanderPlanner.State.Disabled, planner.CurrentState);
    }

    #endregion

    #region ArriveAtCurrentWaypoint — non-exhausted advance

    /// <summary>
    /// Pins the <i>non-exhausted</i> arm of
    /// <see cref="FaunaWanderPlanner.ArriveAtCurrentWaypoint"/>:
    /// after arriving at an interior waypoint of a multi-segment path,
    /// the planner stays in Walking, advances <c>_pathIndex</c>, and
    /// returns <c>false</c>. The existing sibling tests cover the
    /// exhausted-on-arrive arm (open-area paths collapse to a single
    /// segment under LOS smoothing), but not the false-return arm
    /// where <c>_pathIndex &lt; _path.Count</c> after the increment.
    /// An L-shaped corridor forces a multi-waypoint smoothed path
    /// because LOS cannot bridge the bend.
    /// </summary>
    [TestMethod]
    public void ArriveAtCurrentWaypoint_InteriorWaypointOfLPath_StaysWalkingAndReturnsFalse() {
      // Arrange — L-shaped corridor (horizontal arm + vertical arm).
      // LOS smoothing cannot collapse the bend, so the smoothed path
      // contains at least one interior waypoint between start and goal.
      var topo = new FakeTopology();
      for (var x = 0; x <= 3; x++) topo.Add(Region, x, 0);
      for (var y = 0; y <= 3; y++) topo.Add(Region, 3, y);

      // Iterate seeds until the destination picker selects a tile that
      // forces a multi-waypoint smoothed path. Seeded RNG keeps this
      // deterministic per attempt.
      FaunaWanderPlanner? planner = null;
      var foundMultiSegment = false;
      for (var seed = 0; seed < 50 && !foundMultiSegment; seed++) {
        planner = MakePlanner(rng: new Random(seed), wanderRadius: 6,
            maxDestinationAttempts: 200);
        planner.EnterIdle(new TileCoord(0, 0));
        if (planner.TryStartWalkFromIdle(topo, Region)
            && !planner.IsAtFinalWaypoint) {
          foundMultiSegment = true;
        }
      }
      Assert.IsTrue(foundMultiSegment,
          "Test setup must produce a multi-waypoint path; the L-shape "
          + "should defeat LOS smoothing for at least one destination.");

      // Act — arrive at the first (interior) waypoint.
      var exhausted = planner!.ArriveAtCurrentWaypoint();

      // Assert — interior arrive does NOT exhaust the path. This pins
      // the `_pathIndex >= _path.Count` guard's false arm and the
      // `return false` at the bottom of the method.
      Assert.IsFalse(exhausted,
          "Arrive on an interior waypoint must return false (path not "
          + "yet exhausted); pins the non-exhausted arm of the guard.");
      Assert.AreEqual(FaunaWanderPlanner.State.Walking, planner.CurrentState,
          "Interior arrive must NOT transition to Idle.");
      Assert.IsNotNull(planner.CurrentWaypoint,
          "Interior arrive must leave the next waypoint available.");
    }

    #endregion

    #region DescribeStuck — unknown trigger fallback

    /// <summary>
    /// Pins the <c>default:</c> arm of
    /// <see cref="FaunaWanderPlanner.DescribeStuck"/>: an unknown
    /// <see cref="FaunaWanderPlanner.StuckTrigger"/> value (e.g. a new
    /// trigger added to the enum but not yet wired into the switch)
    /// must fall back to <c>trigger.ToString()</c> rather than
    /// returning <c>null</c> or throwing. The fallback exists so the
    /// diagnostic panel can still display <i>something</i> when a
    /// future trigger arm is added without updating the describer.
    /// </summary>
    [TestMethod]
    public void DescribeStuck_UnknownTrigger_FallsBackToEnumToString() {
      // Arrange — fabricate an out-of-range trigger value to drive the
      // default arm. Cast to bypass the enum's value-set check; this
      // is the same shape a forward-incompatible save or a future
      // enum extension would produce.
      var planner = MakePlanner();
      var unknown = (FaunaWanderPlanner.StuckTrigger)999;

      // Act
      var description = planner.DescribeStuck(unknown);

      // Assert — fallback is `trigger.ToString()` (the enum's default
      // ToString shows the underlying integer for unnamed values).
      Assert.AreEqual(unknown.ToString(), description,
          "Unknown trigger must fall back to enum ToString rather than "
          + "throwing or returning null.");
    }

    #endregion

  }

}

