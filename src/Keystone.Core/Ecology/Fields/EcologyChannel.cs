namespace Keystone.Core.Ecology.Fields {

  /// <summary>
  /// Fixed scalar channels carried by every <see cref="RegionEcologyField"/>.
  /// Per-blueprint plant channels are addressed by integer index, not via
  /// this enum -- plants are dynamic (catalogued at mod load) and can't be
  /// hardcoded.
  ///
  /// <para>The integer values are part of the type's stable layout: the
  /// field's backing array indexes by channel ordinal. Don't reorder.</para>
  /// </summary>
  public enum EcologyChannel {

    /// <summary>Chunk-mean of <c>SurfaceSurvey.WaterDepth</c> (raw tile-units, continuous).</summary>
    WaterDepth = 0,

    /// <summary>Chunk-mean of <c>SurfaceSurvey.Flow.Magnitude</c> (raw tile-units, continuous).</summary>
    WaterFlowMagnitude = 1,

    /// <summary>
    /// Chunk-fraction of in-region tiles whose
    /// <c>SurfaceSurvey.IsMoist</c> is true. 0..1. The boolean predicate
    /// is what Timberborn itself uses to decide "is this tile moist
    /// enough to support flora life," so this channel matches the
    /// game's own threshold rather than us inventing one.
    /// </summary>
    Moisture = 2,

    /// <summary>
    /// Chunk-fraction of in-region tiles whose
    /// <c>SurfaceSurvey.IsContaminated</c> is true. 0..1. Same boolean-
    /// predicate logic as <see cref="Moisture"/>.
    /// </summary>
    Contamination = 3,

    /// <summary>
    /// Chunk-fraction of in-region tiles whose water column is
    /// contaminated — i.e. tiles where
    /// <see cref="Keystone.Core.Ports.IWaterQuery.WaterContaminationAt"/>
    /// reports a non-zero value. Distinct from <see cref="Contamination"/>,
    /// which is the soil-side predicate (whether the dirt at the
    /// surface is in the contamination plume) and lags water
    /// contamination by hours. Drives
    /// <see cref="Keystone.Core.Biomes.BiomeKind.Badwater"/>.
    /// </summary>
    WaterContamination = 4,

  }

}
