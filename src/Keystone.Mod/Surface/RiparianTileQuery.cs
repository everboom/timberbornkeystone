using Keystone.Core.Biomes;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;

namespace Keystone.Mod.Surface {

  /// <summary>
  /// Computes a surface's per-tile riparian dominance inputs -- the
  /// <c>(suitability, maturity)</c> pair the per-tile
  /// <c>ChunkBiomeSampler.SampleDominantBiome</c> folds into its argmax.
  /// Single owner of the "clean near-water" definition on the read side,
  /// shared by <see cref="Recipes.ChunkRulesApplier"/> (content dispatch)
  /// and the tile debug panel so the two can't disagree.
  ///
  /// <list type="bullet">
  ///   <item><b>Suitability</b> -- the binary clean-near-water indicator
  ///   (<c>1</c> within the near-water band and not toxic, else <c>0</c>),
  ///   on the same <c>[0,1]</c> scale as the per-chunk suitabilities.
  ///   Mirrors the accrual's gate (<see cref="RiparianMaturityParameters.IsNearWater"/>
  ///   + badwater/contamination), so dominance and accrual agree on what
  ///   "riparian conditions" means.</item>
  ///   <item><b>Maturity</b> -- the per-tile R from
  ///   <see cref="SurfaceFieldStore"/>, carried only when the tile
  ///   qualifies (it's irrelevant when suitability is 0, so the store
  ///   read is skipped off-shoreline).</item>
  /// </list>
  ///
  /// <para><b>Hot path.</b> Called once per surface in the rules sweep,
  /// so it early-outs cheaply: a single stored-water-distance read +
  /// near-water test rejects the common inland tile before any toxic
  /// probe or store lookup.</para>
  /// </summary>
  public sealed class RiparianTileQuery {

    #region Fields

    private readonly SurfaceFieldStore _surfaceFields;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly TileSlotRegistry _tileSlots;
    private readonly IWaterQuery _water;
    private readonly IContaminationQuery _contamination;

    private int _waterDistanceSlot = -1;

    #endregion

    #region Construction

    public RiparianTileQuery(
        SurfaceFieldStore surfaceFields,
        IEcologyFieldQuery fieldQuery,
        TileSlotRegistry tileSlots,
        IWaterQuery water,
        IContaminationQuery contamination) {
      _surfaceFields = surfaceFields;
      _fieldQuery = fieldQuery;
      _tileSlots = tileSlots;
      _water = water;
      _contamination = contamination;
    }

    #endregion

    #region Query

    /// <summary>
    /// Riparian's per-tile dominance inputs for <paramref name="surface"/>
    /// in <paramref name="region"/>. Returns <c>(0, 0)</c> -- riparian
    /// out of contention -- for any tile that isn't clean near-water,
    /// without touching the maturity store.
    /// </summary>
    public (float Suitability, float Maturity) Sample(RegionId region, SurfaceCoord surface) {
      var tileData = _fieldQuery.TileDataFor(region);
      var slot = WaterDistanceSlot();
      if (tileData == null || slot < 0 || !tileData.Contains(surface.X, surface.Y)) {
        return (0f, 0f);
      }

      var waterDistance = tileData.Get(surface.X, surface.Y, slot);
      if (!RiparianMaturityParameters.IsNearWater(waterDistance)) {
        return (0f, 0f);
      }

      // Near water -- now the (rarer) toxic probe decides whether the
      // conditions are clean enough for riparian to claim the tile.
      var toxic = _contamination.IsContaminatedAt(surface)
          || _water.WaterContaminationAt(surface) >= WaterContamination.Threshold;
      if (toxic) return (0f, 0f);

      // Clean near-water: riparian qualifies (suitability 1). Carry its
      // accumulated maturity so the sampler can return it when riparian
      // wins.
      var maturity = 0f;
      if (_surfaceFields.TryResolveSurfaceIndex(surface.X, surface.Y, surface.Z, out var index3D)) {
        maturity = _surfaceFields.GetAt(SurfaceField.RiparianMaturity, index3D);
      }
      return (1f, maturity);
    }

    #endregion

    #region Internals

    /// <summary>The water-distance tile slot ordinal, resolved lazily on
    /// first use (the slot is registered during EcologyFieldUpdater's
    /// PostLoad and the registry frozen before the first tick). Returns
    /// -1 until the slot exists.</summary>
    private int WaterDistanceSlot() {
      if (_waterDistanceSlot < 0) {
        _waterDistanceSlot = _tileSlots.TryOrdinalFor("keystone.tile.waterDistance") ?? -1;
      }
      return _waterDistanceSlot;
    }

    #endregion

  }

}
