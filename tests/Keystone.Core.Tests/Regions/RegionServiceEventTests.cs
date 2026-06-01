using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Core.Tests.Helpers;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Regions {

  /// <summary>
  /// Pins the lifecycle events <see cref="RegionService"/> emits:
  /// <list type="bullet">
  ///   <item><see cref="RegionService.RegionSplit"/> — fires on a
  ///         split, args <c>(parentId, orphanId)</c>.</item>
  ///   <item><see cref="RegionService.RegionMerged"/> — fires before
  ///         the loser is dropped, args <c>(loserId, survivorId)</c>.</item>
  ///   <item><see cref="RegionService.RegionRemoved"/> — fires before
  ///         a region is removed from the index, arg is the dying id.</item>
  /// </list>
  ///
  /// <para>These events are the contract that lets observers
  /// (<c>RegionScoreStore</c>, <c>ChunkValueStore</c>) carry per-region
  /// accumulator state forward across topology changes. Previously
  /// they were only exercised transitively through chunk-store
  /// inheritance side-effects in <c>PersistenceIntegrationTests</c> —
  /// a regression that broke argument order or firing count would
  /// surface there as a confusing "chunk value at wrong region" failure
  /// rather than localising to the events themselves.</para>
  /// </summary>
  [TestClass]
  public class RegionServiceEventTests {

    #region RegionSplit

    [TestMethod]
    public void Split_FiresOnceWithParentAndOrphanIds() {
      // 3x1 plateau, remove the middle → splits into two regions.
      // Parent keeps id 0 (contains lowest-sort-order surface);
      // orphan gets the freshly-allocated id 1.
      var (terrain, surveyor, regions) = SetupTerrain(width: 3, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 5));
      regions.Index();
      var parentId = regions.All.GetEnumerator().Current?.Id ?? new RegionId(0);
      // Re-fetch deterministically:
      parentId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;

      var splits = new List<(RegionId Parent, RegionId Orphan)>();
      regions.RegionSplit += (p, o) => splits.Add((p, o));

      // Bisect by dropping the middle column.
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      Assert.AreEqual(1, splits.Count, "Exactly one split event per split.");
      var (parent, orphan) = splits[0];
      Assert.AreEqual(parentId, parent,
          "Parent id is the original region (the piece that keeps the parent id).");
      Assert.AreNotEqual(parent, orphan, "Orphan must be a distinct id.");
      // The orphan should be the live id of the piece that didn't keep
      // the parent id. The kept piece always contains the lowest-sorting
      // surface (0,0,5).
      var keptId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var newId = regions.Containing(new SurfaceCoord(2, 0, 5))!.Id;
      Assert.AreEqual(parentId, keptId);
      Assert.AreEqual(orphan, newId, "Orphan id must match the new region's live id.");
    }

    [TestMethod]
    public void Split_NoSplit_FiresNothing() {
      // Removing an edge surface shrinks but doesn't split the region.
      var (terrain, surveyor, regions) = SetupTerrain(width: 3, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 5));
      regions.Index();

      var splits = new List<(RegionId, RegionId)>();
      regions.RegionSplit += (p, o) => splits.Add((p, o));

      terrain.Heights.Remove(new TileCoord(2, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      Assert.AreEqual(0, splits.Count, "Shrink-without-split fires no split event.");
    }

    #endregion

    #region RegionMerged

    [TestMethod]
    public void Merge_FiresWithLoserAndSurvivorIds() {
      // Two z=5 plateaus separated by a z=4 column; raise the column
      // to z=5 → the two regions merge.
      var (terrain, surveyor, regions) = SetupTerrain(width: 5, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 4),
          (new TileCoord(3, 0), 5),
          (new TileCoord(4, 0), 5));
      regions.Index();
      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;
      Assert.AreNotEqual(leftId, rightId);

      var merges = new List<(RegionId Loser, RegionId Survivor)>();
      regions.RegionMerged += (l, s) => merges.Add((l, s));

      // Bridge: raise (2,0) to z=5.
      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      Assert.IsTrue(merges.Count >= 1, "At least one merge event must fire.");
      // Survivor policy: tied-size, lower-id wins. So survivor = lower of
      // {leftId, rightId}, loser = the other.
      var expectedSurvivor = leftId.Value < rightId.Value ? leftId : rightId;
      var expectedLoser = leftId.Value < rightId.Value ? rightId : leftId;
      Assert.AreEqual(expectedLoser, merges[0].Loser);
      Assert.AreEqual(expectedSurvivor, merges[0].Survivor);
    }

    [TestMethod]
    public void Merge_NoMerge_FiresNothing() {
      // Adding an isolated surface doesn't merge anything.
      var (terrain, surveyor, regions) = SetupTerrain(width: 3, height: 1,
          (new TileCoord(0, 0), 5));
      regions.Index();

      var merges = new List<(RegionId, RegionId)>();
      regions.RegionMerged += (l, s) => merges.Add((l, s));

      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };  // isolated, not adjacent
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      Assert.AreEqual(0, merges.Count);
    }

    #endregion

    #region RegionRemoved

    [TestMethod]
    public void Removed_FiresWhenLastSurfaceIsRemoved() {
      // Single-surface region; remove it → RegionRemoved fires.
      var (terrain, surveyor, regions) = SetupTerrain(width: 1, height: 1,
          (new TileCoord(0, 0), 5));
      regions.Index();
      var doomedId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;

      var removed = new List<RegionId>();
      regions.RegionRemoved += id => removed.Add(id);

      terrain.Heights.Remove(new TileCoord(0, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      Assert.AreEqual(1, removed.Count);
      Assert.AreEqual(doomedId, removed[0]);
      Assert.AreEqual(0, regions.Count, "Region should be gone after removal.");
    }

    [TestMethod]
    public void Removed_DoesNotFireOnMergeForTheLoser() {
      // Pins the boundary between RegionMerged and RegionRemoved.
      // Per the docstring on RegionRemoved, the event is reserved for
      // "size hit zero or split-detection inconsistency." Merge losers
      // disappear from _byId via direct dictionary removal — they are
      // signalled via RegionMerged, NOT RegionRemoved. Observers that
      // need to migrate loser-keyed state must subscribe to RegionMerged;
      // if a refactor ever bridged the two events, the chunk-store
      // inheritance path would double-fire and corrupt state.
      var (terrain, surveyor, regions) = SetupTerrain(width: 5, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 4),
          (new TileCoord(3, 0), 5),
          (new TileCoord(4, 0), 5));
      regions.Index();
      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;
      var expectedLoser = leftId.Value < rightId.Value ? rightId : leftId;

      var removed = new List<RegionId>();
      regions.RegionRemoved += id => removed.Add(id);

      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      CollectionAssert.DoesNotContain(removed, expectedLoser,
          "Merge loser must NOT be the subject of a RegionRemoved event — that's RegionMerged's responsibility.");
    }

    [TestMethod]
    public void Removed_DoesNotFireOnShrink() {
      // Shrinking a region (removing an edge) keeps the region alive;
      // no removal event.
      var (terrain, surveyor, regions) = SetupTerrain(width: 3, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 5));
      regions.Index();

      var removed = new List<RegionId>();
      regions.RegionRemoved += id => removed.Add(id);

      terrain.Heights.Remove(new TileCoord(2, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      Assert.AreEqual(0, removed.Count);
    }

    #endregion

    #region Ordering

    [TestMethod]
    public void Merge_RegionMergedFiresWhileLoserIsStillInIndex() {
      // The RegionMerged docstring promises the event fires "before
      // the loser is removed from the index." Pin that observers can
      // still address the loser through RegionService when their
      // RegionMerged handler runs — critical for state migration
      // (e.g. reading loser's size before it's purged).
      var (terrain, surveyor, regions) = SetupTerrain(width: 5, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 4),
          (new TileCoord(3, 0), 5),
          (new TileCoord(4, 0), 5));
      regions.Index();
      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;
      var expectedLoser = leftId.Value < rightId.Value ? rightId : leftId;

      var loserWasAddressableInHandler = false;
      regions.RegionMerged += (loser, _) => {
        // Try to look up the loser in the index — it must still be
        // there at this moment.
        loserWasAddressableInHandler = regions.Get(loser) != null;
      };

      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      Assert.IsTrue(loserWasAddressableInHandler,
          "Loser region must still be in the index when RegionMerged handlers run.");
      Assert.IsNull(regions.Get(expectedLoser),
          "After ProcessChanges returns, the loser must be gone.");
    }

    #endregion

    #region Touched set (reconciliation scope)

    [TestMethod]
    public void ProcessChanges_OnSplit_TouchedContainsParentAndOrphan() {
      // The post-flush ChunkReconciler is scoped to this set; if a split's
      // orphan id is missing, the chunks that moved to it never re-home.
      var (terrain, surveyor, regions) = SetupTerrain(width: 3, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 5));
      regions.Index();
      var parentId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;

      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      var touched = new List<RegionId>(regions.ProcessChanges(diff.Detached, diff.Attached));

      var orphanId = regions.Containing(new SurfaceCoord(2, 0, 5))!.Id;
      CollectionAssert.Contains(touched, parentId, "parent must be in the touched set");
      CollectionAssert.Contains(touched, orphanId, "orphan must be in the touched set");
    }

    [TestMethod]
    public void ProcessChanges_OnMerge_TouchedContainsSurvivorAndLoser() {
      // The loser id is dead after the merge but its chunk data is still
      // keyed under it; the touched set must include it so reconciliation
      // re-homes that data onto the survivor.
      var (terrain, surveyor, regions) = SetupTerrain(width: 5, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 4),
          (new TileCoord(3, 0), 5),
          (new TileCoord(4, 0), 5));
      regions.Index();
      var leftId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rightId = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;

      terrain.Heights[new TileCoord(2, 0)] = new[] { 5 };
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      var touched = new List<RegionId>(regions.ProcessChanges(diff.Detached, diff.Attached));

      CollectionAssert.Contains(touched, leftId);
      CollectionAssert.Contains(touched, rightId);
    }

    [TestMethod]
    public void ProcessChanges_OnRemove_TouchedContainsRemovedId() {
      // A dead region's stranded chunk data is re-homed/dropped only if its
      // id is in the touched set.
      var (terrain, surveyor, regions) = SetupTerrain(width: 1, height: 1,
          (new TileCoord(0, 0), 5));
      regions.Index();
      var doomedId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;

      terrain.Heights.Remove(new TileCoord(0, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      var touched = new List<RegionId>(regions.ProcessChanges(diff.Detached, diff.Attached));

      CollectionAssert.Contains(touched, doomedId);
    }

    [TestMethod]
    public void ProcessChanges_OnShrink_TouchedContainsAffectedRegion() {
      // Even a non-structural shrink can change which region owns a chunk
      // near the edge, so the affected region stays in scope.
      var (terrain, surveyor, regions) = SetupTerrain(width: 3, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(2, 0), 5));
      regions.Index();
      var id = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;

      terrain.Heights.Remove(new TileCoord(2, 0));
      var diff = surveyor.ResurveyColumn(new TileCoord(2, 0));
      var touched = new List<RegionId>(regions.ProcessChanges(diff.Detached, diff.Attached));

      CollectionAssert.Contains(touched, id);
    }

    #endregion

    #region Footprint owner index

    [TestMethod]
    public void BuildChunkFootprintOwnerIndex_AgreesWithFindRegionByChunkFootprint() {
      // 0,1 form region A; tile 2 is empty; tile 3 is region B. With
      // chunkSize 2, chunk (0,*) holds A and chunk (1,*) holds only B.
      var (terrain, surveyor, regions) = SetupTerrain(width: 4, height: 1,
          (new TileCoord(0, 0), 5),
          (new TileCoord(1, 0), 5),
          (new TileCoord(3, 0), 5));
      regions.Index();
      var a = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var b = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;
      Assert.AreNotEqual(a, b);

      const int size = 2;
      var index = regions.BuildChunkFootprintOwnerIndex(size);

      // Every indexed key's majority owner reproduces the per-chunk query
      // exactly.
      foreach (var kv in index) {
        var (cx, cy, z) = kv.Key;
        Assert.AreEqual(regions.FindRegionByChunkFootprint(cx, cy, size, z), kv.Value.Majority,
            $"index disagrees with footprint query at ({cx},{cy},{z})");
      }

      // Reverse direction: over a bounded window (incl. negative and empty
      // coords), index membership must match the query being non-null, and
      // agree on value where present. Pins that the index neither omits a
      // chunk the query would answer nor invents one.
      for (var z = 4; z <= 6; z++)
        for (var cy = -1; cy <= 2; cy++)
          for (var cx = -1; cx <= 3; cx++) {
            var fromQuery = regions.FindRegionByChunkFootprint(cx, cy, size, z);
            var inIndex = index.TryGetValue((cx, cy, z), out var fromIndex);
            Assert.AreEqual(fromQuery.HasValue, inIndex,
                $"index presence vs query-null mismatch at ({cx},{cy},{z})");
            if (fromQuery.HasValue) Assert.AreEqual(fromQuery.Value, fromIndex.Majority);
          }

      // Both populated chunks are present and resolve to the right region.
      Assert.AreEqual(a, index[(0, 0, 5)].Majority);
      Assert.AreEqual(b, index[(1, 0, 5)].Majority);

      // An empty chunk coordinate is absent — equivalent to the query
      // returning null.
      Assert.IsFalse(index.ContainsKey((9, 9, 5)));
      Assert.IsNull(regions.FindRegionByChunkFootprint(9, 9, size, 5));
    }

    [TestMethod]
    public void BuildChunkFootprintOwnerIndex_TwoRegionsShareChunk_TieBreaksToLowestId() {
      // Two disconnected same-Z regions interleave inside one chunk
      // footprint with an EQUAL surface count — the tie must break to the
      // lowest region id, matching FindRegionByChunkFootprint. This is the
      // branch the single-owner equivalence test above can't reach.
      //
      // chunkSize 4 -> chunk (0,0) spans tiles x,y in [0,4).
      //   A = {(0,0),(0,1)}, B = {(3,0),(3,1)}; columns 1-2 empty keep them
      //   disconnected. Each contributes 2 surfaces to chunk (0,0,5): a tie.
      var (terrain, surveyor, regions) = SetupTerrain(width: 4, height: 2,
          (new TileCoord(0, 0), 5),
          (new TileCoord(0, 1), 5),
          (new TileCoord(3, 0), 5),
          (new TileCoord(3, 1), 5));
      regions.Index();
      var a = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var b = regions.Containing(new SurfaceCoord(3, 0, 5))!.Id;
      Assert.AreNotEqual(a, b);
      Assert.IsTrue(a.Value < b.Value, "A seeds first (lowest-sort surface), so holds the lower id");

      const int size = 4;
      var index = regions.BuildChunkFootprintOwnerIndex(size);

      // 2-2 tie in chunk (0,0,5) -> lowest id (A) wins the majority, and the
      // index agrees with the per-chunk query on the tiebreak.
      Assert.AreEqual(a, index[(0, 0, 5)].Majority);
      Assert.AreEqual(regions.FindRegionByChunkFootprint(0, 0, size, 5), index[(0, 0, 5)].Majority);

      // Both regions are recorded as present co-owners of the shared chunk —
      // the presence set is what lets the reconciler KEEP a minority
      // co-owner instead of re-homing it.
      Assert.IsTrue(index[(0, 0, 5)].Contains(a), "A must be a present co-owner");
      Assert.IsTrue(index[(0, 0, 5)].Contains(b), "B must be a present co-owner");
    }

    #endregion

    #region Merge + split in one flush

    [TestMethod]
    public void ProcessChanges_BisectAndMergeInOneFlush_StrandedPieceSplitsOff() {
      // One flush both (a) bisects region R by detaching its connector and
      // (b) bridges R to region S, electing S the merge survivor. MergeInto
      // retags ALL of R's surfaces — including the now-disconnected piece —
      // onto S. Without transferring R's split-check to the survivor, S
      // would silently span disconnected geometry, breaking the connected-
      // component invariant ComputeCanonicalIdMap relies on. The stranded
      // piece must instead split back off into its own region.
      //
      // Layout at z=5 (gap column x=1 keeps S and R separate initially):
      //   S = (0,0),(0,1)        R = (2,0),(2,1),(2,2)
      // Flush: detach (2,1) [bisects R into (2,0) and (2,2)] + attach (1,0)
      //   [bridges S-(0,0) to R-(2,0); S wins the size tie on lower id].
      var (terrain, surveyor, regions) = SetupTerrain(width: 3, height: 3,
          (new TileCoord(0, 0), 5),
          (new TileCoord(0, 1), 5),
          (new TileCoord(2, 0), 5),
          (new TileCoord(2, 1), 5),
          (new TileCoord(2, 2), 5));
      regions.Index();
      var sId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var rId = regions.Containing(new SurfaceCoord(2, 0, 5))!.Id;
      Assert.AreNotEqual(sId, rId);
      Assert.IsTrue(sId.Value < rId.Value, "S seeds first, so it's the lower-id merge survivor");

      // Detach the connector and add the bridge in one combined flush.
      terrain.Heights.Remove(new TileCoord(2, 1));
      terrain.Heights[new TileCoord(1, 0)] = new[] { 5 };
      var dDetach = surveyor.ResurveyColumn(new TileCoord(2, 1));
      var dAttach = surveyor.ResurveyColumn(new TileCoord(1, 0));
      var detached = new List<SurfaceCoord>();
      detached.AddRange(dDetach.Detached);
      detached.AddRange(dAttach.Detached);
      var attached = new List<SurfaceCoord>();
      attached.AddRange(dDetach.Attached);
      attached.AddRange(dAttach.Attached);
      regions.ProcessChanges(detached, attached);

      // The bridge-connected piece (2,0), the bridge tile (1,0), and the
      // original S body are all one region (the survivor).
      var survivor = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      Assert.AreEqual(survivor, regions.Containing(new SurfaceCoord(2, 0, 5))!.Id);
      Assert.AreEqual(survivor, regions.Containing(new SurfaceCoord(1, 0, 5))!.Id);

      // The stranded piece (2,2) must be its OWN region, not folded into S.
      var stranded = regions.Containing(new SurfaceCoord(2, 2, 5))!.Id;
      Assert.AreNotEqual(survivor, stranded,
          "disconnected (2,2) must split off, not leave the survivor spanning a gap");
    }

    #endregion

    #region Setup + fakes

    private static (FakeTerrain terrain, TerrainSurveyor surveyor, RegionService regions)
        SetupTerrain(int width, int height, params (TileCoord coord, int z)[] surfaces) {
      var terrain = new FakeTerrain(width, height);
      foreach (var (coord, z) in surfaces) {
        terrain.Heights[coord] = new[] { z };
      }
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, new FakeClock());
      return (terrain, surveyor, regions);
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
        if (!Heights.TryGetValue(column, out var list)) return System.Array.Empty<int>();
        var sorted = (int[])list.Clone();
        System.Array.Sort(sorted);
        return sorted;
      }

      public bool HasTerrainAbove(SurfaceCoord surface) {
        if (!Heights.TryGetValue(surface.Column, out var list)) return false;
        for (var i = 0; i < list.Length; i++) if (list[i] > surface.Z) return true;
        return false;
      }

      public bool IsTerrainVoxel(int x, int y, int z) => false;
    }

    private sealed class FakeBuilding : IBuildingQuery {
      public Keystone.Core.Buildings.BuildingKind ClassifyAt(SurfaceCoord voxel) =>
          Keystone.Core.Buildings.BuildingKind.None;
      public static FakeBuilding NothingBuilt() => new();
    }

    #endregion

  }

}
