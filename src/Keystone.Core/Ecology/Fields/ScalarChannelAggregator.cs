using Keystone.Core.Ports;
using Keystone.Core.Tiles;

namespace Keystone.Core.Ecology.Fields {

  /// <summary>
  /// Per-surface scalar-channel increment for the chunk aggregator's
  /// fold. Reads the four scalar/predicate signals via Core ports and
  /// adds their per-surface contributions to the destination buffer
  /// in <see cref="EcologyChannel"/> ordinal order.
  ///
  /// <para><b>What this is.</b> The pure-logic core of one iteration
  /// of <c>EcologyFieldUpdater.ProcessUnit</c>'s surface loop. The
  /// caller owns the loop, the scratch buffer, the sample count, and
  /// the post-loop division-by-sample-count; this method just folds
  /// one surface's port readings into the running totals.</para>
  ///
  /// <para><b>Threshold predicates.</b> Water-contamination uses the
  /// existing Core threshold
  /// (<see cref="WaterContamination.IsBadwater"/>); already tested
  /// independently. Moist and contaminated come from the
  /// already-aggregated port predicates
  /// (<see cref="IMoistureQuery.IsMoistAt"/>,
  /// <see cref="IContaminationQuery.IsContaminatedAt"/>).</para>
  /// </summary>
  public static class ScalarChannelAggregator {

    /// <summary>Add one surface's contributions to
    /// <paramref name="destination"/>. The destination buffer must be
    /// at least <see cref="RegionEcologyField.FixedChannelCount"/>
    /// entries long; values are accumulated, not assigned.</summary>
    public static void AccumulateSurface(
        SurfaceCoord surface,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IWaterQuery water,
        float[] destination) {
      var column = new TileCoord(surface.X, surface.Y);
      destination[(int)EcologyChannel.WaterDepth] += water.WaterDepthAt(surface);
      destination[(int)EcologyChannel.WaterFlowMagnitude] += water.FlowAt(surface).Magnitude;
      // Boolean predicates contribute 1 per matching surface so the
      // post-loop mean is the fraction of in-region surfaces where the
      // predicate holds.
      if (moisture.IsMoistAt(surface)) destination[(int)EcologyChannel.Moisture] += 1f;
      if (contamination.IsContaminatedAt(surface)) destination[(int)EcologyChannel.Contamination] += 1f;
      if (WaterContamination.IsBadwater(water.WaterContaminationAt(surface))) {
        destination[(int)EcologyChannel.WaterContamination] += 1f;
      }
    }

    /// <summary>Offset variant of <see cref="AccumulateSurface"/> for
    /// pooled buffers where multiple chunks share one flat array.
    /// Accumulates into <paramref name="destination"/> starting at
    /// <paramref name="offset"/>.</summary>
    public static void AccumulateSurfaceInto(
        SurfaceCoord surface,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IWaterQuery water,
        float[] destination, int offset) {
      var column = new TileCoord(surface.X, surface.Y);
      destination[offset + (int)EcologyChannel.WaterDepth] += water.WaterDepthAt(surface);
      destination[offset + (int)EcologyChannel.WaterFlowMagnitude] += water.FlowAt(surface).Magnitude;
      if (moisture.IsMoistAt(surface)) destination[offset + (int)EcologyChannel.Moisture] += 1f;
      if (contamination.IsContaminatedAt(surface)) destination[offset + (int)EcologyChannel.Contamination] += 1f;
      if (WaterContamination.IsBadwater(water.WaterContaminationAt(surface))) {
        destination[offset + (int)EcologyChannel.WaterContamination] += 1f;
      }
    }

    /// <summary>Divide every channel in <paramref name="destination"/>
    /// by <paramref name="sampleCount"/> in place, turning per-surface
    /// sums into the chunk mean / fraction. No-op when
    /// <paramref name="sampleCount"/> is non-positive (caller handles
    /// the empty-chunk case).</summary>
    public static void Normalise(float[] destination, int sampleCount) {
      if (sampleCount <= 0) return;
      var inv = 1f / sampleCount;
      for (var ch = 0; ch < destination.Length; ch++) {
        destination[ch] *= inv;
      }
    }

  }

}
