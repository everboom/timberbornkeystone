using System;
using System.Diagnostics;
using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Quick-and-dirty microbenchmark for
  /// <see cref="ChunkBiomeSampler.SampleDominantBiome"/>: A/B
  /// compares the fast path (top-3 union over hoisted
  /// <see cref="ChunkData"/> corner refs) against the
  /// legacy fallback (per-read dispatch through
  /// <see cref="IChunkBiomeValues"/>'s
  /// <see cref="IChunkBiomeValues.GetSuitability"/> /
  /// <see cref="IChunkBiomeValues.GetMaturity"/> on every
  /// channel × every corner).
  ///
  /// <para>Both paths run over the same synthetic map (one region,
  /// ~64×64 chunks, realistic 1–3 active biomes per chunk) with the
  /// same tile coordinate sequence. Output goes to
  /// <see cref="TestContext"/> so <c>dotnet test --logger
  /// "console;verbosity=detailed"</c> prints the numbers.</para>
  ///
  /// <para><b>Not a CI gate.</b> Stopwatch microbenchmarks are
  /// susceptible to JIT warmup, GC pauses, and ambient system
  /// load -- treat the speedup ratio as a "2× / 10× / 30× ballpark"
  /// signal, not a precise number. If the win is borderline, switch
  /// to BenchmarkDotNet in a dedicated project.</para>
  /// </summary>
  [TestClass]
  public class ChunkBiomeSamplerBenchmark {

    #region Knobs

    /// <summary>Region dimensions. 64 × 64 chunks = 4096 chunks =
    /// 16384 tiles per region, a representative mid-size Timberborn
    /// map.</summary>
    private const int ChunksX = 64;
    private const int ChunksY = 64;

    /// <summary>SampleDominantBiome invocations per measured path.
    /// 200k × 2 paths × ~µs-per-call lands in the 100s-of-ms range
    /// total — adds a noticeable but tolerable chunk to the test
    /// suite. Crank up to 1M+ for tighter numbers when investigating.</summary>
    private const int Iterations = 200_000;

    /// <summary>Warmup invocations per path before timing. The
    /// hot-path code has cold JIT on first call; warming each path
    /// independently keeps the timed loop measuring steady-state.</summary>
    private const int Warmup = 5_000;

    /// <summary>RNG seed for reproducible synthetic biome values
    /// across runs. Change to A/B different terrain shapes.</summary>
    private const int Seed = 1234;

    #endregion

    #region Fixture state

    public TestContext TestContext { get; set; } = null!;

    private static readonly RegionId Region = new(1);

    private ChunkValueRegistry _registry = null!;
    private ChunkDataStore _store = null!;
    private BiomeSuitabilityUpdater _updater = null!;

    #endregion

    #region Setup

    [TestInitialize]
    public void Setup() {
      _registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(_registry);
      _registry.Freeze();
      _store = new ChunkDataStore(_registry);
      _updater = new BiomeSuitabilityUpdater();
      PopulateMap();
    }

    [TestCleanup]
    public void Cleanup() {
      BiomeValueKinds.ResetOrdinals();
    }

    /// <summary>
    /// Write a synthetic but spatially-coherent biome distribution
    /// onto the chunk grid. The map is partitioned into zones
    /// (<see cref="ZoneSize"/> chunks per side), each zone owns one
    /// primary biome with high Suitability, and zone interiors are
    /// nearly uniform. Transitions live at zone boundaries: chunks
    /// within <see cref="TransitionWidth"/> of a different zone pick
    /// up the neighbouring primary as a second biome.
    ///
    /// <para>This mirrors how biomes actually distribute on real
    /// Timberborn maps -- large coherent stretches of one biome with
    /// thin transition bands -- which is the regime the top-3-union
    /// optimisation is designed for. Random per-chunk assignment
    /// (the previous shape) gave near-worst-case candidate-set sizes
    /// (~6–8 across the 4 corners) and underestimated the real
    /// speedup.</para>
    ///
    /// <para>Not going through <see cref="BiomeSuitabilityUpdater"/>
    /// because that takes <see cref="ChunkBiomeInputs"/> and
    /// produces values via <see cref="BiomeTargets.Compute"/> --
    /// the bench cares about the sampler's per-call cost, not the
    /// realism of value computation.</para>
    /// </summary>
    private void PopulateMap() {
      var rng = new Random(Seed);
      var allBiomes = BiomeValueKinds.AllBiomes;

      // Pre-assign a primary biome per zone.
      var zonesX = (ChunksX + ZoneSize - 1) / ZoneSize;
      var zonesY = (ChunksY + ZoneSize - 1) / ZoneSize;
      var zonePrimary = new int[zonesX, zonesY];
      for (var zy = 0; zy < zonesY; zy++) {
        for (var zx = 0; zx < zonesX; zx++) {
          zonePrimary[zx, zy] = rng.Next(allBiomes.Length);
        }
      }

      for (var cy = 0; cy < ChunksY; cy++) {
        for (var cx = 0; cx < ChunksX; cx++) {
          var data = _store.GetOrCreate(Region, cx, cy);
          var zx = cx / ZoneSize;
          var zy = cy / ZoneSize;
          var primary = zonePrimary[zx, zy];

          // Always write the primary at high Suitability.
          var primaryValue = 0.6f + (float)rng.NextDouble() * 0.3f;
          data.Set(
              BiomeValueKinds.SuitabilityOrdinal(allBiomes[primary]),
              primaryValue);
          data.Set(
              BiomeValueKinds.MaturityOrdinal(allBiomes[primary]),
              (float)rng.NextDouble() * 30f);

          int top0 = primary, top1 = -1, top2 = -1;
          var top0Value = primaryValue;
          var top1Value = 0f;

          // Within TransitionWidth of a different zone? Mix in that
          // zone's primary as a secondary biome at moderate Suitability.
          // Skip self-touches.
          if (IsNearZoneEdge(cx, cy, zx, zy, zonesX, zonesY, zonePrimary,
                             out var neighborPrimary, rng)
              && neighborPrimary != primary) {
            var secondaryValue = 0.2f + (float)rng.NextDouble() * 0.3f;
            data.Set(
                BiomeValueKinds.SuitabilityOrdinal(allBiomes[neighborPrimary]),
                secondaryValue);
            data.Set(
                BiomeValueKinds.MaturityOrdinal(allBiomes[neighborPrimary]),
                (float)rng.NextDouble() * 30f);

            if (secondaryValue > top0Value) {
              top1 = top0; top1Value = top0Value;
              top0 = neighborPrimary; top0Value = secondaryValue;
            } else {
              top1 = neighborPrimary; top1Value = secondaryValue;
            }
          }

          // 5% of chunks get a third biome — rare corner cases or
          // contamination scars. Just enough to keep the third rank
          // non-trivially populated.
          if (rng.NextDouble() < 0.05) {
            int third;
            do { third = rng.Next(allBiomes.Length); }
            while (third == top0 || third == top1);
            var tertiaryValue = 0.05f + (float)rng.NextDouble() * 0.15f;
            data.Set(
                BiomeValueKinds.SuitabilityOrdinal(allBiomes[third]),
                tertiaryValue);
            data.Set(
                BiomeValueKinds.MaturityOrdinal(allBiomes[third]),
                (float)rng.NextDouble() * 30f);

            // tertiaryValue is small so it lands at rank 2.
            top2 = third;
          }

          data.SetTopBiomes(top0, top1, top2);
        }
      }
    }

    /// <summary>Width of the transition zone at the boundary
    /// between two zones, in chunks. Chunks within this distance of
    /// a different-zone neighbor will mix that neighbor's primary
    /// biome at moderate Suitability.</summary>
    private const int TransitionWidth = 1;

    /// <summary>Size of one biome zone in chunks. 16-chunk zones at
    /// 4-tile chunks = 64-tile zones, a realistic biome patch size
    /// on a Timberborn map.</summary>
    private const int ZoneSize = 16;

    private static bool IsNearZoneEdge(
        int cx, int cy, int zx, int zy, int zonesX, int zonesY,
        int[,] zonePrimary, out int neighborPrimary, Random rng) {
      // Distance from the chunk to each of its zone's edges.
      var leftDist = cx - zx * ZoneSize;
      var rightDist = (zx + 1) * ZoneSize - 1 - cx;
      var topDist = cy - zy * ZoneSize;
      var bottomDist = (zy + 1) * ZoneSize - 1 - cy;

      // Pick the closest edge that has a valid neighbor zone.
      var candidates = 0;
      var pickedNeighbor = -1;
      if (leftDist < TransitionWidth && zx > 0) {
        candidates++;
        if (rng.Next(candidates) == 0) pickedNeighbor = zonePrimary[zx - 1, zy];
      }
      if (rightDist < TransitionWidth && zx < zonesX - 1) {
        candidates++;
        if (rng.Next(candidates) == 0) pickedNeighbor = zonePrimary[zx + 1, zy];
      }
      if (topDist < TransitionWidth && zy > 0) {
        candidates++;
        if (rng.Next(candidates) == 0) pickedNeighbor = zonePrimary[zx, zy - 1];
      }
      if (bottomDist < TransitionWidth && zy < zonesY - 1) {
        candidates++;
        if (rng.Next(candidates) == 0) pickedNeighbor = zonePrimary[zx, zy + 1];
      }

      neighborPrimary = pickedNeighbor;
      return pickedNeighbor >= 0;
    }

    #endregion

    #region Benchmark

    [TestMethod]
    public void Benchmark_FastPath_VsLegacyFallback() {
      const int chunkSize = Keystone.Core.Ecology.Fields.RegionEcologyField.ChunkSize;
      var fastValues = new ChunkDataStoreReader(_store);
      var slowValues = new FallbackOnlyValues(fastValues);

      // Pre-generate tile coordinates so the path comparison sees
      // identical work — same coordinate sequence on both runs, no
      // RNG state leak between them.
      var rng = new Random(Seed + 1);
      var maxTileX = ChunksX * chunkSize;
      var maxTileY = ChunksY * chunkSize;
      var tilesX = new float[Iterations + Warmup];
      var tilesY = new float[Iterations + Warmup];
      for (var i = 0; i < tilesX.Length; i++) {
        tilesX[i] = (float)(rng.NextDouble() * maxTileX);
        tilesY[i] = (float)(rng.NextDouble() * maxTileY);
      }

      // Sanity check: same inputs → same outputs across paths. If
      // this assert ever fires, the benchmark is comparing two
      // semantically-different functions and the speedup number is
      // meaningless.
      for (var i = 0; i < 100; i++) {
        var fast = ChunkBiomeSampler.SampleDominantBiome(
            fastValues, Region, 0, 0, ChunksX, ChunksY, tilesX[i], tilesY[i]);
        var slow = ChunkBiomeSampler.SampleDominantBiome(
            slowValues, Region, 0, 0, ChunksX, ChunksY, tilesX[i], tilesY[i]);
        Assert.AreEqual(fast.Biome, slow.Biome,
            $"Path disagreement at tile ({tilesX[i]}, {tilesY[i]}): "
            + $"fast={fast.Biome} slow={slow.Biome}");
        Assert.AreEqual(fast.Maturity, slow.Maturity, 1e-4f,
            $"Maturity disagreement at tile ({tilesX[i]}, {tilesY[i]})");
      }

      // Warmup each path independently so the timed loop measures
      // hot-cache steady-state, not cold-JIT initialisation.
      var sink = 0f;
      for (var i = 0; i < Warmup; i++) {
        sink += ChunkBiomeSampler.SampleDominantBiome(
            fastValues, Region, 0, 0, ChunksX, ChunksY, tilesX[i], tilesY[i]).Maturity;
        sink += ChunkBiomeSampler.SampleDominantBiome(
            slowValues, Region, 0, 0, ChunksX, ChunksY, tilesX[i], tilesY[i]).Maturity;
      }

      var fastMs = TimePath(fastValues, tilesX, tilesY, ref sink);
      var slowMs = TimePath(slowValues, tilesX, tilesY, ref sink);

      var fastNs = fastMs * 1_000_000.0 / Iterations;
      var slowNs = slowMs * 1_000_000.0 / Iterations;
      var speedup = slowMs / fastMs;

      // Force the sink to survive the dead-store optimiser. The
      // sampler results are summed into `sink`; without referencing
      // it the JIT may notice we don't use them and skip the work.
      TestContext.WriteLine($"sink={sink:F2}  (anti-DCE; ignore)");
      TestContext.WriteLine(
          $"Fast path  ({Iterations} calls): {fastMs,8:F1} ms total, {fastNs,6:F0} ns/call");
      TestContext.WriteLine(
          $"Fallback   ({Iterations} calls): {slowMs,8:F1} ms total, {slowNs,6:F0} ns/call");
      TestContext.WriteLine($"Speedup: {speedup:F2}×");

      // No strict perf gate -- microbench numbers are too noisy for
      // a hard threshold. We do assert "fast path is at least
      // somewhat faster" as a smoke test against accidental
      // regressions where the supposedly-fast path becomes slower.
      Assert.IsTrue(speedup > 1.2,
          $"Fast path is supposed to be faster than the fallback; "
          + $"got speedup of {speedup:F2}× ({fastMs:F1} ms vs {slowMs:F1} ms). "
          + "If this fires, the optimisation may have regressed.");
    }

    private double TimePath(
        IChunkBiomeValues values, float[] tilesX, float[] tilesY, ref float sink) {
      var sw = Stopwatch.StartNew();
      for (var i = 0; i < Iterations; i++) {
        sink += ChunkBiomeSampler.SampleDominantBiome(
            values, Region, 0, 0, ChunksX, ChunksY,
            tilesX[Warmup + i], tilesY[Warmup + i]).Maturity;
      }
      sw.Stop();
      return sw.Elapsed.TotalMilliseconds;
    }

    #endregion

    #region Fallback wrapper

    /// <summary>
    /// <see cref="IChunkBiomeValues"/> wrapper that forces the
    /// sampler down its legacy per-read path by returning <c>null</c>
    /// from <see cref="GetChunkData"/>. The other methods delegate
    /// to the real reader so the underlying values stay identical.
    /// </summary>
    private sealed class FallbackOnlyValues : IChunkBiomeValues {

      private readonly ChunkDataStoreReader _real;

      public FallbackOnlyValues(ChunkDataStoreReader real) {
        _real = real;
      }

      public float? GetSuitability(RegionId regionId, int chunkX, int chunkY, BiomeKind biome) =>
          _real.GetSuitability(regionId, chunkX, chunkY, biome);

      public float? GetMaturity(RegionId regionId, int chunkX, int chunkY, BiomeKind biome) =>
          _real.GetMaturity(regionId, chunkX, chunkY, biome);

      public ChunkData? GetChunkData(RegionId regionId, int chunkX, int chunkY) => null;

    }

    #endregion

  }

}
