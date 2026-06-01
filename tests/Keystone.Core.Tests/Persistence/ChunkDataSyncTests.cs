using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Tests that verify the ChunkValueStore → ChunkDataStore sync
  /// produces ordinal-indexed values that match the string-keyed
  /// originals. Exercises the sync logic in isolation — the actual
  /// sync call lives in <c>ChunkBiomeTicker</c> (Mod layer).
  /// </summary>
  [TestClass]
  public class ChunkDataSyncTests {

    private ChunkValueRegistry _registry = null!;

    [TestInitialize]
    public void Init() {
      _registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(_registry);
      _registry.Freeze();
    }

    [TestCleanup]
    public void Cleanup() {
      BiomeValueKinds.ResetOrdinals();
    }

    /// <summary>Simulate the sync that ChunkBiomeTicker.SyncToChunkData does.</summary>
    private static void Sync(
        ChunkValueStore source, ChunkDataStore dest,
        RegionId regionId, int chunkX, int chunkY) {
      var data = dest.GetOrCreate(regionId, chunkX, chunkY);
      var values = data.Values;
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        values[BiomeValueKinds.SuitabilityOrdinal(biome)] =
            source.Get(regionId, chunkX, chunkY, BiomeValueKinds.ForSuitability(biome)) ?? 0f;
        values[BiomeValueKinds.MaturityOrdinal(biome)] =
            source.Get(regionId, chunkX, chunkY, BiomeValueKinds.ForMaturity(biome)) ?? 0f;
      }
    }

    [TestMethod]
    public void SyncFromChunkValueStore_AllBiomeSlotsPopulated() {
      // Arrange
      var cvs = new ChunkValueStore();
      var cds = new ChunkDataStore(_registry);
      var region = new RegionId(1);
      var value = 0.1f;
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        cvs.Set(region, 2, 3, BiomeValueKinds.ForSuitability(biome), value);
        cvs.Set(region, 2, 3, BiomeValueKinds.ForMaturity(biome), value + 1f);
        value += 0.1f;
      }

      // Act
      Sync(cvs, cds, region, 2, 3);

      // Assert
      var data = cds.Get(region, 2, 3);
      Assert.IsNotNull(data);
      value = 0.1f;
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        Assert.AreEqual(value, data!.Get(BiomeValueKinds.SuitabilityOrdinal(biome)), 1e-6f,
            $"Suitability mismatch for {biome}.");
        Assert.AreEqual(value + 1f, data!.Get(BiomeValueKinds.MaturityOrdinal(biome)), 1e-6f,
            $"Maturity mismatch for {biome}.");
        value += 0.1f;
      }
    }

    [TestMethod]
    public void SyncFromChunkValueStore_MissingKind_DefaultsToZero() {
      // Arrange — write only Forest suitability, leave everything else absent
      var cvs = new ChunkValueStore();
      var cds = new ChunkDataStore(_registry);
      var region = new RegionId(1);
      cvs.Set(region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.75f);

      // Act
      Sync(cvs, cds, region, 0, 0);

      // Assert
      var data = cds.Get(region, 0, 0)!;
      Assert.AreEqual(0.75f, data.Get(BiomeValueKinds.SuitabilityOrdinal(BiomeKind.Forest)), 1e-6f);
      Assert.AreEqual(0f, data.Get(BiomeValueKinds.MaturityOrdinal(BiomeKind.Forest)), 1e-6f);
      Assert.AreEqual(0f, data.Get(BiomeValueKinds.SuitabilityOrdinal(BiomeKind.Grassland)), 1e-6f);
    }

    [TestMethod]
    public void OrdinalValues_MatchStringValues_AfterSync() {
      // Arrange
      var cvs = new ChunkValueStore();
      var cds = new ChunkDataStore(_registry);
      var region = new RegionId(5);
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        var s = (float)(int)biome * 0.05f;
        var m = (float)(int)biome * 0.1f + 1f;
        cvs.Set(region, 4, 7, BiomeValueKinds.ForSuitability(biome), s);
        cvs.Set(region, 4, 7, BiomeValueKinds.ForMaturity(biome), m);
      }

      // Act
      Sync(cvs, cds, region, 4, 7);

      // Assert — ordinal-indexed values match string-keyed values
      var data = cds.Get(region, 4, 7)!;
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        var expectedSuit = cvs.Get(region, 4, 7, BiomeValueKinds.ForSuitability(biome)) ?? 0f;
        var expectedMat = cvs.Get(region, 4, 7, BiomeValueKinds.ForMaturity(biome)) ?? 0f;
        Assert.AreEqual(expectedSuit, data.Get(BiomeValueKinds.SuitabilityOrdinal(biome)), 1e-6f,
            $"Suitability round-trip mismatch for {biome}.");
        Assert.AreEqual(expectedMat, data.Get(BiomeValueKinds.MaturityOrdinal(biome)), 1e-6f,
            $"Maturity round-trip mismatch for {biome}.");
      }
    }

  }

}
