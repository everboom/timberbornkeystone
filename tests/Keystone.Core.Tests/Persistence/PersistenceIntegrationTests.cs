using System.Collections.Generic;
using System.Linq;
using Keystone.Core.Persistence;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Core.Tests.Helpers;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Drives the full persistence path end-to-end at the Core level:
  /// build state, encode via <see cref="SnapshotCodec"/>, decode into a
  /// freshly-built world, restore stamps and scores, verify equivalence.
  /// Tests Mod-side <c>KeystonePersistence</c> indirectly by exercising
  /// the same Core seams it goes through.
  ///
  /// <para>The ticker logic (Mod-side <c>RegionScoreTicker</c>) is
  /// inlined here as a simple per-tick loop, since the ticker's whole
  /// job is "for each region, score += dt" -- there's no Mod-only
  /// behaviour worth duplicating in test infrastructure.</para>
  /// </summary>
  [TestClass]
  public class PersistenceIntegrationTests {

    /// <summary>Test-only stand-in for "any per-chunk accumulator
    /// score". Used by the chunk-store round-trip and lifecycle tests
    /// to exercise the persistence path under a non-biome-prefix kind
    /// without depending on any specific real accumulator's semantics.</summary>
    private const string TestChunkKind = "keystone.chunk.test.demo";

    #region Region clock-stamp round-trip

    [TestMethod]
    public void Save_Load_RoundTripsRegionClockStamps() {
      // Arrange — build a world with two disjoint regions stamped at
      // distinct (cycle/day, weather, totalDays) values.
      var clock = new FakeClock();
      var (terrain, surveyor, regions) = Setup(width: 5, height: 1);

      clock.Now = new GameTimestamp(1, 2, 0.5f);
      clock.CurrentWeather = WeatherKind.Drought;
      clock.TotalDaysElapsed = 7.5f;
      terrain.Heights[new TileCoord(0, 0)] = new[] { 5 };
      ResurveyAndIndex(surveyor, regions);

      clock.Now = new GameTimestamp(3, 1, 0.25f);
      clock.CurrentWeather = WeatherKind.Badtide;
      clock.TotalDaysElapsed = 19.25f;
      terrain.Heights[new TileCoord(4, 0)] = new[] { 5 };
      ResurveyAndIndex(surveyor, regions);

      Assert.AreEqual(2, regions.Count);
      var idA = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;
      var idB = regions.Containing(new SurfaceCoord(4, 0, 5))!.Id;
      var stampA = SnapshotRecord(regions.Get(idA)!);
      var stampB = SnapshotRecord(regions.Get(idB)!);

      // Act — encode current state.
      var snapshot = new KeystoneSnapshot();
      foreach (var r in regions.All) snapshot.Regions.Add(SnapshotRecord(r));
      var payload = SnapshotCodec.Encode(snapshot);

      // Build a fresh world with the same terrain (deterministic ids
      // guarantee idA / idB rebind to the same regions) and decode.
      var (_, _, regions2) = Setup(width: 5, height: 1, terrainCopy: CopyHeights(terrain), clock: clock);
      regions2.Index();

      var decoded = SnapshotCodec.Decode(payload);
      var byId = new Dictionary<RegionId, RegionPersistedRecord>();
      foreach (var record in decoded.Regions) byId[record.Id] = record;
      regions2.RestoreCreatedAt(byId);

      // Assert — every region's stamp matches the pre-save value.
      var rA = regions2.Get(idA)!;
      var rB = regions2.Get(idB)!;
      AssertStampEquals(stampA, rA);
      AssertStampEquals(stampB, rB);
    }

    #endregion

    #region Score round-trip

    [TestMethod]
    public void Save_Load_RoundTripsScores() {
      // Arrange
      var store = new RegionValueStore();
      store.Set(new RegionId(0), KnownValueKinds.RegionAgeDays, 4.0f);
      store.Set(new RegionId(1), KnownValueKinds.RegionAgeDays, 12.5f);
      store.Set(new RegionId(1), "folktails.regrowth", 0.6f);

      var snapshot = new KeystoneSnapshot();
      foreach (var kv in store.Entries) snapshot.RegionValues[kv.Key] = kv.Value;

      // Act — encode then decode into a fresh store.
      var payload = SnapshotCodec.Encode(snapshot);
      var decoded = SnapshotCodec.Decode(payload);
      var fresh = new RegionValueStore();
      fresh.RehydrateFrom(decoded.RegionValues);

      // Assert
      Assert.AreEqual(3, fresh.Count);
      Assert.AreEqual(4.0f, fresh.Get(new RegionId(0), KnownValueKinds.RegionAgeDays));
      Assert.AreEqual(12.5f, fresh.Get(new RegionId(1), KnownValueKinds.RegionAgeDays));
      Assert.AreEqual(0.6f, fresh.Get(new RegionId(1), "folktails.regrowth"));
    }

    #endregion

    #region Accumulator-vs-derivation

    [TestMethod]
    public void Save_Load_AfterClockAdvances_ScorePicksUpFromPersistedValue() {
      // Arrange — region exists, age accumulator already at 5.0.
      var clock = new FakeClock { TotalDaysElapsed = 10f };
      var (terrain, surveyor, regions) = Setup(width: 1, height: 1);
      terrain.Heights[new TileCoord(0, 0)] = new[] { 5 };
      ResurveyAndIndex(surveyor, regions);
      var regionId = regions.All.GetEnumerator().MoveNext()
          ? new RegionId(0)
          : throw new System.InvalidOperationException("no region");
      // Use the actual id rather than guess.
      foreach (var r in regions.All) regionId = r.Id;

      var store = new RegionValueStore();
      store.Set(regionId, KnownValueKinds.RegionAgeDays, 5.0f);

      // Save
      var snapshot = new KeystoneSnapshot();
      foreach (var r in regions.All) snapshot.Regions.Add(SnapshotRecord(r));
      foreach (var kv in store.Entries) snapshot.RegionValues[kv.Key] = kv.Value;
      var payload = SnapshotCodec.Encode(snapshot);

      // Reload into a fresh world; same terrain so deterministic ids
      // reattach the persisted score to the correct region.
      var (_, _, regions2) = Setup(width: 1, height: 1, terrainCopy: CopyHeights(terrain), clock: clock);
      regions2.Index();
      var decoded = SnapshotCodec.Decode(payload);
      var byId = new Dictionary<RegionId, RegionPersistedRecord>();
      foreach (var record in decoded.Regions) byId[record.Id] = record;
      regions2.RestoreCreatedAt(byId);
      var store2 = new RegionValueStore();
      store2.RehydrateFrom(decoded.RegionValues);

      Assert.AreEqual(5.0f, store2.Get(regionId, KnownValueKinds.RegionAgeDays),
          "rehydrated score must match the persisted value before any further ticking");

      // Act — advance the clock by 3 days, run the (inline) ticker pass.
      clock.TotalDaysElapsed = 13f;
      RunTickerOnce(regions2, store2, dtDays: 3f);

      // Assert — 5.0 (persisted) + 3.0 (this tick) = 8.0.
      Assert.AreEqual(8.0f, store2.Get(regionId, KnownValueKinds.RegionAgeDays),
          "ticker must accumulate from the persisted base, not derive from now - createdAt");
    }

    #endregion

    #region Chunk-score round-trip

    [TestMethod]
    public void Save_Load_RoundTripsChunkScores() {
      // Arrange — chunk store carries entries for two regions across
      // multiple chunks; encode-then-decode must put each entry back.
      var store = new ChunkValueStore();
      store.Set(new RegionId(0), 0, 0, TestChunkKind, 4.0f);
      store.Set(new RegionId(1), 0, 0, TestChunkKind, 12.5f);
      store.Set(new RegionId(1), 2, 3, TestChunkKind, 7.0f);
      store.Set(new RegionId(1), 0, 0, "folktails.chunk.regrowth", 0.6f);

      var snapshot = new KeystoneSnapshot();
      foreach (var kv in store.SortedSnapshot()) snapshot.ChunkValues[kv.Key] = kv.Value;

      // Act — encode then decode into a fresh store.
      var payload = SnapshotCodec.Encode(snapshot);
      var decoded = SnapshotCodec.Decode(payload);
      var fresh = new ChunkValueStore();
      fresh.RehydrateFrom(decoded.ChunkValues);

      // Assert
      Assert.AreEqual(4, fresh.Count);
      Assert.AreEqual(4.0f, fresh.Get(new RegionId(0), 0, 0, TestChunkKind));
      Assert.AreEqual(12.5f, fresh.Get(new RegionId(1), 0, 0, TestChunkKind));
      Assert.AreEqual(7.0f, fresh.Get(new RegionId(1), 2, 3, TestChunkKind));
      Assert.AreEqual(0.6f, fresh.Get(new RegionId(1), 0, 0, "folktails.chunk.regrowth"));
    }

    [TestMethod]
    public void Save_Load_AfterClockAdvances_ChunkAccumulatorPicksUpFromPersistedValue() {
      // Arrange — region exists, chunk-moisture-time accumulator at 5.0
      // for chunk (0, 0) of the (single) region.
      var clock = new FakeClock { TotalDaysElapsed = 10f };
      var (terrain, surveyor, regions) = Setup(width: 1, height: 1);
      terrain.Heights[new TileCoord(0, 0)] = new[] { 5 };
      ResurveyAndIndex(surveyor, regions);
      var regionId = new RegionId(0);
      foreach (var r in regions.All) regionId = r.Id;

      var store = new ChunkValueStore();
      store.Set(regionId, 0, 0, TestChunkKind, 5.0f);

      // Save
      var snapshot = new KeystoneSnapshot();
      foreach (var r in regions.All) snapshot.Regions.Add(SnapshotRecord(r));
      foreach (var kv in store.SortedSnapshot()) snapshot.ChunkValues[kv.Key] = kv.Value;
      var payload = SnapshotCodec.Encode(snapshot);

      // Reload into a fresh world; same terrain so deterministic ids
      // reattach the persisted score to the correct region.
      var (_, _, regions2) = Setup(width: 1, height: 1, terrainCopy: CopyHeights(terrain), clock: clock);
      regions2.Index();
      var decoded = SnapshotCodec.Decode(payload);
      var byId = new Dictionary<RegionId, RegionPersistedRecord>();
      foreach (var record in decoded.Regions) byId[record.Id] = record;
      regions2.RestoreCreatedAt(byId);
      var store2 = new ChunkValueStore();
      store2.RehydrateFrom(decoded.ChunkValues);

      Assert.AreEqual(5.0f, store2.Get(regionId, 0, 0, TestChunkKind),
          "rehydrated chunk score must match the persisted value before any further ticking");

      // Act — advance the clock by 3 days, run the (inline) ticker pass.
      clock.TotalDaysElapsed = 13f;
      RunChunkTickerOnce(regions2, store2, dtDays: 3f);

      // Assert — 5.0 (persisted) + 3.0 (this tick) = 8.0.
      Assert.AreEqual(8.0f, store2.Get(regionId, 0, 0, TestChunkKind),
          "ticker must accumulate from the persisted base, not derive from now - createdAt");
    }

    #endregion

    #region Representative-surface recovery

    // The pure RegionService property test
    // (`ComputeRepresentativeSurfaces_PicksMinSortedMemberPerRegion`)
    // lives in RegionServiceDeterminismTests now — it doesn't need
    // the codec scaffolding that this file otherwise wires up.

    [TestMethod]
    public void SnapshotCodec_RoundTripsRepresentativeSurface() {
      // Encode/decode a region with a non-sentinel representative,
      // verify it survives the round trip.
      var snapshot = new KeystoneSnapshot();
      var rep = new SurfaceCoord(7, 3, 5);
      snapshot.Regions.Add(new RegionPersistedRecord(
          new RegionId(42),
          new GameTimestamp(0, 0, 0f),
          WeatherKind.Temperate,
          TotalDaysAtCreation: 0f,
          Representative: rep));

      var payload = SnapshotCodec.Encode(snapshot);
      var decoded = SnapshotCodec.Decode(payload);

      Assert.AreEqual(1, decoded.Regions.Count);
      Assert.AreEqual(rep, decoded.Regions[0].Representative);
      Assert.IsTrue(decoded.Regions[0].HasRepresentative);
    }

    [TestMethod]
    public void SnapshotCodec_V1Save_DecodesWithoutRepresentative() {
      // Simulate a v1 save: the codec receives a payload whose
      // representative lists are empty. Decoded records should
      // carry the NoRepresentative sentinel and HasRepresentative
      // should return false so the load layer falls through to
      // ID-only matching for them.
      var v1Payload = new SnapshotPayload(
          SchemaVersion: 1,
          RegionIds: new[] { 11 },
          RegionTotalDaysAtCreation: new[] { 1.5f },
          RegionCreatedCycle: new[] { 0 },
          RegionCreatedCycleDay: new[] { 1 },
          RegionCreatedPartialCycleDay: new[] { 0.5f },
          RegionWeather: new[] { (int)WeatherKind.Temperate },
          RegionRepresentativeX: System.Array.Empty<int>(),
          RegionRepresentativeY: System.Array.Empty<int>(),
          RegionRepresentativeZ: System.Array.Empty<int>(),
          RegionValueIds: System.Array.Empty<int>(),
          RegionValueKinds: System.Array.Empty<string>(),
          RegionValueFloats: System.Array.Empty<float>(),
          ChunkValueRegionIds: System.Array.Empty<int>(),
          ChunkValueChunkXs: System.Array.Empty<int>(),
          ChunkValueChunkYs: System.Array.Empty<int>(),
          ChunkValueKinds: System.Array.Empty<string>(),
          ChunkValueFloats: System.Array.Empty<float>());

      var decoded = SnapshotCodec.Decode(v1Payload);

      Assert.AreEqual(1, decoded.Regions.Count);
      Assert.IsFalse(decoded.Regions[0].HasRepresentative,
          "v1 decode populates Representative with the NoRepresentative sentinel");
      Assert.AreEqual(RegionPersistedRecord.NoRepresentative,
          decoded.Regions[0].Representative);
    }

    #endregion

    #region Canonical-ID round trip

    // The pure-RegionService canonical-id property test
    // (`ComputeCanonicalIdMap_AfterProcessChanges_RemapsToIndexOutput`)
    // lives in RegionServiceDeterminismTests now. The end-to-end save/
    // load round-trip below still belongs here.

    [TestMethod]
    public void Save_Load_RoundTripsChunkValuesAcrossIdRenumbering() {
      // The end-to-end value of canonical-id saving: a chunk value
      // stashed under a ProcessChanges-allocated id survives a save→
      // reload cycle and reattaches to the same physical region (rather
      // than being misattributed or pruned).
      var (terrain, surveyor, regions) = Setup(
          width: 3, height: 1,
          terrainCopy: new Dictionary<TileCoord, int[]> {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
          });
      regions.Index();

      terrain.Heights[new TileCoord(0, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      var liveA = regions.Containing(new SurfaceCoord(0, 0, 4))!.Id;
      var liveBC = regions.Containing(new SurfaceCoord(1, 0, 5))!.Id;

      // Stash distinguishable values under each live region.
      var chunkStore = new ChunkValueStore();
      const float valueA = 4.0f;
      const float valueBC = 9.5f;
      chunkStore.Set(liveA, 0, 0, TestChunkKind, valueA);
      chunkStore.Set(liveBC, 0, 0, TestChunkKind, valueBC);

      // Build the snapshot the way KeystonePersistence.Save does:
      // walk the live state, translate every RegionId through the
      // canonical map, and write under canonical IDs.
      var canonical = regions.ComputeCanonicalIdMap();
      var snapshot = new KeystoneSnapshot();
      foreach (var r in regions.All) {
        snapshot.Regions.Add(new RegionPersistedRecord(
            canonical[r.Id], r.CreatedAt, r.WeatherAtCreation, r.TotalDaysAtCreation,
            RegionPersistedRecord.NoRepresentative));
      }
      foreach (var kv in chunkStore.SortedSnapshot()) {
        snapshot.ChunkValues[new ChunkValueKey(
            canonical[kv.Key.RegionId], kv.Key.ChunkX, kv.Key.ChunkY, kv.Key.Kind)] = kv.Value;
      }
      var payload = SnapshotCodec.Encode(snapshot);

      // Reload into a fresh world with the SAME final terrain.
      var (_, _, regions2) = Setup(width: 3, height: 1, terrainCopy: CopyHeights(terrain));
      regions2.Index();
      var rehydratedA = regions2.Containing(new SurfaceCoord(0, 0, 4))!.Id;
      var rehydratedBC = regions2.Containing(new SurfaceCoord(1, 0, 5))!.Id;
      Assert.AreEqual(new RegionId(0), rehydratedA, "fresh Index() puts A first");
      Assert.AreEqual(new RegionId(1), rehydratedBC, "fresh Index() puts BC second");

      var decoded = SnapshotCodec.Decode(payload);
      var store2 = new ChunkValueStore();
      store2.RehydrateFrom(decoded.ChunkValues);

      // No pruning happens here because the rehydrated IDs match the
      // canonical IDs written by the save side. Verify both values
      // reattached to the physically correct region.
      var liveIds2 = new HashSet<RegionId>();
      foreach (var r in regions2.All) liveIds2.Add(r.Id);
      var pruned = store2.PruneToLiveRegions(liveIds2);
      Assert.AreEqual(0, pruned, "no chunk values should be pruned: every saved ID matches a live ID");

      Assert.AreEqual(valueA, store2.Get(rehydratedA, 0, 0, TestChunkKind),
          "the chunk value originally on A's live id reattaches to A's physical region");
      Assert.AreEqual(valueBC, store2.Get(rehydratedBC, 0, 0, TestChunkKind),
          "the chunk value originally on BC's live id reattaches to BC's physical region");
    }

    [TestMethod]
    public void Save_WithoutCanonicalIds_MisattributesChunkValuesOnReload() {
      // Demonstrates the bug that the canonical-id rewrite fixes:
      // saving live IDs directly (the pre-fix behaviour) leaves chunk
      // values pointing at the wrong physical region after reload.
      // This test pins the failure mode so a future regression can be
      // caught.
      var (terrain, surveyor, regions) = Setup(
          width: 3, height: 1,
          terrainCopy: new Dictionary<TileCoord, int[]> {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
          });
      regions.Index();

      terrain.Heights[new TileCoord(0, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(0, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      var liveA = regions.Containing(new SurfaceCoord(0, 0, 4))!.Id;
      var liveBC = regions.Containing(new SurfaceCoord(1, 0, 5))!.Id;
      var chunkStore = new ChunkValueStore();
      const float valueA = 4.0f;
      const float valueBC = 9.5f;
      chunkStore.Set(liveA, 0, 0, TestChunkKind, valueA);
      chunkStore.Set(liveBC, 0, 0, TestChunkKind, valueBC);

      // Build snapshot under LIVE ids (no canonical translation).
      var snapshot = new KeystoneSnapshot();
      foreach (var r in regions.All) snapshot.Regions.Add(SnapshotRecord(r));
      foreach (var kv in chunkStore.SortedSnapshot()) snapshot.ChunkValues[kv.Key] = kv.Value;
      var payload = SnapshotCodec.Encode(snapshot);

      // Reload.
      var (_, _, regions2) = Setup(width: 3, height: 1, terrainCopy: CopyHeights(terrain));
      regions2.Index();
      var rehydratedA = regions2.Containing(new SurfaceCoord(0, 0, 4))!.Id;
      var rehydratedBC = regions2.Containing(new SurfaceCoord(1, 0, 5))!.Id;

      var decoded = SnapshotCodec.Decode(payload);
      var store2 = new ChunkValueStore();
      store2.RehydrateFrom(decoded.ChunkValues);

      // Without canonicalization, the saved ids are the swapped live
      // ids: A's value (under saved id 1) lands on the region with
      // rehydrated id 1, which is BC. BC's value (under saved id 0)
      // lands on A. The values are misattributed.
      Assert.AreEqual(valueBC, store2.Get(rehydratedA, 0, 0, TestChunkKind),
          "without canonicalization, BC's value misattributes to A");
      Assert.AreEqual(valueA, store2.Get(rehydratedBC, 0, 0, TestChunkKind),
          "without canonicalization, A's value misattributes to BC");
    }

    #endregion

    #region Region split inherits scores

    [TestMethod]
    public void ChunkValueStoreInherit_WiredToRegionSplit_OrphanInheritsParentEntry() {
      // Pins the public ChunkValueStore.Inherit store API (external-mod
      // surface) behaving correctly when wired to a region split. This is
      // NOT how Mod 1 handles chunk data — production re-binds chunk data
      // spatially via ChunkReconciler (a footprint re-home after the
      // topology flush; see ChunkReconcilerTests), and the chunk-store
      // Inherit has no Mod 1 caller. Kept because the method is public API.
      // Arrange — same 3x1 plateau as the region-split test, but with a
      // chunk-level entry on the parent. Chunk (0, 0) is global because
      // OriginX = 0 for this terrain (the surveyor's chunk-aligned bbox
      // anchors at 0 for plateaus that include x=0).
      var clock = new FakeClock { TotalDaysElapsed = 5f };
      var (terrain, surveyor, regions) = Setup(
          width: 3, height: 1,
          terrainCopy: new Dictionary<TileCoord, int[]> {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
          },
          clock: clock);
      regions.Index();
      var parentId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;

      var chunkStore = new ChunkValueStore();
      chunkStore.Set(parentId, 0, 0, TestChunkKind, 4.0f);

      // Wire the store's Inherit to RegionSplit (as an external mod managing
      // its own region-keyed chunk values might).
      regions.RegionSplit += (parent, orphan) => chunkStore.Inherit(parent, orphan);

      // Act — drop the middle column to z=4, bisecting the plateau.
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — two z=5 regions; the kept-id piece keeps the parent's
      // entry (untouched), the orphan piece has a freshly-copied entry.
      var atFive = regions.All.Where(r => r.Z == 5).ToList();
      Assert.AreEqual(2, atFive.Count);
      var orphan = atFive.Single(r => r.Id != parentId);

      Assert.AreEqual(4.0f, chunkStore.Get(parentId, 0, 0, TestChunkKind),
          "kept-id piece's chunk score is untouched");
      Assert.AreEqual(4.0f, chunkStore.Get(orphan.Id, 0, 0, TestChunkKind),
          "orphan inherits parent's chunk-moisture-time entry");
    }

    [TestMethod]
    public void RegionSplit_OrphanPiece_InheritsParentScores() {
      // Arrange — linear 3x1 plateau, all in one region, with an
      // accumulated age score on it.
      var clock = new FakeClock { TotalDaysElapsed = 5f };
      var (terrain, surveyor, regions) = Setup(
          width: 3, height: 1,
          terrainCopy: new Dictionary<TileCoord, int[]> {
              [new TileCoord(0, 0)] = new[] { 5 },
              [new TileCoord(1, 0)] = new[] { 5 },
              [new TileCoord(2, 0)] = new[] { 5 },
          },
          clock: clock);
      regions.Index();
      var parentId = regions.Containing(new SurfaceCoord(0, 0, 5))!.Id;

      var store = new RegionValueStore();
      store.Set(parentId, KnownValueKinds.RegionAgeDays, 4.0f);
      store.Set(parentId, "keystone.region.forestScore", 0.6f);

      // Wire the same lifecycle handler the Mod-side does in production.
      regions.RegionSplit += (parent, orphan) => store.Inherit(parent, orphan);

      // Act — drop the middle column to z=4, bisecting the plateau.
      terrain.Heights[new TileCoord(1, 0)] = new[] { 4 };
      var diff = surveyor.ResurveyColumn(new TileCoord(1, 0));
      regions.ProcessChanges(diff.Detached, diff.Attached);

      // Assert — two z=5 regions; the kept-id piece keeps the parent's
      // entries (untouched), the orphan piece has freshly-copied entries
      // for every kind.
      var atFive = regions.All.Where(r => r.Z == 5).ToList();
      Assert.AreEqual(2, atFive.Count);
      var orphan = atFive.Single(r => r.Id != parentId);

      Assert.AreEqual(4.0f, store.Get(parentId, KnownValueKinds.RegionAgeDays),
          "kept-id piece's score is untouched");
      Assert.AreEqual(4.0f, store.Get(orphan.Id, KnownValueKinds.RegionAgeDays),
          "orphan inherits parent's age score; the land didn't suddenly become younger");
      Assert.AreEqual(0.6f, store.Get(orphan.Id, "keystone.region.forestScore"),
          "orphan inherits ALL of parent's score kinds, not just age");
    }

    #endregion

    #region Helpers

    private static RegionPersistedRecord SnapshotRecord(Region r) =>
        new(r.Id, r.CreatedAt, r.WeatherAtCreation, r.TotalDaysAtCreation,
            RegionPersistedRecord.NoRepresentative);

    private static void AssertStampEquals(RegionPersistedRecord expected, Region actual) {
      Assert.AreEqual(expected.CreatedAt, actual.CreatedAt);
      Assert.AreEqual(expected.WeatherAtCreation, actual.WeatherAtCreation);
      Assert.AreEqual(expected.TotalDaysAtCreation, actual.TotalDaysAtCreation);
    }

    /// <summary>
    /// Inline equivalent of <c>RegionScoreTicker.Tick</c>: increment
    /// <see cref="KnownValueKinds.RegionAgeDays"/> by <paramref name="dtDays"/>
    /// for every region in <paramref name="regions"/>.
    /// </summary>
    private static void RunTickerOnce(RegionService regions, RegionValueStore scores, float dtDays) {
      if (dtDays <= 0f) return;
      foreach (var r in regions.All) {
        var existing = scores.Get(r.Id, KnownValueKinds.RegionAgeDays) ?? 0f;
        scores.Set(r.Id, KnownValueKinds.RegionAgeDays, existing + dtDays);
      }
    }

    /// <summary>
    /// Generic per-chunk-accumulator stand-in: increment
    /// <see cref="TestChunkKind"/> by <paramref name="dtDays"/>
    /// for each region's chunk (0, 0). Doesn't sample the field
    /// (there's no real ecology field in the Core test setup) -- the
    /// test-relevant invariant is that the accumulator picks up from
    /// the persisted base, which only needs the dt-add behaviour.
    /// </summary>
    private static void RunChunkTickerOnce(RegionService regions, ChunkValueStore chunkScores, float dtDays) {
      if (dtDays <= 0f) return;
      foreach (var r in regions.All) {
        var existing = chunkScores.Get(r.Id, 0, 0, TestChunkKind) ?? 0f;
        chunkScores.Set(r.Id, 0, 0, TestChunkKind, existing + dtDays);
      }
    }

    private static (FakeTerrain terrain, TerrainSurveyor surveyor, RegionService regions) Setup(
        int width, int height, Dictionary<TileCoord, int[]>? terrainCopy = null, FakeClock? clock = null) {
      var terrain = new FakeTerrain(width, height);
      if (terrainCopy != null) {
        foreach (var kv in terrainCopy) terrain.Heights[kv.Key] = kv.Value;
      }
      var surveyor = new TerrainSurveyor(terrain, FakeBuilding.NothingBuilt(), FakeBlocking.NothingBlocked());
      surveyor.Survey();
      var regions = new RegionService(surveyor, clock ?? new FakeClock());
      return (terrain, surveyor, regions);
    }

    private static Dictionary<TileCoord, int[]> CopyHeights(FakeTerrain terrain) {
      var copy = new Dictionary<TileCoord, int[]>();
      foreach (var kv in terrain.Heights) copy[kv.Key] = (int[])kv.Value.Clone();
      return copy;
    }

    /// <summary>Resurvey a freshly-mutated terrain and Index regions from scratch -- simulates a "post-edit" pass for tests.</summary>
    private static void ResurveyAndIndex(TerrainSurveyor surveyor, RegionService regions) {
      surveyor.Survey();
      regions.Index();
    }

    #endregion

    #region Fakes

    private sealed class FakeClock : IClock {
      public GameTimestamp Now { get; set; } = GameTimestamp.Origin;
      public WeatherKind CurrentWeather { get; set; } = WeatherKind.Temperate;
      public float TotalDaysElapsed { get; set; }
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

    #endregion

  }

}
