using System;
using System.Collections.Generic;
using Keystone.Core.Diagnostics;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Re-binds per-chunk data to the region that physically owns each
  /// chunk's <c>(X, Y, Z)</c> footprint, the single operation that keeps
  /// accumulated chunk values (per-biome Maturity, etc.) attached to the
  /// land as region ids churn under terrain edits, building placement,
  /// and save/load.
  ///
  /// <para><b>Why this exists.</b> Chunk data is keyed by
  /// <c>(RegionId, X, Y)</c>, but RegionIds are not stable: a terrain
  /// edit can split, merge, or kill a region, stranding or destroying the
  /// chunk data keyed under the old id — visible in-game as an entire
  /// cluster of chunks losing all its Maturity at once. The fix is to
  /// treat the <c>(X, Y, Z)</c> footprint as the source of truth and the
  /// RegionId as a derived binding that gets reconciled. Each chunk
  /// carries its Z (<see cref="ChunkData.Z"/>) so it can re-bind even
  /// after its original region is gone.</para>
  ///
  /// <para><b>What it does, per chunk.</b> Looks up the region that owns
  /// the chunk's footprint at the chunk's Z via
  /// <see cref="IChunkOwnerQuery"/>:
  /// <list type="bullet">
  ///   <item><b>Same region</b> — already correctly bound; left alone.</item>
  ///   <item><b>Different live region</b> — re-keyed onto the new owner in
  ///         both stores (the data store's instance and every Kind in the
  ///         value store move together).</item>
  ///   <item><b>No owner</b> (no region at that Z in the footprint) —
  ///         dropped. This is the accepted localized loss: it only happens
  ///         where the topology genuinely changed, and only after the
  ///         best-effort re-home above failed to find a home.</item>
  /// </list></para>
  ///
  /// <para><b>Collision policy — High beats Low.</b> When a re-home target
  /// already holds data for that footprint (two distinct regions owned the
  /// same chunk and one is re-homing onto the other), the record with the
  /// greater value wins and the loser is discarded — no per-slot blending,
  /// so the surviving record is one that actually existed. "Greater" is
  /// measured by the single highest value slot
  /// (<see cref="MaxSlot"/>) for the data store and per-Kind
  /// <c>max(existing, incoming)</c> for the value store.
  /// <para><b>TODO (biome hierarchy).</b> The real tiebreaker should
  /// respect the ecological precedence Badwater ▸ Dry ▸ irrigated ▸ wet
  /// rather than raw magnitude; High-beats-Low is the deliberate simple
  /// stand-in. Collisions are rare and the loss is bounded to one chunk,
  /// so this is acceptable until a consumer needs the hierarchy-aware
  /// rule.</para></para>
  ///
  /// <para><b>Scope.</b> <see cref="ReconcileFromDataStore"/> takes an
  /// optional region-id set: mid-game callers pass the regions a topology
  /// flush touched (plus any that just died) so the sweep is bounded to
  /// where change actually happened; passing <c>null</c> reconciles every
  /// chunk in the store — the map-wide pass used by the manual self-test.</para>
  ///
  /// <para><b>Pure Core.</b> Operates only on the two stores and the
  /// <see cref="IChunkOwnerQuery"/> port — no Timberborn or region-service
  /// dependency in the type itself, so it is driven directly from MSTest.</para>
  /// </summary>
  public sealed class ChunkReconciler {

    #region Fields

    private readonly ChunkDataStore _data;
    private readonly ChunkValueStore _values;
    private readonly IChunkOwnerQuery _owners;

    #endregion

    #region Construction

    public ChunkReconciler(
        ChunkDataStore data, ChunkValueStore values, IChunkOwnerQuery owners) {
      _data = data;
      _values = values;
      _owners = owners;
    }

    #endregion

    #region Reconcile

    /// <summary>
    /// Walk the data store (optionally restricted to chunks keyed under a
    /// region in <paramref name="scope"/>), and for each chunk re-bind it
    /// to the region that owns its <c>(X, Y, Z)</c> footprint. The same
    /// region remap is applied to the value store's entries for those
    /// chunks across every Kind, so the two stores stay in lockstep.
    /// </summary>
    /// <param name="scope">When non-null, only chunks whose current
    /// (possibly stale or dead) RegionId is in this set are considered;
    /// all others are left untouched. Pass dead region ids here too so
    /// their stranded chunks get re-homed. Null = reconcile every chunk.</param>
    /// <param name="ownerOverride">Optional owner query to use instead of
    /// the injected one. The map-wide sweep (scope null) passes a
    /// <see cref="PrecomputedChunkOwnerQuery"/> here so each chunk is an
    /// O(1) lookup rather than a per-chunk footprint walk; the scoped
    /// per-flush path leaves this null and uses the injected query.</param>
    /// <param name="dryRun">When true, decide and COUNT but apply nothing
    /// (no re-key, no drop). Lets a diagnostic (the manual self-test)
    /// report what a sweep would do without mutating game state.</param>
    /// <returns>Per-pass counts for diagnostics and the "chunks lost"
    /// warning that replaces the old map-wide retention percentage.</returns>
    public ChunkReconcileResult ReconcileFromDataStore(
        HashSet<RegionId>? scope = null, IChunkOwnerQuery? ownerOverride = null,
        bool dryRun = false, IPerfScope? perf = null) {
      var owners = ownerOverride ?? _owners;
      // Phase 1: decide. Read-only over the live dictionary; nothing is
      // mutated until every decision is captured, so we never enumerate a
      // collection we're also editing.
      List<(ChunkCoord From, RegionId To)>? moves = null;
      List<ChunkCoord>? homeless = null;
      var walked = 0;
      var scanned = 0;
      var kept = 0;
      var droppedWithMaturity = 0;

      void Decide(ChunkCoord coord, ChunkData data) {
        scanned++;
        var z = data.Z;

        // KEEP if the keyed region still owns surfaces in this footprint —
        // it's a valid owner, whether the majority OR a minority co-owner
        // of a chunk that straddles a region boundary (the biome ticker
        // maintains a copy under EACH region that has surfaces in such a
        // chunk; collapsing the minority copy onto the majority just churns
        // — the ticker recreates it next cycle). Only a genuinely STRANDED
        // chunk — its keyed region absent from the footprint (region died,
        // or lost all its surfaces here) — is re-homed or dropped.
        if (owners.RegionOwnsChunk(coord.RegionId, coord.GlobalChunkX, coord.GlobalChunkY, z)) {
          kept++;
          return;
        }

        var owner = owners.OwnerOfChunk(coord.GlobalChunkX, coord.GlobalChunkY, z);
        if (owner is null) {
          (homeless ??= new List<ChunkCoord>()).Add(coord);
          // Split the loss: a chunk holding accumulated Maturity is real,
          // unrecoverable ecology history; one with none (only the
          // recomputable Suitability channel, or nothing) is benign churn.
          // Only the former is worth alarming about.
          if (HoldsMaturity(data)) droppedWithMaturity++;
        } else {
          (moves ??= new List<(ChunkCoord, RegionId)>()).Add((coord, owner.Value));
        }
      }

      // Scoped path: visit only the chunks of the regions this flush touched,
      // via the data store's per-region index — O(in-scope chunks) instead of
      // walking the whole store to filter. Each chunk is keyed under exactly
      // one RegionId, so iterating distinct scope regions visits each at most
      // once. Map-wide path (scope null) walks every chunk.
      if (scope == null) {
        foreach (var kv in _data.Entries) {
          walked++;
          Decide(kv.Key, kv.Value);
        }
      } else {
        foreach (var regionId in scope) {
          foreach (var kv in _data.EntriesForRegion(regionId)) {
            walked++;
            Decide(kv.Key, kv.Value);
          }
        }
      }

      // Phase 2: apply (or, on a dry run, just tally collisions).
      var rehomed = 0;
      var collisions = 0;
      if (moves != null) {
        foreach (var (from, to) in moves) {
          if (dryRun) {
            if (_data.Get(new ChunkCoord(to, from.GlobalChunkX, from.GlobalChunkY)) != null) {
              collisions++;
            }
          } else if (ApplyMove(from, to)) {
            collisions++;
          }
          rehomed++;
        }
      }
      var dropped = 0;
      if (homeless != null) {
        foreach (var coord in homeless) {
          if (!dryRun) ApplyDrop(coord);
          dropped++;
        }
      }

      // Diagnostic: walked = chunks the Phase-1 loop iterated; scanned = the
      // subset in scope. With the per-region index the scoped path iterates
      // only in-scope chunks, so walked ≈ scanned; a regression (walked ≫
      // scanned on a scoped call) would mean the index path was bypassed. The
      // map-wide path (scope null) still walks the whole store, so walked is
      // the full chunk count there.
      if (perf != null) {
        perf.RecordCount("ChunkReconciler.Walked", walked);
        perf.RecordCount("ChunkReconciler.Scanned", scanned);
        perf.RecordCount("ChunkReconciler.Rehomed", rehomed);
        perf.RecordCount("ChunkReconciler.Dropped", dropped);
      }

      return new ChunkReconcileResult(
          scanned, kept, rehomed, dropped, droppedWithMaturity, collisions);
    }

    #endregion

    #region Maturity classification

    /// <summary>Registry ordinals declared as the
    /// <see cref="ChunkValueRole.Maturity"/> channel. Computed once from
    /// <see cref="ChunkDataStore.Registry"/> and cached once the registry
    /// is frozen. Reads the registrant-declared role rather than
    /// re-deriving meaning from the kind-name string, so renaming the
    /// maturity-kind prefix can't silently reclassify real loss as benign.
    /// Stays free of any biome dependency — it only asks the registry
    /// which slots are Maturity.</summary>
    private int[]? _maturityOrdinals;

    private int[] MaturityOrdinals() {
      if (_maturityOrdinals != null) return _maturityOrdinals;
      var registry = _data.Registry;
      List<int>? ordinals = null;
      for (var o = 0; o < registry.SlotCount; o++) {
        if (registry.RoleOf(o) == ChunkValueRole.Maturity) {
          (ordinals ??= new List<int>()).Add(o);
        }
      }
      var result = ordinals?.ToArray() ?? Array.Empty<int>();
      // Only cache once the registry can no longer grow, so a (defensive)
      // pre-freeze call doesn't pin an incomplete set.
      if (registry.IsFrozen) _maturityOrdinals = result;
      return result;
    }

    /// <summary>True if any of the chunk's Maturity slots is non-zero —
    /// i.e. it carries accumulated ecology history that a drop would
    /// permanently lose.</summary>
    private bool HoldsMaturity(ChunkData data) {
      var ordinals = MaturityOrdinals();
      for (var i = 0; i < ordinals.Length; i++) {
        if (data.Get(ordinals[i]) != 0f) return true;
      }
      return false;
    }

    #endregion

    #region Apply

    /// <summary>Move chunk <paramref name="from"/> onto the same global
    /// chunk coords under region <paramref name="to"/>, in both stores.
    /// Returns true if the destination already held data (a collision
    /// resolved High-beats-Low).</summary>
    private bool ApplyMove(ChunkCoord from, RegionId to) {
      var cx = from.GlobalChunkX;
      var cy = from.GlobalChunkY;

      // Data store: whole-record move with High-beats-Low on collision.
      var collision = false;
      var fromData = _data.Get(from);
      if (fromData != null) {
        var toCoord = new ChunkCoord(to, cx, cy);
        var existing = _data.Get(toCoord);
        if (existing == null) {
          _data.GetOrCreate(toCoord).CopyFrom(fromData);
        } else {
          collision = true;
          if (MaxSlot(fromData) > MaxSlot(existing)) existing.CopyFrom(fromData);
          // else the incumbent wins; the incoming record is discarded.
        }
        _data.Remove(from);
      }

      // Value store: every Kind for this footprint moves; per-Kind
      // max(existing, incoming) on collision.
      MoveValueEntries(from.RegionId, to, cx, cy);
      return collision;
    }

    /// <summary>Drop chunk <paramref name="coord"/> from both stores — no
    /// live region owns its footprint at its Z.</summary>
    private void ApplyDrop(ChunkCoord coord) {
      _data.Remove(coord);
      RemoveValueEntries(coord.RegionId, coord.GlobalChunkX, coord.GlobalChunkY);
    }

    /// <summary>Re-key every value-store Kind for
    /// <c>(<paramref name="from"/>, cx, cy)</c> onto
    /// <c>(<paramref name="to"/>, cx, cy)</c>, keeping the larger value
    /// per Kind on collision.
    ///
    /// <para><b>This per-Kind collision pick is transient for
    /// Keystone-synced kinds.</b> The value store is the persisted/API
    /// projection; the authoritative hot copy is <c>ChunkDataStore</c>,
    /// which <c>ChunkBiomeTicker</c> syncs forward (data → value) every
    /// tick. So on a collision the data store's whole-record winner
    /// (<see cref="ApplyMove"/>'s <see cref="MaxSlot"/>) overwrites this
    /// per-Kind result on the next sync — the two can momentarily disagree.
    /// This per-Kind merge only durably matters for kinds an external mod
    /// writes that Keystone doesn't sync. Don't read the two stores as a
    /// single source of truth across a collision until the next tick.</para></summary>
    private void MoveValueEntries(RegionId from, RegionId to, int cx, int cy) {
      // Snapshot before mutating: EntriesForChunk yields live dictionary
      // entries.
      List<KeyValuePair<string, float>>? entries = null;
      foreach (var kv in _values.EntriesForChunk(from, cx, cy)) {
        (entries ??= new List<KeyValuePair<string, float>>())
            .Add(new KeyValuePair<string, float>(kv.Key.Kind, kv.Value));
      }
      if (entries == null) return;
      foreach (var kv in entries) {
        _values.Remove(from, cx, cy, kv.Key);
        var existing = _values.Get(to, cx, cy, kv.Key);
        var keep = existing.HasValue
            ? (existing.Value >= kv.Value ? existing.Value : kv.Value)
            : kv.Value;
        _values.Set(to, cx, cy, kv.Key, keep);
      }
    }

    private void RemoveValueEntries(RegionId region, int cx, int cy) {
      List<string>? kinds = null;
      foreach (var kv in _values.EntriesForChunk(region, cx, cy)) {
        (kinds ??= new List<string>()).Add(kv.Key.Kind);
      }
      if (kinds == null) return;
      foreach (var kind in kinds) _values.Remove(region, cx, cy, kind);
    }

    /// <summary>Largest value across a chunk's slots — the domain-neutral
    /// proxy for "more accumulated" used by the collision tiebreaker. See
    /// the class docstring's High-beats-Low note and the biome-hierarchy
    /// TODO.</summary>
    private static float MaxSlot(ChunkData data) {
      var values = data.Values;
      var max = float.NegativeInfinity;
      for (var i = 0; i < values.Length; i++) {
        if (values[i] > max) max = values[i];
      }
      return max;
    }

    #endregion

  }

  /// <summary>
  /// Per-pass tally returned by
  /// <see cref="ChunkReconciler.ReconcileFromDataStore"/>. The diagnostic
  /// surface that replaces the hamfisted map-wide retention percentage:
  /// <see cref="HomelessDropped"/> is the region/chunk-granular "this much
  /// chunk data could not be re-homed and was lost" signal, and a large
  /// <see cref="Rehomed"/> count flags heavy topology churn.
  /// </summary>
  /// <param name="Scanned">Chunks considered (within scope).</param>
  /// <param name="Kept">Chunks already bound to their correct owner.</param>
  /// <param name="Rehomed">Chunks re-keyed onto a different live region.</param>
  /// <param name="HomelessDropped">Chunks with no live region at their Z;
  ///   dropped. Total of empty + with-maturity.</param>
  /// <param name="HomelessDroppedWithMaturity">Subset of
  ///   <paramref name="HomelessDropped"/> that carried accumulated Maturity
  ///   — the only drops that lose unrecoverable ecology history and the
  ///   ones worth alarming about. The remainder
  ///   (<see cref="HomelessDroppedEmpty"/>) are benign churn (no Maturity,
  ///   just the recomputable Suitability channel or nothing).</param>
  /// <param name="CollisionsResolved">Re-homes whose destination already
  ///   held data, resolved High-beats-Low.</param>
  public readonly record struct ChunkReconcileResult(
      int Scanned,
      int Kept,
      int Rehomed,
      int HomelessDropped,
      int HomelessDroppedWithMaturity,
      int CollisionsResolved) {

    /// <summary>Dropped chunks that held no Maturity — benign churn.</summary>
    public int HomelessDroppedEmpty => HomelessDropped - HomelessDroppedWithMaturity;

    /// <summary>True when the pass moved or dropped at least one chunk.</summary>
    public bool AnyChange => Rehomed > 0 || HomelessDropped > 0;

    /// <summary>
    /// Neutral classification of what this pass did, in descending order of
    /// significance: <see cref="ChunkReconcileOutcome.MaturityLost"/> (a
    /// chunk holding maturity was dropped) ▸
    /// <see cref="ChunkReconcileOutcome.RehomedNoLoss"/> (chunks re-bound,
    /// no maturity lost) ▸ <see cref="ChunkReconcileOutcome.EmptyDropsOnly"/>
    /// (only empty chunks dropped) ▸ <see cref="ChunkReconcileOutcome.Clean"/>.
    ///
    /// <para>Describes <i>what happened</i>, not how bad it is — consumers
    /// apply their own severity, because the same outcome reads differently
    /// by context. A flush treats re-homes as normal and only alarms on
    /// <c>MaturityLost</c>; the map-wide self-test treats any re-home as
    /// drift its scoped per-flush counterpart should have already handled.
    /// Centralised here so that field-precedence is defined and tested once
    /// rather than re-derived in each consumer.</para>
    /// </summary>
    public ChunkReconcileOutcome Outcome =>
        HomelessDroppedWithMaturity > 0 ? ChunkReconcileOutcome.MaturityLost
        : Rehomed > 0 ? ChunkReconcileOutcome.RehomedNoLoss
        : HomelessDroppedEmpty > 0 ? ChunkReconcileOutcome.EmptyDropsOnly
        : ChunkReconcileOutcome.Clean;

  }

  /// <summary>Neutral outcome categories for a reconcile pass. See
  /// <see cref="ChunkReconcileResult.Outcome"/> for precedence and the
  /// rationale for keeping severity out of the names.</summary>
  public enum ChunkReconcileOutcome {

    /// <summary>No chunk moved or dropped.</summary>
    Clean,

    /// <summary>Only empty chunks (no accumulated Maturity) were dropped;
    /// nothing re-homed. Benign churn.</summary>
    EmptyDropsOnly,

    /// <summary>Chunks were re-bound to new owners and no chunk holding
    /// Maturity was dropped. Normal during a flush; a drift signal during a
    /// full sweep.</summary>
    RehomedNoLoss,

    /// <summary>At least one chunk holding accumulated Maturity was dropped
    /// — real, unrecoverable ecology loss.</summary>
    MaturityLost,

  }

}
