using System.Collections.Generic;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Ecology.Fields {

  /// <summary>
  /// Pins <see cref="ScalarChannelAggregator.AccumulateSurface"/>'s
  /// six per-surface channel contributions and
  /// <see cref="ScalarChannelAggregator.Normalise"/>'s post-loop
  /// sample-mean division.
  /// </summary>
  [TestClass]
  public class ScalarChannelAggregatorTests {

    #region Fakes

    private sealed class FakeMoisture : IMoistureQuery {
      public Dictionary<TileCoord, float> ColumnMoisture { get; } = new();
      public HashSet<SurfaceCoord> Moist { get; } = new();
      public float MoistureAt(TileCoord column)
          => ColumnMoisture.TryGetValue(column, out var v) ? v : 0f;
      public bool IsMoistAt(SurfaceCoord surface) => Moist.Contains(surface);
    }

    private sealed class FakeContamination : IContaminationQuery {
      public HashSet<SurfaceCoord> Contaminated { get; } = new();
      public float ContaminationAt(TileCoord column) => 0f;
      public bool IsContaminatedAt(SurfaceCoord surface) => Contaminated.Contains(surface);
    }

    private sealed class FakeWater : IWaterQuery {
      public Dictionary<SurfaceCoord, float> Depths { get; } = new();
      public Dictionary<SurfaceCoord, FlowVector> Flows { get; } = new();
      public Dictionary<SurfaceCoord, float> Contamination { get; } = new();
      public float WaterDepthAt(SurfaceCoord s)
          => Depths.TryGetValue(s, out var v) ? v : 0f;
      public float WaterSurfaceHeightAt(SurfaceCoord s)
          => Depths.TryGetValue(s, out var v) && v > 0f ? s.Z + v : 0f;
      public FlowVector FlowAt(SurfaceCoord s)
          => Flows.TryGetValue(s, out var v) ? v : FlowVector.Zero;
      public bool HasWaterAtColumn(TileCoord column) {
        foreach (var kv in Depths) {
          if (kv.Key.X == column.X && kv.Key.Y == column.Y && kv.Value > 0f) return true;
        }
        return false;
      }
      public float WaterContaminationAt(SurfaceCoord s)
          => Contamination.TryGetValue(s, out var v) ? v : 0f;
    }

    private static float[] FreshBuffer() => new float[RegionEcologyField.FixedChannelCount];

    #endregion

    #region AccumulateSurface — per-channel contributions

    [TestMethod]
    public void Accumulate_WaterDepth_SumsRawValue() {
      var moisture = new FakeMoisture();
      var contamination = new FakeContamination();
      var water = new FakeWater();
      var surface = new SurfaceCoord(0, 0, 5);
      water.Depths[surface] = 0.7f;
      var buf = FreshBuffer();

      ScalarChannelAggregator.AccumulateSurface(surface, moisture, contamination, water, buf);

      Assert.AreEqual(0.7f, buf[(int)EcologyChannel.WaterDepth], 1e-6f);
    }

    [TestMethod]
    public void Accumulate_WaterFlow_SumsMagnitude() {
      var moisture = new FakeMoisture();
      var contamination = new FakeContamination();
      var water = new FakeWater();
      var surface = new SurfaceCoord(0, 0, 5);
      water.Flows[surface] = new FlowVector(3f, 4f);  // magnitude 5
      var buf = FreshBuffer();

      ScalarChannelAggregator.AccumulateSurface(surface, moisture, contamination, water, buf);

      Assert.AreEqual(5f, buf[(int)EcologyChannel.WaterFlowMagnitude], 1e-5f);
    }

    [TestMethod]
    public void Accumulate_MoistPredicateTrue_IncrementsMoistureChannelByOne() {
      var moisture = new FakeMoisture();
      var contamination = new FakeContamination();
      var water = new FakeWater();
      var surface = new SurfaceCoord(0, 0, 5);
      moisture.Moist.Add(surface);
      var buf = FreshBuffer();

      ScalarChannelAggregator.AccumulateSurface(surface, moisture, contamination, water, buf);

      Assert.AreEqual(1f, buf[(int)EcologyChannel.Moisture]);
    }

    [TestMethod]
    public void Accumulate_MoistPredicateFalse_LeavesMoistureChannelAtZero() {
      var moisture = new FakeMoisture();
      var contamination = new FakeContamination();
      var water = new FakeWater();
      var surface = new SurfaceCoord(0, 0, 5);
      var buf = FreshBuffer();

      ScalarChannelAggregator.AccumulateSurface(surface, moisture, contamination, water, buf);

      Assert.AreEqual(0f, buf[(int)EcologyChannel.Moisture]);
    }

    [TestMethod]
    public void Accumulate_ContaminationPredicateTrue_IncrementsContaminationChannel() {
      var moisture = new FakeMoisture();
      var contamination = new FakeContamination();
      var water = new FakeWater();
      var surface = new SurfaceCoord(0, 0, 5);
      contamination.Contaminated.Add(surface);
      var buf = FreshBuffer();

      ScalarChannelAggregator.AccumulateSurface(surface, moisture, contamination, water, buf);

      Assert.AreEqual(1f, buf[(int)EcologyChannel.Contamination]);
    }

    [TestMethod]
    public void Accumulate_WaterContamination_OnlyIncrementsAtOrAboveBadwaterThreshold() {
      // Reuses Core WaterContamination.IsBadwater — saturation ≥ 0.05
      // increments. Strict <0.05 doesn't.
      var moisture = new FakeMoisture();
      var contamination = new FakeContamination();
      var water = new FakeWater();
      var surface = new SurfaceCoord(0, 0, 5);
      water.Contamination[surface] = 0.04f;
      var below = FreshBuffer();
      ScalarChannelAggregator.AccumulateSurface(surface, moisture, contamination, water, below);
      Assert.AreEqual(0f, below[(int)EcologyChannel.WaterContamination]);

      water.Contamination[surface] = 0.05f;
      var atThreshold = FreshBuffer();
      ScalarChannelAggregator.AccumulateSurface(surface, moisture, contamination, water, atThreshold);
      Assert.AreEqual(1f, atThreshold[(int)EcologyChannel.WaterContamination]);
    }

    #endregion

    #region AccumulateSurface — accumulation across multiple surfaces

    [TestMethod]
    public void Accumulate_TwoSurfaces_AccumulatesInPlace() {
      var moisture = new FakeMoisture();
      var contamination = new FakeContamination();
      var water = new FakeWater();
      var s1 = new SurfaceCoord(0, 0, 5);
      var s2 = new SurfaceCoord(1, 0, 5);
      water.Depths[s1] = 0.5f;
      water.Depths[s2] = 0.3f;
      moisture.Moist.Add(s1);  // s1 is moist, s2 isn't

      var buf = FreshBuffer();
      ScalarChannelAggregator.AccumulateSurface(s1, moisture, contamination, water, buf);
      ScalarChannelAggregator.AccumulateSurface(s2, moisture, contamination, water, buf);

      Assert.AreEqual(0.8f, buf[(int)EcologyChannel.WaterDepth], 1e-5f);
      Assert.AreEqual(1f, buf[(int)EcologyChannel.Moisture], "Only s1 was moist.");
    }

    #endregion

    #region Normalise

    [TestMethod]
    public void Normalise_DividesAllChannelsBySampleCount() {
      var buf = new[] { 4f, 8f, 12f, 16f, 20f, 24f };
      ScalarChannelAggregator.Normalise(buf, sampleCount: 4);
      CollectionAssert.AreEqual(new[] { 1f, 2f, 3f, 4f, 5f, 6f }, buf);
    }

    [TestMethod]
    public void Normalise_ZeroSampleCount_NoOp() {
      // Empty chunk: caller skips normalisation. Defensive against
      // div-by-zero.
      var buf = new[] { 7f, 13f };
      ScalarChannelAggregator.Normalise(buf, sampleCount: 0);
      CollectionAssert.AreEqual(new[] { 7f, 13f }, buf);
    }

    [TestMethod]
    public void Normalise_NegativeSampleCount_NoOp() {
      var buf = new[] { 7f, 13f };
      ScalarChannelAggregator.Normalise(buf, sampleCount: -1);
      CollectionAssert.AreEqual(new[] { 7f, 13f }, buf);
    }

    #endregion

  }

}
