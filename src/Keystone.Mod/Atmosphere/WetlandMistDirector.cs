using System;
using System.Collections.Generic;
using Keystone.Core.Atmosphere;
using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Core.Tiles;
using Keystone.Core.Time;
using Keystone.Mod.Assets;
using Keystone.Mod.Decoration;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Settings;
using Timberborn.TickSystem;
using UnityEngine;

namespace Keystone.Mod.Atmosphere {

  /// <summary>
  /// Atmospheric director: a per-tile, time-of-day-driven scatter of
  /// Ground Fog particle instances over the deep interior of wetland
  /// zones, choreographed across a single in-game cycle per map day.
  /// The intended visual effect is a "morning mist" rolling in across
  /// the bog -- in-game-day fraction <c>0</c> happens to correspond to
  /// sunrise (per observation), and the spawn window is set so the
  /// mist materialises as the player's morning starts.
  ///
  /// <para><b>The choreography.</b> Each in-game day, when the clock
  /// first reaches <see cref="SpawnWindowStart"/>, the director first
  /// rolls a single <see cref="FoggyNightProbability"/> die to decide
  /// whether today's pre-dawn window is "a foggy morning" at all. On
  /// a failed roll nothing is scheduled and the day is marked
  /// processed. On a pass, the director walks every Wetland-dominant
  /// chunk's tiles. For each tile whose four cardinal neighbours are
  /// also in Wetland-dominant chunks (i.e. deep wetland interior, not
  /// the zone's rim), it rolls <see cref="MistDensity"/> with a
  /// deterministic <c>(day, x, y)</c> seed; passing tiles get a
  /// random spawn time uniformly in
  /// <c>[</c><see cref="SpawnWindowStart"/><c>,
  /// </c><see cref="SpawnWindowEnd"/><c>]</c> and a random despawn
  /// time uniformly in
  /// <c>[</c><see cref="DespawnWindowStart"/><c>,
  /// </c><see cref="DespawnWindowEnd"/><c>]</c> for the same day.
  /// The director maintains a queue of pending spawn events and a list
  /// of live instances; on each tick it processes both.</para>
  ///
  /// <para><b>Why a time-of-day director, not a recipe.</b> The recipe
  /// layer is biome-keyed and fires on a per-day cycle to decide *what*
  /// spawns *where*. Time-of-day ephemera (mist at dawn, fireflies at
  /// dusk, etc.) need a different mechanism: a per-tick poll that
  /// triggers based on the clock, not on biome score thresholds. This
  /// director is the prototype for that pattern; later extensions may
  /// re-use it for different particles at different windows (hence the
  /// non-time-qualified class name).</para>
  ///
  /// <para><b>No persistence.</b> Mist is <c>Class A</c> -- not a
  /// <c>BlockObject</c>, not in any save state. If the player saves
  /// mid-cycle and reloads, mist is gone. The director's
  /// <c>_lastDayRolled</c> resets to <c>-1</c> on load, but the day-
  /// crossing check that fires the roll requires
  /// <c>TotalDaysElapsed</c>'s integer part to be greater than
  /// <c>_lastDayRolled</c> AND the hour-of-day to be inside the spawn
  /// window. Loaded saves past the spawn window mark the day as rolled
  /// and skip today's spawn (deliberate -- agreed with the user as the
  /// simplest behaviour).</para>
  ///
  /// <para><b>Density tuning.</b> <see cref="MistDensity"/> (currently
  /// 0.25) is the per-tile probability. Total mist instances scale
  /// linearly with eligible (deep-wetland-interior) tile count. There
  /// is no absolute cap by design -- lower the density if FPS suffers
  /// over very large wetland zones. The wetland-interior gate
  /// naturally caps density on small zones (the rim is excluded).</para>
  ///
  /// <para><b>Per-chunk dominant biome.</b> "Wetland tile" is read as
  /// "the tile's chunk is Wetland-dominant," not bilinearly-interpolated
  /// per-tile. Adjacent tiles in the same chunk share the dominant
  /// biome, so the 4-neighbour check is meaningful only at chunk
  /// boundaries -- where it screens out chunks on the rim of a wetland
  /// zone whose neighbour chunk is a different biome. Cached per
  /// <c>(RegionId, chunkX, chunkY)</c> during each roll. Per-tile
  /// resolution isn't needed here because each mist instance has a
  /// roughly 3x3-tile visual footprint and at most one mist spawns
  /// per chunk -- the chunk grain matches the effect's grain.</para>
  ///
  /// <para><b>Three-gate eligibility.</b> A tile is mist-eligible only
  /// when all three gates pass:
  /// <list type="bullet">
  ///   <item><i>Wetland Suitability wins dominance</i> at the tile's
  ///         chunk -- the standard Suitability-pass + max-Suitability
  ///         check from <see cref="ChunkBiomeSampler.SampleDominantBiome"/>
  ///         answers "is this chunk currently wet?"</item>
  ///   <item><i>Wetland Maturity ≥ <see cref="MistMinMaturity"/></i>
  ///         answers "has it been wet long enough?" Freshly-flooded
  ///         chunks win dominance the moment Suitability crosses the
  ///         gate but wait for Maturity to accrue before mist starts
  ///         appearing -- the visual is a reward for sustained
  ///         conditions, not transient ones.</item>
  ///   <item><i>Water depth at the topmost surface in
  ///         <c>(<see cref="MistWaterDepthMin"/>,
  ///         <see cref="MistWaterDepthMax"/>)</c></i> answers "is this
  ///         tile actually shallow standing water?" Skips dry-mud
  ///         pockets within a wet chunk (no water to mist above) and
  ///         open-water tiles like river channels or lake interiors
  ///         (where the mist would compete with the water surface).</item>
  /// </list></para>
  /// </summary>
  public sealed class WetlandMistDirector : ITickableSingleton {

    #region Constants

    /// <summary>The in-game day is 24 hours (matches the per-day cadence
    /// of <see cref="IClock.TotalDaysElapsed"/>'s fractional component).
    /// Window constants below are expressed in those in-game hours and
    /// converted to day-fraction by dividing by this constant.</summary>
    private const float HoursPerDay = 24f;

    /// <summary>Spawn window in in-game hours from day start (sunrise).
    /// 18.5 - 19 corresponds to the player's morning -- the in-game
    /// clock runs offset such that this range reads in-game as just
    /// before dawn; tuned visually rather than from a clock-conversion
    /// theory.</summary>
    private const float SpawnWindowStart = 18.5f / HoursPerDay;
    private const float SpawnWindowEnd = 19f / HoursPerDay;

    /// <summary>Despawn window in in-game hours from day start.
    /// 0 - 1 happens chronologically <i>after</i> the spawn window,
    /// which means despawn wraps into the next in-game day. The roll
    /// schedules the despawn at <c>day+1+despawnFrac</c>, not
    /// <c>day+despawnFrac</c> -- see <see cref="TryScheduleTile"/>.</summary>
    private const float DespawnWindowStart = 0f / HoursPerDay;
    private const float DespawnWindowEnd = 1f / HoursPerDay;

    /// <summary>Per-tile spawn probability rolled with a deterministic
    /// <c>(day, x, y)</c> seed. Lower if FPS suffers over large wetland
    /// zones; there is no absolute cap. Only applied on days that passed
    /// the <see cref="FoggyNightProbability"/> gate.</summary>
    private const float MistDensity = 0.25f;

    /// <summary>Wetland Maturity (game-days) a chunk must have
    /// accumulated before it becomes mist-eligible. Pairs with the
    /// Suitability-pass gate baked into
    /// <see cref="ChunkBiomeSampler.SampleDominantBiome"/>: Suitability
    /// answers "is this chunk currently wet?", and this threshold
    /// answers "has it been wet long enough to <i>earn</i> the
    /// atmospheric mist visual?" A newly-flooded chunk wins Wetland
    /// dominance immediately (Suitability passes) but only attracts
    /// mist after roughly 2.5 days of sustained wetness.</summary>
    private const float MistMinMaturity = 2.5f;

    /// <summary>How long the emission ramps from 0 to the prefab's
    /// authored baseline rate after each instance's spawn time. One
    /// in-game hour gives a visible but unhurried roll-in over a
    /// fraction of the typical mist lifetime.</summary>
    private const float FadeInDuration = 1f / HoursPerDay;

    /// <summary>How long the emission ramps from baseline back to 0
    /// ahead of each instance's despawn time. Symmetric with
    /// <see cref="FadeInDuration"/> for a clean visual cadence; long
    /// enough that in-flight particles at zero-rate have time to die
    /// off naturally before the GameObject itself is destroyed.</summary>
    private const float FadeOutDuration = 1f / HoursPerDay;

    /// <summary>Vertical offset, in world units, the mist cloud's
    /// centre sits ABOVE the water surface (or above the tile surface
    /// when the tile has no water). Tile-height = 1 world unit, so
    /// <c>1.0</c> places the emitter one tile-height up.</summary>
    private const float MistHeightAboveWater = 1.0f;

    /// <summary>Lower bound (exclusive) on the water depth at a candidate
    /// tile's topmost surface for the tile to be mist-eligible. Voxel
    /// units (typical convention: 1.0 = one full tile-height of water).
    /// Tiles below this floor are "dry-ish mud" -- visually the mist
    /// wouldn't read as floating above water, so we skip them. Paired
    /// with <see cref="MistWaterDepthMax"/> to form a band that targets
    /// shallow standing water specifically.</summary>
    private const float MistWaterDepthMin = 0.1f;

    /// <summary>Upper bound (exclusive) on the water depth at a candidate
    /// tile's topmost surface for the tile to be mist-eligible. Tiles
    /// at or above this depth are "open water" (river channels, deeper
    /// lake interiors) where the mist read would compete with the
    /// water surface itself. The band <c>(0.1, 0.5)</c> targets the
    /// marshland sweet spot.</summary>
    private const float MistWaterDepthMax = 0.5f;

    /// <summary>Perf scope for the whole per-tick body (drains + the
    /// once-per-day roll). Fires every sim tick, so it lands in the
    /// Perf window's per-tick table. Mirrors the <c>.Tick</c> naming
    /// the rolling-sweep tickers use so all Keystone tick scopes share
    /// a prefix.</summary>
    private const string TickScope = nameof(WetlandMistDirector) + ".Tick";

    /// <summary>Perf scope for the once-per-day mist schedule roll —
    /// the full-map region/chunk scan in <see cref="RollDailySchedule"/>.
    /// Nested under <see cref="TickScope"/> so the perf window renders it
    /// as a child, and isolated so the daily scan's spike shows up on its
    /// own row (sporadic table, high max) rather than hiding inside the
    /// per-tick average.</summary>
    private const string RollScope = TickScope + ".DailyRoll";

    #endregion

    #region Dependencies

    private readonly IClock _clock;
    private readonly PerfTracker _perf;
    private readonly RegionService _regions;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly TerrainSurveyor _surveyor;
    private readonly IWaterQuery _water;
    private readonly KeystoneDecorationRegistry _decorations;
    private readonly KeystoneAssetService _assets;
    private readonly KeystoneEffectsSettings _settings;

    #endregion

    #region State

    /// <summary>Last day (integer part of <c>TotalDaysElapsed</c>) the
    /// director attempted to roll for. <c>-1</c> means never rolled
    /// (fresh load). The day crosses to a higher value when in-game
    /// time advances past midnight; we wait until time enters the
    /// spawn window before rolling, then mark the day as processed.</summary>
    private int _lastDayRolled = -1;

    /// <summary>Pending spawn events: tile + absolute spawn time + absolute
    /// despawn time. Drained as the clock advances past each
    /// <c>SpawnTime</c>.</summary>
    private readonly List<ScheduledMist> _scheduled = new();

    /// <summary>Live decorations + their scheduled despawn times.
    /// Drained as the clock advances past each <c>DespawnTime</c>.</summary>
    private readonly List<ActiveMist> _active = new();

    /// <summary>Per-roll cache of <c>(region, chunkX, chunkY)</c> →
    /// "is Wetland the chunk's dominant biome." Cleared at the start
    /// of each roll. Avoids redundant
    /// <see cref="ChunkBiomeSampler.SampleDominantBiome"/> calls when
    /// multiple tiles in the same chunk are evaluated.</summary>
    private readonly Dictionary<(RegionId, int, int), bool> _wetlandChunkCache = new();

    /// <summary>Per-tick counter for throttled diagnostic telemetry. We
    /// log renderer state every <see cref="TelemetryEveryNTicks"/> ticks
    /// when verbose mode is on, so the log isn't drowned in updates.</summary>
    private int _telemetryTickCounter;

    /// <summary>Throttle: emit one telemetry line per active mist every
    /// N ticks. Tick rate is variable with game speed; at 1x this is
    /// roughly 1-2 seconds, plenty to see visibility flips.</summary>
    private const int TelemetryEveryNTicks = 20;

    #endregion

    #region Construction

    public WetlandMistDirector(
        IClock clock,
        PerfTracker perf,
        RegionService regions,
        IEcologyFieldQuery fieldQuery,
        IChunkBiomeValues biomeValues,
        TerrainSurveyor surveyor,
        IWaterQuery water,
        KeystoneDecorationRegistry decorations,
        KeystoneAssetService assets,
        KeystoneEffectsSettings settings) {
      _clock = clock;
      _perf = perf;
      _regions = regions;
      _fieldQuery = fieldQuery;
      _biomeValues = biomeValues;
      _surveyor = surveyor;
      _water = water;
      _decorations = decorations;
      _assets = assets;
      _settings = settings;
    }

    #endregion

    #region Tick

    public void Tick() {
      try {
        using (_perf.Track(TickScope)) {
          var now = _clock.TotalDaysElapsed;
          var today = (int)Math.Floor(now);
          var hourFraction = now - today;

          if (today > _lastDayRolled) {
            if (hourFraction >= SpawnWindowStart && hourFraction < SpawnWindowEnd) {
              _lastDayRolled = today;
              // Isolate the once-per-day full-map scan in its own scope so
              // its spike is attributable rather than lost in the per-tick
              // average / inside vanilla Engine.TickWork.
              using (_perf.Track(RollScope)) {
                RollDailySchedule(today, hourFraction);
              }
            } else if (hourFraction >= SpawnWindowEnd) {
              _lastDayRolled = today;
            }
          }

          for (var i = _scheduled.Count - 1; i >= 0; i--) {
            var s = _scheduled[i];
            if (now >= s.SpawnTime) {
              SpawnMistAt(s.Tile, s.SpawnTime, s.DespawnTime);
              _scheduled.RemoveAt(i);
            }
          }

          for (var i = _active.Count - 1; i >= 0; i--) {
            var a = _active[i];
            if (now >= a.DespawnTime) {
              _decorations.Despawn(a.Decoration);
              _active.RemoveAt(i);
              continue;
            }
            UpdateEmissionRamp(a, now);
          }

          EmitTelemetryIfDue();
        }
      } catch (Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorOnce(
            "WetlandMistDirector.Tick", "Subsystem failed", ex, ref _tickFailureLogged);
      }
    }

    private bool _tickFailureLogged;

    /// <summary>Dev-mode-only diagnostic. For each live mist instance,
    /// logs the renderer's visibility flag (Unity's "is this on screen"
    /// answer), its world-space AABB, current alive particle count,
    /// the GameObject's layer, and the active camera's name. Used to
    /// diagnose the "whole cloud vanishes/reappears as the camera
    /// rotates" symptom by capturing what the engine sees when the
    /// flicker happens.</summary>
    private void EmitTelemetryIfDue() {
      if (!KeystoneLog.IsVerbose) return;
      if (_active.Count == 0) return;
      _telemetryTickCounter++;
      if (_telemetryTickCounter % TelemetryEveryNTicks != 0) return;

      var camera = UnityEngine.Camera.main;
      var cameraName = camera != null ? camera.name : "<none>";
      for (var i = 0; i < _active.Count; i++) {
        var a = _active[i];
        var psr = a.Renderer;
        var ps = a.ParticleSystem;
        if (psr == null || ps == null) continue;
        var b = psr.bounds;
        KeystoneLog.Verbose(
            $"[Keystone] Mist@{a.Decoration.Tile}: " +
            $"isVisible={psr.isVisible} " +
            $"bounds.center={b.center} bounds.size={b.size} " +
            $"alive={ps.particleCount} " +
            $"layer={a.Decoration.Root.layer} " +
            $"cam={cameraName}");
      }
    }

    /// <summary>Lerps the live instance's emission rate from 0 up to
    /// its captured baseline and back down to 0 across its scheduled
    /// life. The triangular factor <c>min(fadeIn, fadeOut, 1)</c> handles
    /// overlapping fades (short lifetimes) gracefully -- the curve
    /// stays continuous and never exceeds 1.</summary>
    private static void UpdateEmissionRamp(ActiveMist a, float now) {
      if (a.ParticleSystem == null) return;
      var fadeIn = (now - a.SpawnTime) / FadeInDuration;
      var fadeOut = (a.DespawnTime - now) / FadeOutDuration;
      var factor = Mathf.Clamp01(Mathf.Min(fadeIn, fadeOut, 1f));
      var emission = a.ParticleSystem.emission;
      emission.rateOverTime = a.BaselineRate * factor;
    }

    #endregion

    #region Daily roll

    private void RollDailySchedule(int day, float currentHourFraction) {
      _wetlandChunkCache.Clear();

      // Day-level binary gate. Independent deterministic seed (day only)
      // so it can't correlate with per-tile rolls below. On a failed
      // roll we short-circuit: no chunks walked, nothing scheduled.
      // Threshold is player-tunable via the "Foggy mornings" slider:
      // probability = 1 / FoggyMorningDays, so slider 1 = every day,
      // 4 = the default 25%, 7 = roughly once a week. Re-read each day
      // so mid-game slider changes take effect on the next roll.
      if (!MistScheduleRoll.ShouldRollToday(day, _settings.FoggyMorningProbability)) return;

      var scheduledCount = 0;
      var skippedPast = 0;

      foreach (var region in _regions.All) {
        if (region.IsSettled) continue;
        var field = _fieldQuery.FieldFor(region.Id);
        if (field == null) continue;

        const int chunkSize = RegionEcologyField.ChunkSize;
        var originChunkX = field.OriginX / chunkSize;
        var originChunkY = field.OriginY / chunkSize;

        for (var cy = 0; cy < field.ChunksY; cy++) {
          for (var cx = 0; cx < field.ChunksX; cx++) {
            var chunkX = originChunkX + cx;
            var chunkY = originChunkY + cy;
            if (!IsChunkWetland(region.Id, field, chunkX, chunkY)) continue;

            // Cap: at most one mist per chunk. Walk tiles in row-major
            // order; the first tile that passes the neighbour + RNG
            // gates claims the chunk's slot, and the rest of the chunk
            // is skipped this roll. With a deterministic per-tile seed
            // the same tile wins on a reload at the same day.
            var chunkClaimed = false;
            for (var ty = 0; ty < chunkSize && !chunkClaimed; ty++) {
              for (var tx = 0; tx < chunkSize && !chunkClaimed; tx++) {
                var x = chunkX * chunkSize + tx;
                var y = chunkY * chunkSize + ty;
                if (TryScheduleTile(day, currentHourFraction, x, y)) {
                  scheduledCount++;
                  chunkClaimed = true;
                }
              }
            }
          }
        }
      }

      KeystoneLog.Verbose(
          $"[Keystone] WetlandMistDirector: day {day} roll scheduled " +
          $"{scheduledCount} mist instance(s) (capped at 1 per chunk).");
    }

    /// <summary>Roll for a single tile and append a
    /// <see cref="ScheduledMist"/> if it passes every gate. Returns
    /// <c>true</c> iff a mist was actually scheduled at this tile
    /// (caller uses this to claim the chunk's one-mist slot).</summary>
    private bool TryScheduleTile(int day, float currentHourFraction, int x, int y) {
      // 4-neighbour gate: all four cardinal neighbours must also be in
      // Wetland-dominant chunks. Interior tiles of a Wetland chunk pass
      // trivially; edge tiles of a Wetland chunk pass only if the
      // adjacent chunk is also Wetland.
      if (!IsTileWetlandChunk(x - 1, y)) return false;
      if (!IsTileWetlandChunk(x + 1, y)) return false;
      if (!IsTileWetlandChunk(x, y - 1)) return false;
      if (!IsTileWetlandChunk(x, y + 1)) return false;

      // Pure deterministic-RNG decision: density gate + spawn/despawn
      // time computation + day-wrap + mid-window-load skip. Lives in
      // Core MistScheduleRoll so the determinism contract is
      // unit-testable.
      var times = MistScheduleRoll.TryRollTile(
          day, x, y,
          SpawnWindowStart, SpawnWindowEnd,
          DespawnWindowStart, DespawnWindowEnd,
          MistDensity, currentHourFraction);
      if (times == null) return false;

      // Find a surface to place the mist on -- topmost in the column.
      // Skip tiles with no surface (off-map / inside-cliff).
      var heights = _surveyor.ColumnSurfaceHeights(new TileCoord(x, y));
      if (heights.Count == 0) return false;
      var z = heights[heights.Count - 1];

      // Water-depth gate. Mist should only ride on shallow standing
      // water: deep enough that the read is "above the water surface"
      // (>0.1), shallow enough that the tile isn't a river channel or
      // lake interior where the mist would compete with open water
      // visually (<0.5). Tiles that are dry mud (depth ~0) or open
      // water (depth >= 0.5) within an otherwise mist-eligible
      // Wetland-interior chunk are excluded.
      var waterDepth = _water.WaterDepthAt(new SurfaceCoord(x, y, z));
      if (waterDepth <= MistWaterDepthMin || waterDepth >= MistWaterDepthMax) {
        return false;
      }

      _scheduled.Add(new ScheduledMist {
          Tile = new Vector3Int(x, y, z),
          SpawnTime = times.Value.SpawnTime,
          DespawnTime = times.Value.DespawnTime,
      });
      return true;
    }

    /// <summary>Cached "is this chunk Wetland-dominant in this region's
    /// field?" Caches per <c>(RegionId, chunkX, chunkY)</c>. The chunk
    /// argument is in absolute (not field-relative) coordinates;
    /// out-of-field chunks resolve to <c>false</c>.</summary>
    private bool IsChunkWetland(RegionId regionId, RegionEcologyField field, int chunkX, int chunkY) {
      var key = (regionId, chunkX, chunkY);
      if (_wetlandChunkCache.TryGetValue(key, out var cached)) return cached;

      const int chunkSize = RegionEcologyField.ChunkSize;
      var originChunkX = field.OriginX / chunkSize;
      var originChunkY = field.OriginY / chunkSize;
      if (chunkX < originChunkX || chunkX >= originChunkX + field.ChunksX
          || chunkY < originChunkY || chunkY >= originChunkY + field.ChunksY) {
        _wetlandChunkCache[key] = false;
        return false;
      }

      const float centreOffset = (chunkSize - 1) * 0.5f;
      var centreTileX = chunkX * chunkSize + centreOffset;
      var centreTileY = chunkY * chunkSize + centreOffset;
      // Two gates layered. SampleDominantBiome bakes in the
      // Suitability-pass check + max-Suitability tiebreak, so
      // dominant==Wetland already means "Wetland Suitability passes
      // and is the highest passing Suitability here." The
      // Maturity ≥ MistMinMaturity check on top of that adds the
      // "been wet long enough to earn the mist" qualifier the visual
      // is a reward for; freshly-flooded chunks win dominance but
      // wait for Maturity to accrue before mist starts appearing.
      var (dominant, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          _biomeValues, regionId,
          field.OriginX, field.OriginY,
          field.ChunksX, field.ChunksY,
          centreTileX, centreTileY);
      var isWetland = dominant == BiomeKind.Wetland
          && maturity >= MistMinMaturity;
      _wetlandChunkCache[key] = isWetland;
      return isWetland;
    }

    /// <summary>Resolve the region containing the topmost surface in
    /// the tile's column, then check that region's chunk-at-tile
    /// dominant biome. Returns <c>false</c> for tiles with no surface,
    /// no region, or no ecology field for the resolved region.</summary>
    private bool IsTileWetlandChunk(int tileX, int tileY) {
      var heights = _surveyor.ColumnSurfaceHeights(new TileCoord(tileX, tileY));
      if (heights.Count == 0) return false;
      var z = heights[heights.Count - 1];
      var region = _regions.Containing(new SurfaceCoord(tileX, tileY, z));
      if (region == null) return false;
      var field = _fieldQuery.FieldFor(region.Id);
      if (field == null) return false;

      const int chunkSize = RegionEcologyField.ChunkSize;
      var chunkX = (int)Math.Floor(tileX / (float)chunkSize);
      var chunkY = (int)Math.Floor(tileY / (float)chunkSize);
      return IsChunkWetland(region.Id, field, chunkX, chunkY);
    }

    #endregion

    #region Spawn / despawn

    private void SpawnMistAt(Vector3Int tile, float spawnTime, float despawnTime) {
      var prefab = _assets.GroundFogPrefab;
      if (prefab == null) {
        KeystoneLog.Error(
            "[Keystone] WetlandMistDirector: GroundFogPrefab is null; " +
            "skipping spawn. See preceding KeystoneAssetService log.");
        return;
      }
      try {
        var go = UnityEngine.Object.Instantiate(prefab);
        go.name = $"Keystone.MorningMist.{tile}";

        // Defensive prewarm-suppression. The Ground Fog prefab has
        // been corrected at source (`prewarm: 0` in the .prefab YAML),
        // but bundles built before that fix still spawn pre-populated
        // -- and any future custom fog prefab may set prewarm=1 by
        // accident. The runtime dance (stop+clear, disable prewarm,
        // zero rateOverTime, replay) guarantees a clean empty start
        // regardless of bundle state. With a correct prefab the
        // Stop+Clear is a cheap no-op against an empty system.
        //
        // Apply to every ParticleSystem in the hierarchy (compound
        // emitters are common for fog effects); pick the first one's
        // authored rate as the "baseline" for the ramp factor.
        var systems = go.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
        var baselineRate = 0f;
        var ps = systems.Length > 0 ? systems[0] : null;
        if (ps != null) {
          baselineRate = ps.emission.rateOverTime.constant;
        }
        for (var i = 0; i < systems.Length; i++) {
          var s = systems[i];
          s.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
          var main = s.main;
          main.prewarm = false;
          var em = s.emission;
          em.rateOverTime = 0f;
          s.Play();
          // Disable dynamic occlusion culling. The renderer's auto-bounds
          // collapses to a small AABB at the emitter (especially during
          // the rate=0 fade-in), and Timberborn's static block objects
          // bake into Unity's occlusion data -- the small AABB lands
          // entirely inside an occluded region from many camera angles
          // and the whole renderer is skipped, producing all-or-nothing
          // visibility flicker as the camera rotates. Disabling the
          // dynamic-occlusion test leaves frustum culling intact and
          // does not affect simulation cost (cullingMode still pauses
          // the sim off-screen).
          var psr = s.GetComponent<ParticleSystemRenderer>();
          if (psr != null) psr.allowOcclusionWhenDynamic = false;
        }

        var decoration = _decorations.RegisterExisting(go, tile, controller: null);
        // RegisterExisting plants the transform at the tile's world
        // position (terrain-level). Lift the mist clear of the water
        // surface (depth + MistHeightAboveWater); on dry land the depth
        // is 0 and we lift by MistHeightAboveWater alone. See the
        // MistHeightAboveWater docstring for why we need the offset to
        // not straddle the water surface.
        var waterDepth = _water.WaterDepthAt(new SurfaceCoord(tile.x, tile.y, tile.z));
        var lift = waterDepth + MistHeightAboveWater;
        go.transform.position += new Vector3(0f, lift, 0f);

        _active.Add(new ActiveMist {
            Decoration = decoration,
            ParticleSystem = ps,
            Renderer = ps != null ? ps.GetComponent<ParticleSystemRenderer>() : null,
            BaselineRate = baselineRate,
            SpawnTime = spawnTime,
            DespawnTime = despawnTime,
        });
      } catch (Exception ex) {
        KeystoneLog.Error(
            $"[Keystone] WetlandMistDirector: spawn at {tile} threw: " +
            $"{ex.GetType().Name}: {ex.Message}");
      }
    }

    #endregion

    #region Dev placement

    /// <summary>Dev entry point for the toolbar Class-A placer. Spawns
    /// a mist instance at <paramref name="tile"/> using the same
    /// <see cref="SpawnMistAt"/> setup as the auto-roll path (prewarm
    /// suppression, occlusion-culling override, fade-in/out ramp), so
    /// manual placement exercises the same code under test. Refuses
    /// to place if there's already an active or scheduled mist at
    /// <paramref name="tile"/> (returns <c>false</c>).
    ///
    /// <para>Lifetime: 1 in-game day, with a 30-minute fade-in and
    /// 30-minute fade-out. Long enough to orbit the camera around
    /// while iterating on render fixes; short enough that abandoned
    /// dev-placed mist eventually clears itself if the test session
    /// runs long.</para>
    /// </summary>
    public bool PlaceTestMist(Vector3Int tile) {
      for (var i = 0; i < _active.Count; i++) {
        if (_active[i].Decoration.Tile == tile) return false;
      }
      for (var i = 0; i < _scheduled.Count; i++) {
        if (_scheduled[i].Tile == tile) return false;
      }
      var now = (float)_clock.TotalDaysElapsed;
      SpawnMistAt(tile, spawnTime: now, despawnTime: now + 1f);
      return true;
    }

    #endregion

    #region Nested types

    private struct ScheduledMist {
      public Vector3Int Tile;
      public float SpawnTime;
      public float DespawnTime;
    }

    private struct ActiveMist {
      public KeystoneDecoration Decoration;
      public ParticleSystem? ParticleSystem;
      public ParticleSystemRenderer? Renderer;
      public float BaselineRate;
      public float SpawnTime;
      public float DespawnTime;
    }

    #endregion

  }

}
