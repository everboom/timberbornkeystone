using System.Collections.Generic;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Diagnostics.SelfTests;
using Keystone.Mod.Survey;
using Timberborn.GameSaveRuntimeSystem;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;

namespace Keystone.Mod.Persistence {

  /// <summary>
  /// Save/load owner for Keystone state. Single Mod-side singleton; all
  /// <see cref="IObjectSaver"/>/<see cref="IObjectLoader"/> calls live
  /// here. Translates between Timberborn's loader API and Core's
  /// <see cref="SnapshotPayload"/> via <see cref="SnapshotCodec"/>.
  ///
  /// <para><b>Lifecycle.</b>
  /// <list type="bullet">
  ///   <item><c>Load()</c> reads the saved blob into a private
  ///         <see cref="KeystoneSnapshot"/> buffer. No terrain or
  ///         region access yet -- those aren't valid until
  ///         <c>PostLoad</c>.</item>
  ///   <item><c>PostLoad()</c> first forces the surveyor's PostLoad
  ///         (idempotent), so regions are freshly Indexed with
  ///         deterministic ids; then it drains the buffer onto the
  ///         live <see cref="RegionService"/>,
  ///         <see cref="RegionValueStore"/>, and
  ///         <see cref="ChunkValueStore"/> and clears the buffer.</item>
  ///   <item><c>Save()</c> reads current region clock-stamps, region-
  ///         value-store contents, and chunk-value-store contents into
  ///         a fresh snapshot, encodes via <see cref="SnapshotCodec"/>,
  ///         and writes the parallel-list payload to the saver.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Schema versioning.</b> The current
  /// <see cref="SnapshotCodec.CurrentSchemaVersion"/> is written to
  /// every save. On read, missing version is treated as 1 (first
  /// release); higher version is logged loudly and load proceeds
  /// best-effort (don't crash; downgrade-safe).</para>
  /// </summary>
  public sealed class KeystonePersistence
      : ILoadableSingleton, IPostLoadableSingleton, ISaveableSingleton, IKeystoneLoadStatus {

    #region Save keys

    /// <summary>Mod-namespaced singleton key. Avoids collision with the base game and other mods.</summary>
    private static readonly SingletonKey RootKey = new("Keystone.RegionService");

    private static readonly PropertyKey<int> SchemaVersionKey = new("SchemaVersion");
    private static readonly ListKey<int> RegionIdsKey = new("RegionIds");
    private static readonly ListKey<float> RegionTotalDaysKey = new("RegionTotalDaysAtCreation");
    private static readonly ListKey<int> RegionCycleKey = new("RegionCreatedCycle");
    private static readonly ListKey<int> RegionCycleDayKey = new("RegionCreatedCycleDay");
    private static readonly ListKey<float> RegionPartialCycleDayKey = new("RegionCreatedPartialCycleDay");
    private static readonly ListKey<int> RegionWeatherKey = new("RegionWeather");
    private static readonly ListKey<int> RegionRepresentativeXKey = new("RegionRepresentativeX");
    private static readonly ListKey<int> RegionRepresentativeYKey = new("RegionRepresentativeY");
    private static readonly ListKey<int> RegionRepresentativeZKey = new("RegionRepresentativeZ");
    private static readonly ListKey<int> RegionValueIdsKey = new("RegionValueIds");
    private static readonly ListKey<string> RegionValueKindsKey = new("RegionValueKinds");
    private static readonly ListKey<float> RegionValueFloatsKey = new("RegionValueFloats");
    private static readonly ListKey<int> ChunkValueRegionIdsKey = new("ChunkValueRegionIds");
    private static readonly ListKey<int> ChunkValueChunkXsKey = new("ChunkValueChunkXs");
    private static readonly ListKey<int> ChunkValueChunkYsKey = new("ChunkValueChunkYs");
    private static readonly ListKey<string> ChunkValueKindsKey = new("ChunkValueKinds");
    private static readonly ListKey<float> ChunkValueFloatsKey = new("ChunkValueFloats");

    #endregion

    #region Fields

    private readonly ISingletonLoader _singletonLoader;
    private readonly RegionService _regions;
    private readonly RegionValueStore _regionValues;
    private readonly ChunkValueStore _chunkValues;
    private readonly IClock _clock;
    private readonly KeystoneSurveyor _surveyor;
    private readonly RegionUpdater _regionUpdater;
    private readonly GameLoader _gameLoader;
    private readonly PerfTracker _perf;
    private KeystoneSnapshot? _pending;
    private bool _isNewGame;
    private SnapshotLoadReport _loadReport = SnapshotLoadReport.Empty;

    /// <summary>True once <see cref="Load"/> has run (regardless of
    /// whether a saved blob was found). Read by the startup
    /// self-check to defer the snapshot report until Load is done.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>True once <see cref="PostLoad"/> has run. The
    /// drop/prune counts in <see cref="LoadReport"/> are only
    /// meaningful after this flag flips, so the startup self-check
    /// gates on both <see cref="IsLoaded"/> and this.</summary>
    public bool IsPostLoaded { get; private set; }

    /// <inheritdoc />
    string IKeystoneLoadStatus.LoaderName => nameof(KeystonePersistence);

    /// <summary>
    /// <see cref="IKeystoneLoadStatus.IsLoaded"/> reports our final
    /// lifecycle step (<c>PostLoad</c>), not just <c>Load</c> -- the
    /// survival test asks "did your initialisation complete?", and
    /// for us the snapshot drain happens in <c>PostLoad</c>. The
    /// public <see cref="IsLoaded"/> property is unchanged (callers
    /// reading it for the Load-only meaning still get it).
    /// </summary>
    bool IKeystoneLoadStatus.IsLoaded => IsPostLoaded;

    #endregion

    #region Construction

    public KeystonePersistence(
        ISingletonLoader singletonLoader,
        RegionService regions,
        RegionValueStore regionValues,
        ChunkValueStore chunkValues,
        IClock clock,
        KeystoneSurveyor surveyor,
        RegionUpdater regionUpdater,
        GameLoader gameLoader,
        PerfTracker perf) {
      _singletonLoader = singletonLoader;
      _regions = regions;
      _regionValues = regionValues;
      _chunkValues = chunkValues;
      _clock = clock;
      _surveyor = surveyor;
      _regionUpdater = regionUpdater;
      _gameLoader = gameLoader;
      _perf = perf;
    }

    #endregion

    #region Public state

    /// <summary>True when this session was started from a freshly
    /// created settlement, false when loading an existing save (of
    /// any age).
    /// <para><b>Source.</b> Reads
    /// <see cref="GameLoader.IsNewGame"/> at <see cref="Load"/> time —
    /// Timberborn's authoritative signal, set by the game's save-
    /// loading layer based on whether <c>LoadNew</c> or <c>Load</c>
    /// was called. Using the game's own signal (rather than "is the
    /// Keystone singleton blob present?") means we stay correct when
    /// loading a save written before Keystone was installed, or any
    /// other scenario where our save state might be absent on a non-
    /// fresh settlement.</para>
    /// <para>Read by <c>KeystoneStartupWarmup</c> to decide whether
    /// to seed the per-chunk Maturity channel + run the pre-game
    /// rule pass. Stable from end of <see cref="Load"/> for the rest
    /// of the session.</para></summary>
    public bool IsNewGame => _isNewGame;

    /// <summary>Summary of what <see cref="Load"/> observed in the
    /// saved blob: whether the singleton was present, what schema
    /// version it claimed, and how many records came back per
    /// section. Empty when there's no save (new game) or before
    /// <see cref="Load"/> has run. Read by <c>SnapshotStartupCheck</c>
    /// so a player whose save came back surprisingly empty hears
    /// about it.</summary>
    public SnapshotLoadReport LoadReport => _loadReport;

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      try {
        LoadInner();
      } catch (System.Exception ex) {
        Diagnostics.LifecycleGuard.HandleError("KeystonePersistence.Load", "Subsystem failed", ex);
      } finally {
        // Mark as loaded regardless of which branch ran -- the
        // startup self-check is "did Load happen yet", not "did Load
        // find data". Set in finally so an exception in LoadInner
        // still flips the flag (the dialog should then surface the
        // exception via the Harmony or snapshot check rather than
        // hanging forever waiting for a flag that never flips).
        IsLoaded = true;
      }
    }

    private void LoadInner() {
      // New vs loaded is read from GameLoader, not from snapshot
      // presence -- a save written before Keystone was installed
      // also has no singleton blob, but it's not a new game.
      _isNewGame = _gameLoader.IsNewGame;

      // Fresh-game branch: TryGetSingleton returns false, leave _pending
      // null so PostLoad knows there's nothing to drain.
      if (!_singletonLoader.TryGetSingleton(RootKey, out var obj)) {
        _pending = null;
        _loadReport = SnapshotLoadReport.Empty;
        return;
      }

      var version = obj.Has(SchemaVersionKey) ? obj.Get(SchemaVersionKey) : 1;
      if (version > SnapshotCodec.CurrentSchemaVersion) {
        KeystoneLog.Warn(
            $"[Keystone] Save schema version {version} is newer than supported {SnapshotCodec.CurrentSchemaVersion}; loading best-effort.");
      }

      // Best-effort: each list defaults to empty if absent. The codec
      // throws if region or value sub-block lengths disagree, which is a
      // genuine corruption signal we want loud.
      //
      // Direct calls (rather than a helper) are needed because the
      // generic Get<T>(ListKey<T>) overload is constrained to Enum on
      // this platform; we have to use the typed native overloads
      // (int, float, string).
      var regionIds = obj.Has(RegionIdsKey) ? (IReadOnlyList<int>)obj.Get(RegionIdsKey) : System.Array.Empty<int>();
      var regionTotalDays = obj.Has(RegionTotalDaysKey) ? (IReadOnlyList<float>)obj.Get(RegionTotalDaysKey) : System.Array.Empty<float>();
      var regionCycle = obj.Has(RegionCycleKey) ? (IReadOnlyList<int>)obj.Get(RegionCycleKey) : System.Array.Empty<int>();
      var regionCycleDay = obj.Has(RegionCycleDayKey) ? (IReadOnlyList<int>)obj.Get(RegionCycleDayKey) : System.Array.Empty<int>();
      var regionPartial = obj.Has(RegionPartialCycleDayKey) ? (IReadOnlyList<float>)obj.Get(RegionPartialCycleDayKey) : System.Array.Empty<float>();
      var regionWeather = obj.Has(RegionWeatherKey) ? (IReadOnlyList<int>)obj.Get(RegionWeatherKey) : System.Array.Empty<int>();
      var regionRepX = obj.Has(RegionRepresentativeXKey) ? (IReadOnlyList<int>)obj.Get(RegionRepresentativeXKey) : System.Array.Empty<int>();
      var regionRepY = obj.Has(RegionRepresentativeYKey) ? (IReadOnlyList<int>)obj.Get(RegionRepresentativeYKey) : System.Array.Empty<int>();
      var regionRepZ = obj.Has(RegionRepresentativeZKey) ? (IReadOnlyList<int>)obj.Get(RegionRepresentativeZKey) : System.Array.Empty<int>();
      var regionValueIds = obj.Has(RegionValueIdsKey) ? (IReadOnlyList<int>)obj.Get(RegionValueIdsKey) : System.Array.Empty<int>();
      var regionValueKinds = obj.Has(RegionValueKindsKey) ? (IReadOnlyList<string>)obj.Get(RegionValueKindsKey) : System.Array.Empty<string>();
      var regionValueFloats = obj.Has(RegionValueFloatsKey) ? (IReadOnlyList<float>)obj.Get(RegionValueFloatsKey) : System.Array.Empty<float>();
      var chunkValueRegionIds = obj.Has(ChunkValueRegionIdsKey) ? (IReadOnlyList<int>)obj.Get(ChunkValueRegionIdsKey) : System.Array.Empty<int>();
      var chunkValueChunkXs = obj.Has(ChunkValueChunkXsKey) ? (IReadOnlyList<int>)obj.Get(ChunkValueChunkXsKey) : System.Array.Empty<int>();
      var chunkValueChunkYs = obj.Has(ChunkValueChunkYsKey) ? (IReadOnlyList<int>)obj.Get(ChunkValueChunkYsKey) : System.Array.Empty<int>();
      var chunkValueKinds = obj.Has(ChunkValueKindsKey) ? (IReadOnlyList<string>)obj.Get(ChunkValueKindsKey) : System.Array.Empty<string>();
      var chunkValueFloats = obj.Has(ChunkValueFloatsKey) ? (IReadOnlyList<float>)obj.Get(ChunkValueFloatsKey) : System.Array.Empty<float>();

      var payload = new SnapshotPayload(
          version,
          regionIds, regionTotalDays, regionCycle, regionCycleDay, regionPartial, regionWeather,
          regionRepX, regionRepY, regionRepZ,
          regionValueIds, regionValueKinds, regionValueFloats,
          chunkValueRegionIds, chunkValueChunkXs, chunkValueChunkYs, chunkValueKinds, chunkValueFloats);
      _pending = SnapshotCodec.Decode(payload);
      _loadReport = new SnapshotLoadReport(
          HasSnapshot: true,
          SchemaVersion: version,
          RegionCount: _pending.Regions.Count,
          RegionValueCount: _pending.RegionValues.Count,
          ChunkValueCount: _pending.ChunkValues.Count,
          MatchedRegionStamps: 0,
          RecoveredRegionStamps: 0,
          DroppedRegionStamps: 0,
          DroppedRegionValues: 0,
          DroppedChunkValues: 0,
          RescuedChunkValues: 0);
    }

    #endregion

    #region IPostLoadableSingleton

    /// <summary>Run <see cref="PostLoad"/> if it hasn't run this
    /// session. Idempotent. Used by <c>KeystoneStartupWarmup</c> to
    /// guarantee the save is drained into <see cref="ChunkValueStore"/>
    /// before the biome ticker reads from it.</summary>
    public void EnsurePostLoaded() {
      if (IsPostLoaded) return;
      PostLoad();
    }

    /// <inheritdoc />
    public void PostLoad() {
      try {
        PostLoadInner();
      } catch (System.Exception ex) {
        // Record before letting the finally flip IsPostLoaded. Without
        // this record SnapshotStartupCheck would still run (its IsReady
        // gates on IsPostLoaded which is set in the finally below), but
        // it would read a stale _loadReport (default-zero from Load's
        // pre-throw state) and either emit a misleading "no snapshot"
        // warning or stay silent -- either way the actual failure is
        // invisible to the dialog. The TryRecord here surfaces it under
        // the dialog-worthy "Subsystem failed" category.
        Diagnostics.LifecycleGuard.HandleError("KeystonePersistence.PostLoad", "Subsystem failed", ex);
      } finally {
        IsPostLoaded = true;
      }
    }

    private void PostLoadInner() {
      // Force the surveyor to PostLoad first if Timberborn called us
      // before it. EnsurePostLoaded is idempotent; it short-circuits
      // when the surveyor has already run.
      _surveyor.EnsurePostLoaded();

      if (_pending is null || _pending.IsEmpty) {
        // Fresh game (no save) or empty save -- regions are already
        // stamped with "now" by the freshly-Indexed pass and the value
        // stores are empty. Nothing to do.
        _pending?.Clear();
        _pending = null;
        return;
      }

      // Build saved-RegionId -> effective-live-RegionId remap, always
      // anchoring through the saved record's representative surface.
      // Saved RegionIds are not trusted directly: between save and load
      // the Index() algorithm's inputs can drift (terrain mutation,
      // surveyor predicate change, blockage placement, mod-set change),
      // and a saved ID matching a live ID by coincidence isn't a
      // guarantee they refer to the same physical region. The
      // representative surface IS a physical anchor -- "which live
      // region owns this surface right now" is unambiguous regardless
      // of ID allocation.
      //
      // Categorisation only:
      //   - Matched: representative resolved AND the live region's ID
      //     equals the saved ID. The canonical-ID save was right by
      //     coincidence; nothing surprising to report.
      //   - Recovered: representative resolved but the ID differs. The
      //     canonical-ID drifted (this is the common case after any
      //     topology-affecting change); load corrected for it.
      //   - Dropped: no representative recorded (v1 saves), or the
      //     representative surface no longer exists in the live state
      //     (terrain was edited externally between save and load).
      var remap = new Dictionary<RegionId, RegionId>(_pending.Regions.Count);
      var stampsByEffectiveId = new Dictionary<RegionId, RegionPersistedRecord>(_pending.Regions.Count);
      var matched = 0;
      var recovered = 0;
      var droppedStamps = 0;
      foreach (var record in _pending.Regions) {
        if (!record.HasRepresentative) {
          droppedStamps++;
          KeystoneLog.Warn(
              $"[Keystone] persisted region {record.Id} has no representative recorded " +
              "(v1 save); dropping creation stamp.");
          continue;
        }
        var liveRegion = _regions.Containing(record.Representative);
        if (liveRegion == null) {
          droppedStamps++;
          KeystoneLog.Warn(
              $"[Keystone] persisted region {record.Id}'s representative " +
              $"{record.Representative} no longer exists in the live state; dropping creation stamp.");
          continue;
        }
        remap[record.Id] = liveRegion.Id;
        stampsByEffectiveId[liveRegion.Id] = record with { Id = liveRegion.Id };
        if (liveRegion.Id.Value == record.Id.Value) {
          matched++;
        } else {
          recovered++;
        }
      }
      _regions.RestoreCreatedAt(stampsByEffectiveId);

      // Translate region/chunk values through the remap before
      // rehydrating. Entries whose saved RegionId didn't resolve to a
      // live region get filtered out at this step rather than via
      // post-rehydrate pruning.
      var translatedRegionValues = new Dictionary<RegionValueKey, float>(_pending.RegionValues.Count);
      var droppedRegionValues = 0;
      foreach (var kv in _pending.RegionValues) {
        if (!remap.TryGetValue(kv.Key.RegionId, out var effectiveId)) {
          droppedRegionValues++;
          continue;
        }
        translatedRegionValues[new RegionValueKey(effectiveId, kv.Key.Kind)] = kv.Value;
      }
      // Chunk values use spatial rescue, not the region-level remap.
      // The remap is one-to-one at the region level, which loses chunks
      // when a saved region splits across a now-introduced blockage
      // (only one half ends up in the remap) or when the saved
      // representative is now blocked (the saved region drops, and
      // every chunk that belonged to it drops with it). Chunks are
      // spatially keyed by (ChunkX, ChunkY); we re-bind each chunk to
      // whichever live region owns the majority of surfaces in the
      // chunk's tile footprint right now. The saved RegionId is
      // informational only -- BUT its Z layer is LOAD-BEARING: see
      // the Z invariant on ChunkValueKey. A chunk's data was attached
      // to a region at a specific Z and MUST rebind to a region at
      // that same Z. Without the Z constraint, vertically stacked
      // regions at the same (X, Y) compete and a higher region can
      // win the majority vote, silently reattaching the chunk to the
      // wrong layer. Losing a chunk to "no live region at this Z"
      // is correct; misattaching across Z would silently corrupt
      // per-chunk Maturity history.
      //
      // Build a savedRegionId -> Z lookup from the saved Regions list
      // so each chunk's rescue is anchored to its original Z. Saved
      // regions without a representative (v1 saves) won't appear in
      // the lookup; those chunks fall back to no-Z-constraint rescue
      // (lossy but matches v1's already-lossy behaviour).
      var savedRegionZ = new Dictionary<RegionId, int>(_pending.Regions.Count);
      foreach (var record in _pending.Regions) {
        if (record.HasRepresentative) {
          savedRegionZ[record.Id] = record.Representative.Z;
        }
      }

      var translatedChunkValues = new Dictionary<ChunkValueKey, float>(_pending.ChunkValues.Count);
      var droppedChunkValues = 0;
      var rescuedChunkValues = 0;
      var zMismatches = 0;
      // Distinct dropped-chunk locations (several value kinds share one
      // chunk) and a capped sample, so the startup check can say WHERE the
      // ecology reset, not just how much. We track two sets: every dropped
      // chunk (droppedChunkAreas, for the detail log) and the subset that
      // held accumulated *maturity* (droppedMaturityAreas). Only the latter
      // is real, unrecoverable ecology loss worth alarming about — the
      // suitability channel re-derives within a few ticks, so a
      // suitability-only / empty drop is benign churn. (This mirrors the
      // mid-game reconciler's maturity-vs-empty split; before, load alarmed
      // on raw dropped value rows, which one destroyed chunk inflated to
      // ~10-20 — suitability + maturity across every biome — so a single
      // legitimately-removed chunk could trip the warning floor.)
      var droppedChunkAreas = new HashSet<DroppedChunkLocation>();
      var droppedMaturityAreas = new HashSet<DroppedChunkLocation>();
      var droppedMaturitySample = new List<DroppedChunkLocation>(DroppedChunkLocation.SampleCap);
      foreach (var kv in _pending.ChunkValues) {
        int? targetZ = savedRegionZ.TryGetValue(kv.Key.RegionId, out var z) ? z : (int?)null;
        var liveOwner = _regions.FindRegionByChunkFootprint(
            kv.Key.ChunkX, kv.Key.ChunkY, RegionEcologyField.ChunkSize, targetZ);
        if (liveOwner is null) {
          // No live region at the saved Z in this chunk's footprint
          // (every voxel blocked, out of bounds, or only stacked
          // regions at other Z layers exist now). Drop the chunk
          // rather than misattach it across Z.
          droppedChunkValues++;
          var loc = new DroppedChunkLocation(kv.Key.ChunkX, kv.Key.ChunkY, targetZ);
          droppedChunkAreas.Add(loc);
          // Real loss only if this dropped row is a non-zero maturity value.
          if (kv.Value != 0f
              && kv.Key.Kind.StartsWith(KnownValueKinds.ChunkMaturityPrefix, System.StringComparison.Ordinal)) {
            if (droppedMaturityAreas.Add(loc)
                && droppedMaturitySample.Count < DroppedChunkLocation.SampleCap) {
              droppedMaturitySample.Add(loc);
            }
          }
          continue;
        }
        var effectiveId = liveOwner.Value;

        // Defensive Z-invariant check. With Z-strict rescue the
        // live region's Z MUST equal the saved Z by construction
        // (FindRegionByChunkFootprint filters surfaces to targetZ;
        // a region's surfaces all share its Z). A non-zero count
        // here means a regression in the rescue plumbing -- the
        // ChunkValueKey Z invariant has been violated. Log loudly;
        // we still attach because dropping mid-pass would be
        // surprising (the rescue claimed success), but the error
        // logged below makes the corruption visible at startup.
        if (targetZ.HasValue) {
          var live = _regions.Get(effectiveId);
          if (live != null && live.Z != targetZ.Value) {
            zMismatches++;
          }
        }

        // Count as "rescued" when the saved-region remap either failed
        // or pointed somewhere different from the spatial answer --
        // either way, the existing region-level path would have lost
        // or misrouted this chunk. Straight match means the spatial
        // answer agrees with the remap (or the saved region was a
        // direct ID hit), so the chunk would have rehydrated correctly
        // under either path.
        if (!remap.TryGetValue(kv.Key.RegionId, out var remappedId)
            || remappedId.Value != effectiveId.Value) {
          rescuedChunkValues++;
        }
        translatedChunkValues[new ChunkValueKey(
            effectiveId, kv.Key.ChunkX, kv.Key.ChunkY, kv.Key.Kind)] = kv.Value;
      }
      if (zMismatches > 0) {
        KeystoneLog.Error(
            $"[Keystone] PostLoad: {zMismatches} chunk value(s) attached to a " +
            "live region at a different Z than the saved record -- this violates " +
            "the ChunkValueKey Z invariant (see its docstring). Likely a bug in " +
            "chunk-rescue plumbing that bypassed FindRegionByChunkFootprint's " +
            "targetZ filter.");
      }
      MigrateRiparianToLandBiome(translatedChunkValues);
      _regionValues.RehydrateFrom(translatedRegionValues);
      _chunkValues.RehydrateFrom(translatedChunkValues);

      // Defensive post-rehydrate prune. With the pre-filter above this
      // should be zero in normal flow; keeping it catches any future
      // path that bypasses the remap. Counted into the same dropped
      // columns the translation-time drops fill in.
      var liveIds = new HashSet<RegionId>();
      foreach (var r in _regions.All) liveIds.Add(r.Id);
      var prunedRegionValues = _regionValues.PruneToLiveRegions(liveIds);
      var prunedChunkValues = _chunkValues.PruneToLiveRegions(liveIds);

      _loadReport = _loadReport with {
          MatchedRegionStamps = matched,
          RecoveredRegionStamps = recovered,
          DroppedRegionStamps = droppedStamps,
          DroppedRegionValues = droppedRegionValues + prunedRegionValues,
          DroppedChunkValues = droppedChunkValues + prunedChunkValues,
          RescuedChunkValues = rescuedChunkValues,
          DroppedChunkAreas = droppedChunkAreas.Count,
          DroppedChunkAreasWithMaturity = droppedMaturityAreas.Count,
          DroppedChunkSample = droppedMaturitySample,
      };

      KeystoneLog.Verbose(
          $"[Keystone] Restored {matched} matched + {recovered} recovered region stamps " +
          $"({droppedStamps} dropped); {translatedRegionValues.Count} region values, " +
          $"{translatedChunkValues.Count} chunk values rehydrated " +
          $"({rescuedChunkValues} spatially rescued, {droppedChunkValues} dropped across " +
          $"{droppedChunkAreas.Count} chunk area(s), {droppedMaturityAreas.Count} of which lost maturity; " +
          $"region values: {droppedRegionValues} dropped at remap, " +
          $"{prunedRegionValues} swept post-rehydrate).");

      _pending.Clear();
      _pending = null;
    }

    #endregion

    #region Save migration

    private const string RiparianMaturityKind = "keystone.chunk.maturity.riparian";
    private const string RiparianSuitabilityKind = "keystone.chunk.suitability.riparian";
    private const string GrasslandMaturityKind = "keystone.chunk.maturity.grassland";
    private const string ForestMaturityKind = "keystone.chunk.maturity.forest";
    private const string GrasslandSuitabilityKind = "keystone.chunk.suitability.grassland";
    private const string ForestSuitabilityKind = "keystone.chunk.suitability.forest";

    // Suitability kinds for every biome other than Forest and Grassland.
    // Used by the Riparian migration to detect "this chunk is now
    // best classified as something else (typically Wetland)" — in
    // which case carrying Riparian maturity into Forest/Grassland
    // would misplace it. Kinds are spelled out as string literals
    // rather than derived from BiomeValueKinds.ForSuitability(...)
    // because this code reads historical save data; if BiomeKind is
    // ever renamed the migration must keep working against
    // already-saved keys.
    private static readonly string[] NonLandSuitabilityKinds = {
        "keystone.chunk.suitability.monoculture",
        "keystone.chunk.suitability.river",
        "keystone.chunk.suitability.lake",
        "keystone.chunk.suitability.wetland",
        "keystone.chunk.suitability.cave",
        "keystone.chunk.suitability.dry",
        "keystone.chunk.suitability.contaminated",
        "keystone.chunk.suitability.badwater",
    };

    /// <summary>
    /// v0.6 migration: Riparian biome removed. For each chunk with
    /// saved Riparian maturity, decide where it goes by looking at
    /// the *suitabilities* on that chunk:
    /// <list type="bullet">
    ///   <item>If any non-land biome (Wetland, Lake, River, Cave, Dry,
    ///   Contaminated, Badwater, Monoculture) has higher suitability
    ///   than both Forest and Grassland, the chunk is no longer
    ///   land-shaped — discard the Riparian maturity rather than
    ///   misplace it.</item>
    ///   <item>Otherwise transfer the Riparian maturity onto whichever
    ///   of Forest or Grassland has the higher suitability — that's
    ///   the better signal of where the carry-over belongs than
    ///   existing-maturity (a chunk could be historically mature in
    ///   Forest but have shifted to Grassland conditions now).</item>
    ///   <item>If both Forest and Grassland suitability are zero
    ///   (and the non-land guard didn't already fire), discard.</item>
    /// </list>
    /// Riparian suitability entries are discarded unconditionally
    /// (the biome no longer exists, so there's nothing to recompute
    /// toward). Idempotent — future saves contain no Riparian entries.
    /// </summary>
    private static void MigrateRiparianToLandBiome(
        Dictionary<ChunkValueKey, float> chunkValues) {
      var toRemove = new List<ChunkValueKey>();
      var toUpdate = new List<KeyValuePair<ChunkValueKey, float>>();
      var transferred = 0;
      var discarded = 0;

      foreach (var kv in chunkValues) {
        if (kv.Key.Kind == RiparianSuitabilityKind) {
          toRemove.Add(kv.Key);
          continue;
        }
        if (kv.Key.Kind != RiparianMaturityKind) continue;
        if (kv.Value <= 0f) {
          toRemove.Add(kv.Key);
          continue;
        }

        var grasslandMaturityKey = new ChunkValueKey(
            kv.Key.RegionId, kv.Key.ChunkX, kv.Key.ChunkY, GrasslandMaturityKind);
        var forestMaturityKey = new ChunkValueKey(
            kv.Key.RegionId, kv.Key.ChunkX, kv.Key.ChunkY, ForestMaturityKind);
        var grasslandSuitabilityKey = new ChunkValueKey(
            kv.Key.RegionId, kv.Key.ChunkX, kv.Key.ChunkY, GrasslandSuitabilityKind);
        var forestSuitabilityKey = new ChunkValueKey(
            kv.Key.RegionId, kv.Key.ChunkX, kv.Key.ChunkY, ForestSuitabilityKind);
        chunkValues.TryGetValue(grasslandMaturityKey, out var grasslandMaturity);
        chunkValues.TryGetValue(forestMaturityKey, out var forestMaturity);
        chunkValues.TryGetValue(grasslandSuitabilityKey, out var grasslandSuitability);
        chunkValues.TryGetValue(forestSuitabilityKey, out var forestSuitability);

        var landMax = grasslandSuitability >= forestSuitability
            ? grasslandSuitability : forestSuitability;
        var nonLandMax = 0f;
        for (var i = 0; i < NonLandSuitabilityKinds.Length; i++) {
          var key = new ChunkValueKey(
              kv.Key.RegionId, kv.Key.ChunkX, kv.Key.ChunkY,
              NonLandSuitabilityKinds[i]);
          if (chunkValues.TryGetValue(key, out var v) && v > nonLandMax) {
            nonLandMax = v;
          }
        }

        if (landMax <= 0f || nonLandMax > landMax) {
          discarded++;
        } else if (grasslandSuitability >= forestSuitability) {
          toUpdate.Add(new KeyValuePair<ChunkValueKey, float>(
              grasslandMaturityKey, grasslandMaturity + kv.Value));
          transferred++;
        } else {
          toUpdate.Add(new KeyValuePair<ChunkValueKey, float>(
              forestMaturityKey, forestMaturity + kv.Value));
          transferred++;
        }
        toRemove.Add(kv.Key);
      }

      foreach (var update in toUpdate) {
        chunkValues[update.Key] = update.Value;
      }
      foreach (var key in toRemove) {
        chunkValues.Remove(key);
      }

      if (transferred > 0 || discarded > 0) {
        KeystoneLog.Verbose(
            $"[Keystone] Riparian migration: {transferred} chunk(s) transferred " +
            $"to Grassland/Forest, {discarded} discarded (no land biome presence).");
      }
    }

    #endregion

    #region ISaveableSingleton

    /// <inheritdoc />
    public void Save(ISingletonSaver saver) {
      // Force any pending region updates through before serializing.
      // Without this, the player can save during the RegionUpdater's
      // debounce window (after a terrain edit, before the flush fires)
      // and end up serializing a stale topology -- the next reload
      // re-Indexes with current terrain and produces fresh region ids
      // that don't match the saved ones, stranding all values.
      _regionUpdater.FlushPending("save");

      // Time everything after the (already-tracked) flush: the
      // canonical-id map, representative-surface computation, the
      // full region/value/chunk-value iteration + sort, and the
      // encode/write. This runs on save/autosave, off the sim tick, so
      // it's otherwise invisible to the perf window and adds to the
      // autosave hitch. RecordLatest (not Track) keeps the per-save
      // cost in the "Per-cycle totals (latest observed)" section
      // without folding a multi-ms spike into the rolling headline.
      var saveTimer = System.Diagnostics.Stopwatch.StartNew();

      // Canonicalise RegionIds to what a fresh Index() pass would
      // produce on the current surveyor state. Live IDs allocated by
      // ProcessChanges (region splits, fresh orphan regions) aren't
      // reproducible by Index() on reload, so persisting them directly
      // strands their data. Writing under canonical IDs makes save→load
      // round-trips stable regardless of allocation source -- the
      // reload's Index() produces exactly the same IDs we wrote under.
      // See RegionService.ComputeCanonicalIdMap for the algorithm.
      var canonicalIds = _regions.ComputeCanonicalIdMap();

      // Representative surface per region: the min-sorted member at
      // save time. Stored on each RegionPersistedRecord and used by
      // PostLoad as a recovery fallback when the canonical ID doesn't
      // match a freshly-Indexed live region (mod-set change, surveyor
      // edge case, bug in ComputeCanonicalIdMap, etc.).
      var representatives = _regions.ComputeRepresentativeSurfaces();

      var snapshot = new KeystoneSnapshot();
      var droppedRegions = 0;
      foreach (var region in _regions.All) {
        if (!canonicalIds.TryGetValue(region.Id, out var canonicalId)) {
          // Defensive: a live region whose ID didn't appear in the
          // canonical walk has no surveyor surface backing it -- which
          // means the live state is inconsistent (a region with zero
          // members, or members not in _surveyor.Surfaces). Skip it
          // rather than persist a record that can't be rehydrated.
          droppedRegions++;
          continue;
        }
        var representative = representatives.TryGetValue(region.Id, out var rep)
            ? rep
            : RegionPersistedRecord.NoRepresentative;
        snapshot.Regions.Add(new RegionPersistedRecord(
            canonicalId, region.CreatedAt, region.WeatherAtCreation, region.TotalDaysAtCreation,
            representative));
      }
      var droppedRegionValues = 0;
      foreach (var kv in _regionValues.Entries) {
        if (!canonicalIds.TryGetValue(kv.Key.RegionId, out var canonicalId)) {
          droppedRegionValues++;
          continue;
        }
        snapshot.RegionValues[new RegionValueKey(canonicalId, kv.Key.Kind)] = kv.Value;
      }
      // Footprint-orphan sweep. A chunk can be keyed under a region that is
      // still live (so it canonicalises and would be written) while the
      // chunk's own (X, Y) footprint no longer has any live surface at that
      // region's Z -- terrain under part of a surviving region was removed
      // and the chunk's value entry was never cleaned. The mid-game
      // ChunkReconciler walks the *data* store, so a value-store-only chunk
      // (e.g. a loaded chunk the rolling biome ticker hasn't re-touched)
      // slips past it. If we serialised such a chunk, the next load would
      // re-bind it by footprint, find no owner, drop it, and report it as
      // "lost ecology maturity" -- the over-report this sweep exists to
      // prevent. Dropping it here, at save, where we still have full live-
      // region context (including each region's Z), makes saves self-cleaning
      // and is exactly the predicate the load path uses
      // (FindRegionByChunkFootprint == null). One precomputed owner index
      // (O(surfaces)) gives O(1) lookups instead of an O(chunkSize^2) probe
      // per chunk.
      var footprintIndex = _regions.BuildChunkFootprintOwnerIndex(RegionEcologyField.ChunkSize);
      var droppedChunkValues = 0;
      var sweptFootprintOrphans = 0;
      foreach (var kv in _chunkValues.SortedSnapshot()) {
        if (!canonicalIds.TryGetValue(kv.Key.RegionId, out var canonicalId)) {
          droppedChunkValues++;
          continue;
        }
        var region = _regions.Get(kv.Key.RegionId);
        if (region == null
            || !footprintIndex.ContainsKey((kv.Key.ChunkX, kv.Key.ChunkY, region.Z))) {
          sweptFootprintOrphans++;
          continue;
        }
        snapshot.ChunkValues[new ChunkValueKey(canonicalId, kv.Key.ChunkX, kv.Key.ChunkY, kv.Key.Kind)] = kv.Value;
      }
      if (droppedRegions > 0 || droppedRegionValues > 0 || droppedChunkValues > 0) {
        KeystoneLog.Warn(
            $"[Keystone] Save: dropped {droppedRegions} region stamps, " +
            $"{droppedRegionValues} region values, {droppedChunkValues} chunk values " +
            "whose RegionId had no surveyor surface (live state inconsistent with surveyor).");
      }
      if (sweptFootprintOrphans > 0) {
        // Expected cleanup, not an error: terrain was removed under a
        // surviving region and the stale chunk entry is being pruned before
        // it can reach the save (and be mis-reported as lost on next load).
        KeystoneLog.Verbose(
            $"[Keystone] Save: swept {sweptFootprintOrphans} chunk value(s) whose footprint " +
            "has no live region at its Z (terrain removed under a surviving region); not " +
            "persisted, so they won't be reported as lost ecology on the next load.");
      }

      var payload = SnapshotCodec.Encode(snapshot);
      var obj = saver.GetSingleton(RootKey);
      obj.Set(SchemaVersionKey, payload.SchemaVersion);
      // Same generic-Enum-constraint awkwardness as in Load() forces
      // direct typed calls per list; can't generalise.
      obj.Set(RegionIdsKey, (IReadOnlyCollection<int>)payload.RegionIds);
      obj.Set(RegionTotalDaysKey, (IReadOnlyCollection<float>)payload.RegionTotalDaysAtCreation);
      obj.Set(RegionCycleKey, (IReadOnlyCollection<int>)payload.RegionCreatedCycle);
      obj.Set(RegionCycleDayKey, (IReadOnlyCollection<int>)payload.RegionCreatedCycleDay);
      obj.Set(RegionPartialCycleDayKey, (IReadOnlyCollection<float>)payload.RegionCreatedPartialCycleDay);
      obj.Set(RegionWeatherKey, (IReadOnlyCollection<int>)payload.RegionWeather);
      obj.Set(RegionRepresentativeXKey, (IReadOnlyCollection<int>)payload.RegionRepresentativeX);
      obj.Set(RegionRepresentativeYKey, (IReadOnlyCollection<int>)payload.RegionRepresentativeY);
      obj.Set(RegionRepresentativeZKey, (IReadOnlyCollection<int>)payload.RegionRepresentativeZ);
      obj.Set(RegionValueIdsKey, (IReadOnlyCollection<int>)payload.RegionValueIds);
      obj.Set(RegionValueKindsKey, (IReadOnlyCollection<string>)payload.RegionValueKinds);
      obj.Set(RegionValueFloatsKey, (IReadOnlyCollection<float>)payload.RegionValueFloats);
      obj.Set(ChunkValueRegionIdsKey, (IReadOnlyCollection<int>)payload.ChunkValueRegionIds);
      obj.Set(ChunkValueChunkXsKey, (IReadOnlyCollection<int>)payload.ChunkValueChunkXs);
      obj.Set(ChunkValueChunkYsKey, (IReadOnlyCollection<int>)payload.ChunkValueChunkYs);
      obj.Set(ChunkValueKindsKey, (IReadOnlyCollection<string>)payload.ChunkValueKinds);
      obj.Set(ChunkValueFloatsKey, (IReadOnlyCollection<float>)payload.ChunkValueFloats);

      _perf.RecordLatest("Persistence.Save", saveTimer.Elapsed.TotalMilliseconds);
    }

    #endregion

  }

}
