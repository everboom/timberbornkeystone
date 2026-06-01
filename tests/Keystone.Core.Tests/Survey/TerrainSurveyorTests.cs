using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Ecology;
using Keystone.Core.Ports;
using Keystone.Core.Survey;
using Keystone.Core.Tests.Helpers;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Survey {

  /// <summary>
  /// Drives <see cref="TerrainSurveyor"/> with hand-rolled fake ports to
  /// prove the port/adapter seam works without any Timberborn DLLs and
  /// to lock in the single-pass surface-data model.
  ///
  /// <para>The surveyor only caches the structural per-surface state
  /// (existence, <c>IsCave</c>, <c>IsSettled</c>). Volatile ecological
  /// inputs (water depth / flow / moisture / contamination) are
  /// queried live by consumers, not stored on
  /// <see cref="SurfaceSurvey"/>, so the surveyor takes only the
  /// terrain + building ports.</para>
  /// </summary>
  [TestClass]
  public class TerrainSurveyorTests {

    #region Enumeration

    [TestMethod]
    public void Survey_FlatMap_PopulatesOneSurfacePerColumn() {
      // Arrange — 3x2 flat map at z=5.
      var terrain = FakeTerrain.Flat(width: 3, height: 2, surfaceZ: 5);
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());

      // Act
      var result = surveyor.Survey();

      // Assert
      Assert.AreEqual(6, result.Surfaces);
      Assert.AreEqual(6, result.Columns);
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(2, 1, 5), out var tile));
      Assert.IsFalse(tile.IsCave);
      Assert.IsFalse(tile.IsSettled);
    }

    [TestMethod]
    public void Survey_StackedSurfaces_ProducesEntryPerSurface() {
      // Arrange — single column with three buildable surfaces.
      var terrain = FakeTerrain.Single(x: 0, y: 0, surfaceZs: new[] { 2, 5, 8 });
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.AreEqual(3, surveyor.Surfaces.Count);
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 2), out _));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out _));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 8), out _));
      Assert.AreEqual(1, surveyor.Surfaces.Entries.Count(e => e.Key.Z == 8));
    }

    #endregion

    #region Cave classification

    [TestMethod]
    public void Survey_StackedColumn_LowerSurfaceIsCave_TopmostIsNot() {
      // Arrange — single column, two surfaces; the lower has terrain above.
      var terrain = FakeTerrain.Single(x: 0, y: 0, surfaceZs: new[] { 2, 8 });
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 2), out var lower));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 8), out var upper));
      Assert.IsTrue(lower.IsCave);
      Assert.IsFalse(upper.IsCave);
    }

    [TestMethod]
    public void Survey_FlatMap_NoSurfaceIsCave() {
      // Arrange
      var terrain = FakeTerrain.Flat(width: 3, height: 3, surfaceZ: 4);
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      foreach (var entry in surveyor.Surfaces.Entries) {
        Assert.IsFalse(entry.Value.IsCave, $"surface {entry.Key} should not be cave");
      }
    }

    #endregion

    #region IsSettled detection

    [TestMethod]
    public void Survey_BuildingDirectlyAboveSurface_TagsAsSettled() {
      // Arrange — flat surface at (0, 0, 5); a Building voxel sits at (0, 0, 5).
      // (Surface Z is the air-voxel-above-terrain "buildable position";
      // buildings occupy surface.Z directly.)
      var terrain = FakeTerrain.Flat(width: 1, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v =>
          v.X == 0 && v.Y == 0 && v.Z == 5 ? Keystone.Core.Buildings.BuildingKind.Building : Keystone.Core.Buildings.BuildingKind.None);
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var s));
      Assert.IsTrue(s.IsSettled);
    }

    [TestMethod]
    public void Survey_TreeAboveSurface_DoesNotTagAsSettled() {
      // Arrange — even though a block object is at (0, 0, 6), it has no BuildingSpec,
      // so the adapter would have classified it as None. Surface is NOT Settled.
      var terrain = FakeTerrain.Flat(width: 1, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(_ => Keystone.Core.Buildings.BuildingKind.None);
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var s));
      Assert.IsFalse(s.IsSettled);
    }

    [TestMethod]
    public void Survey_PathAdjacentToBuilding_TagsAsSettled() {
      // Arrange — 2x1 map. (0, 0, 5) has a Path; (1, 0, 5) has a Building.
      // Under the halo rule the path-vs-empty distinction doesn't matter --
      // (0, 0) is in the building's 8-neighbour halo so it's Settled.
      var terrain = FakeTerrain.Flat(width: 2, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v => {
        if (v.Z != 5) return Keystone.Core.Buildings.BuildingKind.None;
        if (v.X == 0 && v.Y == 0) return Keystone.Core.Buildings.BuildingKind.Path;
        if (v.X == 1 && v.Y == 0) return Keystone.Core.Buildings.BuildingKind.Building;
        return Keystone.Core.Buildings.BuildingKind.None;
      });
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert — both the building's surface and the adjacent path's surface are Settled.
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var pathSurface));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(1, 0, 5), out var buildingSurface));
      Assert.IsTrue(pathSurface.IsSettled, "voxel in building halo → Settled");
      Assert.IsTrue(buildingSurface.IsSettled, "building → Settled");
    }

    [TestMethod]
    public void Survey_EmptyTileAdjacentToBuilding_TagsAsSettled() {
      // Arrange — 2x1 map. (0, 0, 5) is empty (None), (1, 0, 5) has a Building.
      // Under the halo rule the empty tile next to the building is Settled --
      // this is the changed behaviour that motivated dropping the
      // path-only formulation. Pre-halo, (0, 0) would have been NOT Settled.
      var terrain = FakeTerrain.Flat(width: 2, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v =>
          v.Z == 5 && v.X == 1 && v.Y == 0
              ? Keystone.Core.Buildings.BuildingKind.Building
              : Keystone.Core.Buildings.BuildingKind.None);
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var emptyHalo));
      Assert.IsTrue(emptyHalo.IsSettled, "empty tile adjacent to building → Settled (halo rule)");
    }

    [TestMethod]
    public void Survey_EmptyTileBetweenBuildings_TagsAsSettled() {
      // Arrange — 3x1 map. Buildings at (0, 0) and (2, 0); empty tile at (1, 0).
      // The "courtyard" case the halo rule is designed to fix: an unbuilt
      // tile wedged between buildings should belong to the same Settled
      // region. Pre-halo, this would have been NOT Settled.
      var terrain = FakeTerrain.Flat(width: 3, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v => {
        if (v.Z != 5) return Keystone.Core.Buildings.BuildingKind.None;
        if (v.X == 0 || v.X == 2) return Keystone.Core.Buildings.BuildingKind.Building;
        return Keystone.Core.Buildings.BuildingKind.None;
      });
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(1, 0, 5), out var courtyard));
      Assert.IsTrue(courtyard.IsSettled, "empty courtyard tile between buildings → Settled");
    }

    [TestMethod]
    public void Survey_IsolatedPath_DoesNotTagAsSettled() {
      // Arrange — 5x1 map. All surfaces have a Path; no building anywhere.
      // Path is no longer privileged by the Settled rule: without a
      // building in the halo, paths are not Settled. (Locks down against
      // any future temptation to special-case path → Settled.)
      var terrain = FakeTerrain.Flat(width: 5, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v =>
          v.Z == 5 ? Keystone.Core.Buildings.BuildingKind.Path : Keystone.Core.Buildings.BuildingKind.None);
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert — every surface has IsSettled=false despite the path coverage.
      for (var x = 0; x < 5; x++) {
        Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(x, 0, 5), out var s));
        Assert.IsFalse(s.IsSettled, $"isolated path at x={x} should NOT be Settled");
      }
    }

    [TestMethod]
    public void Survey_VoxelTwoStepsFromBuilding_NotSettled() {
      // Arrange — 5x1 map. Buildings at (0, 0) and (4, 0); the middle tile
      // (2, 0) is two steps from both → out of the halo of either → NOT
      // Settled. The halo radius is 1; this guards against any drift to a
      // larger radius (which would silently merge distant infrastructure).
      var terrain = FakeTerrain.Flat(width: 5, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v => {
        if (v.Z != 5) return Keystone.Core.Buildings.BuildingKind.None;
        if (v.X == 0 || v.X == 4) return Keystone.Core.Buildings.BuildingKind.Building;
        return Keystone.Core.Buildings.BuildingKind.None;
      });
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var b0));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(1, 0, 5), out var halo1));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(2, 0, 5), out var middle));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(3, 0, 5), out var halo3));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(4, 0, 5), out var b4));

      Assert.IsTrue(b0.IsSettled, "left building → Settled");
      Assert.IsTrue(halo1.IsSettled, "voxel adjacent to left building → Settled (halo)");
      Assert.IsFalse(middle.IsSettled, "middle voxel two steps from any building → NOT Settled");
      Assert.IsTrue(halo3.IsSettled, "voxel adjacent to right building → Settled (halo)");
      Assert.IsTrue(b4.IsSettled, "right building → Settled");
    }

    #endregion

    #region No-aura settlement rule

    [TestMethod]
    public void Survey_NoAuraBuildingDirectlyAboveSurface_TagsAsSettled() {
      // Arrange — single column at (0, 0, 5); a no-aura building (a
      // lantern, conceptually) sits there. Self-check must still mark
      // the surface as settled: the building IS infrastructure on this
      // tile, the no-aura tag only suppresses aura propagation.
      var terrain = FakeTerrain.Flat(width: 1, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v =>
          v.X == 0 && v.Y == 0 && v.Z == 5
              ? Keystone.Core.Buildings.BuildingKind.BuildingNoAura
              : Keystone.Core.Buildings.BuildingKind.None);
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var s));
      Assert.IsTrue(s.IsSettled,
          "no-aura building self-tile → Settled (self-check accepts both Building and BuildingNoAura)");
    }

    [TestMethod]
    public void Survey_TileAdjacentToNoAuraBuilding_NotSettled() {
      // Arrange — 2x1 map. (0, 0, 5) empty, (1, 0, 5) has a no-aura
      // building. Under the no-aura rule the empty neighbor must NOT be
      // marked Settled — that's the entire reason BuildingNoAura exists.
      // Compare with the parallel Survey_EmptyTileAdjacentToBuilding...
      // test above which DOES mark it Settled under the normal halo.
      // ===
      // REGRESSION-SENSITIVE: if this asserts true, the surveyor's
      // neighbor check has been broadened to include BuildingNoAura,
      // which would silently sterilize a 3×3 area around every lantern
      // / scarecrow / beehive — exactly the bug the no-aura tier exists
      // to prevent.
      var terrain = FakeTerrain.Flat(width: 2, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v =>
          v.Z == 5 && v.X == 1 && v.Y == 0
              ? Keystone.Core.Buildings.BuildingKind.BuildingNoAura
              : Keystone.Core.Buildings.BuildingKind.None);
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var neighbor));
      Assert.IsFalse(neighbor.IsSettled,
          "empty tile next to a no-aura building → NOT Settled (no aura propagation)");
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(1, 0, 5), out var self));
      Assert.IsTrue(self.IsSettled, "no-aura building's own tile → Settled");
    }

    [TestMethod]
    public void Survey_TileBetweenNoAuraBuildings_NotSettled() {
      // Arrange — 3x1 map. No-aura buildings at (0, 0) and (2, 0); empty
      // (1, 0) between them. Under the normal halo rule this courtyard
      // case settles (Survey_EmptyTileBetweenBuildings_TagsAsSettled
      // above). Under the no-aura rule it stays wild, because neither
      // flanking building propagates an aura.
      var terrain = FakeTerrain.Flat(width: 3, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v => {
        if (v.Z != 5) return Keystone.Core.Buildings.BuildingKind.None;
        if (v.X == 0 || v.X == 2) return Keystone.Core.Buildings.BuildingKind.BuildingNoAura;
        return Keystone.Core.Buildings.BuildingKind.None;
      });
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(1, 0, 5), out var courtyard));
      Assert.IsFalse(courtyard.IsSettled,
          "empty tile flanked by two no-aura buildings → NOT Settled (neither propagates aura)");
    }

    [TestMethod]
    public void Survey_NoAuraBuildingInsideRegularBuildingHalo_TagsAsSettled() {
      // Arrange — 2x1 map. (0, 0, 5) normal Building, (1, 0, 5) no-aura
      // building. The no-aura tile self-settles AND is also in the
      // normal building's halo, so settles via two independent reasons.
      // This pin documents the interaction: BuildingNoAura's neighbor
      // exclusion only suppresses ITS OWN aura emission — it doesn't
      // block other buildings' auras from reaching it.
      var terrain = FakeTerrain.Flat(width: 2, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v => {
        if (v.Z != 5) return Keystone.Core.Buildings.BuildingKind.None;
        if (v.X == 0 && v.Y == 0) return Keystone.Core.Buildings.BuildingKind.Building;
        if (v.X == 1 && v.Y == 0) return Keystone.Core.Buildings.BuildingKind.BuildingNoAura;
        return Keystone.Core.Buildings.BuildingKind.None;
      });
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(0, 0, 5), out var normal));
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(1, 0, 5), out var noAura));
      Assert.IsTrue(normal.IsSettled, "normal building → Settled");
      Assert.IsTrue(noAura.IsSettled,
          "no-aura building inside a normal building's halo → Settled (self + neighbor)");
    }

    [TestMethod]
    public void Survey_EmptyTileNextToNoAuraNextToNormalBuilding_TagsAsSettledByNormalAura() {
      // Arrange — 3x1 map. Normal Building at (0, 0), no-aura at
      // (1, 0), empty at (2, 0). The empty tile is two steps from the
      // normal building (out of its halo) and one step from the no-aura
      // building (whose aura is suppressed). Should NOT be Settled.
      // ===
      // Catches a subtle regression risk: if the no-aura rule were
      // accidentally framed as "transparent to neighbor lookups"
      // instead of "doesn't emit", a no-aura building between a normal
      // building and an empty tile might be misread as bridging the
      // halo two steps further.
      var terrain = FakeTerrain.Flat(width: 3, height: 1, surfaceZ: 5);
      var building = new FakeBuilding(v => {
        if (v.Z != 5) return Keystone.Core.Buildings.BuildingKind.None;
        if (v.X == 0 && v.Y == 0) return Keystone.Core.Buildings.BuildingKind.Building;
        if (v.X == 1 && v.Y == 0) return Keystone.Core.Buildings.BuildingKind.BuildingNoAura;
        return Keystone.Core.Buildings.BuildingKind.None;
      });
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();

      // Assert
      Assert.IsTrue(surveyor.Surfaces.TryGet(new SurfaceCoord(2, 0, 5), out var far));
      Assert.IsFalse(far.IsSettled,
          "empty tile two from normal building, one from no-aura → NOT Settled "
          + "(no-aura doesn't extend the normal building's halo)");
    }

    #endregion

    #region Idempotence

    [TestMethod]
    public void Survey_TwiceProducesSameResult() {
      // Arrange
      var terrain = FakeTerrain.Single(x: 0, y: 0, surfaceZs: new[] { 3 });
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());

      // Act
      surveyor.Survey();
      surveyor.Survey();

      // Assert
      Assert.AreEqual(1, surveyor.Surfaces.Count);
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

      public static FakeTerrain Flat(int width, int height, int surfaceZ) {
        var t = new FakeTerrain(width, height);
        for (var x = 0; x < width; x++) {
          for (var y = 0; y < height; y++) {
            t.Heights[new TileCoord(x, y)] = new[] { surfaceZ };
          }
        }
        return t;
      }

      public static FakeTerrain Single(int x, int y, int[] surfaceZs) {
        var t = new FakeTerrain(x + 1, y + 1);
        t.Heights[new TileCoord(x, y)] = surfaceZs;
        return t;
      }
    }

    private sealed class FakeBuilding : IBuildingQuery {
      private readonly Func<SurfaceCoord, Keystone.Core.Buildings.BuildingKind> _classifyFn;

      public FakeBuilding(Func<SurfaceCoord, Keystone.Core.Buildings.BuildingKind> classifyFn) {
        _classifyFn = classifyFn;
      }

      public Keystone.Core.Buildings.BuildingKind ClassifyAt(SurfaceCoord voxel) => _classifyFn(voxel);

      public static FakeBuilding NothingBuilt() => new(_ => Keystone.Core.Buildings.BuildingKind.None);
    }

    #endregion

  }

}
