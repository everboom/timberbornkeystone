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
  /// Verifies <see cref="Region.Neighbors"/> -- the symmetric topology
  /// graph maintained by <see cref="RegionService"/>. Two regions are
  /// neighbours when their member surfaces are 4-laterally adjacent at
  /// the same Z, or vertically adjacent across a 1-voxel cliff. Wild ↔
  /// settled boundaries are <b>not</b> linked. Splits are conservative
  /// (pieces inherit the parent's neighbour set unchanged).
  /// </summary>
  [TestClass]
  public class RegionServiceNeighborTests {

    #region Index-time linking

    [TestMethod]
    public void Index_LateralCaveAndOpenAtSameZ_AreNeighbours() {
      // 3x1 row at z=5: middle column is cave (terrain at z=9 above it),
      // the two ends are open. Three regions: left-open, mid-cave,
      // right-open. left-open and right-open each touch mid-cave at z=5.
      var terrain = new FakeTerrain(width: 3, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5, 9 },
              [new TileCoord(2, 0)] = new[] { 5 },
          },
      };
      var (_, regions) = Setup(terrain);
      regions.Index();

      var midCave = regions.All.Single(r => r.Z == 5 && r.IsCave);
      var leftOpen = regions.Containing(new SurfaceCoord(0, 0, 5))!;
      var rightOpen = regions.Containing(new SurfaceCoord(2, 0, 5))!;

      Assert.IsTrue(midCave.Neighbors.Contains(leftOpen.Id));
      Assert.IsTrue(midCave.Neighbors.Contains(rightOpen.Id));
      Assert.IsTrue(leftOpen.Neighbors.Contains(midCave.Id));
      Assert.IsTrue(rightOpen.Neighbors.Contains(midCave.Id));
      // The two open ends do NOT share a boundary (cave between them).
      Assert.IsFalse(leftOpen.Neighbors.Contains(rightOpen.Id));
    }

    [TestMethod]
    public void Index_PlateauIslandLinksToFourSeparateLowerCliffs() {
      // Single-voxel island at z=5 surrounded by four single-voxel
      // moats at z=4 in the cardinal directions. Each moat is its own
      // region (none are 4-connected to each other -- only diagonally,
      // and they're at the same Z so no 1-cliff diagonal-fallback
      // helps). The island reaches each moat via a cardinal probe at
      // z-1; each moat reaches the island via a cardinal probe at
      // z+1. Both directions of the ±1 Z window must work for this
      // layout to produce the expected 4-edge star.
      var terrain = new FakeTerrain(width: 3, height: 3) {
          Heights = {
              [new TileCoord(1, 1)] = new[] { 5 },  // island
              [new TileCoord(1, 0)] = new[] { 4 },
              [new TileCoord(1, 2)] = new[] { 4 },
              [new TileCoord(0, 1)] = new[] { 4 },
              [new TileCoord(2, 1)] = new[] { 4 },
          },
      };
      var (_, regions) = Setup(terrain);
      regions.Index();

      Assert.AreEqual(5, regions.Count);
      var island = regions.Containing(new SurfaceCoord(1, 1, 5))!;
      Assert.AreEqual(4, island.Neighbors.Count, "island should reach all four moats");
      foreach (var moatCoord in new[] {
          new SurfaceCoord(1, 0, 4), new SurfaceCoord(1, 2, 4),
          new SurfaceCoord(0, 1, 4), new SurfaceCoord(2, 1, 4)}) {
        var moat = regions.Containing(moatCoord)!;
        Assert.IsTrue(island.Neighbors.Contains(moat.Id));
        Assert.IsTrue(moat.Neighbors.Contains(island.Id), "moat reaches island via z+1 probe");
        Assert.AreEqual(1, moat.Neighbors.Count, "moat is otherwise isolated");
      }
    }

    [TestMethod]
    public void Index_OneVoxelCliff_LinksUpperAndLowerRegion() {
      // Left two columns at z=5, right two at z=4. One-voxel cliff
      // separates the plateaus.
      var terrain = new FakeTerrain(width: 4, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 4 },
              [new TileCoord(3, 0)] = new[] { 4 },
          },
      };
      var (_, regions) = Setup(terrain);
      regions.Index();

      Assert.AreEqual(2, regions.Count);
      var upper = regions.All.Single(r => r.Z == 5);
      var lower = regions.All.Single(r => r.Z == 4);
      Assert.IsTrue(upper.Neighbors.Contains(lower.Id));
      Assert.IsTrue(lower.Neighbors.Contains(upper.Id));
    }

    [TestMethod]
    public void Index_TwoVoxelCliff_DoesNotLink() {
      // Drop is z=6 to z=4 -- that's 2 voxels, exceeds the 1-voxel
      // threshold. Regions exist but are not neighbours.
      var terrain = new FakeTerrain(width: 2, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 6 },
              [new TileCoord(1, 0)] = new[] { 4 },
          },
      };
      var (_, regions) = Setup(terrain);
      regions.Index();

      Assert.AreEqual(2, regions.Count);
      var upper = regions.All.Single(r => r.Z == 6);
      var lower = regions.All.Single(r => r.Z == 4);
      Assert.AreEqual(0, upper.Neighbors.Count);
      Assert.AreEqual(0, lower.Neighbors.Count);
    }

    [TestMethod]
    public void Index_DiagonalCliff_LinksWhenCardinalsAreEmpty() {
      // (0,0) at z=5, (1,1) at z=4. Both surfaces' cardinal columns
      // are empty (nothing at (0,1), (1,0), etc.) -- diagonal fallback
      // fires and the regions link. This is the "diagonal staircase"
      // case that motivates allowing diagonal cliffs at all.
      var terrain = new FakeTerrain(width: 2, height: 2) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 1)] = new[] { 4 },
          },
      };
      var (_, regions) = Setup(terrain);
      regions.Index();

      Assert.AreEqual(2, regions.Count);
      var upper = regions.Containing(new SurfaceCoord(0, 0, 5))!;
      var lower = regions.Containing(new SurfaceCoord(1, 1, 4))!;
      Assert.IsTrue(upper.Neighbors.Contains(lower.Id), "diagonal-cliff link expected when cardinals empty");
      Assert.IsTrue(lower.Neighbors.Contains(upper.Id));
    }

    [TestMethod]
    public void Index_DiagonalStaircase_LinksAdjacentSteps() {
      // Three voxels forming a NE staircase: (0,0,0), (1,1,1), (2,2,2).
      // Each step's cardinals are completely empty, so the diagonal
      // fallback fires; adjacent steps link, but step 0 and step 2
      // (separated by two voxels) do NOT link directly because the
      // cliff between them is 2 voxels.
      var terrain = new FakeTerrain(width: 3, height: 3) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 0 },
              [new TileCoord(1, 1)] = new[] { 1 },
              [new TileCoord(2, 2)] = new[] { 2 },
          },
      };
      var (_, regions) = Setup(terrain);
      regions.Index();

      Assert.AreEqual(3, regions.Count);
      var step0 = regions.Containing(new SurfaceCoord(0, 0, 0))!;
      var step1 = regions.Containing(new SurfaceCoord(1, 1, 1))!;
      var step2 = regions.Containing(new SurfaceCoord(2, 2, 2))!;
      Assert.IsTrue(step0.Neighbors.Contains(step1.Id));
      Assert.IsTrue(step1.Neighbors.Contains(step2.Id));
      Assert.IsFalse(step0.Neighbors.Contains(step2.Id), "non-adjacent steps must not link (>1 voxel cliff)");
    }

    [TestMethod]
    public void Index_DiagonalSkippedWhenForeignCardinalShieldsTheCorner() {
      // Layout (shows the diagonal-skip in action):
      //   A at (0,0,5)   open
      //   C at (0,1,5)   cave (terrain at z=9 above) -- A's N cardinal
      //                  is filled at the same Z, so A's NE diagonal
      //                  is suppressed. A and C are lateral siblings
      //                  split by IsCave -> different regions.
      //   B at (1,1,4)   open. A's NE diagonal target (would link to A
      //                  without the skip), and a cardinal cliff to C
      //                  via C's E cardinal.
      // Without the diagonal-skip, A would link directly to B as well.
      // With it, A reaches B only via C (graph traversal at the
      // migration layer, not a direct edge).
      var terrain = new FakeTerrain(width: 2, height: 2) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(0, 1)] = new[] { 5, 9 },  // C at z=5 with terrain above -> cave
              [new TileCoord(1, 1)] = new[] { 4 },
          },
      };
      var (_, regions) = Setup(terrain);
      regions.Index();

      var a = regions.Containing(new SurfaceCoord(0, 0, 5))!;
      var c = regions.Containing(new SurfaceCoord(0, 1, 5))!;
      var b = regions.Containing(new SurfaceCoord(1, 1, 4))!;
      Assert.IsFalse(a.IsCave);
      Assert.IsTrue(c.IsCave);
      Assert.IsFalse(b.IsCave);
      Assert.AreNotEqual(a.Id, b.Id);
      Assert.AreNotEqual(a.Id, c.Id);
      Assert.AreNotEqual(b.Id, c.Id);

      Assert.IsTrue(a.Neighbors.Contains(c.Id), "A--C is a real same-Z lateral edge (split by IsCave)");
      Assert.IsTrue(b.Neighbors.Contains(c.Id), "B--C is a real cardinal cliff edge");
      Assert.IsFalse(a.Neighbors.Contains(b.Id),
          "A's NE diagonal blocked by foreign cardinal C; A--B must not link");
    }

    [TestMethod]
    public void Index_WildAndSettled_AreNotLinkedAcrossBoundary() {
      // 5x1 plateau at z=5; build at the centre voxel. Halo rule paints
      // (1,0)..(3,0) as Settled. (0,0) and (4,0) stay wild, each
      // adjacent to the settled centre. Wild ↔ settled is the hard
      // cut; the wild ends must NOT have the settled centre as a
      // neighbour and vice versa.
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var building = new MutableFakeBuilding();
      building.Voxels[new SurfaceCoord(2, 0, 5)] = Keystone.Core.Buildings.BuildingKind.Building;
      var (_, regions) = SetupWithBuilding(terrain, building);
      regions.Index();

      var settled = regions.All.Single(r => r.IsSettled);
      foreach (var wild in regions.All.Where(r => !r.IsSettled)) {
        Assert.IsFalse(wild.Neighbors.Contains(settled.Id),
            "wild region must not list the settled neighbour");
      }
      Assert.AreEqual(0, settled.Neighbors.Count,
          "settled region must not list any wild neighbour");
    }

    [TestMethod]
    public void Index_SymmetricEverywhere() {
      // A small varied map. Every neighbour relation must be symmetric.
      var terrain = new FakeTerrain(width: 4, height: 2) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5, 9 },
              [new TileCoord(2, 0)] = new[] { 4 },
              [new TileCoord(3, 0)] = new[] { 4 },
              [new TileCoord(0, 1)] = new[] { 5 },
              [new TileCoord(1, 1)] = new[] { 5 },
              [new TileCoord(2, 1)] = new[] { 4 },
              [new TileCoord(3, 1)] = new[] { 4 },
          },
      };
      var (_, regions) = Setup(terrain);
      regions.Index();

      foreach (var r in regions.All) {
        foreach (var nId in r.Neighbors) {
          var n = regions.Get(nId);
          Assert.IsNotNull(n, $"{r.Id}'s neighbour {nId} should exist");
          Assert.IsTrue(n!.Neighbors.Contains(r.Id),
              $"reciprocal edge missing: {r.Id} -> {nId} has no return edge");
        }
      }
    }

    #endregion

    #region Incremental: attach

    [TestMethod]
    public void ProcessChanges_AttachAddsNeighbourEdge() {
      // Two disjoint plateaus, no neighbour edge initially. Place a
      // single new surface that bridges them at a 1-voxel cliff.
      var terrain = new FakeTerrain(width: 4, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              // Gap at (1, 0)
              [new TileCoord(2, 0)] = new[] { 4 },
              [new TileCoord(3, 0)] = new[] { 4 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(2, 0, 4))!.Id;
      Assert.IsFalse(regions.Get(leftId)!.Neighbors.Contains(rightId),
          "no neighbour edge across the gap");

      // Act: add a surface at (1, 0, 4) -- bridges to the right plateau
      // (lateral z) and to the left plateau (1-voxel cliff up).
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert: left and right are now both neighbours of whichever
      // region holds the new (1,0,4) surface, and they are mutually
      // neighbours through it (since both are 1 cliff away from the
      // bridge).
      var bridgeId = regions.Containing(new SurfaceCoord(1, 0, 4))!.Id;
      Assert.IsTrue(regions.Get(leftId)!.Neighbors.Contains(bridgeId));
      Assert.IsTrue(regions.Get(bridgeId)!.Neighbors.Contains(leftId));
      Assert.AreEqual(bridgeId, rightId, "the bridge should have merged into the right plateau");
    }

    [TestMethod]
    public void ProcessChanges_DetachSurvivingRegion_DoesNotPruneStaleEdges() {
      // Conservative policy: detaching a surface does not prune
      // neighbour edges as long as both regions involved survive.
      // Stale edges persist and self-prune on a later merge or death
      // event.
      //
      // Layout:
      //   strip = {(0,0,5), (1,0,5), (2,0,5)}
      //   cliff = {(3,0,4), (4,0,4)}
      //   strip↔cliff linked via (2,0,5)->(3,0,4) cardinal cliff (z-1).
      //
      // Action: drop (3,0) all the way to z=2 (out of strip's ±1 Z
      // window). (3,0,4) detaches from cliff; cliff shrinks to {(4,0,4)}
      // -- still alive. The new (3,0,2) becomes its own isolated
      // region; nothing is in z=2±1 of either strip (z=5) or cliff
      // (z=4), so no fresh edges form. The strip↔cliff edge is now
      // PHYSICALLY stale (strip can't reach cliff via any surface in
      // its ±1 Z window -- (3,0)'s surface is at z=2, outside) but
      // remains in both Neighbors sets.
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
              [new TileCoord(3, 0)] = new[] { 4 },
              [new TileCoord(4, 0)] = new[] { 4 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var stripId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var cliffId = regions.Containing(new SurfaceCoord(4, 0, 4))!.Id;
      Assert.IsTrue(regions.Get(stripId)!.Neighbors.Contains(cliffId));
      Assert.IsTrue(regions.Get(cliffId)!.Neighbors.Contains(stripId));

      // Drop (3,0) to z=2. (3,0,4) detaches from cliff; cliff is now
      // just (4,0,4) -- size 1, alive.
      terrain.Heights[new TileCoord(3, 0)] = new[] { 2 };
      var diff = surveyor.ResurveyColumn(new TileCoord(3, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Cliff survives at size 1.
      var cliffAfter = regions.Get(cliffId);
      Assert.IsNotNull(cliffAfter, "cliff must not have died");
      Assert.AreEqual(1, cliffAfter!.Size);

      // Physical contact gone: strip's east boundary (2,0,5)'s east
      // cardinal column (3,0) now has its surface at z=2, outside
      // strip's ±1 window. (4,0,4) is two columns away -- not a
      // cardinal of any strip surface either. So strip and cliff no
      // longer have any physical contact. But the edge is still in
      // both sets (conservative-on-detach).
      Assert.IsTrue(regions.Get(stripId)!.Neighbors.Contains(cliffId),
          "strip's stale edge to cliff is preserved (conservative detach)");
      Assert.IsTrue(regions.Get(cliffId)!.Neighbors.Contains(stripId),
          "cliff's stale edge to strip is preserved (conservative detach)");
    }

    #endregion

    #region Incremental: merge transfer

    [TestMethod]
    public void ProcessChanges_MergeTransfersNeighbourEdges() {
      // Layout (z values):
      //   col 0: z=5
      //   col 1: z=5     <-- "left" plateau
      //   col 2: gap
      //   col 3: z=5     <-- "right" plateau
      //   col 4: z=5
      //   col 5: z=4     <-- "below" plateau, neighbour of right via cliff
      // Bridging col 2 to z=5 merges left+right; the merged survivor
      // should inherit "below" as a neighbour from the right loser
      // (or keep it if right was the survivor).
      var terrain = new FakeTerrain(width: 6, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
              [new TileCoord(5, 0)] = new[] { 4 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;
      var belowId = regions.Containing(new SurfaceCoord(5, 0, 4))!.Id;
      Assert.IsTrue(regions.Get(rightId)!.Neighbors.Contains(belowId));
      Assert.IsFalse(regions.Get(leftId)!.Neighbors.Contains(belowId));

      // Bridge.
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Whichever side was survivor, the merged plateau still neighbours below.
      var survivorId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      Assert.AreEqual(survivorId, regions.Containing(new SurfaceCoord(4, 0, 5))!.Id);
      Assert.IsTrue(regions.Get(survivorId)!.Neighbors.Contains(belowId),
          "merged survivor inherits the below-cliff edge");
      Assert.IsTrue(regions.Get(belowId)!.Neighbors.Contains(survivorId),
          "below's reverse pointer was rerouted to survivor");
    }

    [TestMethod]
    public void ProcessChanges_MultiMerge_ConsolidatesAllLoserNeighbours() {
      // Cross pattern: four single-voxel z=5 regions at the four cardinal
      // tiles around (1,1), each with a unique 1-voxel cliff neighbour
      // at z=4. Bridging (1,1,5) cardinally joins all four z=5 regions
      // in a single ProcessChanges call, exercising the multi-merge
      // loop in MergeInto. The survivor must inherit every loser's
      // cliff edge.
      //
      // (Some of the z=5 voxels are also diagonally linked to each
      // other via the diagonal fallback at index time; those become
      // self-loops once they all merge and are scrubbed.)
      var terrain = new FakeTerrain(width: 5, height: 5) {
          Heights = {
              [new TileCoord(0, 1)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 1)] = new[] { 5 },
              [new TileCoord(1, 2)] = new[] { 5 },
              // Unique cliff neighbour per z=5 region.
              [new TileCoord(0, 2)] = new[] { 4 },  // cliff_W (neighbours (0,1,5))
              [new TileCoord(2, 0)] = new[] { 4 },  // cliff_S (neighbours (1,0,5))
              [new TileCoord(2, 2)] = new[] { 4 },  // cliff_E (neighbours (2,1,5))
              [new TileCoord(0, 0)] = new[] { 4 },  // cliff_N (neighbours (1,2,5)) -- NW
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      // Capture cliff ids.
      var cliffWId = regions.Containing(new SurfaceCoord(0, 2, 4))!.Id;
      var cliffSId = regions.Containing(new SurfaceCoord(2, 0, 4))!.Id;
      var cliffEId = regions.Containing(new SurfaceCoord(2, 2, 4))!.Id;
      var cliffNId = regions.Containing(new SurfaceCoord(0, 0, 4))!.Id;

      // Bridge.
      terrain.Heights[new TileCoord(1, 1)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 1));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      var survivor = regions.Containing(new SurfaceCoord(1, 1, 5))!;
      Assert.IsTrue(survivor.Neighbors.Contains(cliffWId), "survivor inherits W loser's cliff");
      Assert.IsTrue(survivor.Neighbors.Contains(cliffSId), "survivor inherits S loser's cliff");
      Assert.IsTrue(survivor.Neighbors.Contains(cliffEId), "survivor inherits E loser's cliff");
      Assert.IsTrue(survivor.Neighbors.Contains(cliffNId), "survivor inherits N loser's cliff");
      Assert.IsFalse(survivor.Neighbors.Contains(survivor.Id), "no self-loop after multi-merge");

      // Reverse pointers: each cliff now references the survivor (not any of the dead loser ids).
      foreach (var cliffId in new[] { cliffWId, cliffSId, cliffEId, cliffNId }) {
        var cliff = regions.Get(cliffId)!;
        Assert.IsTrue(cliff.Neighbors.Contains(survivor.Id), $"cliff {cliffId} reverse-pointer rerouted to survivor");
      }
    }

    #endregion

    #region Incremental: region death scrub

    [TestMethod]
    public void ProcessChanges_RegionDeath_ScrubsItFromNeighbours() {
      // Two single-voxel plateaus 1 cliff apart. Delete one; the
      // surviving plateau must lose the dead one from its neighbour set.
      var terrain = new FakeTerrain(width: 2, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 4 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var upperId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var lowerId = regions.Containing(new SurfaceCoord(1, 0, 4))!.Id;
      Assert.IsTrue(regions.Get(upperId)!.Neighbors.Contains(lowerId));

      // Kill the lower region.
      terrain.Heights.Remove(new TileCoord(1, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      Assert.IsNull(regions.Get(lowerId));
      Assert.IsFalse(regions.Get(upperId)!.Neighbors.Contains(lowerId),
          "upper must no longer reference the dead lower region");
    }

    #endregion

    #region Incremental: IsSettled flip

    [TestMethod]
    public void ProcessChanges_BuildingPlacement_KeepsWildAndSettledIsolated() {
      // 5x1 wild plateau at z=5. Place a building at the centre voxel.
      // Halo rule paints (1,0)..(3,0) as settled, splitting the plateau
      // into:
      //   wild left  = {(0,0,5)}
      //   settled    = {(1,0,5), (2,0,5), (3,0,5)}
      //   wild right = {(4,0,5)}
      // The hard-cut policy says wild and settled never link. Verify:
      //   - settled has no wild neighbours
      //   - wild halves don't reference the settled region
      //   - the conservative-split policy doesn't accidentally leak
      //     settled into wild halves' inherited neighbour sets
      var terrain = new FakeTerrain(width: 5, height: 1) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
          },
      };
      var building = new MutableFakeBuilding();
      var (surveyor, regions) = SetupWithBuilding(terrain, building);
      regions.Index();
      Assert.AreEqual(1, regions.Count);

      // Place the building. Resurvey the centre + halo columns.
      building.Voxels[new SurfaceCoord(2, 0, 5)] = Keystone.Core.Buildings.BuildingKind.Building;
      var detached = new List<SurfaceCoord>();
      var attached = new List<SurfaceCoord>();
      foreach (var col in new[] { new TileCoord(1, 0), new TileCoord(2, 0), new TileCoord(3, 0) }) {
        var diff = surveyor.ResurveyColumn(col);
        detached.AddRange(diff.Detached);
        attached.AddRange(diff.Attached);
      }
      regions.ProcessChanges(detached, attached);

      Assert.AreEqual(3, regions.Count);
      var settled = regions.All.Single(r => r.IsSettled);
      var wildLeft = regions.Containing(new SurfaceCoord(0, 0, 5))!;
      var wildRight = regions.Containing(new SurfaceCoord(4, 0, 5))!;
      Assert.IsFalse(wildLeft.IsSettled);
      Assert.IsFalse(wildRight.IsSettled);

      Assert.AreEqual(0, settled.Neighbors.Count, "settled region must have no neighbours under the wild-cut");
      Assert.IsFalse(wildLeft.Neighbors.Contains(settled.Id),
          "wild left must not reference the settled region");
      Assert.IsFalse(wildRight.Neighbors.Contains(settled.Id),
          "wild right must not reference the settled region");
    }

    #endregion

    #region Split: conservative inheritance

    [TestMethod]
    public void ProcessChanges_Split_BothPiecesInheritParentNeighbours() {
      // Linear z=5 strip 5 columns long; a z=4 island sits to the
      // south of the middle column, neighbouring the parent strip.
      // Splitting the strip in the middle leaves two pieces. Per the
      // conservative policy, BOTH pieces inherit the (full) parent
      // neighbour set, even though only one piece actually still
      // touches the z=4 island.
      var terrain = new FakeTerrain(width: 5, height: 2) {
          Heights = {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
              [new TileCoord(3, 0)] = new[] { 5 },
              [new TileCoord(4, 0)] = new[] { 5 },
              // Cliff-neighbour island below column 1.
              [new TileCoord(1, 1)] = new[] { 4 },
          },
      };
      var (surveyor, regions) = Setup(terrain);
      regions.Index();
      var stripId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var islandId = regions.Containing(new SurfaceCoord(1, 1, 4))!.Id;
      Assert.IsTrue(regions.Get(stripId)!.Neighbors.Contains(islandId));

      // Drop the centre column to z=4 (becomes its own island, splits the strip).
      terrain.Heights[new TileCoord(2, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      var leftPiece = regions.Containing(new SurfaceCoord(0, 0, 5))!;
      var rightPiece = regions.Containing(new SurfaceCoord(4, 0, 5))!;
      Assert.AreNotEqual(leftPiece.Id, rightPiece.Id, "strip must have split");

      // Conservative: both pieces report the original z=4 island as a
      // neighbour, even though physically only the left piece still
      // touches it.
      Assert.IsTrue(leftPiece.Neighbors.Contains(islandId),
          "left piece keeps the parent's island edge (it actually touches)");
      Assert.IsTrue(rightPiece.Neighbors.Contains(islandId),
          "right piece keeps the parent's island edge under conservative policy");
    }

    #endregion

    #region Setup + fakes

    private static (TerrainSurveyor surveyor, RegionService regions) Setup(FakeTerrain terrain) {
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, new FakeClock());
      return (surveyor, regions);
    }

    private static (TerrainSurveyor surveyor, RegionService regions) SetupWithBuilding(
        FakeTerrain terrain, MutableFakeBuilding building) {
      var surveyor = new TerrainSurveyor(terrain, building, FakeBlocking.NothingBlocked());
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
          return System.Array.Empty<int>();
        }
        var sorted = (int[])list.Clone();
        System.Array.Sort(sorted);
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

    private sealed class MutableFakeBuilding : IBuildingQuery {
      public Dictionary<SurfaceCoord, Keystone.Core.Buildings.BuildingKind> Voxels { get; } = new();
      public Keystone.Core.Buildings.BuildingKind ClassifyAt(SurfaceCoord voxel) =>
          Voxels.TryGetValue(voxel, out var k) ? k : Keystone.Core.Buildings.BuildingKind.None;
    }

    private sealed class FakeClock : IClock {
      public GameTimestamp Now { get; set; } = GameTimestamp.Origin;
      public WeatherKind CurrentWeather { get; set; } = WeatherKind.Temperate;
      public float TotalDaysElapsed { get; set; }
    }

    #endregion

  }

}
