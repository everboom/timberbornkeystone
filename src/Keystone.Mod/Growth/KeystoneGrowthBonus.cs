using System;
using Keystone.Core.Biomes;
using Keystone.Mod.Diagnostics;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Growth;
using Keystone.Core.Tiles;
using Keystone.Mod.Settings;
using Keystone.Mod.Survey;
using Timberborn.BlockSystem;
using Timberborn.Fields;
using Timberborn.Forestry;
using Timberborn.Planting;
using Timberborn.Growing;
using Timberborn.NaturalResources;
using Timberborn.NaturalResourcesMoisture;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;

namespace Keystone.Mod.Growth {

  /// <summary>
  /// Per-entity tickable that grants a growth-speed bonus to
  /// natural-resource plants in qualifying biomes. Trees benefit
  /// from Forest biome health; aquatic plants from Wetland; land
  /// crops from Grassland. The land-crop path is intentionally narrow
  /// (non-aquatic <c>CropSpec</c> plants the active faction can sow —
  /// <c>PlantableSpec</c> present) to keep the footprint minimal and
  /// exclude wild bushes, modded non-crops, water crops, and
  /// cross-faction / faction-disabled crops. See
  /// <see cref="StartTickable"/> for the gate and its coupling notes.
  ///
  /// <para>Attached via <c>AddDecorator&lt;GrowableSpec,
  /// KeystoneGrowthBonus&gt;</c> so every <c>Growable</c> entity
  /// gets one. Entities that don't qualify (wild gatherable bushes,
  /// buildings, non-matching biome) set <see cref="_targetBiome"/> to
  /// null at startup and early-out from <see cref="Tick"/> for
  /// zero ongoing cost.</para>
  ///
  /// <para><b>Cadence.</b> The biome check runs 2–3 times per game
  /// day (every <see cref="CheckIntervalTicks"/> ticks). The 128-
  /// bucket tick distribution naturally staggers entities across
  /// frames.</para>
  ///
  /// <para><b>Bonus formula.</b> 50/50 blend of chunk Suitability
  /// (fast, rewards good conditions now) and cluster average
  /// Maturity fraction (slow, rewards sustained ecosystem). See
  /// <see cref="GrowthBonusCalculator"/>.</para>
  /// </summary>
  public sealed class KeystoneGrowthBonus : TickableComponent {

    #region Constants

    /// <summary>Tick-counter cadence for biome checks. ~800 ticks
    /// ≈ 3 checks per game day at the default tick rate.</summary>
    private const int CheckIntervalTicks = 800;

    #endregion

    #region Injected services

    private readonly IDayNightCycle _dayNightCycle;
    private readonly KeystoneSurveyor _surveyor;
    private readonly ChunkClusterIndex _clusterIndex;
    private readonly IChunkBiomeValues _biomeValues;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly ChunkBiomeAdapter _adapter;
    private readonly KeystoneFloraSettings _floraSettings;

    #endregion

    #region Per-instance state

    private Growable? _growable;
    private BlockObject? _blockObject;
    private BiomeKind? _targetBiome;
    private float _maturityCeiling;
    private float _lastCheckDayNumber;
    private int _tickCounter;
    private float _currentBonus;
    private float _currentSuitability;
    private float _currentMaturityFraction;
    private float _currentClusterMaturityFraction;
    private float _totalProgressAdded;

    #endregion

    #region Public API (read by KeystoneGrowthBonusDescriber)

    /// <summary>The biome whose health drives this plant's bonus,
    /// or <c>null</c> when the entity doesn't qualify (crop, no
    /// matching biome, non-NaturalResource).</summary>
    public BiomeKind? TargetBiome => _targetBiome;

    /// <summary>Last-computed bonus fraction in [0, MaxBonus].
    /// Updated every <see cref="CheckIntervalTicks"/> ticks while
    /// the plant is still growing; stays at its last value once
    /// fully grown.</summary>
    public float CurrentBonus => _currentBonus;

    /// <summary>True when this entity qualifies for a growth bonus
    /// (NaturalResource + Growable + matching target biome).</summary>
    public bool IsActive => _targetBiome != null;

    /// <summary>Last-computed chunk suitability of the target biome
    /// at this tile, in [0, 1].</summary>
    public float CurrentSuitability => _currentSuitability;

    /// <summary>Last-computed maturity fraction (maturity / ceiling)
    /// for the target biome, in [0, 1].</summary>
    public float CurrentMaturityFraction => _currentMaturityFraction;

    /// <summary>Cumulative normalised progress added via
    /// <c>IncreaseGrowthProgress</c> since entity creation. Not
    /// persisted — resets on load. Temporary testing readout.</summary>
    public float TotalProgressAdded => _totalProgressAdded;

    /// <summary>The player-configured max bonus fraction from the mod
    /// settings slider, read live each tick.</summary>
    public float ConfiguredMaxBonus => _floraSettings.GrowthBonusPercent.Value / 100f;

    #endregion

    #region Construction

    public KeystoneGrowthBonus(
        IDayNightCycle dayNightCycle,
        KeystoneSurveyor surveyor,
        ChunkClusterIndex clusterIndex,
        IChunkBiomeValues biomeValues,
        IEcologyFieldQuery fieldQuery,
        ChunkBiomeAdapter adapter,
        KeystoneFloraSettings floraSettings) {
      _dayNightCycle = dayNightCycle;
      _surveyor = surveyor;
      _clusterIndex = clusterIndex;
      _biomeValues = biomeValues;
      _fieldQuery = fieldQuery;
      _adapter = adapter;
      _floraSettings = floraSettings;
    }

    #endregion

    #region Lifecycle

    public override void StartTickable() {
      try {
        _growable = GetComponent<Growable>();
        _blockObject = GetComponent<BlockObject>();

        if (_growable == null || _blockObject == null) {
          _targetBiome = null;
          return;
        }

        if (GetComponent<NaturalResourceSpec>() == null) {
          _targetBiome = null;
          return;
        }

        var floodable = GetComponent<FloodableNaturalResourceSpec>();
        var isAquatic = floodable != null && floodable.MinWaterHeight > 0;
        var isTree = GetComponent<TreeComponentSpec>() != null;

        // Land-crop -> Grassland is deliberately the narrowest of the three
        // biome paths, to keep this feature's footprint minimal. A plant
        // qualifies as a land crop only when ALL of these hold:
        //   * CropSpec present  -- it is a farmed crop, not a wild gatherable
        //     bush or a modded growable that merely carries GrowableSpec /
        //     NaturalResourceSpec but isn't intended as a crop.
        //   * NOT aquatic       -- water crops (FloodableNaturalResourceSpec
        //     MinWaterHeight > 0) route to Wetland via the aquatic branch and
        //     must never reach Grassland.
        //   * PlantableSpec present -- the ACTIVE faction can actually sow it.
        //     TemplateCollectionServicePatch strips PlantableSpec from crops
        //     this faction can't plant (cross-faction donors, faction-disabled
        //     content), so requiring it excludes plants not enabled for this
        //     faction.
        // Trees intentionally do NOT get the PlantableSpec gate: Keystone
        // places cross-faction trees as wild Forest content on purpose, and
        // those should keep their Forest bonus.
        // COUPLING: the PlantableSpec requirement leans on that strip patch.
        // If the strip ever stops removing PlantableSpec for unsupported
        // crops, revisit this gate.
        var isCrop = !isAquatic
            && GetComponent<CropSpec>() != null
            && GetComponent<PlantableSpec>() != null;

        _targetBiome = GrowthBonusCalculator.TargetBiome(isAquatic, isTree, isCrop);

        if (_targetBiome != null) {
          _maturityCeiling = MaturityParameters.Ceiling(_targetBiome.Value);
        }

        _lastCheckDayNumber = _dayNightCycle.PartialDayNumber;
        _tickCounter = _blockObject.Coordinates.GetHashCode() & 0x7FFF;
        _tickCounter %= CheckIntervalTicks;

        if (_targetBiome != null) {
          RefreshBiomeState();
        }
      } catch (Exception ex) {
        _targetBiome = null;
        KeystoneLog.Error($"[Keystone] KeystoneGrowthBonus on '{Name}': " +
            $"initialization failed, component disabled. {ex}");
      }
    }

    #endregion

    #region Tick

    public override void Tick() {
      if (_targetBiome == null) return;

      if (++_tickCounter < CheckIntervalTicks) return;
      _tickCounter = 0;

      var currentDay = _dayNightCycle.PartialDayNumber;
      var intervalDays = currentDay - _lastCheckDayNumber;
      _lastCheckDayNumber = currentDay;
      if (intervalDays <= 0f) return;

      RefreshBiomeState();

      // Only apply growth progress while still growing.
      if (_growable!.IsGrown) return;
      if (_currentBonus <= 0f) return;

      var progressToAdd = _currentBonus * intervalDays / _growable.GrowthTimeInDays;
      _growable.IncreaseGrowthProgress(progressToAdd);
      _totalProgressAdded += progressToAdd;
    }

    /// <summary>Recompute suitability, maturity, and bonus from the
    /// current ecology state. Called on tick cadence and also on demand
    /// when the entity panel is open. The richer panel diagnostics
    /// (dominant-biome reads, canopy state) are computed separately in
    /// <see cref="ComputeSignals"/> so they stay off this hot path.</summary>
    public void RefreshBiomeState() {
      var coords = _blockObject!.Coordinates;
      var surface = new SurfaceCoord(coords.x, coords.y, coords.z);
      var region = _surveyor.Regions.Containing(surface);
      if (region == null) return;

      var biome = _targetBiome!.Value;

      // --- Suitability (chunk-level) ---
      var field = _fieldQuery.FieldFor(region.Id);
      float suitability = 0f;
      if (field != null) {
        suitability = ChunkBiomeSampler.SampleSuitability(
            _biomeValues, region.Id, biome,
            field.OriginX, field.OriginY,
            field.ChunksX, field.ChunksY,
            coords.x, coords.y);
      }

      // --- Maturity (max of cluster average and chunk local) ---
      float chunkMaturity = 0f;
      if (field != null) {
        chunkMaturity = ChunkBiomeSampler.SampleMaturity(
            _biomeValues, region.Id, biome,
            field.OriginX, field.OriginY,
            field.ChunksX, field.ChunksY,
            coords.x, coords.y);
      }
      float clusterMaturity = 0f;
      var chunkX = FloorDiv(coords.x, RegionEcologyField.ChunkSize);
      var chunkY = FloorDiv(coords.y, RegionEcologyField.ChunkSize);
      var clusterId = _clusterIndex.ClusterFor(region.Id, chunkX, chunkY);
      if (clusterId.HasValue
          && _clusterIndex.BiomeFor(clusterId.Value) == biome) {
        clusterMaturity = _clusterIndex.AverageMaturity(clusterId.Value);
      }
      var maturity = System.Math.Max(chunkMaturity, clusterMaturity);

      // --- Bonus ---
      _currentSuitability = suitability;
      _currentMaturityFraction = _maturityCeiling > 0f
          ? System.Math.Min(1f, maturity / _maturityCeiling) : 0f;
      _currentClusterMaturityFraction = _maturityCeiling > 0f
          ? System.Math.Min(1f, clusterMaturity / _maturityCeiling) : 0f;
      _currentBonus = GrowthBonusCalculator.ComputeBonus(
          suitability, maturity, _maturityCeiling,
          _floraSettings.GrowthBonusPercent.Value / 100f);
    }

    /// <summary>
    /// Assemble the full <see cref="GrowthSignals"/> bundle that drives
    /// the entity panel's verdict line and hover tooltip. UI-only: called
    /// from the open panel (once per frame for one selected entity), never
    /// from <see cref="Tick"/>.
    ///
    /// <para>Reads the cached suitability/maturity/bonus that
    /// <see cref="RefreshBiomeState"/> last computed, plus two extra
    /// bilinear dominant-biome samples ("what's established here" /
    /// "what do conditions look like") and, for Forest, the on-demand
    /// canopy state from <see cref="GetForestCanopyInfo"/>. Call
    /// <see cref="RefreshBiomeState"/> first.</para>
    /// </summary>
    public GrowthSignals ComputeSignals() {
      var biome = _targetBiome ?? BiomeKind.Forest;

      BiomeKind? dominantByMaturity = null;
      var dominantMaturityFraction = 0f;
      BiomeKind? dominantBySuitability = null;

      if (_blockObject != null) {
        var coords = _blockObject.Coordinates;
        var surface = new SurfaceCoord(coords.x, coords.y, coords.z);
        var region = _surveyor.Regions.Containing(surface);
        var field = region != null ? _fieldQuery.FieldFor(region.Id) : null;
        if (region != null && field != null) {
          var (matBiome, matValue) = ChunkBiomeSampler.SampleDominantByMaturity(
              _biomeValues, region.Id,
              field.OriginX, field.OriginY, field.ChunksX, field.ChunksY,
              coords.x, coords.y);
          dominantByMaturity = matBiome;
          if (matBiome.HasValue) {
            var ceiling = MaturityParameters.Ceiling(matBiome.Value);
            dominantMaturityFraction = ceiling > 0f
                ? System.Math.Min(1f, matValue / ceiling) : 0f;
          }
          var (suitBiome, _) = ChunkBiomeSampler.SampleDominantBiome(
              _biomeValues, region.Id,
              field.OriginX, field.OriginY, field.ChunksX, field.ChunksY,
              coords.x, coords.y);
          dominantBySuitability = suitBiome;
        }
      }

      var canopy = GetForestCanopyInfo();
      var configuredMax = ConfiguredMaxBonus;

      return new GrowthSignals {
          TargetBiome = biome,
          Suitability = _currentSuitability,
          MaturityFraction = _currentMaturityFraction,
          ClusterMaturityFraction = _currentClusterMaturityFraction,
          BonusFraction = configuredMax > 0f ? _currentBonus / configuredMax : 0f,
          DominantByMaturity = dominantByMaturity,
          DominantMaturityFraction = dominantMaturityFraction,
          DominantBySuitability = dominantBySuitability,
          MatureCanopyGate = canopy.MatureCanopyGate,
          WouldBeForestFavorable = canopy.WouldBeForestFavorable,
      };
    }

    /// <summary>Forest mature-canopy facts for the tooltip and verdict,
    /// or a "no data" value for non-Forest targets / unresolved chunks.</summary>
    private readonly struct ForestCanopyInfo {
      /// <summary>Mature-canopy gate [0, 1], or a negative sentinel when
      /// not applicable (non-Forest, or chunk inputs unavailable).</summary>
      public float MatureCanopyGate { get; init; }
      /// <summary>Whether the un-gated Forest score is already favorable
      /// (a dense, diverse planting) — i.e. once the canopy matures the
      /// chunk reads as real Forest.</summary>
      public bool WouldBeForestFavorable { get; init; }

      public static ForestCanopyInfo None =>
          new ForestCanopyInfo { MatureCanopyGate = -1f, WouldBeForestFavorable = false };
    }

    /// <summary>Rebuild the plant's chunk <see cref="ChunkBiomeInputs"/>
    /// on demand through <see cref="ChunkBiomeAdapter"/> and read the
    /// mature-canopy gate + the un-gated-Forest-vs-Monoculture comparison.
    /// The gate isn't a persisted channel, so it can't come from the value
    /// store. Forest-only; returns <see cref="ForestCanopyInfo.None"/>
    /// otherwise. UI-only path (one marks-rect scan per call), kept off
    /// <see cref="Tick"/>.</summary>
    private ForestCanopyInfo GetForestCanopyInfo() {
      if (_targetBiome != BiomeKind.Forest || _blockObject == null)
        return ForestCanopyInfo.None;
      var coords = _blockObject.Coordinates;
      var surface = new SurfaceCoord(coords.x, coords.y, coords.z);
      var region = _surveyor.Regions.Containing(surface);
      if (region == null) return ForestCanopyInfo.None;
      var field = _fieldQuery.FieldFor(region.Id);
      if (field == null) return ForestCanopyInfo.None;
      var localCx = FloorDiv(coords.x, RegionEcologyField.ChunkSize)
          - field.OriginX / RegionEcologyField.ChunkSize;
      var localCy = FloorDiv(coords.y, RegionEcologyField.ChunkSize)
          - field.OriginY / RegionEcologyField.ChunkSize;
      if (localCx < 0 || localCx >= field.ChunksX
          || localCy < 0 || localCy >= field.ChunksY
          || !field.ChunkValid(localCx, localCy)) {
        return ForestCanopyInfo.None;
      }
      var inputs = _adapter.Build(field, localCx, localCy);
      // "Would be a forest once grown" = the un-gated score is genuinely
      // FAVORABLE (dense + diverse), not merely greater than a near-zero
      // Monoculture. The favorability bar excludes lone/sparse trees (whose
      // small positive score would otherwise flicker against ~0 Monoculture
      // and falsely read as "establishing") while still catching real dense
      // diverse young plantings. (1 - Monoculture) is already folded into
      // ForestUngated, so a managed monoculture can't clear the bar either.
      return new ForestCanopyInfo {
          MatureCanopyGate = BiomeTargets.MatureCanopyGate(inputs),
          WouldBeForestFavorable =
              BiomeTargets.ForestUngated(inputs) >= GrowthDiagnostics.SuitabilityFavorable,
      };
    }

    #endregion

    #region Helpers

    private static int FloorDiv(int a, int b) =>
        a >= 0 ? a / b : (a - b + 1) / b;

    #endregion

  }

}
