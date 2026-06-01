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
  /// Exercise the <see cref="IRegionState"/> contract with two sample
  /// implementations -- one extensive ("animal count": divides on
  /// split, sums on merge) and one intensive ("eco health": inherits
  /// on split, weighted-averages on merge). Verifies that
  /// <see cref="RegionService"/> routes split/merge through these
  /// transformations correctly across the same scenarios as the
  /// incremental tests.
  /// </summary>
  [TestClass]
  public class RegionStateTests {

    #region Sample state types

    /// <summary>
    /// Extensive state: total count of something. Divides proportionally
    /// on split (rounded), sums on merge.
    /// </summary>
    private sealed record AnimalCount(int Count) : IRegionState {
      public IRegionState ForChildOnSplit(double sizeRatio) =>
          new AnimalCount((int)Math.Round(Count * sizeRatio));

      public IRegionState Absorbing(IRegionState? other, int mySize, int otherSize) {
        var otherCount = (other as AnimalCount)?.Count ?? 0;
        return new AnimalCount(Count + otherCount);
      }
    }

    /// <summary>
    /// Intensive state: a 0..1 quality value. Inherits unchanged on
    /// split; weighted-averages on merge by region size.
    /// </summary>
    private sealed record EcoHealth(float Value) : IRegionState {
      public IRegionState ForChildOnSplit(double sizeRatio) => this;

      public IRegionState Absorbing(IRegionState? other, int mySize, int otherSize) {
        if (other is not EcoHealth o) return this;
        var total = mySize + otherSize;
        if (total <= 0) return this;
        var blended = (Value * mySize + o.Value * otherSize) / total;
        return new EcoHealth(blended);
      }
    }

    #endregion

    #region Plumbing -- get/set/has

    [TestMethod]
    public void GetState_NoneAttached_ReturnsNull() {
      var r = MakeRegion();
      Assert.IsNull(r.GetState<AnimalCount>());
      Assert.IsFalse(r.HasState<AnimalCount>());
    }

    [TestMethod]
    public void SetAndGetState_Roundtrip() {
      var r = MakeRegion();
      r.SetState(new AnimalCount(5));
      Assert.IsTrue(r.HasState<AnimalCount>());
      Assert.AreEqual(5, r.GetState<AnimalCount>()!.Count);
    }

    [TestMethod]
    public void DifferentStateTypes_Coexist() {
      var r = MakeRegion();
      r.SetState(new AnimalCount(3));
      r.SetState(new EcoHealth(0.7f));
      Assert.AreEqual(3, r.GetState<AnimalCount>()!.Count);
      Assert.AreEqual(0.7f, r.GetState<EcoHealth>()!.Value);
    }

    #endregion

    #region Split distributes state proportionally

    [TestMethod]
    public void Split_ExtensiveState_DividesProportionally() {
      // Arrange — 3x1 plateau, attach AnimalCount=10 to it.
      var terrain = LineTerrain(width: 3, z: 5);
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var originalId = regions.All.Single().Id;
      regions.Get(originalId)!.SetState(new AnimalCount(10));

      // Act — drop the middle column to z=4, splitting the line into two singletons.
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — both pieces are size 1. Phase 1 detach reduced parent's Size from 3 to 2
      // without touching state (state stays put on detach). Phase 3 split divides the state
      // by the post-detach size: ratio = 1/2 per piece, AnimalCount = round(10 * 0.5) = 5.
      // Total state across pieces is conserved (5 + 5 = 10).
      var pieces = regions.All.Where(r => r.Z == 5).ToList();
      Assert.AreEqual(2, pieces.Count);
      foreach (var p in pieces) {
        Assert.AreEqual(1, p.Size);
        Assert.AreEqual(5, p.GetState<AnimalCount>()!.Count, "extensive scaled by post-detach sizeRatio");
      }
    }

    [TestMethod]
    public void Split_IntensiveState_InheritedUnchanged() {
      // Arrange — 3x1 plateau with EcoHealth=0.8.
      var terrain = LineTerrain(width: 3, z: 5);
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var originalId = regions.All.Single().Id;
      regions.Get(originalId)!.SetState(new EcoHealth(0.8f));

      // Act — bisect.
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — both pieces inherit 0.8 unchanged.
      var pieces = regions.All.Where(r => r.Z == 5).ToList();
      Assert.AreEqual(2, pieces.Count);
      foreach (var p in pieces) {
        Assert.AreEqual(0.8f, p.GetState<EcoHealth>()!.Value, "intensive inherits unchanged");
      }
    }

    [TestMethod]
    public void Split_KeptIdRegion_GetsItsOwnSizeRatioToo() {
      // Arrange — 4x1 plateau with AnimalCount=12. Split asymmetrically.
      var terrain = LineTerrain(width: 4, z: 5);
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var originalId = regions.All.Single().Id;
      regions.Get(originalId)!.SetState(new AnimalCount(12));

      // Act — drop column 1 to z=4, splitting [0] and [2,3] (singleton + pair).
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — Phase 1 dropped parent's Size from 4 to 3. Phase 3 splits the AnimalCount
      // across post-detach sizes: pieces of sizes 1 and 2, ratios 1/3 and 2/3.
      // round(12 * 1/3) = 4, round(12 * 2/3) = 8. Sum = 12 (state conserved).
      var pieces = regions.All.Where(r => r.Z == 5).ToList();
      Assert.AreEqual(2, pieces.Count);
      Assert.IsTrue(pieces.Any(p => p.Id == originalId), "parent id retained");
      var totalCount = pieces.Sum(p => p.GetState<AnimalCount>()!.Count);
      Assert.AreEqual(12, totalCount, "extensive state conserved across split (sizes 1+2)");
      Assert.IsTrue(pieces.Any(p => p.GetState<AnimalCount>()!.Count == 4));
      Assert.IsTrue(pieces.Any(p => p.GetState<AnimalCount>()!.Count == 8));
    }

    [TestMethod]
    public void Split_RegionWithoutState_NoCrash() {
      // Arrange — a plain 3x1 plateau with no state attached.
      var terrain = LineTerrain(width: 3, z: 5);
      var (surveyor, regions) = Setup(terrain);
      regions.Index();

      // Act — split.
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — split happened, neither piece has state.
      var pieces = regions.All.Where(r => r.Z == 5).ToList();
      Assert.AreEqual(2, pieces.Count);
      foreach (var p in pieces) {
        Assert.IsFalse(p.HasState<AnimalCount>());
      }
    }

    #endregion

    #region Detach contract: state-unchanged + new-region-blank

    /// <summary>
    /// Plain detach (no split): the region keeps every state value it had
    /// pre-detach. Animals not killed by terrain edits.
    /// </summary>
    [TestMethod]
    public void Detach_WithoutSplit_LeavesStateUnchanged() {
      // Arrange — 3x3 plateau (donut survives a center tile removal without splitting).
      var terrain = SquareTerrain(side: 3, z: 5);
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var originalRegion = regions.All.Single(r => r.Z == 5);
      var originalId = originalRegion.Id;
      originalRegion.SetState(new AnimalCount(40));
      originalRegion.SetState(new EcoHealth(0.7f));

      // Act — drop the center column (no split: donut is still connected via the rim).
      terrain.Heights[new TileCoord(1, 1)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 1));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — z=5 region still single, ID preserved, state values both unchanged.
      var donut = regions.All.Single(r => r.Z == 5);
      Assert.AreEqual(originalId, donut.Id, "no split: ID stable");
      Assert.AreEqual(8, donut.Size, "donut size = 9 - 1");
      Assert.AreEqual(40, donut.GetState<AnimalCount>()!.Count,
          "extensive state UNCHANGED on detach (no animals killed by dynamite)");
      Assert.AreEqual(0.7f, donut.GetState<EcoHealth>()!.Value,
          "intensive state UNCHANGED on detach");
    }

    /// <summary>
    /// The new region created from a freshly-exposed lower surface (the
    /// crater) starts blank -- no animals, no eco-health, nothing
    /// inherited from the plateau above. Animals do not redistribute
    /// down into the crater.
    /// </summary>
    [TestMethod]
    public void Detach_NewlyExposedLowerSurface_HasNoState() {
      // Arrange — 3x1 plateau at z=5 with both extensive and intensive state.
      var terrain = LineTerrain(width: 3, z: 5);
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var plateau = regions.All.Single();
      plateau.SetState(new AnimalCount(10));
      plateau.SetState(new EcoHealth(0.6f));

      // Act — drop the middle column (creates a new z=4 region for the exposed lower surface).
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — the new z=4 region exists and is blank.
      var craterRegion = regions.All.SingleOrDefault(r => r.Z == 4);
      Assert.IsNotNull(craterRegion, "freshly-exposed lower surface forms a region");
      Assert.IsFalse(craterRegion!.HasState<AnimalCount>(),
          "crater region starts with no animals (not redistributed from plateau above)");
      Assert.IsFalse(craterRegion.HasState<EcoHealth>(),
          "crater region starts with no eco data of any kind");
    }

    /// <summary>
    /// A new surface that joins an existing region (because it shares
    /// (Z, IsCave) with a neighbor) does not modify the joined region's
    /// state. The region's Size grows by 1, but its AnimalCount stays
    /// where it was. (The implicit "per-surface density" decreases --
    /// fine, because the new surface didn't bring any new animals.)
    /// </summary>
    [TestMethod]
    public void Attach_JoiningExistingRegion_LeavesStateUnchanged() {
      // Arrange — 2x1 plateau at z=5 with state.
      var terrain = LineTerrain(width: 3, z: 5);
      // Strip column 2 so the plateau is just 2 tiles initially.
      terrain.Heights.Remove(new TileCoord(2, 0));
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var plateau = regions.All.Single();
      plateau.SetState(new AnimalCount(8));
      plateau.SetState(new EcoHealth(0.5f));

      // Act — extend the plateau by adding a third surface at (2, 0, 5).
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — region grew but state values are untouched.
      var grown = regions.All.Single();
      Assert.AreEqual(3, grown.Size);
      Assert.AreEqual(8, grown.GetState<AnimalCount>()!.Count,
          "joining surface does not bring new animals or kill existing ones");
      Assert.AreEqual(0.5f, grown.GetState<EcoHealth>()!.Value,
          "joining surface leaves intensive state alone");
    }

    #endregion

    #region Merge collapses state

    [TestMethod]
    public void Merge_ExtensiveState_Sums() {
      // Arrange — two plateaus, one with AnimalCount=4, the other with =6.
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 4 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();

      var leftRegion = regions.Containing(new SurfaceCoord(0, 0, 5))!;
      var rightRegion = regions.Containing(new SurfaceCoord(3, 0, 5))!;
      leftRegion.SetState(new AnimalCount(4));
      rightRegion.SetState(new AnimalCount(6));

      // Act — bridge.
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — single z=5 region with AnimalCount=10 (4 + 6).
      var merged = regions.All.Single(r => r.Z == 5);
      Assert.AreEqual(5, merged.Size);
      Assert.AreEqual(10, merged.GetState<AnimalCount>()!.Count);
    }

    [TestMethod]
    public void Merge_IntensiveState_WeightedAverages() {
      // Arrange — left plateau has EcoHealth=0.8 (size 2); right has 0.4 (size 2).
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 4 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();

      regions.Containing(new SurfaceCoord(0, 0, 5))!.SetState(new EcoHealth(0.8f));
      regions.Containing(new SurfaceCoord(3, 0, 5))!.SetState(new EcoHealth(0.4f));

      // Act — bridge.
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — weighted average over original sizes (2 and 2): (0.8*2 + 0.4*2) / 4 = 0.6.
      var merged = regions.All.Single(r => r.Z == 5);
      Assert.AreEqual(0.6f, merged.GetState<EcoHealth>()!.Value, 1e-5f);
    }

    [TestMethod]
    public void Merge_StateOnSurvivorOnly_AbsorbsNullCounterpart() {
      // Arrange — one side has state, the other doesn't.
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 4 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      regions.Containing(new SurfaceCoord(0, 0, 5))!.SetState(new AnimalCount(7));

      // Act
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — survivor's AnimalCount unchanged at 7 (sum with null = 7 + 0).
      var merged = regions.All.Single(r => r.Z == 5);
      Assert.AreEqual(7, merged.GetState<AnimalCount>()!.Count);
    }

    [TestMethod]
    public void Merge_StateOnLoserOnly_TransfersToSurvivor() {
      // Arrange — only the right side has state. The bridge tile attaches to whichever
      // becomes the merge survivor; the loser's state must transfer.
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 4 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      regions.Containing(new SurfaceCoord(3, 0, 5))!.SetState(new AnimalCount(5));

      // Act
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — merged region carries the AnimalCount even though the kept-id side didn't have it.
      var merged = regions.All.Single(r => r.Z == 5);
      Assert.AreEqual(5, merged.GetState<AnimalCount>()!.Count);
    }

    #endregion

    #region Setup helpers

    private static Region MakeRegion() =>
        new(new RegionId(0), z: 5, isCave: false, isSettled: false, size: 1,
            new GameTimestamp(0, 0, 0f), WeatherKind.Temperate, totalDaysAtCreation: 0f);

    private static FakeTerrain LineTerrain(int width, int z) {
      var t = new FakeTerrain(width, height: 1);
      for (var x = 0; x < width; x++) {
        t.Heights[new TileCoord(x, 0)] = new[] { z };
      }
      return t;
    }

    private static FakeTerrain SquareTerrain(int side, int z) {
      var t = new FakeTerrain(side, side);
      for (var x = 0; x < side; x++) {
        for (var y = 0; y < side; y++) {
          t.Heights[new TileCoord(x, y)] = new[] { z };
        }
      }
      return t;
    }

    private static (TerrainSurveyor surveyor, RegionService regions) Setup(FakeTerrain terrain) {
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, new FakeClock());
      return (surveyor, regions);
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
        if (!Heights.TryGetValue(column, out var list)) {
          return Array.Empty<int>();
        }
        var sorted = (int[])list.Clone();
        Array.Sort(sorted);
        return sorted;
      }

      public bool HasTerrainAbove(SurfaceCoord surface) {
        if (!Heights.TryGetValue(surface.Column, out var list)) return false;
        for (var i = 0; i < list.Length; i++) {
          if (list[i] > surface.Z) return true;
        }
        return false;
      }

      public bool IsTerrainVoxel(int x, int y, int z) => false;
    }

    private sealed class FakeMoisture : IMoistureQuery {
      public float MoistureAt(TileCoord column) => 0f;
      public bool IsMoistAt(SurfaceCoord surface) => false;
      public static FakeMoisture UniformDry() => new();
    }

    private sealed class FakeContamination : IContaminationQuery {
      public float ContaminationAt(TileCoord column) => 0f;
      public bool IsContaminatedAt(SurfaceCoord surface) => false;
      public static FakeContamination None() => new();
    }

    private sealed class FakeWater : IWaterQuery {
      public float WaterDepthAt(SurfaceCoord surface) => 0f;
      public float WaterSurfaceHeightAt(SurfaceCoord surface) => 0f;
      public FlowVector FlowAt(SurfaceCoord surface) => FlowVector.Zero;
      public bool HasWaterAtColumn(TileCoord column) => false;
      public float WaterContaminationAt(SurfaceCoord _) => 0f;
      public static FakeWater None() => new();
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
