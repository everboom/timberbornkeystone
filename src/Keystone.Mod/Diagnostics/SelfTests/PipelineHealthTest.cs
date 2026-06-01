using System.Text;
using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Keystone.Core.Time;
using Keystone.Mod.Biomes;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Verifies the background biome-value pipeline is alive and
  /// producing consistent data. Three checks:
  /// <list type="number">
  ///   <item><b>Heartbeat.</b> The biome ticker has completed at least
  ///   one full map-scan cycle since game load.</item>
  ///   <item><b>Round-trip.</b> At least one chunk in the
  ///   <see cref="ChunkDataStore"/> has non-zero biome values.</item>
  ///   <item><b>Consistency.</b> For every chunk that has data, the
  ///   values read through <see cref="ChunkDataStoreReader"/> and
  ///   <see cref="ChunkValueStoreReader"/> agree within tolerance.
  ///   Best-effort: if workers are writing during the check, mismatches
  ///   are expected and the result says to pause and re-run.</item>
  /// </list>
  /// </summary>
  internal sealed class PipelineHealthTest : IKeystoneSelfTest {

    private const float Tolerance = 0.001f;

    private readonly ChunkBiomeTicker _ticker;
    private readonly ChunkDataStore _chunkData;
    private readonly ChunkValueStore _chunkValues;
    private readonly IClock _clock;

    public PipelineHealthTest(
        ChunkBiomeTicker ticker,
        ChunkDataStore chunkData,
        ChunkValueStore chunkValues,
        IClock clock) {
      _ticker = ticker;
      _chunkData = chunkData;
      _chunkValues = chunkValues;
      _clock = clock;
    }

    /// <inheritdoc />
    public string Name => "Pipeline health";

    /// <inheritdoc />
    public string Category => "Pipeline";

    /// <inheritdoc />
    public SelfTestResult Run() {
      var detail = new StringBuilder();
      var failed = false;
      var skipped = false;

      // --- Heartbeat ---
      var cycles = _ticker.CyclesCompleted;
      if (cycles == 0) {
        detail.AppendLine("  heartbeat: no cycle completed yet (let the game run briefly)");
        skipped = true;
      } else {
        detail.AppendLine($"  heartbeat: {cycles} cycle(s) completed");
      }

      // --- Round-trip ---
      var chunksWithData = 0;
      var chunksWithNonZero = 0;
      var totalValues = 0;
      var nonZeroValues = 0;
      var oldestAge = 0f;
      var newestAge = float.MaxValue;
      var now = _clock.TotalDaysElapsed;

      foreach (var entry in _chunkData.Entries) {
        var data = entry.Value;
        chunksWithData++;
        var hasNonZero = false;
        for (var i = 0; i < data.SlotCount; i++) {
          totalValues++;
          if (data.Get(i) != 0f) {
            nonZeroValues++;
            hasNonZero = true;
          }
        }
        if (hasNonZero) chunksWithNonZero++;
        if (data.LastUpdatedDay > 0f) {
          var age = now - data.LastUpdatedDay;
          if (age > oldestAge) oldestAge = age;
          if (age < newestAge) newestAge = age;
        }
      }

      if (chunksWithData == 0 && cycles == 0) {
        detail.AppendLine("  round-trip: no data yet (waiting for first cycle)");
      } else if (chunksWithData == 0) {
        detail.AppendLine("  FAIL: ChunkDataStore is empty after cycles completed");
        failed = true;
      } else if (chunksWithNonZero == 0) {
        detail.AppendLine(
            $"  FAIL: {chunksWithData} chunks in store but all values are zero");
        failed = true;
      } else {
        var oldestHours = oldestAge * 24f;
        var newestHours = newestAge < float.MaxValue ? newestAge * 24f : 0f;
        detail.AppendLine(
            $"  round-trip: {chunksWithNonZero}/{chunksWithData} chunks have non-zero values " +
            $"({nonZeroValues}/{totalValues} slots)");
        detail.AppendLine(
            $"  data age: oldest {oldestHours:F1}h, newest {newestHours:F1}h");
      }

      // --- Consistency ---
      // Best-effort comparison of the two data layers. If the game is
      // running, workers may be writing to ChunkData concurrently —
      // mismatches in that case are expected, not a real failure.
      var dataReader = new ChunkDataStoreReader(_chunkData);
      var valueReader = new ChunkValueStoreReader(_chunkValues);
      var mismatches = 0;
      var compared = 0;
      var mismatchDetail = new StringBuilder();

      foreach (var entry in _chunkData.Entries) {
        var coord = entry.Key;
        foreach (var biome in BiomeValueKinds.AllBiomes) {
          var fromData = dataReader.GetSuitability(
              coord.RegionId, coord.GlobalChunkX, coord.GlobalChunkY, biome);
          var fromValues = valueReader.GetSuitability(
              coord.RegionId, coord.GlobalChunkX, coord.GlobalChunkY, biome);
          compared++;
          if (!ValuesMatch(fromData, fromValues)) {
            mismatches++;
            if (mismatches <= 5) {
              mismatchDetail.AppendLine(
                  $"    suitability {biome} at ({coord.GlobalChunkX},{coord.GlobalChunkY}) " +
                  $"r{coord.RegionId}: data={fromData?.ToString("F4") ?? "null"} " +
                  $"vs store={fromValues?.ToString("F4") ?? "null"}");
            }
          }

          var matFromData = dataReader.GetMaturity(
              coord.RegionId, coord.GlobalChunkX, coord.GlobalChunkY, biome);
          var matFromValues = valueReader.GetMaturity(
              coord.RegionId, coord.GlobalChunkX, coord.GlobalChunkY, biome);
          compared++;
          if (!ValuesMatch(matFromData, matFromValues)) {
            mismatches++;
            if (mismatches <= 5) {
              mismatchDetail.AppendLine(
                  $"    maturity {biome} at ({coord.GlobalChunkX},{coord.GlobalChunkY}) " +
                  $"r{coord.RegionId}: data={matFromData?.ToString("F4") ?? "null"} " +
                  $"vs store={matFromValues?.ToString("F4") ?? "null"}");
            }
          }
        }
      }

      if (compared == 0) {
        detail.AppendLine("  consistency: no chunks to compare");
      } else if (mismatches > 0) {
        detail.AppendLine(
            $"  FAIL: {mismatches} mismatch(es) across {compared} comparisons. " +
            "If the game is running, pause and re-run for accurate results.");
        detail.Append(mismatchDetail);
        if (mismatches > 5) {
          detail.AppendLine($"    ... and {mismatches - 5} more");
        }
        failed = true;
      } else {
        detail.AppendLine($"  consistency: {compared} comparisons, all match");
      }

      // --- Result ---
      if (failed) {
        return SelfTestResult.Fail("Pipeline issue(s) detected", detail.ToString());
      }
      if (skipped) {
        return SelfTestResult.Skipped(detail.ToString());
      }

      return SelfTestResult.Pass(
          $"{cycles} cycles, {chunksWithNonZero} chunks, {compared} values consistent");
    }

    private static bool ValuesMatch(float? a, float? b) {
      if (a == null && b == null) return true;
      if (a == null || b == null) return false;
      var diff = a.Value - b.Value;
      return diff > -Tolerance && diff < Tolerance;
    }

  }

}
