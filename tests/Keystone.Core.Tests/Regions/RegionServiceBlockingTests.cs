using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Core.Tests.Helpers;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Regions {

  /// <summary>
  /// Exercises the <c>IsBlocked</c> axis of <see cref="SurfaceSurvey"/>
  /// and the corresponding <see cref="RegionService"/> behaviour.
  /// Blocked surfaces are excluded from region membership entirely:
  /// they are not part of any region, they split regions when they
  /// appear mid-component, and unblocking can merge two regions that
  /// were previously separated.
  ///
  /// <para>The Settled halo does not extend over blocked tiles -- a
  /// building neighbouring a blocked tile does not pull the blocked
  /// tile into Settled (because the blocked tile is in no region at
  /// all). Verified indirectly here; the halo computation itself is
  /// already covered by <see cref="RegionServiceIncrementalTests"/>.</para>
  /// </summary>
  [TestClass]
  public class RegionServiceBlockingTests {

    #region Index-time exclusion

    [TestMethod]
    public void Index_MiddleTileBlocked_ProducesTwoRegions() {
      // Arrange — 3x1 plateau at z=5, middle tile (1,0,5) flagged
      // blocked. Expect the surveyor to mark it IsBlocked=true and the
      // region builder to skip it, splitting the line into {0,0,5}
      // and {2,0,5} as two regions.
      var terrain = MakePlateau(width: 3, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      blocking.Block(new SurfaceCoord(1, 0, 5));

      var (surveyor, regions) = Setup(terrain, blocking);

      // Act
      regions.Index();

      // Assert
      Assert.AreEqual(2, regions.Count);
      Assert.IsNull(regions.Containing(new SurfaceCoord(1, 0, 5)),
          "Blocked surface must not belong to any region.");
      var left = regions.Containing(new SurfaceCoord(0, 0, 5));
      var right = regions.Containing(new SurfaceCoord(2, 0, 5));
      Assert.IsNotNull(left);
      Assert.IsNotNull(right);
      Assert.AreNotEqual(left!.Id, right!.Id, "Sides must be separate regions.");
    }

    [TestMethod]
    public void Index_BlockedSurfaceIsRecordedWithIsBlockedTrue() {
      var terrain = MakePlateau(width: 2, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      blocking.Block(new SurfaceCoord(0, 0, 5));

      var (surveyor, regions) = Setup(terrain, blocking);
      regions.Index();

      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var blocked));
      Assert.IsTrue(blocked.IsBlocked, "Blocked surface should carry IsBlocked=true.");
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(1, 0, 5), out var open));
      Assert.IsFalse(open.IsBlocked, "Open surface should carry IsBlocked=false.");
    }

    #endregion

    #region Incremental split / merge

    [TestMethod]
    public void ProcessChanges_BlockMiddleOfRegion_SplitsIntoTwo() {
      // Arrange — single 3x1 region, no blockages.
      var terrain = MakePlateau(width: 3, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      var (surveyor, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(1, regions.Count);

      // Act — block the middle tile mid-game.
      blocking.Block(new SurfaceCoord(1, 0, 5));
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — region splits into two.
      Assert.AreEqual(2, regions.Count);
      Assert.IsNull(regions.Containing(new SurfaceCoord(1, 0, 5)),
          "Newly blocked surface must drop out of all regions.");
      var left = regions.Containing(new SurfaceCoord(0, 0, 5));
      var right = regions.Containing(new SurfaceCoord(2, 0, 5));
      Assert.IsNotNull(left);
      Assert.IsNotNull(right);
      Assert.AreNotEqual(left!.Id, right!.Id);
    }

    [TestMethod]
    public void ProcessChanges_UnblockBetweenTwoRegions_MergesIntoOne() {
      // Arrange — 3x1 plateau with the middle tile blocked at Index
      // time: two regions on the sides.
      var terrain = MakePlateau(width: 3, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      blocking.Block(new SurfaceCoord(1, 0, 5));
      var (surveyor, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(2, regions.Count);

      // Act — remove the blockage.
      blocking.Unblock(new SurfaceCoord(1, 0, 5));
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — left and right merge into one region containing all three tiles.
      Assert.AreEqual(1, regions.Count);
      var merged = regions.All.Single();
      Assert.AreEqual(3, merged.Size);
      Assert.AreEqual(merged.Id, regions.Containing(new SurfaceCoord(0, 0, 5))!.Id);
      Assert.AreEqual(merged.Id, regions.Containing(new SurfaceCoord(1, 0, 5))!.Id);
      Assert.AreEqual(merged.Id, regions.Containing(new SurfaceCoord(2, 0, 5))!.Id);
    }

    [TestMethod]
    public void ProcessChanges_BlockEdgeOfRegion_ShrinksRegionNoSplit() {
      // Arrange — single 3x1 region.
      var terrain = MakePlateau(width: 3, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      var (surveyor, regions) = Setup(terrain, blocking);
      regions.Index();
      var originalId = regions.All.Single().Id;

      // Act — block the rightmost tile.
      blocking.Block(new SurfaceCoord(2, 0, 5));
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — same region, two members, blocked tile excluded.
      Assert.AreEqual(1, regions.Count);
      var region = regions.All.Single();
      Assert.AreEqual(originalId, region.Id, "ID stable when shrinking from the edge.");
      Assert.AreEqual(2, region.Size);
      Assert.IsNull(regions.Containing(new SurfaceCoord(2, 0, 5)));
    }

    [TestMethod]
    public void ProcessChanges_UnblockIsolatedTile_CreatesNewRegion() {
      // Arrange — single blocked tile with no neighbours.
      var terrain = MakePlateau(width: 1, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      blocking.Block(new SurfaceCoord(0, 0, 5));
      var (surveyor, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(0, regions.Count, "Lone blocked tile yields no region.");

      // Act — unblock.
      blocking.Unblock(new SurfaceCoord(0, 0, 5));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — a fresh region appears.
      Assert.AreEqual(1, regions.Count);
      var region = regions.All.Single();
      Assert.AreEqual(1, region.Size);
      Assert.AreEqual(region.Id, regions.Containing(new SurfaceCoord(0, 0, 5))!.Id);
    }

    #endregion

    #region Diff invariants

    [TestMethod]
    public void Resurvey_NewlyBlockedSurface_ProducesDetachOnly() {
      // Arrange — single tile in a region.
      var terrain = MakePlateau(width: 1, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      var (surveyor, _) = Setup(terrain, blocking);
      surveyor.Survey();

      // Act — block it.
      blocking.Block(new SurfaceCoord(0, 0, 5));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));

      // Assert — detach-only.
      CollectionAssert.AreEquivalent(
          new[] { new SurfaceCoord(0, 0, 5) },
          diff.Detached.ToList(),
          "Newly blocked surface must appear in Detached.");
      Assert.AreEqual(0, diff.Attached.Count, "And must NOT appear in Attached.");
    }

    [TestMethod]
    public void Resurvey_NewlyUnblockedSurface_ProducesAttachOnly() {
      // Arrange — single blocked tile.
      var terrain = MakePlateau(width: 1, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      blocking.Block(new SurfaceCoord(0, 0, 5));
      var (surveyor, _) = Setup(terrain, blocking);
      surveyor.Survey();

      // Act
      blocking.Unblock(new SurfaceCoord(0, 0, 5));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));

      // Assert — attach-only.
      Assert.AreEqual(0, diff.Detached.Count);
      CollectionAssert.AreEquivalent(
          new[] { new SurfaceCoord(0, 0, 5) },
          diff.Attached.ToList(),
          "Newly unblocked surface must appear in Attached.");
    }

    [TestMethod]
    public void Resurvey_BlockedSurfaceThatVanishes_ProducesNoDiff() {
      // Arrange — single blocked tile.
      var terrain = MakePlateau(width: 1, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      blocking.Block(new SurfaceCoord(0, 0, 5));
      var (surveyor, _) = Setup(terrain, blocking);
      surveyor.Survey();

      // Act — the terrain disappears entirely (e.g., dynamited away).
      terrain.Heights.Remove(new TileCoord(0, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));

      // Assert — the blocked surface was never in any region, so its
      // disappearance produces no detach event.
      Assert.AreEqual(0, diff.Detached.Count);
      Assert.AreEqual(0, diff.Attached.Count);
    }

    #endregion

    #region FindRegionByChunkFootprint (chunk-value spatial rescue)

    [TestMethod]
    public void FindRegionByChunkFootprint_SingleRegionFillsChunk_ReturnsThatRegion() {
      // Arrange — 4x4 plateau, all in one region, exactly one ChunkSize=4 chunk at (0,0).
      var terrain = MakePlateau4x4(z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      var (_, regions) = Setup(terrain, blocking);
      regions.Index();
      var only = regions.All.Single();

      // Act
      var owner = regions.FindRegionByChunkFootprint(chunkX: 0, chunkY: 0, chunkSize: 4);

      // Assert
      Assert.AreEqual(only.Id, owner);
    }

    [TestMethod]
    public void FindRegionByChunkFootprint_ChunkStraddlesSplit_ReturnsMajoritySide() {
      // Arrange — 4x4 plateau with the middle column (x=2) blocked, splitting
      // the region into a 2-tile-wide left piece (8 surfaces) and a 1-tile-
      // wide right piece (4 surfaces). The blocked column itself contributes
      // no surfaces to either region. The chunk at (0, 0) covers x in [0..3],
      // y in [0..3] -- straddling both regions but with majority on the left.
      var terrain = MakePlateau4x4(z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      for (var y = 0; y < 4; y++) {
        blocking.Block(new SurfaceCoord(2, y, 5));
      }
      var (_, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(2, regions.Count, "Blocked column should split the plateau.");

      // Identify the majority-side region (whichever owns (0, 0, 5)).
      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;

      // Act
      var owner = regions.FindRegionByChunkFootprint(chunkX: 0, chunkY: 0, chunkSize: 4);

      // Assert — left side has 8 surfaces, right has 4; majority wins.
      Assert.AreEqual(leftId, owner);
    }

    [TestMethod]
    public void FindRegionByChunkFootprint_AllBlocked_ReturnsNull() {
      // Arrange — every surface in the chunk's footprint is blocked.
      var terrain = MakePlateau4x4(z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      for (var x = 0; x < 4; x++) {
        for (var y = 0; y < 4; y++) {
          blocking.Block(new SurfaceCoord(x, y, 5));
        }
      }
      var (_, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(0, regions.Count);

      // Act
      var owner = regions.FindRegionByChunkFootprint(chunkX: 0, chunkY: 0, chunkSize: 4);

      // Assert
      Assert.IsNull(owner);
    }

    [TestMethod]
    public void FindRegionByChunkFootprint_FootprintOutOfBounds_ReturnsNull() {
      // Arrange — a small 1x1 map; chunk (5, 5) is far off the map.
      var terrain = MakePlateau(width: 1, z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      var (_, regions) = Setup(terrain, blocking);
      regions.Index();

      // Act
      var owner = regions.FindRegionByChunkFootprint(chunkX: 5, chunkY: 5, chunkSize: 4);

      // Assert
      Assert.IsNull(owner);
    }

    [TestMethod]
    public void FindRegionByChunkFootprint_StackedRegions_NoTargetZBugIsPossible() {
      // INVARIANT-PINNING TEST. Documents the failure mode that the
      // Z constraint exists to prevent: without targetZ, the rescue
      // picks ONE of two stacked regions arbitrarily (by majority
      // count + lowest id), so any caller that omits targetZ in a
      // stacked-region scenario WILL silently misattach chunk data
      // to the wrong Z layer. See ChunkValueKey's Z invariant.
      //
      // If you're reading this test because it failed: someone has
      // changed FindRegionByChunkFootprint to require targetZ
      // (refusing the no-arg case). That's a stronger invariant than
      // we currently encode -- before strengthening it, audit every
      // caller passing null targetZ to ensure they're either v1-save
      // fallbacks or otherwise immune to cross-Z confusion.
      var terrain = new FakeTerrain(width: 4, height: 4);
      for (var x = 0; x < 4; x++) {
        for (var y = 0; y < 4; y++) {
          terrain.Heights[new TileCoord(x, y)] = new[] { 5, 10 };
        }
      }
      var blocking = FakeBlocking.NothingBlocked();
      var (_, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(2, regions.Count);

      // Without targetZ the result is well-defined (lowest id by tie-
      // break) but bears no relation to the caller's intended layer.
      // That's the bug the invariant prevents.
      var ambiguousResult = regions.FindRegionByChunkFootprint(
          chunkX: 0, chunkY: 0, chunkSize: 4);
      Assert.IsNotNull(ambiguousResult,
          "Without targetZ the rescue returns SOMETHING -- which is precisely " +
          "the risk: silent misattachment to whichever layer wins the tie-break.");
    }

    [TestMethod]
    public void FindRegionByChunkFootprint_StackedRegions_TargetZSelectsCorrectLayer() {
      // Arrange — two surface layers at every (x,y) in the chunk:
      // a lower layer at z=5 and an upper layer at z=10. Each layer
      // forms its own region (different Z → distinct region identity).
      // Without a Z constraint, the rescue can't tell them apart and
      // ties / arbitrary majority picks the wrong layer; this is the
      // bug Z-strict mode fixes.
      var terrain = new FakeTerrain(width: 4, height: 4);
      for (var x = 0; x < 4; x++) {
        for (var y = 0; y < 4; y++) {
          terrain.Heights[new TileCoord(x, y)] = new[] { 5, 10 };
        }
      }
      var blocking = FakeBlocking.NothingBlocked();
      var (_, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(2, regions.Count, "Two stacked layers should form two regions.");
      var lowerId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var upperId = regions.Containing(new SurfaceCoord(0, 0, 10))!.Id;
      Assert.AreNotEqual(lowerId, upperId);

      // Act + Assert — Z=5 selects the lower region; Z=10 selects upper.
      Assert.AreEqual(
          lowerId,
          regions.FindRegionByChunkFootprint(chunkX: 0, chunkY: 0, chunkSize: 4, targetZ: 5),
          "targetZ=5 must route the rescue to the lower-layer region.");
      Assert.AreEqual(
          upperId,
          regions.FindRegionByChunkFootprint(chunkX: 0, chunkY: 0, chunkSize: 4, targetZ: 10),
          "targetZ=10 must route the rescue to the upper-layer region.");
    }

    [TestMethod]
    public void FindRegionByChunkFootprint_StackedRegions_TargetZNoMatchReturnsNull() {
      // Arrange — only an upper region at z=10; no surface at z=5.
      var terrain = new FakeTerrain(width: 4, height: 4);
      for (var x = 0; x < 4; x++) {
        for (var y = 0; y < 4; y++) {
          terrain.Heights[new TileCoord(x, y)] = new[] { 10 };
        }
      }
      var blocking = FakeBlocking.NothingBlocked();
      var (_, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(1, regions.Count);

      // Act — request rescue at Z=5 (no live region at this Z exists).
      var owner = regions.FindRegionByChunkFootprint(
          chunkX: 0, chunkY: 0, chunkSize: 4, targetZ: 5);

      // Assert — strict Z match: drop rather than misattach to the z=10
      // region. Per design, losing maturity on a small number of edge
      // chunks is preferable to silently misapplying it across layers.
      Assert.IsNull(owner);
    }

    [TestMethod]
    public void FindRegionByChunkFootprint_TwoEqualPieces_ReturnsLowestId() {
      // Arrange — 4x4 plateau, middle TWO columns blocked (x=1 and x=2),
      // leaves a 1-wide left piece (4 surfaces) and 1-wide right piece
      // (4 surfaces). Tie -> lowest RegionId wins.
      var terrain = MakePlateau4x4(z: 5);
      var blocking = FakeBlocking.NothingBlocked();
      for (var y = 0; y < 4; y++) {
        blocking.Block(new SurfaceCoord(1, y, 5));
        blocking.Block(new SurfaceCoord(2, y, 5));
      }
      var (_, regions) = Setup(terrain, blocking);
      regions.Index();
      Assert.AreEqual(2, regions.Count);
      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;
      var lowestId = leftId.Value < rightId.Value ? leftId : rightId;

      // Act
      var owner = regions.FindRegionByChunkFootprint(chunkX: 0, chunkY: 0, chunkSize: 4);

      // Assert
      Assert.AreEqual(lowestId, owner);
    }

    #endregion

    #region Setup + fakes

    private static FakeTerrain MakePlateau(int width, int z) {
      var t = new FakeTerrain(width: width, height: 1);
      for (var x = 0; x < width; x++) {
        t.Heights[new TileCoord(x, 0)] = new[] { z };
      }
      return t;
    }

    private static FakeTerrain MakePlateau4x4(int z) {
      var t = new FakeTerrain(width: 4, height: 4);
      for (var x = 0; x < 4; x++) {
        for (var y = 0; y < 4; y++) {
          t.Heights[new TileCoord(x, y)] = new[] { z };
        }
      }
      return t;
    }

    private static (TerrainSurveyor surveyor, RegionService regions) Setup(
        FakeTerrain terrain, FakeBlocking blocking) {
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), blocking);
      surveyor.Survey();
      var regions = new RegionService(surveyor, new FakeClock());
      return (surveyor, regions);
    }

    private sealed class FakeTerrain : ITerrainQuery {
      public FakeTerrain(int width, int height) {
        Width = width;
        Height = height;
      }

      public int Width { get; }
      public int Height { get; }
      public int MaxHeight { get; set; } = 16;
      public Dictionary<TileCoord, int[]> Heights { get; } = new();

      public bool Contains(TileCoord column) =>
          column.X >= 0 && column.X < Width && column.Y >= 0 && column.Y < Height;

      public IReadOnlyList<int> SurfaceHeightsAt(TileCoord column) {
        if (!Heights.TryGetValue(column, out var list)) {
          return Array.Empty<int>();
        }
        var sorted = (int[])list.Clone();
        Array.Sort(sorted);
        return sorted;
      }

      public bool HasTerrainAbove(SurfaceCoord surface) {
        if (!Heights.TryGetValue(surface.Column, out var list)) {
          return false;
        }
        for (var i = 0; i < list.Length; i++) {
          if (list[i] > surface.Z) {
            return true;
          }
        }
        return false;
      }

      public bool IsTerrainVoxel(int x, int y, int z) => false;
    }

    private sealed class FakeBuilding : IBuildingQuery {
      public Keystone.Core.Buildings.BuildingKind ClassifyAt(SurfaceCoord voxel) =>
          Keystone.Core.Buildings.BuildingKind.None;
      public static FakeBuilding NothingBuilt() => new();
    }

    private sealed class FakeClock : IClock {
      public GameTimestamp Now { get; set; } = GameTimestamp.Origin;
      public WeatherKind CurrentWeather { get; set; } = WeatherKind.Temperate;
      public float TotalDaysElapsed { get; set; }
    }

    #endregion

  }

}
