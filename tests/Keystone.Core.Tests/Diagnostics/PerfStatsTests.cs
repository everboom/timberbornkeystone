using System.Diagnostics;
using Keystone.Core.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Diagnostics {

  /// <summary>
  /// Unit tests for the rolling-window <see cref="PerfStats"/> class.
  /// Covers ring-buffer wraparound, the basic stat computations
  /// (average / P99 / max), and the timestamp-derived frequency.
  /// </summary>
  [TestClass]
  public class PerfStatsTests {

    [TestMethod]
    public void Empty_StatsAreZero_AndSampleCountZero() {
      var stats = new PerfStats();
      Assert.AreEqual(0, stats.SampleCount);
      Assert.AreEqual(0.0, stats.Average);
      Assert.AreEqual(0.0, stats.P99);
      Assert.AreEqual(0.0, stats.Max);
      Assert.AreEqual(0.0, stats.FrequencyHz);
    }

    [TestMethod]
    public void SampleCount_NeverExceedsCapacity() {
      var stats = new PerfStats();
      // Push more than Capacity samples; SampleCount caps at Capacity.
      for (var i = 0; i < PerfStats.Capacity + 50; i++) {
        stats.Add(1.0, i);
      }
      Assert.AreEqual(PerfStats.Capacity, stats.SampleCount);
    }

    [TestMethod]
    public void Add_BeyondCapacity_RingsCorrectly_AndStatsReflectLatestWindow() {
      // Push 1..(Capacity+50): the live window holds the last Capacity
      // samples, which are 51..(Capacity+50).
      var stats = new PerfStats();
      for (var i = 1; i <= PerfStats.Capacity + 50; i++) {
        stats.Add(i, i);
      }

      // Latest sample is Capacity+50; max should match.
      Assert.AreEqual(PerfStats.Capacity + 50, stats.Max);

      // Average of integers 51..(Capacity+50) = (51 + Capacity+50) / 2.
      var expectedAvg = (51.0 + (PerfStats.Capacity + 50)) / 2.0;
      Assert.AreEqual(expectedAvg, stats.Average, 1e-6);
    }

    [TestMethod]
    public void Average_OverFullBuffer_ComputesArithmeticMean() {
      var stats = new PerfStats();
      // 1..Capacity: arithmetic series.
      for (var i = 1; i <= PerfStats.Capacity; i++) {
        stats.Add(i, i);
      }
      var expectedAvg = (1.0 + PerfStats.Capacity) / 2.0;
      Assert.AreEqual(expectedAvg, stats.Average, 1e-6);
    }

    [TestMethod]
    public void P99_OnSortedDistribution_PicksCorrectIndex() {
      // 1..100: P99 index = ceil(0.99 * 100) - 1 = 99 - 1 = 98 -> array[98] = 99.
      var stats = new PerfStats();
      for (var i = 1; i <= 100; i++) {
        stats.Add(i, i);
      }
      Assert.AreEqual(99, stats.P99);
    }

    [TestMethod]
    public void Max_TracksObservedMax() {
      var stats = new PerfStats();
      stats.Add(1.0, 0);
      stats.Add(7.5, 1);
      stats.Add(3.2, 2);
      Assert.AreEqual(7.5, stats.Max);
    }

    [TestMethod]
    public void FrequencyHz_OverKnownSpan_ComputesCorrectly() {
      // 11 samples spaced exactly Stopwatch.Frequency ticks apart =>
      // span = 10 * Frequency ticks = 10 seconds wall time;
      // frequency = (count - 1) / span_seconds = 10 / 10 = 1.0 Hz.
      var stats = new PerfStats();
      for (var i = 0; i < 11; i++) {
        stats.Add(0.0, i * Stopwatch.Frequency);
      }
      Assert.AreEqual(1.0, stats.FrequencyHz, 1e-6);
    }

    [TestMethod]
    public void FrequencyHz_SingleSample_ReturnsZero() {
      var stats = new PerfStats();
      stats.Add(1.0, Stopwatch.GetTimestamp());
      Assert.AreEqual(0.0, stats.FrequencyHz);
    }

    [TestMethod]
    public void FrequencyHz_RobustToLongPauseGap() {
      // 5 samples at 1-second intervals, then a 60-second pause
      // (alt-tab / game paused), then 5 more at 1-second intervals.
      // Median inter-sample gap = 1 second; reported Hz = 1.
      // The naive span-based formula would report ~10/69 ≈ 0.145 Hz.
      var stats = new PerfStats();
      for (var i = 0; i < 5; i++) {
        stats.Add(0.0, i * Stopwatch.Frequency);
      }
      // 60-second jump represents the alt-tab / pause window.
      for (var i = 0; i < 5; i++) {
        stats.Add(0.0, (65 + i) * Stopwatch.Frequency);
      }
      Assert.AreEqual(1.0, stats.FrequencyHz, 1e-6);
    }

  }

}
