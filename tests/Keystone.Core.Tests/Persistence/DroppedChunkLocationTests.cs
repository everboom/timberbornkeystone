using System.Collections.Generic;
using Keystone.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Persistence {

  /// <summary>
  /// Pins <see cref="DroppedChunkLocation"/>'s tile-bounds math and the
  /// <see cref="DroppedChunkLocation.Summarize"/> formatter the load-time
  /// and per-flush "chunk(s) could not be matched to a region" diagnostics
  /// use to name <i>where</i> on the map ecology data was lost.
  /// </summary>
  [TestClass]
  public sealed class DroppedChunkLocationTests {

    private const int ChunkSize = 16;

    #region TileBounds / Describe

    [TestMethod]
    public void TileBounds_MultipliesChunkCoordsByChunkSize_InclusiveUpperBound() {
      // Arrange
      var loc = new DroppedChunkLocation(ChunkX: 2, ChunkY: 1, Z: 5);

      // Act
      var (x0, y0, x1, y1) = loc.TileBounds(ChunkSize);

      // Assert: chunk (2,1) covers tiles [32..47, 16..31] at ChunkSize 16.
      Assert.AreEqual(32, x0);
      Assert.AreEqual(16, y0);
      Assert.AreEqual(47, x1, "upper bound is inclusive (x0 + ChunkSize - 1)");
      Assert.AreEqual(31, y1, "upper bound is inclusive (y0 + ChunkSize - 1)");
    }

    [TestMethod]
    public void Describe_KnownZ_RendersTileSpanAndLayer() {
      // Arrange
      var loc = new DroppedChunkLocation(ChunkX: 2, ChunkY: 1, Z: 5);

      // Act
      var text = loc.Describe(ChunkSize);

      // Assert
      Assert.AreEqual("tiles (32..47, 16..31) Z=5", text);
    }

    [TestMethod]
    public void Describe_NullZ_RendersQuestionMark() {
      // A v1 save record with no representative surface has no Z anchor;
      // the location is still useful spatially, so it renders Z=?.
      // Arrange
      var loc = new DroppedChunkLocation(ChunkX: 0, ChunkY: 0, Z: null);

      // Act
      var text = loc.Describe(ChunkSize);

      // Assert
      Assert.AreEqual("tiles (0..15, 0..15) Z=?", text);
    }

    #endregion

    #region Summarize

    [TestMethod]
    public void Summarize_EmptyOrNullSample_ReturnsEmptyString() {
      // Callers append the result unconditionally, so "nothing to report"
      // must be the empty string, not a dangling "near ".
      Assert.AreEqual("", DroppedChunkLocation.Summarize(null, totalAreas: 0, ChunkSize));
      Assert.AreEqual("", DroppedChunkLocation.Summarize(
          new List<DroppedChunkLocation>(), totalAreas: 0, ChunkSize));
    }

    [TestMethod]
    public void Summarize_SampleEqualsTotal_NoMoreTail() {
      // Arrange: the whole drop fits in the sample.
      var sample = new List<DroppedChunkLocation> {
        new DroppedChunkLocation(2, 1, 5),
        new DroppedChunkLocation(0, 0, 3),
      };

      // Act
      var text = DroppedChunkLocation.Summarize(sample, totalAreas: 2, ChunkSize);

      // Assert
      Assert.AreEqual(
          "near tiles (32..47, 16..31) Z=5; tiles (0..15, 0..15) Z=3", text);
    }

    [TestMethod]
    public void Summarize_TotalExceedsSample_AppendsMoreTail() {
      // Arrange: sample is a 1-of-4 preview.
      var sample = new List<DroppedChunkLocation> {
        new DroppedChunkLocation(2, 1, 5),
      };

      // Act
      var text = DroppedChunkLocation.Summarize(sample, totalAreas: 4, ChunkSize);

      // Assert: the elided count is total - sample.Count = 3.
      Assert.AreEqual("near tiles (32..47, 16..31) Z=5 (+3 more area(s))", text);
    }

    #endregion

  }

}
