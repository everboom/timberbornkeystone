using System.Diagnostics;
using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Keystone.Mod.Biomes;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Ecology;
using Keystone.Mod.Persistence;
using Keystone.Mod.Recipes;
using Keystone.Mod.Surface;
using Keystone.Core.Spatial;
using Keystone.Mod.Settings;
using Timberborn.SingletonSystem;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Startup {

  /// <summary>
  /// At PostLoad, drain the data-collection tickers' first cycles
  /// synchronously so the biome state is consistent immediately
  /// rather than waiting for the rolling-sweep cadence to catch up.
  /// Also rebuilds <see cref="ChunkClusterIndex"/> against the
  /// just-warmed Suitability + Maturity values so cluster queries
  /// land on a populated index from the first frame.
  /// On a fresh settlement, additionally seed per-chunk Maturity
  /// (as if the snapped Suitability had held steady for
  /// <see cref="NewGameMaturitySeedDays"/> game-days) and run a
  /// single synchronous pass of <see cref="ChunkRulesApplier"/> so
  /// any L1 rules whose maturity thresholds are crossed by the seed
  /// get a chance to fire before the player's first interaction.
  ///
  /// <para><b>Why the data-collection warmup.</b> Both
  /// <see cref="EcologyFieldUpdater"/> and <see cref="ChunkBiomeTicker"/>
  /// amortise over 1 game-hour. On a fresh load, without this warmup,
  /// the field state is "uninited" for ~1 game-hour (entity counts
  /// read as 0), and biome scores for another game-hour after that
  /// as the biome ticker catches up with the field. Net: ~2 game-
  /// hours of garbage state before the debug overlay or biome-gated
  /// handlers show anything useful. The warmup pays the cost upfront
  /// at load time — the player is in a loading screen anyway — and
  /// the next regular tick continues the rolling cadence normally.</para>
  ///
  /// <para><b>Why the new-game Maturity seed + rule pass.</b>
  /// Without it, every fresh settlement starts with Maturity = 0
  /// across every chunk. No biome level is active (every L1 has a
  /// non-zero <c>LowerMaturity</c>), so the player spends the first
  /// few game-days staring at a bare map while Maturity integrates
  /// up from zero. Seeding Maturity for
  /// <see cref="NewGameMaturitySeedDays"/> at the snapped Suitability
  /// puts each chunk where it would naturally be after that many days
  /// of holding steady, so L1 is at or near saturation from t=0 on
  /// chunks that genuinely belong to the biome — the player sees
  /// "this is what this corner of the map will look like" instead of
  /// "the map is loading." The rule pass then applies those active
  /// levels' content + attrition rules once, so L1 spawns/kills
  /// appear from t=0.</para>
  ///
  /// <para><b>New-game detection.</b> Driven off
  /// <see cref="KeystonePersistence.IsNewGame"/>, set during
  /// <see cref="ILoadableSingleton.Load"/> from whether the singleton
  /// blob was present. Bindito guarantees every <c>Load</c> runs
  /// before every <c>PostLoad</c>, so the flag is stable here.</para>
  ///
  /// <para><b>Ordering inside PostLoad.</b> The rule applier reads
  /// <see cref="BiomeLevelTable"/> (populated by
  /// <see cref="BiomeLevelCatalog.PostLoad"/>) and the handlers read
  /// <see cref="FlourishCatalog"/>. Bindito's PostLoad order isn't
  /// deterministic, so the warmup forces both catalogs through
  /// <c>EnsurePostLoaded</c> before driving the applier. The data-
  /// collection tickers (field, biome) carry their own lazy-init
  /// guards so the field-updater → biome-ticker sequencing here is
  /// safe regardless of whether their <c>PostLoad</c> ran first.</para>
  /// </summary>
  public sealed class KeystoneStartupWarmup : IPostLoadableSingleton {

    /// <summary>Days of Maturity integration applied to every chunk
    /// on a new game. Sized to land a perfect-Suitability chunk near
    /// the L1 ramp endpoint (M ≈ 2.4 after 2.5 days at S=1, ~L1 sat
    /// for biomes whose L1 is <c>[0.5, 2.5]</c>) so the natural L1
    /// content shows immediately on a fresh map rather than ramping
    /// in over the first few game-days. Marginal-Suitability chunks
    /// land lower (M ≈ 1.2 at S=0.5), still inside L1's range.
    /// <para>Bumping past 2.5 starts pre-saturating L2 too, which
    /// would skip the player-visible "biome maturing" feel; dropping
    /// below 2.5 reintroduces the "blank first day" problem the seed
    /// was added to solve.</para></summary>
    private readonly ChunkValueRegistry _valueRegistry;
    private readonly EcologyFieldUpdater _fieldUpdater;
    private readonly ChunkBiomeTicker _biomeTicker;
    private readonly ChunkClusterTicker _clusterTicker;
    private readonly KeystonePersistence _persistence;
    private readonly BiomeLevelCatalog _biomeLevels;
    private readonly FlourishCatalog _flourishCatalog;
    private readonly ChunkRulesApplier _rulesApplier;
    private readonly PerfTracker _perf;
    private readonly KeystoneFloraSettings _floraSettings;
    private readonly TileSlotRegistry _tileSlotRegistry;
    private readonly SurfaceFieldStore _surfaceFields;

    public KeystoneStartupWarmup(
        ChunkValueRegistry valueRegistry,
        EcologyFieldUpdater fieldUpdater,
        ChunkBiomeTicker biomeTicker,
        ChunkClusterTicker clusterTicker,
        KeystonePersistence persistence,
        BiomeLevelCatalog biomeLevels,
        FlourishCatalog flourishCatalog,
        ChunkRulesApplier rulesApplier,
        PerfTracker perf,
        KeystoneFloraSettings floraSettings,
        TileSlotRegistry tileSlotRegistry,
        SurfaceFieldStore surfaceFields) {
      _valueRegistry = valueRegistry;
      _fieldUpdater = fieldUpdater;
      _biomeTicker = biomeTicker;
      _clusterTicker = clusterTicker;
      _persistence = persistence;
      _biomeLevels = biomeLevels;
      _flourishCatalog = flourishCatalog;
      _rulesApplier = rulesApplier;
      _perf = perf;
      _floraSettings = floraSettings;
      _tileSlotRegistry = tileSlotRegistry;
      _surfaceFields = surfaceFields;
    }

    /// <inheritdoc />
    public void PostLoad() {
      // Outermost try/catch around the entire startup-warmup body.
      // This is the largest single piece of startup work in the mod
      // (field-updater cycle, biome-ticker snap, cluster rebuild,
      // optional new-game rule pass). A throw in any of the four
      // phases would otherwise escape to Bindito with the player
      // seeing partial seeding (e.g. fields warmed but cluster index
      // empty, or clusters built but no rule-pass placements) with
      // no diagnostic surface. The dialog category is dialog-worthy
      // because a failed warmup means the player's first gameplay
      // frame runs against a half-initialised state -- highly
      // user-visible. Per-phase isolation would let earlier work
      // commit but is more invasive; the simpler full-body wrap
      // covers the failure mode and the perf log lines downstream
      // make it obvious which phase failed (the last "X ms" line
      // logged identifies the survivor).
      try {
        // Register Keystone's biome value kinds in the parallel data
        // layer's ordinal registry and freeze it. Other mods that
        // injected ChunkValueRegistry during Load() have already had
        // their chance to register additional slots; all Load() calls
        // complete before any PostLoad().
        // Force persistence to drain the saved snapshot into
        // ChunkValueStore before we read from it. Bindito's PostLoad
        // order isn't deterministic — without this, the warmup can
        // run first and SeedFromChunkValueStore reads an empty store.
        _persistence.EnsurePostLoaded();

        BiomeValueKinds.Initialize(_valueRegistry);
        _valueRegistry.Freeze();
        _tileSlotRegistry.Freeze();

        var isNewGame = _persistence.IsNewGame;
        // Per-chunk biome Maturity seed: new game only. A loaded save's
        // Maturity rides the snapshot.
        var maturitySeedDays = isNewGame ? (float)_floraSettings.NewGameWarmupDays.Value : 0f;
        // Per-tile riparian Maturity seed *value* (R), by load kind:
        //  - new game: as if NewGameWarmupDays of sustained near-water,
        //    matching the per-chunk biome seed so a fresh map reads as
        //    established.
        //  - pre-store-save migration: capped small
        //    (RiparianMaturityParameters.MigrationSeedCap) so an existing
        //    settlement's shorelines start just at riparian L1 and grow in
        //    over play, rather than instantly sprouting full riparian
        //    bands across a map the player already knows.
        //  - post-store save: 0 (R restored from the per-tile store).
        float riparianSeed;
        if (isNewGame) {
          riparianSeed = RiparianMaturityUpdater.SeededValue(
              (float)_floraSettings.NewGameWarmupDays.Value);
        } else if (!_surfaceFields.HadPersistedData) {
          riparianSeed = System.Math.Min(
              RiparianMaturityUpdater.SeededValue((float)_floraSettings.NewGameWarmupDays.Value),
              RiparianMaturityParameters.MigrationSeedCap);
        } else {
          riparianSeed = 0f;
        }
        // All four warmup decisions on one greppable line, regardless of
        // which path runs. The migration note flags the one-time case
        // where a pre-store save seeds riparian maturity on load.
        KeystoneLog.Verbose(
            $"[Keystone] Warmup: newGame={isNewGame}, "
            + $"perTileStorePresent={_surfaceFields.HadPersistedData}, "
            + $"biomeSeed={maturitySeedDays}d, riparianSeed={riparianSeed}"
            + (!isNewGame && riparianSeed > 0f
                ? " (one-time pre-store-save migration, capped)"
                : "")
            + ".");

        var sw = Stopwatch.StartNew();
        // Per-tile warmup. Runs the field cycle (surface walk + entity
        // counts), computes water distance for every tile up front (it
        // otherwise lands only on the deferred parallel pass, so without
        // this the per-tile data is absent at load), and seeds riparian
        // maturity for near-water surfaces when riparianSeed > 0.
        // Ordered before the biome ticker and the Class B rule pass
        // below, both of which read the per-tile values: the data has to
        // be there when they run.
        // WarmUpNow returns the wall-clock ms of its per-tile sub-pass
        // (water-distance compute + riparian seed) so it can be reported
        // separately from the whole field warmup below.
        var perTileMs = _fieldUpdater.WarmUpNow(riparianSeed);
        var fieldMs = sw.ElapsedMilliseconds;

        sw.Restart();
        // Biome ticker can't use the base RunCycleNow with dt=0 (Score
        // wouldn't drift) and can't use it with a large dt either (the
        // Investment integration would overshoot). Use the dedicated
        // snap-mode warmup that writes Score = target directly. See
        // ChunkBiomeTicker.RunWarmupNow for the reasoning.
        //
        // On new games, seed Maturity by the player-configured warmup
        // days (computed above, shared with the per-tile warmup). Loaded
        // saves pass 0 — Maturity came back through the snapshot.
        _biomeTicker.RunWarmupNow(maturitySeedDays);
        var biomeMs = sw.ElapsedMilliseconds;

        sw.Restart();
        // Rebuild the cluster index against the just-warmed Suitability +
        // Maturity state. Without this, the cluster index stays empty
        // until the first natural ChunkClusterTicker cycle completes
        // (~1 game-hour after load), and any downstream consumer that
        // queries clusters in the meantime -- the new-game rule pass
        // below, fauna agents' first-frame placement, debug overlays --
        // sees no clusters and bails out. Loaded saves: clusters
        // reflect saved Maturity immediately. New games: seeded
        // Maturity is typically past the cluster threshold (1.0d), so
        // chunks with valid dominant biomes land in clusters from t=0.
        _clusterTicker.RunCycleNow();
        var clusterMs = sw.ElapsedMilliseconds;

        long rulesMs = 0;
        if (isNewGame) {
          // Force both catalogs through PostLoad before the applier
          // reads them. Bindito's PostLoad order isn't deterministic;
          // without these forces the applier could see an empty level
          // table or empty recipe buckets and silently no-op.
          _biomeLevels.EnsurePostLoaded();
          _flourishCatalog.EnsurePostLoaded();

          sw.Restart();
          // Includes RunAtStartup levels (geological / worldgen content
          // like rock clusters that fires exactly once at fresh-map
          // creation, then is silent forever after).
          _rulesApplier.RunCycleIncludingStartupNow();
          rulesMs = sw.ElapsedMilliseconds;
        }

        _perf.RecordOnce("Field updater", fieldMs);
        _perf.RecordOnce("  Per-tile fields", perTileMs);
        _perf.RecordOnce("Biome ticker", biomeMs);
        _perf.RecordOnce("Cluster index", clusterMs);
        if (isNewGame) {
          _perf.RecordOnce("Rule pass (new game)", rulesMs);
        }

        KeystoneLog.Verbose(
            $"[Keystone] Startup warmup: field updater {fieldMs} ms " +
            $"(per-tile {perTileMs:F1} ms), " +
            $"biome ticker {biomeMs} ms, cluster index {clusterMs} ms" +
            (isNewGame
                ? $", new-game rule pass {rulesMs} ms (Maturity seeded {maturitySeedDays}d)."
                : "."));
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleError(
            "KeystoneStartupWarmup.PostLoad", "Subsystem failed", ex);
      }
    }

  }

}
