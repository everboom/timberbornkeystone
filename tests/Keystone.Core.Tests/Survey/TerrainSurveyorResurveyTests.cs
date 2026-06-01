using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Ports;
using Keystone.Core.Survey;
using Keystone.Core.Tests.Helpers;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Survey {

  /// <summary>
  /// Pins the <see cref="TerrainSurveyor.ResurveyColumn"/> diff
  /// contract directly, without going through the region pipeline.
  /// The diff is the contract <c>RegionService.ProcessChanges</c>
  /// consumes; bugs here surface as misleading "region split / merge
  /// did the wrong thing" failures in region tests rather than
  /// localising to the surveyor's diff logic.
  ///
  /// <para>Per the method's docstring, the cases are:
  /// <list type="bullet">
  ///   <item>Surface disappears (no longer surveyed) → detach.</item>
  ///   <item>Surface was in-graph, now blocked → detach.</item>
  ///   <item>Surface was blocked, now in-graph → attach.</item>
  ///   <item>In-graph both, but (IsCave, IsSettled) flipped → detach + attach.</item>
  ///   <item>Newly appeared in-graph → attach.</item>
  ///   <item>Newly appeared blocked → no diff.</item>
  ///   <item>All-axes unchanged → no diff (pollables refresh in place).</item>
  /// </list></para>
  /// </summary>
  [TestClass]
  public class TerrainSurveyorResurveyTests {

    #region Helpers

    private static (FakeTerrain terrain, MutableFakeBuilding building, FakeBlocking blocking, TerrainSurveyor surveyor)
        SetupFlat(int width, int height, int z) {
      var terrain = new FakeTerrain(width, height);
      for (var x = 0; x < width; x++) {
        for (var y = 0; y < height; y++) {
          terrain.Heights[new TileCoord(x, y)] = new[] { z };
        }
      }
      var building = new MutableFakeBuilding();
      var blocking = new FakeBlocking();
      var surveyor = new TerrainSurveyor(terrain, building, blocking);
      surveyor.Survey();
      return (terrain, building, blocking, surveyor);
    }

    #endregion

    #region Surface appearance / disappearance

    [TestMethod]
    public void Resurvey_NewlyAppearedSurfaceInGraph_AttachesIt() {
      var (terrain, _, _, surveyor) = SetupFlat(width: 2, height: 1, z: 5);
      // (1, 0) didn't exist in the initial heights — wait, SetupFlat
      // sets every column. Use a different start.
      terrain.Heights.Remove(new TileCoord(1, 0));
      surveyor.Survey();  // reset to baseline without (1,0)

      terrain.Heights[new TileCoord(1, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));

      CollectionAssert.AreEquivalent(
          new[] { new SurfaceCoord(1, 0, 5) }, diff.Attached.ToList(),
          "A newly-surveyed in-graph surface attaches.");
      Assert.AreEqual(0, diff.Detached.Count);
    }

    [TestMethod]
    public void Resurvey_SurfaceDisappears_Detaches() {
      var (terrain, _, _, surveyor) = SetupFlat(width: 2, height: 1, z: 5);

      terrain.Heights.Remove(new TileCoord(1, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));

      CollectionAssert.AreEquivalent(
          new[] { new SurfaceCoord(1, 0, 5) }, diff.Detached.ToList(),
          "A disappeared in-graph surface detaches.");
      Assert.AreEqual(0, diff.Attached.Count);
    }

    [TestMethod]
    public void Resurvey_NewlyAppearedBlockedSurface_EmitsNoDiff() {
      // A surface that materialises already-blocked never enters any
      // region, so the diff has nothing to say about it.
      var (terrain, _, blocking, surveyor) = SetupFlat(width: 2, height: 1, z: 5);
      terrain.Heights.Remove(new TileCoord(1, 0));
      surveyor.Survey();

      terrain.Heights[new TileCoord(1, 0)] = new[] { 5 };
      blocking.Block(new SurfaceCoord(1, 0, 5));
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));

      Assert.AreEqual(0, diff.Attached.Count);
      Assert.AreEqual(0, diff.Detached.Count);
    }

    #endregion

    #region IsCave flip

    [TestMethod]
    public void Resurvey_IsCaveFlipsToTrue_DetachesAndReattaches() {
      // Stacking a new surface above an existing one turns the lower
      // surface into a cave. Region identity changes; the diff must
      // emit both detach (from old open region) and attach (to new
      // cave region).
      var (terrain, _, _, surveyor) = SetupFlat(width: 1, height: 1, z: 5);

      terrain.Heights[new TileCoord(0, 0)] = new[] { 5, 8 };
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));

      CollectionAssert.Contains(diff.Detached.ToList(), new SurfaceCoord(0, 0, 5),
          "Original open surface detaches as it becomes a cave.");
      CollectionAssert.Contains(diff.Attached.ToList(), new SurfaceCoord(0, 0, 5),
          "Same surface re-attaches under the cave identity.");
      CollectionAssert.Contains(diff.Attached.ToList(), new SurfaceCoord(0, 0, 8),
          "New top surface attaches.");
    }

    #endregion

    #region IsSettled flip

    [TestMethod]
    public void Resurvey_IsSettledFlipsToTrue_DetachesAndReattaches() {
      // Placing a building on a surface changes its IsSettled axis;
      // region identity flips so the diff emits detach + attach.
      var (_, building, _, surveyor) = SetupFlat(width: 1, height: 1, z: 5);

      building.SetAt(new SurfaceCoord(0, 0, 5),
          Keystone.Core.Buildings.BuildingKind.Building);
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));

      CollectionAssert.Contains(diff.Detached.ToList(), new SurfaceCoord(0, 0, 5));
      CollectionAssert.Contains(diff.Attached.ToList(), new SurfaceCoord(0, 0, 5));
    }

    #endregion

    #region IsBlocked transitions

    [TestMethod]
    public void Resurvey_InGraphToBlocked_DetachesNoReattach() {
      // Surface was in a region; blocking flag flips on. Detach only,
      // no attach (a blocked surface isn't in any region).
      var (_, _, blocking, surveyor) = SetupFlat(width: 1, height: 1, z: 5);

      blocking.Block(new SurfaceCoord(0, 0, 5));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));

      Assert.AreEqual(1, diff.Detached.Count);
      Assert.AreEqual(new SurfaceCoord(0, 0, 5), diff.Detached.First());
      Assert.AreEqual(0, diff.Attached.Count);
    }

    [TestMethod]
    public void Resurvey_BlockedToInGraph_AttachesNoDetach() {
      // Inverse of the previous test: blocked surface gets unblocked.
      var (_, _, blocking, surveyor) = SetupFlat(width: 1, height: 1, z: 5);
      blocking.Block(new SurfaceCoord(0, 0, 5));
      surveyor.Survey();  // baseline with blocked surface
      blocking.Unblock(new SurfaceCoord(0, 0, 5));

      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));

      Assert.AreEqual(1, diff.Attached.Count);
      Assert.AreEqual(new SurfaceCoord(0, 0, 5), diff.Attached.First());
      Assert.AreEqual(0, diff.Detached.Count);
    }

    #endregion

    #region No-op resurvey

    [TestMethod]
    public void Resurvey_AllStructuralAxesUnchanged_EmitsEmptyDiff() {
      // A column whose heights and survey state are identical to the
      // previous pass should not appear in the diff. Pollable refresh
      // happens in place but doesn't surface here.
      var (_, _, _, surveyor) = SetupFlat(width: 1, height: 1, z: 5);

      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));

      Assert.AreEqual(0, diff.Detached.Count);
      Assert.AreEqual(0, diff.Attached.Count);
    }

    #endregion

    #region Fakes

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
        if (!Heights.TryGetValue(column, out var list)) return Array.Empty<int>();
        var sorted = (int[])list.Clone();
        Array.Sort(sorted);
        return sorted;
      }

      public bool HasTerrainAbove(SurfaceCoord surface) {
        if (!Heights.TryGetValue(surface.Column, out var list)) return false;
        for (var i = 0; i < list.Length; i++) if (list[i] > surface.Z) return true;
        return false;
      }

      public bool IsTerrainVoxel(int x, int y, int z) => false;
    }

    private sealed class MutableFakeBuilding : IBuildingQuery {
      private readonly Dictionary<SurfaceCoord, Keystone.Core.Buildings.BuildingKind> _at = new();

      public void SetAt(SurfaceCoord voxel, Keystone.Core.Buildings.BuildingKind kind) {
        _at[voxel] = kind;
      }

      public Keystone.Core.Buildings.BuildingKind ClassifyAt(SurfaceCoord voxel) =>
          _at.TryGetValue(voxel, out var k) ? k : Keystone.Core.Buildings.BuildingKind.None;
    }

    #endregion

  }

}
