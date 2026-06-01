using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  /// <summary>
  /// Suitability is stateless: <see cref="BiomeSuitabilityUpdater.Tick"/>
  /// writes <see cref="BiomeTargets.Compute"/>'s output directly each
  /// call, clamped to [0, 1]. These tests cover the stateless contract
  /// and the all-biomes-written invariant. Detailed per-biome and
  /// contamination-cancellation behavior is in <c>BiomeTargetsTests</c>.
  /// </summary>
  [TestClass]
  public class BiomeSuitabilityUpdaterTests {

    private ChunkValueRegistry _registry = null!;
    private int _slotCount;

    private static BiomeSuitabilityUpdater MakeUpdater() => new();

    private ChunkData MakeData() => new(_slotCount);

    private static float Suitability(ChunkData data, BiomeKind biome) {
      return data.Get(BiomeValueKinds.SuitabilityOrdinal(biome));
    }

    [TestInitialize]
    public void Setup() {
      _registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(_registry);
      _registry.Freeze();
      _slotCount = _registry.SlotCount;
    }

    [TestCleanup]
    public void Cleanup() {
      BiomeValueKinds.ResetOrdinals();
    }

    #region Stateless write

    [TestMethod]
    public void Tick_WritesCurrentTargetForEachBiome() {
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 5,
          TreeSpeciesCount = 2,
      };
      updater.Tick(data, in inputs);

      Assert.AreEqual(
          BiomeTargets.Compute(BiomeKind.Forest, inputs),
          Suitability(data, BiomeKind.Forest),
          1e-6f);
      Assert.AreEqual(
          BiomeTargets.Compute(BiomeKind.Grassland, inputs),
          Suitability(data, BiomeKind.Grassland),
          1e-6f);
    }

    [TestMethod]
    public void Tick_AllBiomesWrittenEachCall() {
      // Even biomes with target=0 get a record written, so the
      // persisted snapshot is complete after each tick.
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs { DryLandFraction = 1f };
      updater.Tick(data, in inputs);

      foreach (BiomeKind biome in BiomeValueKinds.AllBiomes) {
        var value = Suitability(data, biome);
        // After Tick, every biome has a computed value (including 0).
        // The old test checked HasValue on nullable; with ChunkData
        // every slot exists by construction. Verify Tick actually wrote
        // by confirming the value equals the fresh compute.
        Assert.AreEqual(BiomeTargets.Compute(biome, inputs), value, 1e-6f,
            $"{biome} should have a Suitability value matching Compute after Tick");
      }
    }

    [TestMethod]
    public void Tick_NoStateRetained_DifferentInputsGiveDifferentOutputs() {
      var updater = MakeUpdater();
      var data = MakeData();

      var irrigated = new ChunkBiomeInputs {
          IrrigatedFraction = 1f, TreeCount = 5, TreeSpeciesCount = 2,
      };
      updater.Tick(data, in irrigated);
      var forestAfterIrrigated = Suitability(data, BiomeKind.Forest);
      Assert.IsTrue(forestAfterIrrigated > 0f);

      var dry = new ChunkBiomeInputs { DryLandFraction = 1f };
      updater.Tick(data, in dry);
      Assert.AreEqual(0f, Suitability(data, BiomeKind.Forest), 1e-6f);
      Assert.AreEqual(1f, Suitability(data, BiomeKind.Dry), 1e-6f);
    }

    [TestMethod]
    public void Tick_FloodedChunk_IrrigatedLandBiomesAreZero() {
      // A flooded chunk reads as 0 for irrigation-dependent biomes
      // without any drift -- the positive predicate's irrigated term
      // is 0, so the answer is computed directly.
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs {
          WaterFraction = 1f,
          ShallowSlowWaterFraction = 1f,
      };
      updater.Tick(data, in inputs);

      Assert.AreEqual(0f, Suitability(data, BiomeKind.Forest), 1e-6f);
      Assert.AreEqual(0f, Suitability(data, BiomeKind.Grassland), 1e-6f);
      Assert.AreEqual(0f, Suitability(data, BiomeKind.Monoculture), 1e-6f);
      Assert.AreEqual(1f, Suitability(data, BiomeKind.Wetland), 1e-6f);
    }

    [TestMethod]
    public void Tick_ContaminatedChunk_HealthyBiomesCancelled_ContaminatedKept() {
      // The contamination cancellation factor lives in BiomeTargets.Compute;
      // this test verifies that the updater propagates it correctly to
      // the stored Suitability.
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f,
          TreeCount = 5,
          TreeSpeciesCount = 2,
          ContaminatedFraction = 0.2f,  // well above kill threshold
      };
      updater.Tick(data, in inputs);

      Assert.AreEqual(0f, Suitability(data, BiomeKind.Forest), 1e-6f);
      Assert.IsTrue(Suitability(data, BiomeKind.Contaminated) > 0f);
    }

    #endregion

    #region Clamp

    [TestMethod]
    public void Tick_ClampsResultToZeroToOne() {
      // BiomeTargets.Compute already returns [0, 1] under all designed
      // inputs; the updater's defensive clamp catches any numerical edge.
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f, TreeCount = 100, TreeSpeciesCount = 100,
      };
      updater.Tick(data, in inputs);

      foreach (BiomeKind biome in BiomeValueKinds.AllBiomes) {
        var s = Suitability(data, biome);
        Assert.IsTrue(s >= 0f && s <= 1f,
            $"{biome} Suitability {s} out of [0,1]");
      }
    }

    #endregion

    #region Cross-biome isolation

    [TestMethod]
    public void Tick_OneBiomeChangeDoesNotAffectOthers() {
      var updater = MakeUpdater();
      var data = MakeData();
      // Pre-seed Grassland to a known value so we can confirm it gets
      // overwritten (rather than retained).
      data.Set(BiomeValueKinds.SuitabilityOrdinal(BiomeKind.Grassland), 0.5f);

      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f, TreeCount = 5, TreeSpeciesCount = 2,
      };
      updater.Tick(data, in inputs);

      // Forest reads from inputs.
      Assert.IsTrue(Suitability(data, BiomeKind.Forest) > 0f);
      // Grassland's pre-seeded 0.5f got overwritten by fresh compute.
      // (Grassland's positive predicate scales down with tree presence,
      // so the new value should differ from 0.5.)
      Assert.AreNotEqual(0.5f, Suitability(data, BiomeKind.Grassland));
    }

    #endregion

    #region Argument validation

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentNullException))]
    public void Tick_NullData_Throws() {
      var inputs = new ChunkBiomeInputs();
      MakeUpdater().Tick(null!, in inputs);
    }

    #endregion

    #region Top-3 biomes cache

    // The top-3 cache on ChunkData is the load-bearing optimisation
    // that lets ChunkBiomeSampler.SampleDominantBiome shrink its
    // bilinear-argmax candidate set from "all 10 biomes" to "the
    // union of the 4 corner chunks' top-3 lists". BiomeSuitabilityUpdater
    // is the sole writer; if it ever stops maintaining the cache in
    // sync with the suitability slots, the sampler will silently
    // return wrong dominance answers on chunks where the true winner
    // is outside the stale top-3.

    [TestMethod]
    public void Tick_TopBiomes_OrderedDescendingByValue() {
      // Irrigated land with trees → Forest and Grassland both
      // positive, Monoculture zero (multi-species). Verify rank 0
      // is genuinely the max-suitability biome and ranks are
      // monotonically non-increasing.
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f, TreeCount = 5, TreeSpeciesCount = 2,
      };
      updater.Tick(data, in inputs);

      var top = data.TopBiomes;
      Assert.IsTrue(top[0] >= 0, "rank 0 should be populated when any biome is positive");

      // For each populated rank, confirm the stored suitability matches
      // the chunk's value-array slot for that biome.
      var v0 = Suitability(data, (BiomeKind)top[0]);
      Assert.IsTrue(v0 > 0f, "rank-0 biome must have positive Suitability");
      if (top[1] >= 0) {
        var v1 = Suitability(data, (BiomeKind)top[1]);
        Assert.IsTrue(v0 >= v1, $"rank 0 ({v0}) must be >= rank 1 ({v1})");
        Assert.IsTrue(v1 > 0f, "rank-1 biome must have positive Suitability when populated");
        if (top[2] >= 0) {
          var v2 = Suitability(data, (BiomeKind)top[2]);
          Assert.IsTrue(v1 >= v2, $"rank 1 ({v1}) must be >= rank 2 ({v2})");
          Assert.IsTrue(v2 > 0f, "rank-2 biome must have positive Suitability when populated");
        }
      }
    }

    [TestMethod]
    public void Tick_TopBiomes_Rank0IsActualMax() {
      // Compute the true argmax independently and confirm rank 0
      // matches it. The cache is wrong if any non-rank-0 biome has
      // strictly higher Suitability than rank 0.
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs {
          IrrigatedFraction = 1f, TreeCount = 5, TreeSpeciesCount = 2,
      };
      updater.Tick(data, in inputs);

      var top = data.TopBiomes;
      Assert.IsTrue(top[0] >= 0);
      var cachedTopValue = Suitability(data, (BiomeKind)top[0]);

      foreach (BiomeKind biome in BiomeValueKinds.AllBiomes) {
        Assert.IsTrue(
            Suitability(data, biome) <= cachedTopValue,
            $"{biome} suitability exceeds the cached rank-0 biome " +
            $"({(BiomeKind)top[0]}); top-3 cache is stale.");
      }
    }

    [TestMethod]
    public void Tick_TopBiomes_ZeroSuitabilityBiomeExcluded() {
      // Single positive biome (Dry under pure drought). Rank 0 is
      // Dry; ranks 1 and 2 are -1 because no other biome has positive
      // Suitability and a top-3 slot holding a 0-value biome would
      // waste a candidate in the sampler at no gain.
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs { DryLandFraction = 1f };
      updater.Tick(data, in inputs);

      var top = data.TopBiomes;
      Assert.AreEqual((int)BiomeKind.Dry, top[0]);
      Assert.AreEqual(-1, top[1], "rank 1 should be -1 when only one biome is positive");
      Assert.AreEqual(-1, top[2], "rank 2 should be -1 when only one biome is positive");
    }

    [TestMethod]
    public void Tick_TopBiomes_AllZero_AllRanksEmpty() {
      // Inputs that produce zero Suitability for every biome.
      // Top-3 should report all -1 (no candidates) -- the sampler
      // takes this as "no biome dominant here" and returns (null, 0).
      var updater = MakeUpdater();
      var data = MakeData();
      var inputs = new ChunkBiomeInputs();
      updater.Tick(data, in inputs);

      var top = data.TopBiomes;
      Assert.AreEqual(-1, top[0]);
      Assert.AreEqual(-1, top[1]);
      Assert.AreEqual(-1, top[2]);
    }

    [TestMethod]
    public void Tick_TopBiomes_OverwrittenOnEachCall() {
      // First call populates the cache; second call with completely
      // different inputs replaces it. Otherwise stale rank-0 values
      // from a previous biome state would haunt the chunk's argmax.
      var updater = MakeUpdater();
      var data = MakeData();

      var dry = new ChunkBiomeInputs { DryLandFraction = 1f };
      updater.Tick(data, in dry);
      Assert.AreEqual((int)BiomeKind.Dry, data.TopBiomes[0]);

      var flooded = new ChunkBiomeInputs {
          WaterFraction = 1f, ShallowSlowWaterFraction = 1f,
      };
      updater.Tick(data, in flooded);
      Assert.AreEqual((int)BiomeKind.Wetland, data.TopBiomes[0]);
      Assert.AreEqual(-1, data.TopBiomes[1], "previous Dry rank should be cleared");
    }

    #endregion

  }

}
