using Keystone.Core.Ecology.Fields;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Ecology.Fields {

  /// <summary>
  /// Verifies that <see cref="RegionEcologyFieldBuilder"/> correctly
  /// accumulates per-tile inputs into per-chunk means (for fixed
  /// channels) and per-chunk counts (for entity channels), and that
  /// validity tracks "at least one scalar tile contributed."
  /// </summary>
  [TestClass]
  public class RegionEcologyFieldBuilderTests {

    [TestMethod]
    public void Build_NoTiles_ProducesAllInvalidChunks() {
      // Empty builder → every chunk invalid, every value 0, sample returns 0.
      var b = new RegionEcologyFieldBuilder(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);
      var field = b.Build();

      for (var cy = 0; cy < 2; cy++) {
        for (var cx = 0; cx < 2; cx++) {
          Assert.IsFalse(field.ChunkValid(cx, cy), $"chunk ({cx},{cy}) should be invalid");
          Assert.AreEqual(0f, field.ChunkValue(EcologyChannel.Moisture, cx, cy));
        }
      }
      Assert.AreEqual(0f, field.Sample(EcologyChannel.Moisture, 3.5f, 3.5f), 1e-6f);
    }

    [TestMethod]
    public void AddTile_MoisturePredicateAggregatesAsFraction() {
      // Three tiles in chunk (0,0); two are IsMoist, one is not.
      // Moisture channel should land at 2/3 ≈ 0.667. No tiles elsewhere
      // → other chunks invalid.
      var b = new RegionEcologyFieldBuilder(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);
      b.AddTile(0, 0, waterDepth: 0f, waterFlowMagnitude: 0f, isMoist: true,  isContaminated: false);
      b.AddTile(2, 1, waterDepth: 0f, waterFlowMagnitude: 0f, isMoist: true,  isContaminated: false);
      b.AddTile(1, 2, waterDepth: 0f, waterFlowMagnitude: 0f, isMoist: false, isContaminated: false);

      var field = b.Build();

      Assert.IsTrue(field.ChunkValid(0, 0));
      Assert.AreEqual(2f / 3f, field.ChunkValue(EcologyChannel.Moisture, 0, 0), 1e-6f);
      Assert.IsFalse(field.ChunkValid(1, 0));
      Assert.IsFalse(field.ChunkValid(0, 1));
      Assert.IsFalse(field.ChunkValid(1, 1));
    }

    [TestMethod]
    public void AddEntity_RecordsRawCount_NotMean() {
      // 3 entity hits of channel 0 in chunk (0,0). Without any AddTile
      // calls the chunk is still invalid (scalar count = 0). With one
      // AddTile to flip validity, the entity count remains 3 (raw, not
      // averaged by tile count).
      var b = new RegionEcologyFieldBuilder(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 1);
      b.AddEntity(0, 0, entityIndex: 0);
      b.AddEntity(1, 1, entityIndex: 0);
      b.AddEntity(2, 2, entityIndex: 0);

      var fieldNoScalar = b.Build();
      Assert.IsFalse(fieldNoScalar.ChunkValid(0, 0), "entity-only chunk is invalid (no scalar tiles)");

      // Re-build with one scalar tile to make it valid; entity count should be 3.
      var b2 = new RegionEcologyFieldBuilder(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 1);
      b2.AddEntity(0, 0, entityIndex: 0);
      b2.AddEntity(1, 1, entityIndex: 0);
      b2.AddEntity(2, 2, entityIndex: 0);
      b2.AddTile(0, 0, waterDepth: 0f, waterFlowMagnitude: 0f, isMoist: false, isContaminated: false);
      var field = b2.Build();

      Assert.IsTrue(field.ChunkValid(0, 0));
      Assert.AreEqual(3f, field.ChunkValueEntity(0, 0, 0), 1e-6f);
    }

    [TestMethod]
    public void AddTile_OutOfBbox_IsSilentlyDropped() {
      // Origin (10, 10), 1 chunk (covers tiles 10..13 in both axes).
      // Tile (5, 5) is outside bbox → must not contribute, not even to
      // the IsMoist fraction. Only the in-bbox tile (10, 10) is counted,
      // and its IsMoist is true → fraction 1.0.
      var b = new RegionEcologyFieldBuilder(originX: 10, originY: 10, chunksX: 1, chunksY: 1, entityChannelCount: 0);
      b.AddTile(5, 5,   waterDepth: 0f, waterFlowMagnitude: 0f, isMoist: true,  isContaminated: false);
      b.AddTile(10, 10, waterDepth: 0f, waterFlowMagnitude: 0f, isMoist: true,  isContaminated: false);

      var field = b.Build();

      Assert.IsTrue(field.ChunkValid(0, 0));
      Assert.AreEqual(1f, field.ChunkValue(EcologyChannel.Moisture, 0, 0), 1e-6f);
    }

    [TestMethod]
    public void Build_PreservesAllFourFixedChannelsIndependently() {
      // One tile with distinct values per channel. Continuous channels
      // (water depth, flow) take their raw value; predicate channels
      // (moisture, contamination) take 1.0 when the predicate is true,
      // 0.0 when false. Verify each channel returns its expected value
      // (no cross-pollination from layout bugs).
      var b = new RegionEcologyFieldBuilder(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 0);
      b.AddTile(0, 0, waterDepth: 1.1f, waterFlowMagnitude: 2.2f, isMoist: true, isContaminated: false);
      var field = b.Build();

      Assert.AreEqual(1.1f, field.ChunkValue(EcologyChannel.WaterDepth, 0, 0), 1e-6f);
      Assert.AreEqual(2.2f, field.ChunkValue(EcologyChannel.WaterFlowMagnitude, 0, 0), 1e-6f);
      Assert.AreEqual(1f, field.ChunkValue(EcologyChannel.Moisture, 0, 0), 1e-6f);
      Assert.AreEqual(0f, field.ChunkValue(EcologyChannel.Contamination, 0, 0), 1e-6f);
    }

    [TestMethod]
    public void Build_MultipleEntityChannels_KeepsCountsSeparate() {
      // 2 entity channels, distinct counts per chunk.
      var b = new RegionEcologyFieldBuilder(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 2);
      b.AddTile(0, 0, waterDepth: 0f, waterFlowMagnitude: 0f, isMoist: false, isContaminated: false);  // valid chunk
      b.AddEntity(0, 0, entityIndex: 0);
      b.AddEntity(1, 1, entityIndex: 0);            // 2 of channel 0
      b.AddEntity(2, 2, entityIndex: 1);            // 1 of channel 1
      var field = b.Build();

      Assert.AreEqual(2f, field.ChunkValueEntity(0, 0, 0), 1e-6f);
      Assert.AreEqual(1f, field.ChunkValueEntity(1, 0, 0), 1e-6f);
    }

  }

}
