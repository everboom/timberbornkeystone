using System.Collections.Generic;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Pins <see cref="ChunkReconciler"/>'s contract: chunk data follows the
  /// region that physically owns its <c>(X, Y, Z)</c> footprint, re-homing
  /// across region-id churn, dropping only the genuinely homeless, and
  /// resolving collisions High-beats-Low. The owning-region lookup is faked
  /// so these stay pure-Core (no surveyor / region graph).
  /// </summary>
  [TestClass]
  public sealed class ChunkReconcilerTests {

    #region Fixture

    private const string MaturityKind = "keystone.chunk.maturity.forest";
    private const string SuitabilityKind = "keystone.chunk.suitability.forest";

    /// <summary>Hand-rolled owner map keyed by (chunkX, chunkY, z).
    /// Z-strict by construction — an unmapped (x, y, z) returns null,
    /// modelling "no region owns this footprint at this Z."</summary>
    private sealed class FakeOwners : IChunkOwnerQuery {
      private readonly Dictionary<(int, int, int), RegionId> _majority = new();
      private readonly Dictionary<(int, int, int), HashSet<RegionId>> _present = new();

      /// <summary>Mark <paramref name="region"/> the majority owner of the
      /// chunk (and present in it).</summary>
      public FakeOwners Own(int chunkX, int chunkY, int z, RegionId region) {
        _majority[(chunkX, chunkY, z)] = region;
        AddPresent(chunkX, chunkY, z, region);
        return this;
      }

      /// <summary>Mark <paramref name="region"/> present in the chunk
      /// without changing the majority — a minority co-owner of a
      /// boundary-straddling chunk.</summary>
      public FakeOwners AlsoPresent(int chunkX, int chunkY, int z, RegionId region) {
        AddPresent(chunkX, chunkY, z, region);
        return this;
      }

      private void AddPresent(int chunkX, int chunkY, int z, RegionId region) {
        if (!_present.TryGetValue((chunkX, chunkY, z), out var set)) {
          set = new HashSet<RegionId>();
          _present[(chunkX, chunkY, z)] = set;
        }
        set.Add(region);
      }

      public RegionId? OwnerOfChunk(int chunkX, int chunkY, int z) {
        return _majority.TryGetValue((chunkX, chunkY, z), out var r) ? r : (RegionId?)null;
      }

      public bool RegionOwnsChunk(RegionId region, int chunkX, int chunkY, int z) {
        return _present.TryGetValue((chunkX, chunkY, z), out var set) && set.Contains(region);
      }
    }

    private static ChunkDataStore NewDataStore() {
      var registry = new ChunkValueRegistry();
      registry.Register(MaturityKind, ChunkValueRole.Maturity);
      registry.Register(SuitabilityKind, ChunkValueRole.Suitability);
      registry.Freeze();
      return new ChunkDataStore(registry);
    }

    /// <summary>Seed a chunk in both stores: a data-store record with its
    /// Z stamped and slot 0 set to <paramref name="maturity"/>, plus a
    /// parallel value-store Maturity entry.</summary>
    private static void Seed(
        ChunkDataStore data, ChunkValueStore values,
        RegionId region, int cx, int cy, int z, float maturity) {
      var d = data.GetOrCreate(new ChunkCoord(region, cx, cy));
      d.Z = z;
      d.Set(0, maturity);
      values.Set(region, cx, cy, MaturityKind, maturity);
    }

    /// <summary>Build a bare result for Outcome-classification tests
    /// (counts only; no store needed).</summary>
    private static ChunkReconcileResult Result(int rehomed, int dropped, int withMaturity) =>
        new ChunkReconcileResult(
            Scanned: rehomed + dropped, Kept: 0, Rehomed: rehomed,
            HomelessDropped: dropped, HomelessDroppedWithMaturity: withMaturity,
            CollisionsResolved: 0);

    #endregion

    #region Kept

    [TestMethod]
    public void Reconcile_OwnerMatchesKeyedRegion_KeptAndUnchanged() {
      // Arrange
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 12f);
      var owners = new FakeOwners().Own(2, 3, 5, new RegionId(5));
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, result.Kept);
      Assert.AreEqual(0, result.Rehomed);
      Assert.AreEqual(0, result.HomelessDropped);
      Assert.IsFalse(result.AnyChange);
      Assert.IsNotNull(data.Get(new RegionId(5), 2, 3));
      Assert.AreEqual(12f, values.Get(new RegionId(5), 2, 3, MaturityKind));
    }

    [TestMethod]
    public void Reconcile_KeyedRegionIsMinorityCoOwner_Kept_NotRehomed() {
      // The Option-B fix: a chunk whose keyed region still owns surfaces in
      // the footprint — here as a MINORITY co-owner of a boundary-straddling
      // chunk — must be KEPT, not collapsed onto the majority. Otherwise the
      // biome ticker's per-region copy churns on every sweep (the symptom the
      // in-game self-test surfaced: re-homed == collisions every run).
      // Arrange — majority is R7; R5 is a present minority co-owner; the
      // chunk is keyed under R5.
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 8f);
      var owners = new FakeOwners()
          .Own(2, 3, 5, new RegionId(7))         // majority R7
          .AlsoPresent(2, 3, 5, new RegionId(5)); // R5 present as minority co-owner

      // Act
      var result = new ChunkReconciler(data, values, owners).ReconcileFromDataStore();

      // Assert — R5 still owns the footprint, so its copy stays put.
      Assert.AreEqual(1, result.Kept, "a present minority co-owner is kept");
      Assert.AreEqual(0, result.Rehomed);
      Assert.IsNotNull(data.Get(new RegionId(5), 2, 3));
      Assert.AreEqual(8f, data.Get(new RegionId(5), 2, 3)!.Get(0));
    }

    #endregion

    #region Dry run

    [TestMethod]
    public void Reconcile_DryRun_CountsButDoesNotMutate() {
      // dryRun reports what a sweep WOULD do without re-keying or dropping —
      // so the manual self-test can observe without churning game state.
      // R5 is stranded (only R7 owns the footprint) so a real sweep would
      // re-home it; the dry run only counts.
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 9f);
      var owners = new FakeOwners().Own(2, 3, 5, new RegionId(7));

      // Act
      var result = new ChunkReconciler(data, values, owners)
          .ReconcileFromDataStore(dryRun: true);

      // Assert
      Assert.AreEqual(1, result.Rehomed, "counts the would-be re-home");
      Assert.IsNotNull(data.Get(new RegionId(5), 2, 3), "dry run leaves the keyed entry in place");
      Assert.IsNull(data.Get(new RegionId(7), 2, 3), "dry run performed no re-key");
    }

    #endregion

    #region Rehome

    [TestMethod]
    public void Reconcile_OwnerDiffers_RehomesBothStoresAndCarriesZ() {
      // Arrange: chunk keyed under a now-stale region R5; the footprint at
      // Z=5 is actually owned by R7 now.
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 12f);
      values.Set(new RegionId(5), 2, 3, SuitabilityKind, 0.5f);
      var owners = new FakeOwners().Own(2, 3, 5, new RegionId(7));
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, result.Rehomed);
      Assert.AreEqual(0, result.HomelessDropped);
      Assert.AreEqual(0, result.CollisionsResolved);

      Assert.IsNull(data.Get(new RegionId(5), 2, 3), "old region key should be vacated");
      var moved = data.Get(new RegionId(7), 2, 3);
      Assert.IsNotNull(moved);
      Assert.AreEqual(12f, moved!.Get(0));
      Assert.AreEqual(5, moved.Z, "carried Z must survive the re-home");

      Assert.IsNull(values.Get(new RegionId(5), 2, 3, MaturityKind));
      Assert.AreEqual(12f, values.Get(new RegionId(7), 2, 3, MaturityKind));
      Assert.AreEqual(0.5f, values.Get(new RegionId(7), 2, 3, SuitabilityKind),
          "every Kind for the footprint moves together");
    }

    #endregion

    #region Homeless

    [TestMethod]
    public void Reconcile_NoOwnerAtZ_DropsFromBothStores() {
      // Arrange: nobody owns (2,3) at Z=5.
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 12f);
      var owners = new FakeOwners(); // empty -> always null
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, result.HomelessDropped);
      Assert.AreEqual(1, result.HomelessDroppedWithMaturity,
          "the dropped chunk held maturity (slot 0), so it counts as a real loss");
      Assert.AreEqual(0, result.HomelessDroppedEmpty);
      Assert.AreEqual(0, result.Rehomed);
      Assert.IsNull(data.Get(new RegionId(5), 2, 3));
      Assert.IsNull(values.Get(new RegionId(5), 2, 3, MaturityKind));
    }

    [TestMethod]
    public void Reconcile_HomelessChunkWithoutMaturity_CountedEmptyNotRealLoss() {
      // A valid chunk that carries only Suitability (no accumulated
      // Maturity) and loses its region is benign churn, not a maturity loss.
      // Arrange
      var data = NewDataStore();
      var values = new ChunkValueStore();
      var d = data.GetOrCreate(new ChunkCoord(new RegionId(5), 2, 3));
      d.Z = 5;
      d.Set(1, 0.7f); // slot 1 == SuitabilityKind; slot 0 (maturity) stays 0
      values.Set(new RegionId(5), 2, 3, SuitabilityKind, 0.7f);
      var owners = new FakeOwners(); // homeless
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, result.HomelessDropped);
      Assert.AreEqual(0, result.HomelessDroppedWithMaturity,
          "no maturity slot was set, so this is not a real ecology loss");
      Assert.AreEqual(1, result.HomelessDroppedEmpty);
      Assert.IsNull(data.Get(new RegionId(5), 2, 3));
    }

    #endregion

    #region Collision (High beats Low)

    [TestMethod]
    public void Reconcile_CollisionIncomingHigher_IncomingWins() {
      // Arrange: R7 already owns its own chunk at (2,3) with maturity 3;
      // R5's stale chunk at the same footprint (maturity 10) re-homes onto
      // R7. High beats Low -> 10 survives.
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(7), cx: 2, cy: 3, z: 5, maturity: 3f);
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 10f);
      var owners = new FakeOwners().Own(2, 3, 5, new RegionId(7));
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, result.Kept, "R7's own chunk is already correctly bound");
      Assert.AreEqual(1, result.Rehomed);
      Assert.AreEqual(1, result.CollisionsResolved);
      Assert.AreEqual(10f, data.Get(new RegionId(7), 2, 3)!.Get(0));
      Assert.AreEqual(10f, values.Get(new RegionId(7), 2, 3, MaturityKind));
      Assert.IsNull(data.Get(new RegionId(5), 2, 3));
    }

    [TestMethod]
    public void Reconcile_CollisionIncumbentHigher_IncumbentStays() {
      // Arrange: incumbent R7 (maturity 10) outranks the incoming R5 (3).
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(7), cx: 2, cy: 3, z: 5, maturity: 10f);
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 3f);
      var owners = new FakeOwners().Own(2, 3, 5, new RegionId(7));
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, result.CollisionsResolved);
      Assert.AreEqual(10f, data.Get(new RegionId(7), 2, 3)!.Get(0));
      Assert.AreEqual(10f, values.Get(new RegionId(7), 2, 3, MaturityKind));
    }

    [TestMethod]
    public void Reconcile_Collision_ValueStoreResolvesPerKind_IndependentOfDataStoreRecord() {
      // The two stores use different collision metrics — the data store
      // keeps the whole record with the larger single slot (MaxSlot, over
      // all slots incl. Suitability), the value store keeps the larger
      // value per Kind. This pins that they decide independently: a case
      // where the data store keeps the incumbent record but the value
      // store's Maturity Kind takes the incoming value.
      //
      // slot 0 = MaturityKind, slot 1 = SuitabilityKind (NewDataStore order).
      // Incumbent R7: maturity 2, suitability 9  -> MaxSlot 9
      // Incoming  R5: maturity 5, suitability 1  -> MaxSlot 5
      // Data store: 9 > 5, so R7's record wins -> its maturity slot stays 2.
      // Value store, per Kind: maturity max(2,5)=5, suitability max(9,1)=9.
      // Arrange
      var data = NewDataStore();
      var values = new ChunkValueStore();
      var incumbent = data.GetOrCreate(new ChunkCoord(new RegionId(7), 2, 3));
      incumbent.Z = 5;
      incumbent.Set(0, 2f);
      incumbent.Set(1, 9f);
      values.Set(new RegionId(7), 2, 3, MaturityKind, 2f);
      values.Set(new RegionId(7), 2, 3, SuitabilityKind, 9f);
      var incoming = data.GetOrCreate(new ChunkCoord(new RegionId(5), 2, 3));
      incoming.Z = 5;
      incoming.Set(0, 5f);
      incoming.Set(1, 1f);
      values.Set(new RegionId(5), 2, 3, MaturityKind, 5f);
      values.Set(new RegionId(5), 2, 3, SuitabilityKind, 1f);
      var owners = new FakeOwners().Own(2, 3, 5, new RegionId(7));
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert — the stores diverge, proving independent resolution.
      Assert.AreEqual(1, result.CollisionsResolved);
      Assert.AreEqual(2f, data.Get(new RegionId(7), 2, 3)!.Get(0),
          "data store kept the incumbent record (its MaxSlot 9 > incoming 5)");
      Assert.AreEqual(5f, values.Get(new RegionId(7), 2, 3, MaturityKind),
          "value store took the incoming Maturity per-Kind (max 2 vs 5), independent of the data-store record pick");
      Assert.AreEqual(9f, values.Get(new RegionId(7), 2, 3, SuitabilityKind),
          "value store kept the incumbent Suitability per-Kind (max 9 vs 1)");
    }

    #endregion

    #region Z is the lookup key

    [TestMethod]
    public void Reconcile_UsesCarriedZForOwnerLookup_NotSomeOtherZ() {
      // Arrange: chunk carries Z=9. The footprint is owned by R5 at Z=9,
      // but by a DIFFERENT region (R7) at Z=5. A correct lookup keys on
      // Z=9 -> R5 -> Kept. A lookup that ignored Z (or used the wrong one)
      // would resolve to R7 and spuriously re-home.
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 9, maturity: 12f);
      var owners = new FakeOwners()
          .Own(2, 3, 9, new RegionId(5))
          .Own(2, 3, 5, new RegionId(7));
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, result.Kept);
      Assert.AreEqual(0, result.Rehomed);
      Assert.IsNotNull(data.Get(new RegionId(5), 2, 3));
    }

    #endregion

    #region Scope

    [TestMethod]
    public void Reconcile_Scoped_LeavesOutOfScopeChunksUntouched() {
      // Arrange: two chunks that would both re-home; scope to only R5's.
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 12f);
      Seed(data, values, new RegionId(6), cx: 4, cy: 4, z: 6, maturity: 7f);
      var owners = new FakeOwners()
          .Own(2, 3, 5, new RegionId(8))
          .Own(4, 4, 6, new RegionId(9));
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act
      var result = reconciler.ReconcileFromDataStore(
          new HashSet<RegionId> { new RegionId(5) });

      // Assert
      Assert.AreEqual(1, result.Scanned, "only the in-scope chunk is considered");
      Assert.AreEqual(1, result.Rehomed);
      Assert.IsNotNull(data.Get(new RegionId(8), 2, 3), "in-scope chunk re-homed");
      Assert.IsNotNull(data.Get(new RegionId(6), 4, 4), "out-of-scope chunk untouched");
      Assert.IsNull(data.Get(new RegionId(9), 4, 4));
    }

    #endregion

    #region Owner override

    [TestMethod]
    public void Reconcile_OwnerOverride_IsUsedInsteadOfInjectedQuery() {
      // The map-wide self-test path passes a PrecomputedChunkOwnerQuery via
      // ownerOverride; pin that the override is honored over the injected
      // query. The injected query says "homeless" (would drop); the
      // override says R7 owns the footprint (should re-home). If the
      // override were ignored, the chunk would be dropped instead.
      // Arrange
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 12f);
      var injected = new FakeOwners(); // empty -> everything homeless
      var reconciler = new ChunkReconciler(data, values, injected);
      var overrideOwners = new FakeOwners().Own(2, 3, 5, new RegionId(7));

      // Act
      var result = reconciler.ReconcileFromDataStore(
          scope: null, ownerOverride: overrideOwners);

      // Assert
      Assert.AreEqual(1, result.Rehomed, "override said R7 owns it -> re-home, not drop");
      Assert.AreEqual(0, result.HomelessDropped,
          "the injected (homeless) query must not have been consulted");
      Assert.IsNotNull(data.Get(new RegionId(7), 2, 3));
      Assert.IsNull(data.Get(new RegionId(5), 2, 3));
    }

    #endregion

    #region Maturity classification — multi-slot & freeze caching

    [TestMethod]
    public void Reconcile_MaturityInAnyMaturitySlot_CountsAsRealLoss() {
      // The classifier must collect ALL maturity-prefixed slots, not just
      // the first. A chunk with the first maturity slot zero but a later
      // maturity slot non-zero is still a real loss.
      // Arrange
      const string matForest = "keystone.chunk.maturity.forest";
      const string matWetland = "keystone.chunk.maturity.wetland";
      const string suitForest = "keystone.chunk.suitability.forest";
      var registry = new ChunkValueRegistry();
      registry.Register(matForest, ChunkValueRole.Maturity);   // ordinal 0
      var wetlandOrd = registry.Register(matWetland, ChunkValueRole.Maturity);  // ordinal 1
      registry.Register(suitForest, ChunkValueRole.Suitability);  // ordinal 2
      registry.Freeze();
      var data = new ChunkDataStore(registry);
      var values = new ChunkValueStore();
      var d = data.GetOrCreate(new ChunkCoord(new RegionId(5), 2, 3));
      d.Z = 5;
      d.Set(wetlandOrd, 4f); // forest-maturity slot stays 0; wetland > 0
      values.Set(new RegionId(5), 2, 3, matWetland, 4f);
      var reconciler = new ChunkReconciler(data, values, new FakeOwners());

      // Act
      var result = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, result.HomelessDropped);
      Assert.AreEqual(1, result.HomelessDroppedWithMaturity,
          "a later maturity slot must be recognized, not only the first");
    }

    [TestMethod]
    public void Reconcile_MaturityOrdinals_NotCachedUntilRegistryFrozen() {
      // A defensive guard caches the maturity-ordinal set only once the
      // registry is frozen, so a (pre-freeze) classification doesn't pin an
      // incomplete set before late-registering mods add their kinds. Run a
      // homeless classification while UNFROZEN, then register another
      // maturity kind + freeze, and confirm the new slot is recognized.
      // Arrange
      var registry = new ChunkValueRegistry();
      registry.Register("keystone.chunk.maturity.forest", ChunkValueRole.Maturity); // ordinal 0, unfrozen
      var data = new ChunkDataStore(registry);
      var values = new ChunkValueStore();
      var reconciler = new ChunkReconciler(data, values, new FakeOwners());

      // First pass while unfrozen, with a homeless chunk carrying forest
      // maturity — triggers the lazy ordinal computation. Must not cache.
      var d1 = data.GetOrCreate(new ChunkCoord(new RegionId(5), 0, 0));
      d1.Z = 5;
      d1.Set(0, 1f);
      var first = reconciler.ReconcileFromDataStore();
      Assert.AreEqual(1, first.HomelessDroppedWithMaturity);

      // A late-registering mod adds a second maturity kind, then freeze.
      var wetlandOrd = registry.Register("keystone.chunk.maturity.wetland", ChunkValueRole.Maturity); // ordinal 1
      registry.Freeze();

      // New chunk with maturity ONLY in the late-registered slot.
      var d2 = data.GetOrCreate(new ChunkCoord(new RegionId(6), 1, 1));
      d2.Z = 5;
      d2.Set(wetlandOrd, 4f); // ordinal 1; ordinal 0 stays 0

      // Act
      var second = reconciler.ReconcileFromDataStore();

      // Assert
      Assert.AreEqual(1, second.HomelessDroppedWithMaturity,
          "the late-registered maturity slot must be recognized — proves the "
          + "unfrozen first pass did not cache an incomplete ordinal set");
    }

    #endregion

    #region Outcome classification

    [TestMethod]
    public void Outcome_MapsEachCategory() {
      Assert.AreEqual(ChunkReconcileOutcome.Clean,
          Result(rehomed: 0, dropped: 0, withMaturity: 0).Outcome);
      Assert.AreEqual(ChunkReconcileOutcome.EmptyDropsOnly,
          Result(rehomed: 0, dropped: 2, withMaturity: 0).Outcome);
      Assert.AreEqual(ChunkReconcileOutcome.RehomedNoLoss,
          Result(rehomed: 2, dropped: 0, withMaturity: 0).Outcome);
      Assert.AreEqual(ChunkReconcileOutcome.MaturityLost,
          Result(rehomed: 0, dropped: 1, withMaturity: 1).Outcome);
    }

    [TestMethod]
    public void Outcome_Precedence_MaturityLossOverRehome_RehomeOverEmpty() {
      // Maturity loss is the top signal even when re-homes and empty drops
      // also happened.
      Assert.AreEqual(ChunkReconcileOutcome.MaturityLost,
          Result(rehomed: 3, dropped: 2, withMaturity: 1).Outcome);
      // With no maturity lost, a re-home outranks empty drops (matches the
      // self-test's branch order: re-home is the drift signal).
      Assert.AreEqual(ChunkReconcileOutcome.RehomedNoLoss,
          Result(rehomed: 3, dropped: 2, withMaturity: 0).Outcome);
    }

    #endregion

    #region Scope contains a dead region id

    [TestMethod]
    public void Reconcile_ScopeContainsDeadRegionId_StrandedChunkRehomes() {
      // Documented contract: callers pass dead region ids in the scope so a
      // chunk stranded under a now-defunct region still re-homes. R5 is
      // "dead" (not a live owner anywhere); the footprint is now owned by R7.
      // Arrange
      var data = NewDataStore();
      var values = new ChunkValueStore();
      Seed(data, values, new RegionId(5), cx: 2, cy: 3, z: 5, maturity: 9f);
      var owners = new FakeOwners().Own(2, 3, 5, new RegionId(7));
      var reconciler = new ChunkReconciler(data, values, owners);

      // Act — scope names only the dead id.
      var result = reconciler.ReconcileFromDataStore(
          new HashSet<RegionId> { new RegionId(5) });

      // Assert
      Assert.AreEqual(1, result.Rehomed);
      Assert.IsNotNull(data.Get(new RegionId(7), 2, 3), "stranded chunk re-homed to the live owner");
      Assert.IsNull(data.Get(new RegionId(5), 2, 3));
    }

    #endregion

  }

}
