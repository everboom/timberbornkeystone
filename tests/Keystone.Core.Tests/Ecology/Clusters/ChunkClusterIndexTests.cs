using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Spatial;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Ecology.Clusters {

  /// <summary>
  /// Verifies the union-find rebuild over qualifying chunks: tuple
  /// rule (Suitability argmax + Maturity ≥ threshold), 4-neighbour
  /// adjacency, region scoping, and aggregate accounting (chunks +
  /// tile count).
  /// </summary>
  [TestClass]
  public class ChunkClusterIndexTests {

    private const float Threshold = 1.0f;
    private const int TilesPerChunk = RegionEcologyField.ChunkSize * RegionEcologyField.ChunkSize;
    private static readonly RegionId R = new RegionId(1);
    private static readonly RegionId R2 = new RegionId(2);

    [TestMethod]
    public void Rebuild_SingleQualifyingChunk_NotEmitted() {
      // Arrange: one valid chunk, Wetland-dominant, matured past threshold,
      // but in a region with no other valid chunks. The region-level
      // early-out (<2 valid chunks) skips ProcessRegion entirely; even if
      // it didn't, the cluster-level singleton drop would still filter
      // the resulting 1-chunk component. Pins both filters from the
      // outside: a lone qualifying chunk is NOT in the index.
      var (store, query) = MakeWorld(chunksX: 1, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(0, index.ClusterCount,
          "Single qualifying chunks no longer emit clusters; the region-of-1 early-out and the singleton drop both apply.");
      Assert.IsNull(index.ClusterFor(R, 0, 0));
    }

    [TestMethod]
    public void Rebuild_RegionWithSingleValidChunk_NoClusters() {
      // Pin the region-level early-out specifically: a region whose
      // field carries only one valid chunk is skipped entirely (no
      // ProcessRegion call, no per-region allocations). Distinguished
      // from the SingleQualifyingChunk_NotEmitted test in that here
      // the field has a 2-chunk capacity but only one is marked valid
      // — exercising the early-out check rather than the chunk-level
      // singleton drop.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      // Chunk (1,0) intentionally NOT marked valid.
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(0, index.ClusterCount);
      Assert.IsNull(index.ClusterFor(R, 0, 0));
    }

    [TestMethod]
    public void Rebuild_LoneQualifyingChunkAmidDifferentBiomes_NotEmitted() {
      // Pin the cluster-level singleton drop independently of the
      // region-level early-out: a region big enough that
      // HasAtLeastTwoValidChunks returns true (so ProcessRegion runs),
      // but where one qualifying chunk's biome connects to no same-
      // biome neighbour. That chunk's connected component has size 1
      // and is dropped post-union-find. Surrounding clusters of a
      // different (clusterable) biome are unaffected.
      // Layout: LL W LL — left Lake pair, lone Wetland, right Lake pair.
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 1);
      var field = query.GetField(R)!;
      for (var i = 0; i < 5; i++) MarkValid(field, i, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Lake, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Lake, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 2, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);  // lone qualifier
      SetBiome(store, R, 3, 0, BiomeKind.Lake, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 4, 0, BiomeKind.Lake, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert: two Lake clusters survive (the W blocks union between
      // them), the lone Wetland is dropped as a singleton.
      Assert.AreEqual(2, index.ClusterCount);
      Assert.IsNull(index.ClusterFor(R, 2, 0),
          "Lone Wetland chunk amid Lakes is a singleton and must be dropped.");
      var idLeft = index.ClusterFor(R, 0, 0)!.Value;
      var idRight = index.ClusterFor(R, 3, 0)!.Value;
      Assert.AreNotEqual(idLeft, idRight);
      Assert.AreEqual(BiomeKind.Lake, index.BiomeFor(idLeft));
      Assert.AreEqual(BiomeKind.Lake, index.BiomeFor(idRight));
      Assert.AreEqual(2, index.ChunksIn(idLeft).Count);
      Assert.AreEqual(2, index.ChunksIn(idRight).Count);
    }

    [DataTestMethod]
    [DataRow(BiomeKind.Monoculture)]
    [DataRow(BiomeKind.Dry)]
    [DataRow(BiomeKind.Contaminated)]
    [DataRow(BiomeKind.Badwater)]
    [DataRow(BiomeKind.Cave)]
    public void Rebuild_NonClusterableDominatesOverClusterable_ChunkDoesNotQualify(BiomeKind nonClusterable) {
      // Regression: dominance must be computed over ALL biomes (with
      // the clusterable-whitelist applied as a post-filter), NOT over
      // ClusterableBiomes directly. Previously the qualifier asked
      // "of the clusterable biomes, which has the highest Maturity?"
      // -- which forced a clusterable answer even when the chunk's
      // true Maturity-argmax was Monoculture / Dry / etc. and any
      // residual Grassland Maturity should have been ignored.
      //
      // Layout: GG[X]GG along one row, where X has Grassland Maturity
      // 2 (above the qualification threshold of 1) but the supplied
      // non-clusterable biome has Maturity 10 (the true argmax).
      // Under the old code, X reported as Grassland and union-found
      // into one 5-chunk Grassland cluster spanning the X chunk.
      // Under the fix, X is non-qualifying (true dominant is non-
      // clusterable), so the row splits into two 2-chunk Grassland
      // clusters with X as the boundary.
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 1);
      var field = query.GetField(R)!;
      for (var i = 0; i < 5; i++) MarkValid(field, i, 0);
      for (var i = 0; i < 5; i++) {
        SetBiome(store, R, i, 0, BiomeKind.Grassland, suitability: 0.8f, maturity: 2f);
      }
      // X chunk also carries a higher Maturity on the non-clusterable
      // biome. This is the realistic scenario: a chunk that used to
      // be Grassland (residual Maturity still present) was farmed up
      // into Monoculture (higher Maturity now), or dried out (Dry
      // Maturity grew).
      SetBiome(store, R, 2, 0, nonClusterable, suitability: 0.8f, maturity: 10f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(2, index.ClusterCount,
          $"With chunk 2 dominantly {nonClusterable}, the Grassland row must split into two clusters with chunk 2 as boundary. " +
          "If you see 1 cluster, the qualifier is restricting dominance to ClusterableBiomes (regressed) and silently relabelling the chunk as Grassland.");
      Assert.IsNull(index.ClusterFor(R, 2, 0),
          $"Chunk dominantly {nonClusterable} must not be a cluster member, even though its residual Grassland Maturity is above the threshold.");
      var idLeft = index.ClusterFor(R, 0, 0)!.Value;
      var idRight = index.ClusterFor(R, 3, 0)!.Value;
      Assert.AreNotEqual(idLeft, idRight,
          "The two Grassland pairs must not be unioned across a non-clusterable-dominant chunk.");
      Assert.AreEqual(BiomeKind.Grassland, index.BiomeFor(idLeft));
      Assert.AreEqual(BiomeKind.Grassland, index.BiomeFor(idRight));
      Assert.AreEqual(2, index.ChunksIn(idLeft).Count);
      Assert.AreEqual(2, index.ChunksIn(idRight).Count);
    }

    [DataTestMethod]
    [DataRow(BiomeKind.Badwater)]
    [DataRow(BiomeKind.Contaminated)]
    [DataRow(BiomeKind.Dry)]
    [DataRow(BiomeKind.Cave)]
    [DataRow(BiomeKind.Monoculture)]
    public void Rebuild_NonClusterableBiomeDominant_DoesNotQualify(BiomeKind biome) {
      // Pin the ClusterableBiomes whitelist: two adjacent chunks whose
      // dominant biome is outside the whitelist would form a 2-chunk
      // cluster without the filter, but are absent from the index. The
      // five rows cover the current non-clusterable set (three aggressors
      // plus Cave and Monoculture, which have no current consumer); if
      // any biome is added to ClusterableBiomes its row should move
      // out of this test into an inclusion-test elsewhere.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, biome, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, biome, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(0, index.ClusterCount,
          $"Biome {biome} is not in ClusterableBiomes; no clusters should form even with 2+ adjacent same-biome chunks.");
      Assert.IsNull(index.ClusterFor(R, 0, 0));
      Assert.IsNull(index.ClusterFor(R, 1, 0));
    }

    [TestMethod]
    public void Rebuild_TwoAdjacentSameBiomeChunks_MergesIntoOneCluster() {
      // Arrange: two horizontally adjacent Wetland chunks.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(1, index.ClusterCount);
      var id0 = index.ClusterFor(R, 0, 0)!.Value;
      var id1 = index.ClusterFor(R, 1, 0)!.Value;
      Assert.AreEqual(id0, id1, "Adjacent same-biome chunks should share a cluster.");
      Assert.AreEqual(2, index.ChunksIn(id0).Count);
      Assert.AreEqual(2 * TilesPerChunk, index.TileCount(id0));
    }

    [TestMethod]
    public void Rebuild_AdjacentDifferentBiomes_StayInSeparateClusters() {
      // Arrange: WWLL — a Wetland pair next to a Lake pair. The
      // shared boundary between the two pairs must NOT union them
      // (clusters are biome-pure). Pairs (rather than singletons) so
      // both clusters survive the singleton-drop filter; the design
      // point being pinned is the biome-purity of union-find, not the
      // existence of singleton clusters.
      var (store, query) = MakeWorld(chunksX: 4, chunksY: 1);
      var field = query.GetField(R)!;
      MarkValid(field, 0, 0); MarkValid(field, 1, 0);
      MarkValid(field, 2, 0); MarkValid(field, 3, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 2, 0, BiomeKind.Lake, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 3, 0, BiomeKind.Lake, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(2, index.ClusterCount);
      var idW = index.ClusterFor(R, 0, 0)!.Value;
      var idL = index.ClusterFor(R, 2, 0)!.Value;
      Assert.AreNotEqual(idW, idL,
          "Same row, different biomes — the W-L boundary must not union.");
      Assert.AreEqual(idW, index.ClusterFor(R, 1, 0));
      Assert.AreEqual(idL, index.ClusterFor(R, 3, 0));
      Assert.AreEqual(BiomeKind.Wetland, index.BiomeFor(idW));
      Assert.AreEqual(BiomeKind.Lake, index.BiomeFor(idL));
    }

    [TestMethod]
    public void Rebuild_ImmatureChunkBetween_BlocksUnion() {
      // Arrange: WW (mature) W (immature) WW (mature). The immature
      // middle chunk must block union between the two mature pairs.
      // Pairs on each side (rather than single chunks) so that the
      // mature regions survive the singleton-drop filter; the design
      // point being pinned is that immaturity breaks union, not that
      // singletons exist.
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 1);
      var field = query.GetField(R)!;
      for (var i = 0; i < 5; i++) MarkValid(field, i, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 2, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 0.2f);  // below threshold
      SetBiome(store, R, 3, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 4, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(2, index.ClusterCount,
          "Two mature pairs separated by an immature chunk must not union through it.");
      Assert.IsNull(index.ClusterFor(R, 2, 0), "Immature chunk must not be in the index.");
      var idLeft = index.ClusterFor(R, 0, 0)!.Value;
      var idRight = index.ClusterFor(R, 3, 0)!.Value;
      Assert.AreNotEqual(idLeft, idRight);
      Assert.AreEqual(idLeft, index.ClusterFor(R, 1, 0));
      Assert.AreEqual(idRight, index.ClusterFor(R, 4, 0));
    }

    [TestMethod]
    public void Rebuild_InvalidChunk_Skipped() {
      // Arrange: two valid chunks plus one invalid one with biome values
      // already in the store. Pins that <c>ChunkValid==false</c> excludes
      // a chunk regardless of stored values. The two valid chunks pair
      // up so the surviving cluster clears the singleton-drop filter,
      // making "invalid is excluded" observable as a null lookup
      // alongside a real cluster.
      var (store, query) = MakeWorld(chunksX: 3, chunksY: 1);
      var field = query.GetField(R)!;
      MarkValid(field, 0, 0);
      MarkValid(field, 1, 0);
      // Chunk (2,0) intentionally NOT marked valid.
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 2, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);  // values present but chunk invalid
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(1, index.ClusterCount);
      var id = index.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(id, index.ClusterFor(R, 1, 0));
      Assert.AreEqual(2, index.ChunksIn(id).Count);
      Assert.IsNull(index.ClusterFor(R, 2, 0),
          "Invalid chunk must not be in the index regardless of stored values.");
    }

    [TestMethod]
    public void Rebuild_NoDominantBiome_NotInIndex() {
      // Arrange: two valid chunks, neither with any biome data in
      // the store (degenerate empty-classification state). Pins that
      // a chunk with no dominant biome is absent from the index.
      // <para><b>Two chunks, not one:</b> a single-valid-chunk region
      // would short-circuit at the region-of-1 early-out and satisfy
      // the <c>ClusterCount==0</c> assertion without ever exercising
      // the "no dominant biome" code path. Two chunks force the
      // per-chunk qualification check to actually run.</para>
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      // No SetBiome calls — no entries in store for either chunk.
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(0, index.ClusterCount);
      Assert.IsNull(index.ClusterFor(R, 0, 0));
      Assert.IsNull(index.ClusterFor(R, 1, 0));
    }

    [TestMethod]
    public void Rebuild_AdjacentSameBiomeAcrossRegions_StaysInSeparateClusters() {
      // Arrange: each region has its OWN 2-chunk Wetland cluster.
      // v1 union-find is per-region; cross-region unioning is
      // future work and this test pins that today's behaviour is
      // explicitly per-region. Two chunks per region so each side's
      // cluster survives the singleton-drop filter.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      query.AddRegion(R2, originX: 0, originY: 0, chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      MarkValid(query.GetField(R2)!, 0, 0);
      MarkValid(query.GetField(R2)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R2, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R2, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R, R2 }, Threshold);

      // Assert
      Assert.AreEqual(2, index.ClusterCount,
          "Cross-region clusters are not unioned in v1; same-region same-biome connectivity is the only union axis.");
      Assert.AreNotEqual(index.ClusterFor(R, 0, 0), index.ClusterFor(R2, 0, 0));
    }

    #region ChunksAtChunkXY (secondary XY index)

    [TestMethod]
    public void ChunksAtChunkXY_NoChunkPresent_ReturnsEmpty() {
      // Pin the empty-coord case: a (chunkX, chunkY) that no region
      // produces a qualifying cluster at must return an empty list,
      // not throw. Consumers iterate the return value directly without
      // null-checking, so an empty list (not null) is the contract.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);
      index.Rebuild(new[] { R }, Threshold);

      // Act: ask for a coord far outside any region's chunks.
      var entries = index.ChunksAtChunkXY(99, 99);

      // Assert
      Assert.IsNotNull(entries, "Must return empty list, not null.");
      Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void ChunksAtChunkXY_SingleClusterAtCoord_ReturnsSingleEntry() {
      // Two-chunk Wetland cluster at (0,0) and (1,0). For each coord,
      // the secondary index reports exactly one (region, cluster) pair
      // whose cluster id matches ClusterFor's answer. Pins the basic
      // shape: ChunksAtChunkXY is consistent with the primary index.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);
      index.Rebuild(new[] { R }, Threshold);

      // Act + Assert
      var clusterId = index.ClusterFor(R, 0, 0)!.Value;
      var entries00 = index.ChunksAtChunkXY(0, 0);
      Assert.AreEqual(1, entries00.Count);
      Assert.AreEqual(R, entries00[0].Region);
      Assert.AreEqual(clusterId, entries00[0].Cluster);

      var entries10 = index.ChunksAtChunkXY(1, 0);
      Assert.AreEqual(1, entries10.Count);
      Assert.AreEqual(R, entries10[0].Region);
      Assert.AreEqual(clusterId, entries10[0].Cluster);
    }

    [TestMethod]
    public void ChunksAtChunkXY_TwoRegionsAtSameCoord_ReturnsBothEntries() {
      // Two distinct regions both have a 2-chunk Wetland cluster at
      // (0,0)-(1,0). At chunkXY (0,0), ChunksAtChunkXY must return
      // BOTH (R, clusterInR) and (R2, clusterInR2). This is the
      // load-bearing case for the KeystoneNatureSource fix: a building
      // on top of (or near) one region's chunk needs to discover the
      // OTHER region's chunk at the same XY -- which the per-column
      // surveyor scan couldn't do if the building's columns sat
      // entirely in one region.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      query.AddRegion(R2, originX: 0, originY: 0, chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      MarkValid(query.GetField(R2)!, 0, 0);
      MarkValid(query.GetField(R2)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R2, 0, 0, BiomeKind.Lake, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R2, 1, 0, BiomeKind.Lake, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);
      index.Rebuild(new[] { R, R2 }, Threshold);

      // Act
      var entries = index.ChunksAtChunkXY(0, 0);

      // Assert: exactly two entries, one per region, biome matches
      // each region's seeded biome. Cluster ids match ClusterFor's
      // per-region answer.
      Assert.AreEqual(2, entries.Count,
          "Both regions' clusters must be discoverable at (0,0) via the secondary index.");
      var idInR = index.ClusterFor(R, 0, 0)!.Value;
      var idInR2 = index.ClusterFor(R2, 0, 0)!.Value;
      CollectionAssert.AreEquivalent(
          new[] { (R, idInR), (R2, idInR2) },
          entries.ToList(),
          "Entries must enumerate both (region, cluster) pairs regardless of order.");
      // Sanity: the two clusters are different biomes -- the
      // secondary index doesn't conflate them.
      Assert.AreEqual(BiomeKind.Wetland, index.BiomeFor(idInR));
      Assert.AreEqual(BiomeKind.Lake, index.BiomeFor(idInR2));
    }

    [TestMethod]
    public void ChunksAtChunkXY_AfterRebuild_ReflectsFreshSnapshot() {
      // After Rebuild swaps in fresh state, the secondary index must
      // reflect the new snapshot, not the old. Pins lifecycle: shadow
      // → live swap covers _chunksByXY as well as _chunkToCluster.
      // Without the swap, ChunksAtChunkXY would silently return stale
      // (region, cluster) tuples after a Rebuild.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);
      index.Rebuild(new[] { R }, Threshold);
      var beforeIds = index.ChunksAtChunkXY(0, 0).Select(e => e.Cluster).ToList();
      Assert.AreEqual(1, beforeIds.Count);

      // Mutate the world so the cluster no longer qualifies.
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 0.1f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 0.1f);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert: secondary index reflects the new (empty) snapshot.
      Assert.AreEqual(0, index.ChunksAtChunkXY(0, 0).Count,
          "After a rebuild that produces no clusters, the secondary index must be empty -- the shadow→live swap covers _chunksByXY.");
    }

    #endregion

    [TestMethod]
    public void Rebuild_LShape_AllFourChunksOneCluster() {
      // Arrange: 2x2 grid with three of four corners filled in an
      // L shape — verifies that the union-find correctly merges
      // through both right- and down-adjacency in one pass.
      //   (0,0) (1,0)
      //   (0,1)
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 2);
      var field = query.GetField(R)!;
      MarkValid(field, 0, 0);
      MarkValid(field, 1, 0);
      MarkValid(field, 0, 1);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 0, 1, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(1, index.ClusterCount);
      var id = index.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(id, index.ClusterFor(R, 1, 0));
      Assert.AreEqual(id, index.ClusterFor(R, 0, 1));
      Assert.AreEqual(3, index.ChunksIn(id).Count);
      Assert.AreEqual(3 * TilesPerChunk, index.TileCount(id));
    }

    #region 4-connectivity

    [TestMethod]
    public void Rebuild_DiagonalNeighboursSameBiome_StayInSeparateClusters() {
      // Arrange: 4x2 grid with two 2-chunk same-biome pairs arranged
      // diagonally — top-left pair at row 0, bottom-right pair at row 1,
      // offset so their endpoints touch only at a diagonal corner.
      //   W W . .
      //   . . W W
      // The (1,0)-(2,1) corner is diagonal, not 4-adjacent. Pins
      // 4-connectivity (rejects "let's also do diagonals" drift).
      // Pairs (rather than the older 2x2 singleton arrangement) so
      // each surviving cluster has size ≥ 2.
      var (store, query) = MakeWorld(chunksX: 4, chunksY: 2);
      var field = query.GetField(R)!;
      MarkValid(field, 0, 0); MarkValid(field, 1, 0);
      MarkValid(field, 2, 1); MarkValid(field, 3, 1);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 2, 1, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      SetBiome(store, R, 3, 1, BiomeKind.Wetland, suitability: 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(2, index.ClusterCount, "Diagonal-only adjacency must not union.");
      var idTop = index.ClusterFor(R, 0, 0)!.Value;
      var idBottom = index.ClusterFor(R, 2, 1)!.Value;
      Assert.AreNotEqual(idTop, idBottom);
      Assert.AreEqual(idTop, index.ClusterFor(R, 1, 0));
      Assert.AreEqual(idBottom, index.ClusterFor(R, 3, 1));
    }

    [TestMethod]
    public void Rebuild_CrossPatternWithDiagonalArms_FiveClustersNoneUnioned() {
      // Arrange: 5x5 grid with five same-biome 2-chunk pairs arranged
      // in a cross (four corners + center). Layout:
      //   W W . W W
      //   . . . . .
      //   . . W W .
      //   . . . . .
      //   W W . W W
      // No pair is 4-adjacent to any other (corner-pair endpoints are
      // separated from the center pair by empty rows AND columns; the
      // four corner pairs are separated from each other by both an
      // empty column and an empty row). Pins both axes of the design:
      // no diagonal union, and no spurious 4-connectivity across gaps.
      // (Previously a 3x3 singleton cross; extended to 2-chunk pairs so
      // each cluster survives the singleton-drop filter without altering
      // the no-union design point.)
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 5);
      var field = query.GetField(R)!;
      // Top-left pair
      MarkValid(field, 0, 0); MarkValid(field, 1, 0);
      // Top-right pair
      MarkValid(field, 3, 0); MarkValid(field, 4, 0);
      // Center pair
      MarkValid(field, 2, 2); MarkValid(field, 3, 2);
      // Bottom-left pair
      MarkValid(field, 0, 4); MarkValid(field, 1, 4);
      // Bottom-right pair
      MarkValid(field, 3, 4); MarkValid(field, 4, 4);
      foreach (var (x, y) in new[] {
          (0,0),(1,0), (3,0),(4,0), (2,2),(3,2),
          (0,4),(1,4), (3,4),(4,4) }) {
        SetBiome(store, R, x, y, BiomeKind.Wetland, 0.8f, 5f);
      }
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert: five distinct 2-chunk clusters, no union across the
      // diagonal / gap relationships.
      Assert.AreEqual(5, index.ClusterCount);
      var idTL = index.ClusterFor(R, 0, 0)!.Value;
      var idTR = index.ClusterFor(R, 3, 0)!.Value;
      var idC  = index.ClusterFor(R, 2, 2)!.Value;
      var idBL = index.ClusterFor(R, 0, 4)!.Value;
      var idBR = index.ClusterFor(R, 3, 4)!.Value;
      var all = new HashSet<ChunkClusterId> { idTL, idTR, idC, idBL, idBR };
      Assert.AreEqual(5, all.Count, "All five pairs should be distinct clusters.");
      // Each pair's two chunks share their cluster id.
      Assert.AreEqual(idTL, index.ClusterFor(R, 1, 0));
      Assert.AreEqual(idTR, index.ClusterFor(R, 4, 0));
      Assert.AreEqual(idC,  index.ClusterFor(R, 3, 2));
      Assert.AreEqual(idBL, index.ClusterFor(R, 1, 4));
      Assert.AreEqual(idBR, index.ClusterFor(R, 4, 4));
    }

    #endregion

    #region Threshold semantics

    [TestMethod]
    public void Rebuild_MaturityExactlyAtThreshold_Qualifies() {
      // Pin the boundary: ≥ threshold, not strictly greater. Two
      // adjacent chunks at Maturity == Threshold form one cluster
      // (the boundary case is observable because the resulting pair
      // clears the singleton-drop filter; a single boundary chunk
      // would be dropped and we couldn't distinguish "boundary
      // exclusive" from "singleton dropped").
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: Threshold);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.8f, maturity: Threshold);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(1, index.ClusterCount);
      var id = index.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(id, index.ClusterFor(R, 1, 0));
    }

    [TestMethod]
    public void Rebuild_ZeroThreshold_AnyChunkWithPositiveMaturityQualifies() {
      // Pin the Maturity-dominance contract: cluster identity is
      // argmax over per-biome Maturity (not Suitability). With
      // threshold=0, any chunk whose dominant biome has *some*
      // positive Maturity qualifies. A chunk with Maturity=0 for
      // every biome has no Maturity-dominant biome and is absent —
      // distinct from the prior Suitability-dominance behaviour
      // where Suitability=0.8 alone was enough to qualify.
      var (store, query) = MakeWorld(chunksX: 3, chunksY: 1);
      var field = query.GetField(R)!;
      MarkValid(field, 0, 0); MarkValid(field, 1, 0); MarkValid(field, 2, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, maturity: 0.1f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, maturity: 0.1f);
      // Chunk (2,0) is valid but has no biome entries at all —
      // every Maturity reads 0, so it's not in any cluster.
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, maturityThreshold: 0f);

      // Assert
      Assert.AreEqual(1, index.ClusterCount, "Maturity-zero chunk has no Maturity-dominant biome so it's absent; (0,0)+(1,0) form one cluster.");
      var id = index.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(id, index.ClusterFor(R, 1, 0));
      Assert.IsNull(index.ClusterFor(R, 2, 0));
    }

    [TestMethod]
    public void Rebuild_MaturityDominanceIgnoresSuitabilityFlips() {
      // The bug this design shift fixes: under the old Suitability-
      // dominance contract, a brief Suitability transient could flip
      // a chunk's dominant biome and (if the new dominant's Maturity
      // was below the cluster threshold) drop the chunk from the
      // cluster index, despawning fauna on it. Under Maturity-
      // dominance, the chunk's slow-rising Maturity for the
      // historical biome is what determines identity — a Suitability
      // flip to a younger biome can't dislodge it.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      var field = query.GetField(R)!;
      MarkValid(field, 0, 0); MarkValid(field, 1, 0);
      // Both chunks: Wetland accumulated lots of Maturity over time;
      // Lake's Suitability briefly spiked higher than Wetland's
      // but its Maturity is still nascent.
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, suitability: 0.2f, maturity: 5f);
      SetBiome(store, R, 0, 0, BiomeKind.Lake, suitability: 0.9f, maturity: 0.1f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, suitability: 0.2f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Lake, suitability: 0.9f, maturity: 0.1f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, maturityThreshold: 1f);

      // Assert
      Assert.AreEqual(1, index.ClusterCount,
          "Maturity-dominance pins identity to Wetland; Suitability flip to Lake is ignored.");
      var id = index.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(BiomeKind.Wetland, index.BiomeFor(id));
    }

    [TestMethod]
    public void Rebuild_VeryHighThreshold_NoClusters() {
      // Threshold higher than any chunk's Maturity → nothing
      // qualifies → empty index.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0); MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, maturity: 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, maturity: 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, maturityThreshold: 100f);

      // Assert
      Assert.AreEqual(0, index.ClusterCount);
      Assert.IsNull(index.ClusterFor(R, 0, 0));
    }

    #endregion

    #region Multi-cluster and multi-region

    [TestMethod]
    public void Rebuild_ThreeDisjointWetlandClustersOneRegion_AllPresent() {
      // Arrange: 1x8 row with three Wetland pairs separated by empty
      // chunks: W W . W W . W W. Pins that disjoint same-biome groups
      // in one region surface as distinct clusters (no merging through
      // gaps). All three groups are 2-chunk pairs (previously the third
      // was a singleton); using pairs keeps the design point — three
      // disjoint regions in one biome — while satisfying the singleton-
      // drop filter.
      var (store, query) = MakeWorld(chunksX: 8, chunksY: 1);
      var field = query.GetField(R)!;
      for (var i = 0; i < 8; i++) MarkValid(field, i, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      // Chunk (2,0) empty — no dominant biome.
      SetBiome(store, R, 3, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 4, 0, BiomeKind.Wetland, 0.8f, 5f);
      // Chunk (5,0) empty.
      SetBiome(store, R, 6, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 7, 0, BiomeKind.Wetland, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert: 3 clusters, each a 2-chunk pair, all distinct.
      Assert.AreEqual(3, index.ClusterCount);
      var idA = index.ClusterFor(R, 0, 0)!.Value;
      var idB = index.ClusterFor(R, 3, 0)!.Value;
      var idC = index.ClusterFor(R, 6, 0)!.Value;
      Assert.AreEqual(2, index.ChunksIn(idA).Count);
      Assert.AreEqual(2, index.ChunksIn(idB).Count);
      Assert.AreEqual(2, index.ChunksIn(idC).Count);
      Assert.AreNotEqual(idA, idB);
      Assert.AreNotEqual(idB, idC);
      Assert.AreNotEqual(idA, idC);
      Assert.IsNull(index.ClusterFor(R, 2, 0));
      Assert.IsNull(index.ClusterFor(R, 5, 0));
    }

    [TestMethod]
    public void Rebuild_TwoRegionsEachWithOwnClusters_IndependentCounts() {
      // Arrange: each region has its own 2-chunk Wetland cluster.
      // Verifies that the per-region rebuild loop produces 2
      // clusters total, each containing the right region's chunks.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      query.AddRegion(R2, originX: 0, originY: 0, chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);  MarkValid(query.GetField(R)!, 1, 0);
      MarkValid(query.GetField(R2)!, 0, 0); MarkValid(query.GetField(R2)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R2, 0, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(store, R2, 1, 0, BiomeKind.Lake, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R, R2 }, Threshold);

      // Assert
      Assert.AreEqual(2, index.ClusterCount);
      var idR = index.ClusterFor(R, 0, 0)!.Value;
      var idR2 = index.ClusterFor(R2, 0, 0)!.Value;
      Assert.AreEqual(BiomeKind.Wetland, index.BiomeFor(idR));
      Assert.AreEqual(BiomeKind.Lake, index.BiomeFor(idR2));
      Assert.AreEqual(2, index.ChunksIn(idR).Count);
      Assert.AreEqual(2, index.ChunksIn(idR2).Count);
    }

    [TestMethod]
    public void Rebuild_WetlandLakeWetlandRow_DistinctClusters() {
      // Arrange: WW LL WW row. The two Wetland pairs aren't adjacent
      // (Lake pair separates), so they're distinct clusters. Three
      // clusters total. Pins the "only same-biome unions" rule against
      // a thinko where neighbours might merge if either side qualifies.
      // All three groups are 2-chunk pairs (previously singletons) so
      // each survives the singleton-drop filter; the design point being
      // pinned is biome-purity of unions, not the singleton arrangement.
      var (store, query) = MakeWorld(chunksX: 6, chunksY: 1);
      var field = query.GetField(R)!;
      for (var i = 0; i < 6; i++) MarkValid(field, i, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 2, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(store, R, 3, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(store, R, 4, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 5, 0, BiomeKind.Wetland, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(3, index.ClusterCount);
      var idWLeft = index.ClusterFor(R, 0, 0)!.Value;
      var idL = index.ClusterFor(R, 2, 0)!.Value;
      var idWRight = index.ClusterFor(R, 4, 0)!.Value;
      Assert.AreNotEqual(idWLeft, idWRight,
          "Wetland pairs separated by a Lake pair must not union through it.");
      Assert.AreNotEqual(idWLeft, idL);
      Assert.AreNotEqual(idWRight, idL);
      Assert.AreEqual(BiomeKind.Wetland, index.BiomeFor(idWLeft));
      Assert.AreEqual(BiomeKind.Lake, index.BiomeFor(idL));
      Assert.AreEqual(BiomeKind.Wetland, index.BiomeFor(idWRight));
    }

    [TestMethod]
    public void Rebuild_FiveChunkRowSameBiome_TileCountIsFiveTimesChunkArea() {
      // Pin tile-count accounting for multi-chunk clusters.
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 1);
      var field = query.GetField(R)!;
      for (var i = 0; i < 5; i++) {
        MarkValid(field, i, 0);
        SetBiome(store, R, i, 0, BiomeKind.Wetland, 0.8f, 5f);
      }
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      Assert.AreEqual(1, index.ClusterCount);
      var id = index.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(5, index.ChunksIn(id).Count);
      Assert.AreEqual(5 * TilesPerChunk, index.TileCount(id));
    }

    #endregion

    #region Rebuild lifecycle

    [TestMethod]
    public void Rebuild_NoRegions_EmptyIndex() {
      var (store, query) = MakeWorld(chunksX: 1, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act: rebuild with no regions in the iterator at all.
      index.Rebuild(System.Array.Empty<RegionId>(), Threshold);

      // Assert
      Assert.AreEqual(0, index.ClusterCount);
      Assert.IsNull(index.ClusterFor(R, 0, 0));
    }

    [TestMethod]
    public void Rebuild_RegionWithoutPublishedField_Skipped() {
      // Region exists in the iterator but has no field yet
      // (FieldFor returns null). Should be silently skipped without
      // affecting the real region's output. The real region carries
      // two valid chunks so its cluster survives the singleton-drop
      // filter and the assertion can observe it; the design point is
      // "ghost region is harmless," and we need a non-empty index to
      // observe that.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      var ghostRegion = new RegionId(99);  // no AddRegion for this id
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R, ghostRegion }, Threshold);

      // Assert: real region contributes one cluster; ghost is skipped.
      Assert.AreEqual(1, index.ClusterCount);
      Assert.IsNotNull(index.ClusterFor(R, 0, 0));
      Assert.IsNull(index.ClusterFor(ghostRegion, 0, 0));
    }

    [TestMethod]
    public void Rebuild_RebuiltAfterValueChange_ReflectsNewState() {
      // Arrange: WWLL — initial state has a Wetland pair and a Lake
      // pair, two clusters. Pins that after the store mutates and we
      // rebuild, the new state surfaces (no cached/stale memo). Four
      // chunks rather than two so a post-mutation cluster still
      // survives the singleton-drop filter; the design point is
      // "rebuild reflects current store contents," not the cluster
      // arrangement itself.
      var (store, query) = MakeWorld(chunksX: 4, chunksY: 1);
      var field = query.GetField(R)!;
      for (var i = 0; i < 4; i++) MarkValid(field, i, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 2, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(store, R, 3, 0, BiomeKind.Lake, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);
      index.Rebuild(new[] { R }, Threshold);
      Assert.AreEqual(2, index.ClusterCount, "Pre-state: WW + LL = two clusters.");

      // Act: flip chunk (1,0) from Wetland to Lake. New row is W L L L
      // → a lone Wetland at (0,0) (dropped as singleton) plus a 3-chunk
      // Lake cluster spanning (1,0)-(3,0).
      store.Set(R, 1, 0, BiomeValueKinds.ForSuitability(BiomeKind.Wetland), 0f);
      store.Set(R, 1, 0, BiomeValueKinds.ForMaturity(BiomeKind.Wetland), 0f);
      store.Set(R, 1, 0, BiomeValueKinds.ForSuitability(BiomeKind.Lake), 0.8f);
      store.Set(R, 1, 0, BiomeValueKinds.ForMaturity(BiomeKind.Lake), 5f);
      index.Rebuild(new[] { R }, Threshold);

      // Assert: state reflects the mutation. The Wetland cluster from
      // pre-state is gone (its right chunk flipped, leaving a singleton
      // which drops). The Lake cluster grew from 2 to 3 chunks.
      Assert.AreEqual(1, index.ClusterCount);
      Assert.IsNull(index.ClusterFor(R, 0, 0),
          "(0,0) is now a lone Wetland chunk and drops as a singleton.");
      var idLake = index.ClusterFor(R, 1, 0)!.Value;
      Assert.AreEqual(BiomeKind.Lake, index.BiomeFor(idLake));
      Assert.AreEqual(3, index.ChunksIn(idLake).Count,
          "Post-flip Lake cluster includes (1,0)(2,0)(3,0).");
      Assert.AreEqual(idLake, index.ClusterFor(R, 2, 0));
      Assert.AreEqual(idLake, index.ClusterFor(R, 3, 0));
    }

    [TestMethod]
    public void Rebuild_PriorEntriesDoNotLeakAcrossRebuilds() {
      // Arrange: first rebuild produces a cluster for R from two
      // adjacent Wetland chunks (two — not one — so the cluster
      // survives the singleton-drop filter and we have something
      // non-empty to verify gets cleared).
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);
      index.Rebuild(new[] { R }, Threshold);
      Assert.IsNotNull(index.ClusterFor(R, 0, 0));
      Assert.AreEqual(1, index.ClusterCount);

      // Act: rebuild with an empty region list. The prior R cluster
      // must not survive.
      index.Rebuild(System.Array.Empty<RegionId>(), Threshold);

      // Assert
      Assert.AreEqual(0, index.ClusterCount);
      Assert.IsNull(index.ClusterFor(R, 0, 0));
      Assert.IsNull(index.ClusterFor(R, 1, 0));
    }

    #endregion

    #region Incremental rebuild API

    [TestMethod]
    public void IncrementalRebuild_MatchesAtomicRebuild_OverSameInputs() {
      // Pin that Begin -> Include* -> Commit produces an index
      // observationally equivalent to a single atomic Rebuild call
      // over the same regions. (Rebuild itself is implemented as a
      // thin wrapper over the incremental API, so this is also a
      // direct regression on the wrapper.)
      var (storeA, queryA) = MakeWorld(chunksX: 4, chunksY: 1);
      queryA.AddRegion(R2, originX: 4, originY: 0, chunksX: 2, chunksY: 1);
      var fieldA = queryA.GetField(R)!;
      var fieldR2A = queryA.GetField(R2)!;
      for (var i = 0; i < 4; i++) MarkValid(fieldA, i, 0);
      MarkValid(fieldR2A, 0, 0);
      MarkValid(fieldR2A, 1, 0);
      SetBiome(storeA, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(storeA, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(storeA, R, 2, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(storeA, R, 3, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(storeA, R2, 4, 0, BiomeKind.Forest, 0.8f, 5f);
      SetBiome(storeA, R2, 5, 0, BiomeKind.Forest, 0.8f, 5f);
      var indexAtomic = new ChunkClusterIndex(storeA, queryA);
      indexAtomic.Rebuild(new[] { R, R2 }, Threshold);

      var (storeB, queryB) = MakeWorld(chunksX: 4, chunksY: 1);
      queryB.AddRegion(R2, originX: 4, originY: 0, chunksX: 2, chunksY: 1);
      var fieldB = queryB.GetField(R)!;
      var fieldR2B = queryB.GetField(R2)!;
      for (var i = 0; i < 4; i++) MarkValid(fieldB, i, 0);
      MarkValid(fieldR2B, 0, 0);
      MarkValid(fieldR2B, 1, 0);
      SetBiome(storeB, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(storeB, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(storeB, R, 2, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(storeB, R, 3, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(storeB, R2, 4, 0, BiomeKind.Forest, 0.8f, 5f);
      SetBiome(storeB, R2, 5, 0, BiomeKind.Forest, 0.8f, 5f);
      var indexIncremental = new ChunkClusterIndex(storeB, queryB);

      // Act: drive the incremental API in the same shape the ticker would.
      indexIncremental.BeginRebuild();
      indexIncremental.IncludeRegionInRebuild(R, Threshold);
      indexIncremental.IncludeRegionInRebuild(R2, Threshold);
      indexIncremental.CommitRebuild();

      // Assert: both indexes agree on cluster count, biome assignment,
      // and score aggregates for every chunk.
      Assert.AreEqual(indexAtomic.ClusterCount, indexIncremental.ClusterCount);
      foreach (var (region, cx, cy) in new[] {
          (R, 0, 0), (R, 1, 0), (R, 2, 0), (R, 3, 0),
          (R2, 4, 0), (R2, 5, 0) }) {
        var idA = indexAtomic.ClusterFor(region, cx, cy);
        var idI = indexIncremental.ClusterFor(region, cx, cy);
        Assert.AreEqual(idA.HasValue, idI.HasValue, $"({region},{cx},{cy}) membership mismatch.");
        if (!idA.HasValue) continue;
        Assert.AreEqual(indexAtomic.BiomeFor(idA.Value), indexIncremental.BiomeFor(idI!.Value));
        Assert.AreEqual(indexAtomic.RawScore(idA.Value), indexIncremental.RawScore(idI.Value), 1e-5f);
        Assert.AreEqual(indexAtomic.ChunkCount(idA.Value), indexIncremental.ChunkCount(idI.Value));
      }
    }

    [TestMethod]
    public void IncrementalRebuild_DuringBuild_QueriesReturnPreviousSnapshot() {
      // The shadow must not be observable mid-build. Consumers reading
      // ClusterFor between Begin and Commit should see the previous
      // snapshot consistently. Pin that the live state is untouched
      // until CommitRebuild swaps it in.
      var (store, query) = MakeWorld(chunksX: 4, chunksY: 1);
      var field = query.GetField(R)!;
      for (var i = 0; i < 4; i++) MarkValid(field, i, 0);
      // Pre-state: two Wetland chunks form a cluster; the Lake half is
      // empty in the store, so no Lake cluster exists yet.
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);
      index.Rebuild(new[] { R }, Threshold);

      var preCount = index.ClusterCount;
      var preWetlandId = index.ClusterFor(R, 0, 0)!.Value;
      var preVersion = index.Version;

      // Act: start an incremental rebuild but don't commit yet. Add
      // Lake values to the store too so the new snapshot will differ.
      SetBiome(store, R, 2, 0, BiomeKind.Lake, 0.8f, 5f);
      SetBiome(store, R, 3, 0, BiomeKind.Lake, 0.8f, 5f);
      index.BeginRebuild();
      index.IncludeRegionInRebuild(R, Threshold);
      // CommitRebuild deliberately NOT called yet.

      // Assert: queries still see the pre-state snapshot. Version is
      // unchanged. The Lake chunks are not yet observable.
      Assert.AreEqual(preCount, index.ClusterCount,
          "Mid-build ClusterCount must equal the previous snapshot's count.");
      Assert.AreEqual(preVersion, index.Version,
          "Version must not bump until CommitRebuild fires.");
      Assert.AreEqual(preWetlandId, index.ClusterFor(R, 0, 0)!.Value,
          "Pre-state cluster id must remain stable until the swap.");
      Assert.IsNull(index.ClusterFor(R, 2, 0),
          "Newly-added Lake chunks must not be visible until CommitRebuild.");

      // Cleanup: commit so the rebuild state isn't left dangling.
      index.CommitRebuild();
      Assert.AreNotEqual(preVersion, index.Version, "Version bumps on Commit.");
    }

    [TestMethod]
    public void IncrementalRebuild_BeginWithoutCommit_ThenBeginAgain_Throws() {
      // Pin the API-ordering guard: calling BeginRebuild twice without
      // an intervening Commit is a programmer error. Catching it here
      // protects against a driver that loses track of its own state.
      var (store, query) = MakeWorld(chunksX: 1, chunksY: 1);
      var index = new ChunkClusterIndex(store, query);
      index.BeginRebuild();
      Assert.ThrowsException<System.InvalidOperationException>(() => index.BeginRebuild());
      // Cleanup so the assertion-throw doesn't leave the index in a
      // weird state for other tests sharing setup helpers.
      index.CommitRebuild();
    }

    [TestMethod]
    public void IncrementalRebuild_IncludeWithoutBegin_Throws() {
      var (store, query) = MakeWorld(chunksX: 1, chunksY: 1);
      var index = new ChunkClusterIndex(store, query);
      Assert.ThrowsException<System.InvalidOperationException>(
          () => index.IncludeRegionInRebuild(R, Threshold));
    }

    [TestMethod]
    public void IncrementalRebuild_CommitWithoutBegin_Throws() {
      var (store, query) = MakeWorld(chunksX: 1, chunksY: 1);
      var index = new ChunkClusterIndex(store, query);
      Assert.ThrowsException<System.InvalidOperationException>(() => index.CommitRebuild());
    }

    [TestMethod]
    public void IncrementalRebuild_BeginCommitWithNoIncludes_EmptyIndex() {
      // Begin + Commit with zero Includes is the cycle-on-empty-world
      // case (no regions; or all regions skipped). Result is an empty
      // index, matching Rebuild(empty, _).
      var (store, query) = MakeWorld(chunksX: 1, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);
      // Seed prior state to verify Commit wipes it.
      index.Rebuild(new[] { R }, Threshold);

      // Act
      index.BeginRebuild();
      index.CommitRebuild();

      // Assert
      Assert.AreEqual(0, index.ClusterCount);
      Assert.IsNull(index.ClusterFor(R, 0, 0));
    }

    #endregion

    #region Invalid id and missing-entry queries

    [TestMethod]
    public void Queries_OnUnknownChunkCoord_ReturnNull() {
      var (store, query) = MakeWorld(chunksX: 1, chunksY: 1);
      var index = new ChunkClusterIndex(store, query);
      index.Rebuild(new[] { R }, Threshold);

      Assert.IsNull(index.ClusterFor(R, 42, 42));
      Assert.IsNull(index.ClusterFor(new RegionId(999), 0, 0));
    }

    [TestMethod]
    public void Queries_OnNegativeIdOrOutOfRange_ReturnEmptyOrNull() {
      // Defends ChunksIn / TileCount / BiomeFor against a stale or
      // forged ChunkClusterId without any entries in the current
      // snapshot.
      var (store, _) = MakeWorld(chunksX: 1, chunksY: 1);
      var index = new ChunkClusterIndex(store, new FakeFieldQuery());
      index.Rebuild(System.Array.Empty<RegionId>(), Threshold);  // empty snapshot

      var forged = new ChunkClusterId(-1);
      Assert.AreEqual(0, index.ChunksIn(forged).Count);
      Assert.AreEqual(0, index.TileCount(forged));
      Assert.IsNull(index.BiomeFor(forged));

      var forgedTooLarge = new ChunkClusterId(100);
      Assert.AreEqual(0, index.ChunksIn(forgedTooLarge).Count);
      Assert.AreEqual(0, index.TileCount(forgedTooLarge));
      Assert.IsNull(index.BiomeFor(forgedTooLarge));
    }

    #endregion

    #region Per-cluster aggregates

    [TestMethod]
    public void Aggregates_MultiChunkCluster_AverageIsArithmeticMean() {
      // Arrange: three Wetland chunks at 3, 6, 12 → mean = 7.
      var (store, query) = MakeWorld(chunksX: 3, chunksY: 1);
      var field = query.GetField(R)!;
      MarkValid(field, 0, 0); MarkValid(field, 1, 0); MarkValid(field, 2, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, maturity: 3f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, maturity: 6f);
      SetBiome(store, R, 2, 0, BiomeKind.Wetland, 0.8f, maturity: 12f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      var id = index.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(3, index.ChunkCount(id));
      Assert.AreEqual(7f, index.AverageMaturity(id), 1e-5f);
      Assert.AreEqual(12f, index.MaxMaturity(id), 1e-5f);
    }

    [TestMethod]
    public void Aggregates_TileCountsAbove_ParallelToThresholdsList_WeightedBySampleCount() {
      // Arrange: five-chunk Wetland cluster spanning the five
      // threshold bands. Chunks use varied sample counts so the
      // threshold buckets distinguish tile-weighted from chunk-counted.
      //   maturity 2.0 (clears nothing, below 2.5), sampleCount 8
      //   maturity 3.0 (clears 2.5),                sampleCount 4
      //   maturity 7.0 (clears 2.5, 5.0),           sampleCount 16
      //   maturity 12.0 (clears 2.5, 5.0, 10.0),    sampleCount 16
      //   maturity 22.0 (clears all five),          sampleCount 12
      // Expected tile counts per threshold:
      //   ≥2.5:  4 + 16 + 16 + 12 = 48
      //   ≥5.0:      16 + 16 + 12 = 44
      //   ≥10.0:          16 + 12 = 28
      //   ≥15.0:               12 = 12
      //   ≥20.0:               12 = 12
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 1);
      var field = query.GetField(R)!;
      var maturities    = new[] {  2f,  3f,  7f, 12f, 22f };
      var sampleCounts  = new[] {   8,   4,  16,  16,  12 };
      for (var i = 0; i < 5; i++) {
        MarkValidWithSampleCount(field, i, 0, sampleCounts[i]);
        SetBiome(store, R, i, 0, BiomeKind.Wetland, 0.8f, maturities[i]);
      }
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      var id = index.ClusterFor(R, 0, 0)!.Value;
      var counts = index.TileCountsAbove(id);
      Assert.AreEqual(ChunkClusterIndex.Thresholds.Count, counts.Count);
      CollectionAssert.AreEqual(new[] { 48, 44, 28, 12, 12 }, counts.ToArray());
      // Cluster's overall TileCount = sum of all sample counts.
      Assert.AreEqual(8 + 4 + 16 + 16 + 12, index.TileCount(id));
    }

    [TestMethod]
    public void Aggregates_TileCount_ExactlyAtBoundary_Qualifies() {
      // Pin "≥ threshold, not strictly greater" for the tile-count
      // buckets. Two adjacent chunks both at Maturity exactly 5.0:
      // both belong in the ≥5.0 bucket (32 tiles total at the default
      // 16-tile sample), neither in the ≥10.0 bucket. Two chunks rather
      // than one so the cluster survives the singleton-drop filter;
      // the design point is the boundary-inclusion semantics, observable
      // identically on any cluster size.
      var (store, query) = MakeWorld(chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      SetBiome(store, R, 0, 0, BiomeKind.Wetland, 0.8f, maturity: 5.0f);
      SetBiome(store, R, 1, 0, BiomeKind.Wetland, 0.8f, maturity: 5.0f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      var id = index.ClusterFor(R, 0, 0)!.Value;
      var counts = index.TileCountsAbove(id);
      var idx5 = IndexOfThreshold(5.0f);
      var idx10 = IndexOfThreshold(10.0f);
      Assert.AreEqual(2 * TilesPerChunk, counts[idx5],
          "Maturity == 5.0 must put both chunks' tiles in the ≥5.0 bucket.");
      Assert.AreEqual(0, counts[idx10], "Maturity == 5.0 must NOT contribute to the ≥10.0 bucket.");
    }

    private static int IndexOfThreshold(float value) {
      for (var i = 0; i < ChunkClusterIndex.Thresholds.Count; i++) {
        if (ChunkClusterIndex.Thresholds[i] == value) return i;
      }
      throw new System.ArgumentException($"Threshold {value} not in Thresholds list.");
    }

    [TestMethod]
    public void Aggregates_InvalidId_ReturnDefaults() {
      // Forged / out-of-range cluster ids must produce safe defaults
      // (0 / empty), matching the existing TileCount / BiomeFor /
      // ChunksIn defensive behaviour pinned in
      // Queries_OnNegativeIdOrOutOfRange_ReturnEmptyOrNull.
      var (store, _) = MakeWorld(chunksX: 1, chunksY: 1);
      var index = new ChunkClusterIndex(store, new FakeFieldQuery());
      index.Rebuild(System.Array.Empty<RegionId>(), Threshold);

      var forged = new ChunkClusterId(-1);
      Assert.AreEqual(0, index.ChunkCount(forged));
      Assert.AreEqual(0f, index.AverageMaturity(forged), 1e-5f);
      Assert.AreEqual(0f, index.MaxMaturity(forged), 1e-5f);
      Assert.AreEqual(0, index.TileCountsAbove(forged).Count);
      Assert.AreEqual(0, index.TilesInTier(forged).Count);
      Assert.AreEqual(0f, index.RawScore(forged), 1e-5f);
      Assert.AreEqual(0f, index.Score(forged), 1e-5f);
    }

    #endregion

    #region Score and tier histogram

    [TestMethod]
    public void Score_TilesInTier_DerivedFromTileCountsAboveByAdjacentDifference() {
      // Arrange: same five-band fixture as the TileCountsAbove test so
      // we can cross-check both views in one place. Cumulative counts
      // come out to {48, 44, 28, 12, 12}; per-tier histogram is
      // {48-44, 44-28, 28-12, 12-12, 12} = {4, 16, 16, 0, 12}. Pins
      // the "histogram and cumulative always agree" invariant.
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 1);
      var field = query.GetField(R)!;
      var maturities   = new[] {  2f,  3f,  7f, 12f, 22f };
      var sampleCounts = new[] {   8,   4,  16,  16,  12 };
      for (var i = 0; i < 5; i++) {
        MarkValidWithSampleCount(field, i, 0, sampleCounts[i]);
        SetBiome(store, R, i, 0, BiomeKind.Wetland, 0.8f, maturities[i]);
      }
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert: histogram derived from cumulative array by differences.
      var id = index.ClusterFor(R, 0, 0)!.Value;
      var tier = index.TilesInTier(id);
      Assert.AreEqual(5, tier.Count);
      CollectionAssert.AreEqual(new[] { 4, 16, 16, 0, 12 }, tier.ToArray());
      // Cross-check: histogram sums equal lowest cumulative bucket.
      var sumTier = 0;
      for (var i = 0; i < tier.Count; i++) sumTier += tier[i];
      Assert.AreEqual(index.TileCountsAbove(id)[0], sumTier,
          "Sum of tier histogram must equal the ≥lowest-threshold cumulative count.");
    }

    [TestMethod]
    public void Score_RawScore_WeightedSumOverTileCountsAbove() {
      // Arrange: same fixture. Per BucketWeights {0.625, 1.25, 2.5,
      // 3.75, 5.0} and TileCountsAbove {48, 44, 28, 12, 12}:
      //   raw = 0.625*48 + 1.25*44 + 2.5*28 + 3.75*12 + 5.0*12
      //       = 30 + 55 + 70 + 45 + 60 = 260.
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 1);
      var field = query.GetField(R)!;
      var maturities   = new[] {  2f,  3f,  7f, 12f, 22f };
      var sampleCounts = new[] {   8,   4,  16,  16,  12 };
      for (var i = 0; i < 5; i++) {
        MarkValidWithSampleCount(field, i, 0, sampleCounts[i]);
        SetBiome(store, R, i, 0, BiomeKind.Wetland, 0.8f, maturities[i]);
      }
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert
      var id = index.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(260f, index.RawScore(id), 1e-3f);
    }

    [TestMethod]
    public void Score_NormalisedScore_IsHyperbolicSaturation() {
      // Score = raw / (raw + K). With K = 1000 and raw = 260,
      // expected score ≈ 260/1260 ≈ 0.2063.
      var (store, query) = MakeWorld(chunksX: 5, chunksY: 1);
      var field = query.GetField(R)!;
      var maturities   = new[] {  2f,  3f,  7f, 12f, 22f };
      var sampleCounts = new[] {   8,   4,  16,  16,  12 };
      for (var i = 0; i < 5; i++) {
        MarkValidWithSampleCount(field, i, 0, sampleCounts[i]);
        SetBiome(store, R, i, 0, BiomeKind.Wetland, 0.8f, maturities[i]);
      }
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert: matches the SaturatedScore static helper too.
      var id = index.ClusterFor(R, 0, 0)!.Value;
      var raw = index.RawScore(id);
      var expected = raw / (raw + ChunkClusterIndex.HalfSaturationRaw);
      Assert.AreEqual(expected, index.Score(id), 1e-5f);
      Assert.AreEqual(expected, ChunkClusterIndex.SaturatedScore(raw), 1e-5f);
    }

    [TestMethod]
    public void Score_SaturatedScore_AsymptoticIn0to1_NeverReaches1() {
      // Pin the asymptote: even at very large raw, Score < 1. This is
      // the design point the user asked for ("score should be asymptotic
      // for tile count") -- no cliff at saturation. Also pins the K
      // semantics: score(K) = 0.5 exactly.
      Assert.AreEqual(0f, ChunkClusterIndex.SaturatedScore(0f), 1e-7f);
      Assert.AreEqual(0.5f, ChunkClusterIndex.SaturatedScore(ChunkClusterIndex.HalfSaturationRaw), 1e-5f);
      Assert.IsTrue(ChunkClusterIndex.SaturatedScore(1e9f) < 1f,
          "Hyperbolic saturation must never reach 1, no matter how large the raw score.");
      Assert.IsTrue(ChunkClusterIndex.SaturatedScore(1e9f) > 0.999f,
          "Very-large raw should approach 1 closely.");
    }

    [TestMethod]
    public void Score_SaturatedScore_NegativeOrZero_ReturnsZero() {
      // Defensive: a degenerate empty cluster (raw = 0) returns score 0,
      // not NaN. Negative raw can't arise from the rebuild but guard
      // against it anyway.
      Assert.AreEqual(0f, ChunkClusterIndex.SaturatedScore(0f), 1e-7f);
      Assert.AreEqual(0f, ChunkClusterIndex.SaturatedScore(-100f), 1e-7f);
    }

    [TestMethod]
    public void Score_DistributionMatters_BimodalBeatsUniformMid() {
      // Arrange: two clusters with the SAME chunk count, different
      // maturity distributions. Convex per-tile contribution means
      // half-pristine + half-decent beats uniform-mid. Pins the
      // "quality dominates" property that motivated the score.
      //   Uniform: 10 chunks all at maturity 10 (each clears 3 buckets,
      //   contributes 0.625+1.25+2.5 = 4.375 per tile).
      //   Bimodal: 5 chunks at 20 (clears all 5, 13.125 per tile) +
      //   5 chunks at 11 (clears 3, 4.375 per tile).
      // With 16 tiles per chunk:
      //   raw(uniform) = 10*16*4.375 = 700
      //   raw(bimodal) = 5*16*13.125 + 5*16*4.375 = 1050 + 350 = 1400
      var (storeU, queryU) = MakeWorld(chunksX: 10, chunksY: 1);
      var fieldU = queryU.GetField(R)!;
      for (var i = 0; i < 10; i++) {
        MarkValid(fieldU, i, 0);
        SetBiome(storeU, R, i, 0, BiomeKind.Wetland, 0.8f, 10f);
      }
      var indexU = new ChunkClusterIndex(storeU, queryU);
      indexU.Rebuild(new[] { R }, Threshold);

      var (storeB, queryB) = MakeWorld(chunksX: 10, chunksY: 1);
      var fieldB = queryB.GetField(R)!;
      for (var i = 0; i < 5; i++) {
        MarkValid(fieldB, i, 0);
        SetBiome(storeB, R, i, 0, BiomeKind.Wetland, 0.8f, 20f);
      }
      for (var i = 5; i < 10; i++) {
        MarkValid(fieldB, i, 0);
        SetBiome(storeB, R, i, 0, BiomeKind.Wetland, 0.8f, 11f);
      }
      var indexB = new ChunkClusterIndex(storeB, queryB);
      indexB.Rebuild(new[] { R }, Threshold);

      // Act + Assert
      var rawU = indexU.RawScore(indexU.ClusterFor(R, 0, 0)!.Value);
      var rawB = indexB.RawScore(indexB.ClusterFor(R, 0, 0)!.Value);
      Assert.AreEqual(700f, rawU, 1e-3f);
      Assert.AreEqual(1400f, rawB, 1e-3f);
      Assert.IsTrue(rawB > rawU,
          "Bimodal half-pristine half-decent must out-score uniform-mid; quality dominates.");
    }

    [TestMethod]
    public void Score_QualityTimesQuantityTradeOff_SmallPristineMatchesLargeMediocre() {
      // Arrange: two clusters with very different sizes but similar
      // raw scores. The score formula treats them as ecologically
      // comparable -- the design point the user described as
      // "deer in a smaller very nice area or a larger less nice one."
      //   Small pristine: 8 chunks at maturity 22 (13.125 per tile, 16
      //   tiles each) -> raw = 8 * 16 * 13.125 = 1680.
      //   Large mediocre: 24 chunks at maturity 6 (clears 0,1 → 1.875
      //   per tile) -> raw = 24 * 16 * 1.875 = 720. Too low; bump to
      //   28 chunks at maturity 10 (4.375 per tile) -> raw = 28 * 16 *
      //   4.375 = 1960.
      // Both raws land between 1500 and 2000 → both score around
      // 0.6-0.65 with K=1000. Pin that they're within 15% of each
      // other to assert the quality/quantity trade-off is balanced.
      var (storeS, queryS) = MakeWorld(chunksX: 8, chunksY: 1);
      var fieldS = queryS.GetField(R)!;
      for (var i = 0; i < 8; i++) {
        MarkValid(fieldS, i, 0);
        SetBiome(storeS, R, i, 0, BiomeKind.Wetland, 0.8f, 22f);
      }
      var indexS = new ChunkClusterIndex(storeS, queryS);
      indexS.Rebuild(new[] { R }, Threshold);

      var (storeL, queryL) = MakeWorld(chunksX: 28, chunksY: 1);
      var fieldL = queryL.GetField(R)!;
      for (var i = 0; i < 28; i++) {
        MarkValid(fieldL, i, 0);
        SetBiome(storeL, R, i, 0, BiomeKind.Wetland, 0.8f, 10f);
      }
      var indexL = new ChunkClusterIndex(storeL, queryL);
      indexL.Rebuild(new[] { R }, Threshold);

      // Act
      var scoreS = indexS.Score(indexS.ClusterFor(R, 0, 0)!.Value);
      var scoreL = indexL.Score(indexL.ClusterFor(R, 0, 0)!.Value);

      // Assert: both meaningful (≥0.5), within 25% of each other.
      Assert.IsTrue(scoreS >= 0.5f, $"Small-pristine score {scoreS} should hit ≥0.5.");
      Assert.IsTrue(scoreL >= 0.5f, $"Large-mediocre score {scoreL} should hit ≥0.5.");
      var ratio = scoreS / scoreL;
      Assert.IsTrue(ratio > 0.75f && ratio < 1.33f,
          $"Quality/quantity trade-off should yield comparable scores; got S={scoreS}, L={scoreL}, ratio={ratio}.");
    }

    [TestMethod]
    public void Score_ChunksBelowFirstBucket_CountInClusterButContributeNothingToScore() {
      // Pin the gap between cluster qualification (Maturity ≥ 1.0) and
      // the first score bucket (Maturity ≥ 2.5). A chunk at maturity 1.5
      // is in the cluster (counts toward ChunkCount and TileCount) but
      // contributes zero to RawScore. Demonstrated by comparing two
      // clusters with the same "ecologically meaningful" content (two
      // mature chunks at Maturity 10) but different amounts of trailing
      // barely-mature padding (indexA has none, indexB has four).
      // <para>Two mature chunks (not one) so indexA's cluster survives
      // the singleton-drop filter; the design point is the gap between
      // qualification and scoring, which is observable on any cluster
      // size as long as both compared clusters exist.</para>
      var (storeA, queryA) = MakeWorld(chunksX: 2, chunksY: 1);
      var fieldA = queryA.GetField(R)!;
      MarkValid(fieldA, 0, 0); SetBiome(storeA, R, 0, 0, BiomeKind.Wetland, 0.8f, 10f);
      MarkValid(fieldA, 1, 0); SetBiome(storeA, R, 1, 0, BiomeKind.Wetland, 0.8f, 10f);
      var indexA = new ChunkClusterIndex(storeA, queryA);
      indexA.Rebuild(new[] { R }, Threshold);

      var (storeB, queryB) = MakeWorld(chunksX: 6, chunksY: 1);
      var fieldB = queryB.GetField(R)!;
      MarkValid(fieldB, 0, 0); SetBiome(storeB, R, 0, 0, BiomeKind.Wetland, 0.8f, 10f);
      MarkValid(fieldB, 1, 0); SetBiome(storeB, R, 1, 0, BiomeKind.Wetland, 0.8f, 10f);
      MarkValid(fieldB, 2, 0); SetBiome(storeB, R, 2, 0, BiomeKind.Wetland, 0.8f, 1.5f);
      MarkValid(fieldB, 3, 0); SetBiome(storeB, R, 3, 0, BiomeKind.Wetland, 0.8f, 1.5f);
      MarkValid(fieldB, 4, 0); SetBiome(storeB, R, 4, 0, BiomeKind.Wetland, 0.8f, 1.5f);
      MarkValid(fieldB, 5, 0); SetBiome(storeB, R, 5, 0, BiomeKind.Wetland, 0.8f, 1.5f);
      var indexB = new ChunkClusterIndex(storeB, queryB);
      indexB.Rebuild(new[] { R }, Threshold);

      // Act + Assert
      var idA = indexA.ClusterFor(R, 0, 0)!.Value;
      var idB = indexB.ClusterFor(R, 0, 0)!.Value;
      Assert.AreEqual(2, indexA.ChunkCount(idA));
      Assert.AreEqual(6, indexB.ChunkCount(idB), "Trailing maturity-1.5 chunks are cluster members.");
      Assert.AreEqual(indexA.RawScore(idA), indexB.RawScore(idB), 1e-3f,
          "Barely-mature chunks count toward size but contribute 0 to RawScore (below first bucket).");
    }

    #endregion

    #region Coordinate correctness

    [TestMethod]
    public void Rebuild_FieldWithNonZeroOrigin_UsesGlobalChunkCoords() {
      // Arrange: field originX/Y both non-zero. The store keys are
      // global chunk coords, so the cluster must look up the chunk
      // values at the global coord, not the local field index. Set
      // values at the GLOBAL coord and verify clustering still
      // finds them.
      var store = new ChunkValueStore();
      var query = new FakeFieldQuery();
      const int chunkSize = RegionEcologyField.ChunkSize;
      const int originTileX = 40;  // global chunk 10 (40 / 4)
      const int originTileY = 20;  // global chunk 5
      query.AddRegion(R, originX: originTileX, originY: originTileY, chunksX: 2, chunksY: 1);
      MarkValid(query.GetField(R)!, 0, 0);
      MarkValid(query.GetField(R)!, 1, 0);
      var globalCx0 = originTileX / chunkSize;
      var globalCy0 = originTileY / chunkSize;
      SetBiome(store, R, globalCx0,     globalCy0, BiomeKind.Wetland, 0.8f, 5f);
      SetBiome(store, R, globalCx0 + 1, globalCy0, BiomeKind.Wetland, 0.8f, 5f);
      var index = new ChunkClusterIndex(store, query);

      // Act
      index.Rebuild(new[] { R }, Threshold);

      // Assert: lookups MUST use the global coord, not the local one.
      Assert.AreEqual(1, index.ClusterCount);
      Assert.IsNotNull(index.ClusterFor(R, globalCx0, globalCy0));
      Assert.IsNotNull(index.ClusterFor(R, globalCx0 + 1, globalCy0));
      // Local-coord queries (0,0) and (1,0) must NOT find anything.
      Assert.IsNull(index.ClusterFor(R, 0, 0));
      Assert.IsNull(index.ClusterFor(R, 1, 0));
      // And the stored ChunkCoords in the cluster carry the global form.
      var chunks = index.ChunksIn(index.ClusterFor(R, globalCx0, globalCy0)!.Value);
      foreach (var c in chunks) {
        Assert.IsTrue(c.GlobalChunkX >= globalCx0, "Cluster chunks should carry global X.");
        Assert.IsTrue(c.GlobalChunkY >= globalCy0, "Cluster chunks should carry global Y.");
      }
    }

    #endregion

    #region Test helpers

    private static (ChunkValueStore Store, FakeFieldQuery Query) MakeWorld(int chunksX, int chunksY) {
      var store = new ChunkValueStore();
      var query = new FakeFieldQuery();
      query.AddRegion(R, originX: 0, originY: 0, chunksX: chunksX, chunksY: chunksY);
      return (store, query);
    }

    /// <summary>Marks one chunk valid via WriteChunk with zeroed
    /// channel values. We don't care about the ecology-field channels
    /// in cluster tests — only the validity flag + sample count
    /// affect <see cref="ChunkClusterIndex"/>. Default sample count
    /// is a full chunk (16 tiles); aggregate tests that need a
    /// different fill use <see cref="MarkValidWithSampleCount"/>.</summary>
    private static void MarkValid(RegionEcologyField field, int cx, int cy) =>
        MarkValidWithSampleCount(field, cx, cy, RegionEcologyField.ChunkSize * RegionEcologyField.ChunkSize);

    private static void MarkValidWithSampleCount(RegionEcologyField field, int cx, int cy, int sampleCount) {
      var scalars = new float[RegionEcologyField.FixedChannelCount];
      field.WriteChunk(cx, cy, valid: true, sampleCount, scalars, System.ReadOnlySpan<float>.Empty);
    }

    private static void SetBiome(
        ChunkValueStore store, RegionId region, int cx, int cy,
        BiomeKind biome, float suitability, float maturity) {
      store.Set(region, cx, cy, BiomeValueKinds.ForSuitability(biome), suitability);
      store.Set(region, cx, cy, BiomeValueKinds.ForMaturity(biome), maturity);
    }

    /// <summary>Minimal in-memory <see cref="IEcologyFieldQuery"/> for
    /// cluster tests. We only need <see cref="FieldFor"/>; the other
    /// members return defaults.</summary>
    private sealed class FakeFieldQuery : IEcologyFieldQuery {
      private readonly Dictionary<RegionId, RegionEcologyField> _fields = new();
      public void AddRegion(RegionId id, int originX, int originY, int chunksX, int chunksY) {
        _fields[id] = new RegionEcologyField(originX, originY, chunksX, chunksY, entityChannelCount: 0);
      }
      public RegionEcologyField? GetField(RegionId id) =>
          _fields.TryGetValue(id, out var f) ? f : null;
      public RegionEcologyField? FieldFor(RegionId region) => GetField(region);
      public int? EntityIndex(string blueprintName) => null;
      public IReadOnlyList<string> KnownEntityBlueprints { get; } = System.Array.Empty<string>();
      public int FieldShapeVersion => 0;
      public RegionTileData? TileDataFor(RegionId region) => null;
    }

    #endregion

  }

}
