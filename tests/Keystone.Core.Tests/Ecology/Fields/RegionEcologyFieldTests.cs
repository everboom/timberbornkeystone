using System;
using Keystone.Core.Ecology.Fields;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Ecology.Fields {

  /// <summary>
  /// Locks down the bilinear sampler and raw-access surface of
  /// <see cref="RegionEcologyField"/>. Tests construct fields directly via
  /// the internal constructor (Keystone.Core.Tests has
  /// InternalsVisibleTo) so the math can be exercised without going
  /// through the builder; <see cref="RegionEcologyFieldBuilderTests"/>
  /// covers the builder's accumulation path.
  /// </summary>
  [TestClass]
  public class RegionEcologyFieldTests {

    #region Helpers

    /// <summary>
    /// Construct a field with given chunk dimensions, channel values, and
    /// validity flags. <paramref name="scalarValues"/> is laid out
    /// <c>[channel, cy * chunksX + cx]</c>; pass null for entity data when
    /// the test doesn't care.
    /// </summary>
    private static RegionEcologyField Build(
        int chunksX, int chunksY,
        float[][] scalarValues,
        bool[] valid,
        float[][]? entityValues = null) {
      var entityCount = entityValues?.Length ?? 0;
      var totalChannels = RegionEcologyField.FixedChannelCount + entityCount;
      var totalChunks = chunksX * chunksY;
      var data = new float[totalChannels * totalChunks];
      for (var ch = 0; ch < RegionEcologyField.FixedChannelCount; ch++) {
        for (var i = 0; i < totalChunks; i++) {
          data[ch * totalChunks + i] = scalarValues[ch][i];
        }
      }
      if (entityValues != null) {
        for (var e = 0; e < entityCount; e++) {
          for (var i = 0; i < totalChunks; i++) {
            data[(RegionEcologyField.FixedChannelCount + e) * totalChunks + i] = entityValues[e][i];
          }
        }
      }
      // Helper-built fields use a uniform sample count of 1 for every
      // valid chunk — the tile-weighted aggregates aren't exercised by
      // the field tests themselves; they're covered in the cluster
      // tests where sample counts are varied deliberately.
      var sampleCounts = new int[totalChunks];
      for (var i = 0; i < totalChunks; i++) {
        if (valid[i]) sampleCounts[i] = 1;
      }
      return new RegionEcologyField(
          originX: 0, originY: 0,
          chunksX: chunksX, chunksY: chunksY,
          entityChannelCount: entityCount,
          data: data, valid: valid, sampleCounts: sampleCounts);
    }

    private static float[][] Scalars(
        float[] depth, float[] flow, float[] moist, float[] contam,
        float[]? waterContam = null)
        => new[] {
            depth, flow, moist, contam,
            waterContam ?? new float[depth.Length],
        };

    /// <summary>Convenience: produce a [n] array filled with one value (used when only one channel matters in a test).</summary>
    private static float[] Filled(int n, float value) {
      var a = new float[n];
      for (var i = 0; i < n; i++) a[i] = value;
      return a;
    }

    #endregion

    #region Single-chunk field

    [TestMethod]
    public void SingleChunk_SampleAtCentre_ReturnsChunkValue() {
      // Arrange — one chunk; moisture=0.7, all other channels zero, valid.
      var field = Build(
          chunksX: 1, chunksY: 1,
          scalarValues: Scalars(Filled(1, 0f), Filled(1, 0f), new[] { 0.7f }, Filled(1, 0f)),
          valid: new[] { true });

      // Act — sample at chunk centre (tile 1.5, 1.5 for ChunkSize=4 origin 0).
      var v = field.Sample(EcologyChannel.Moisture, 1.5f, 1.5f);

      // Assert
      Assert.AreEqual(0.7f, v, 1e-6f);
    }

    [TestMethod]
    public void SingleChunk_SampleAnywhere_ReturnsSameValue() {
      // With one chunk, every tile in the bbox should sample to the same value
      // (clamping pins the lerp to that single chunk's centre).
      var field = Build(
          chunksX: 1, chunksY: 1,
          scalarValues: Scalars(new[] { 0.5f }, Filled(1, 0f), Filled(1, 0f), Filled(1, 0f)),
          valid: new[] { true });

      Assert.AreEqual(0.5f, field.Sample(EcologyChannel.WaterDepth, 0f, 0f), 1e-6f);
      Assert.AreEqual(0.5f, field.Sample(EcologyChannel.WaterDepth, 3f, 3f), 1e-6f);
      Assert.AreEqual(0.5f, field.Sample(EcologyChannel.WaterDepth, 1.5f, 1.5f), 1e-6f);
    }

    [TestMethod]
    public void SingleChunk_Invalid_ReturnsZero() {
      // No valid corners → zero, regardless of stored value.
      var field = Build(
          chunksX: 1, chunksY: 1,
          scalarValues: Scalars(Filled(1, 0f), Filled(1, 0f), new[] { 0.7f }, Filled(1, 0f)),
          valid: new[] { false });

      Assert.AreEqual(0f, field.Sample(EcologyChannel.Moisture, 1.5f, 1.5f), 1e-6f);
    }

    #endregion

    #region 2x2 bilinear interior

    [TestMethod]
    public void TwoByTwo_SampleAtTrueCentre_ReturnsAverageOfFourCorners() {
      // 2x2 chunks, moisture: TL=0, TR=1, BL=2, BR=3. The point exactly
      // between all four chunk centres should yield the mean = 1.5.
      // Layout in the flat array: [TL, TR, BL, BR] = [cy=0,cx=0], [cy=0,cx=1], [cy=1,cx=0], [cy=1,cx=1].
      var moist = new[] { 0f, 1f, 2f, 3f };
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), moist, Filled(4, 0)),
          valid: new[] { true, true, true, true });

      // Chunk centres at tile (1.5, 1.5), (5.5, 1.5), (1.5, 5.5), (5.5, 5.5).
      // True centre between all four is at (3.5, 3.5).
      var v = field.Sample(EcologyChannel.Moisture, 3.5f, 3.5f);
      Assert.AreEqual(1.5f, v, 1e-6f);
    }

    [TestMethod]
    public void TwoByTwo_SampleAtChunkCentre_ReturnsThatChunksValue() {
      // Sampling exactly at one chunk's centre should return that chunk's value
      // (bilinear weights collapse to that corner).
      var moist = new[] { 0f, 1f, 2f, 3f };
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), moist, Filled(4, 0)),
          valid: new[] { true, true, true, true });

      Assert.AreEqual(0f, field.Sample(EcologyChannel.Moisture, 1.5f, 1.5f), 1e-6f);
      Assert.AreEqual(1f, field.Sample(EcologyChannel.Moisture, 5.5f, 1.5f), 1e-6f);
      Assert.AreEqual(2f, field.Sample(EcologyChannel.Moisture, 1.5f, 5.5f), 1e-6f);
      Assert.AreEqual(3f, field.Sample(EcologyChannel.Moisture, 5.5f, 5.5f), 1e-6f);
    }

    #endregion

    #region Renormalisation when corners are invalid

    [TestMethod]
    public void TwoByTwo_OneCornerInvalid_RenormalisesOverRemainingThree() {
      // TL=10, TR=10, BL=10, BR=invalid. At the centre point, bilinear
      // weights are equal (0.25 each). With BR dropped, valid weights
      // sum to 0.75, all three remaining values are 10, so result = 10.
      // Then bump TR to 6 to verify renormalisation isn't just averaging:
      // result should be (10*0.25 + 6*0.25 + 10*0.25) / 0.75 = 26/3 ≈ 8.667.
      var moist = new[] { 10f, 6f, 10f, 999f };  // BR=999 should be ignored
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), moist, Filled(4, 0)),
          valid: new[] { true, true, true, false });

      var v = field.Sample(EcologyChannel.Moisture, 3.5f, 3.5f);
      Assert.AreEqual(26f / 3f, v, 1e-5f);
    }

    [TestMethod]
    public void TwoByTwo_OnlyOneCornerValid_ReturnsThatCorner() {
      // Only TL valid; all others invalid. Sample at the centre should
      // collapse to TL's value irrespective of where in the field it is.
      var moist = new[] { 0.42f, 999f, 999f, 999f };
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), moist, Filled(4, 0)),
          valid: new[] { true, false, false, false });

      var v = field.Sample(EcologyChannel.Moisture, 3.5f, 3.5f);
      Assert.AreEqual(0.42f, v, 1e-6f);
    }

    [TestMethod]
    public void AllCornersInvalid_ReturnsZero() {
      // Pathological: every chunk in the stencil is invalid → zero.
      var moist = new[] { 1f, 2f, 3f, 4f };
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), moist, Filled(4, 0)),
          valid: new[] { false, false, false, false });

      Assert.AreEqual(0f, field.Sample(EcologyChannel.Moisture, 3.5f, 3.5f), 1e-6f);
    }

    #endregion

    #region Entity channels

    [TestMethod]
    public void EntitySample_ReturnsBilinearOfEntityCounts() {
      // Single entity index. Counts: TL=4, TR=8, BL=4, BR=8. At centre,
      // bilinear of counts = (4+8+4+8)/4 = 6.
      var entity = new[] { 4f, 8f, 4f, 8f };
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), Filled(4, 0), Filled(4, 0)),
          valid: new[] { true, true, true, true },
          entityValues: new[] { entity });

      Assert.AreEqual(6f, field.SampleEntity(0, 3.5f, 3.5f), 1e-6f);
    }

    [TestMethod]
    public void RawAccess_ReturnsExactChunkValues() {
      // Verify ChunkValue / ChunkValueEntity return raw stored values
      // (no interpolation, no clamping).
      var moist = new[] { 0f, 1f, 2f, 3f };
      var entity = new[] { 5f, 10f, 15f, 20f };
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), moist, Filled(4, 0)),
          valid: new[] { true, true, true, false },
          entityValues: new[] { entity });

      Assert.AreEqual(0f, field.ChunkValue(EcologyChannel.Moisture, 0, 0));
      Assert.AreEqual(3f, field.ChunkValue(EcologyChannel.Moisture, 1, 1));
      Assert.AreEqual(20f, field.ChunkValueEntity(0, 1, 1));  // raw, even though invalid
      Assert.IsFalse(field.ChunkValid(1, 1));
      Assert.IsTrue(field.ChunkValid(0, 0));
    }

    #endregion

    #region Empty allocation + WriteChunk mutation

    [TestMethod]
    public void EmptyConstructor_AllChunksInvalid_AllValuesZero() {
      // Production allocation path: empty field, no values, no validity.
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);

      for (var cy = 0; cy < 2; cy++) {
        for (var cx = 0; cx < 2; cx++) {
          Assert.IsFalse(field.ChunkValid(cx, cy));
          Assert.AreEqual(0f, field.ChunkValue(EcologyChannel.Moisture, cx, cy));
        }
      }
      // Sampling on a fully-invalid field returns 0.
      Assert.AreEqual(0f, field.Sample(EcologyChannel.Moisture, 3.5f, 3.5f));
    }

    [TestMethod]
    public void WriteChunk_OverwritesValuesAndValidity() {
      // Empty field -> write to one chunk -> sample reflects the new value.
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 1);

      var scalars = new float[] {
          1f,    // WaterDepth
          2f,    // WaterFlowMagnitude
          0.5f,  // Moisture
          3f,    // Contamination
          0f,    // WaterContamination
      };
      var entities = new float[] { 7f };
      field.WriteChunk(cx: 0, cy: 0, valid: true, sampleCount: 1, scalars, entities);

      Assert.IsTrue(field.ChunkValid(0, 0));
      Assert.AreEqual(0.5f, field.ChunkValue(EcologyChannel.Moisture, 0, 0));
      Assert.AreEqual(7f, field.ChunkValueEntity(0, 0, 0));
      // Other chunks still invalid.
      Assert.IsFalse(field.ChunkValid(1, 0));
      Assert.IsFalse(field.ChunkValid(0, 1));
      Assert.IsFalse(field.ChunkValid(1, 1));
    }

    [TestMethod]
    public void WriteChunk_OverwriteAllFour_ProducesUniformBilinear() {
      // Write the same value to all four chunks; sample anywhere should return that value.
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);
      var scalars = new float[] { 0f, 0f, 0.42f, 0f, 0f };
      var entities = ReadOnlySpan<float>.Empty;

      for (var cy = 0; cy < 2; cy++) {
        for (var cx = 0; cx < 2; cx++) {
          field.WriteChunk(cx, cy, valid: true, sampleCount: 1, scalars, entities);
        }
      }

      Assert.AreEqual(0.42f, field.Sample(EcologyChannel.Moisture, 3.5f, 3.5f), 1e-6f);
      Assert.AreEqual(0.42f, field.Sample(EcologyChannel.Moisture, 1.5f, 1.5f), 1e-6f);
    }

    [TestMethod]
    public void WriteChunk_ChangedValueIsImmediatelyVisible() {
      // First write -> sample sees value. Second write to same chunk -> sample sees new value.
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 0);
      var first = new float[] { 0f, 0f, 0.1f, 0f, 0f };
      var second = new float[] { 0f, 0f, 0.9f, 0f, 0f };

      field.WriteChunk(0, 0, valid: true, sampleCount: 1, first, ReadOnlySpan<float>.Empty);
      Assert.AreEqual(0.1f, field.Sample(EcologyChannel.Moisture, 1.5f, 1.5f), 1e-6f);

      field.WriteChunk(0, 0, valid: true, sampleCount: 1, second, ReadOnlySpan<float>.Empty);
      Assert.AreEqual(0.9f, field.Sample(EcologyChannel.Moisture, 1.5f, 1.5f), 1e-6f);
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void WriteChunk_WrongScalarLength_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 0);
      var bad = new float[] { 1f, 2f };  // fewer than FixedChannelCount entries
      field.WriteChunk(0, 0, valid: true, sampleCount: 1, bad, ReadOnlySpan<float>.Empty);
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void WriteChunk_WrongEntityLength_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 2);
      var ok = new float[] { 0f, 0f, 0f, 0f, 0f };
      var bad = new float[] { 1f };  // only 1 of 2 entity channels
      field.WriteChunk(0, 0, valid: true, sampleCount: 1, ok, bad);
    }

    #endregion

    #region Public-constructor preconditions

    /// <summary>
    /// Pins the design constraint that a field must contain at least one chunk
    /// along X. Zero/negative chunk dimensions would produce a zero-length
    /// backing array; downstream samplers index it unconditionally, so a
    /// silent zero-size construction would NRE far from the cause. The public
    /// constructor fails loudly at the API boundary instead.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void PublicConstructor_ChunksXZero_Throws() {
      _ = new RegionEcologyField(originX: 0, originY: 0, chunksX: 0, chunksY: 1, entityChannelCount: 0);
    }

    /// <summary>
    /// Companion to <see cref="PublicConstructor_ChunksXZero_Throws"/> for the
    /// Y axis. Both axes need to be guarded — only one was previously
    /// exercised in tests.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void PublicConstructor_ChunksYZero_Throws() {
      _ = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 0, entityChannelCount: 0);
    }

    /// <summary>
    /// Pins the constraint that the public constructor rejects a negative
    /// chunk count on X (separate branch from "zero" because the guard is
    /// `<= 0`). Negative values would compute a negative array length and
    /// throw deep in the runtime instead of at the API boundary.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void PublicConstructor_ChunksXNegative_Throws() {
      _ = new RegionEcologyField(originX: 0, originY: 0, chunksX: -1, chunksY: 1, entityChannelCount: 0);
    }

    /// <summary>
    /// Companion to <see cref="PublicConstructor_ChunksXNegative_Throws"/>.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void PublicConstructor_ChunksYNegative_Throws() {
      _ = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: -1, entityChannelCount: 0);
    }

    /// <summary>
    /// Pins the constraint that entity-channel count must be non-negative.
    /// A negative count would silently compute a smaller-than-expected
    /// backing array (because `FixedChannelCount + negative` is still
    /// positive at small magnitudes), letting fixed-channel writes
    /// succeed while entity reads NRE downstream. The constructor
    /// rejects it at the boundary instead.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void PublicConstructor_NegativeEntityChannelCount_Throws() {
      _ = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: -1);
    }

    /// <summary>
    /// Pins that <c>entityChannelCount == 0</c> is a legal configuration
    /// (regions with no catalogued entity blueprints). Distinct from the
    /// negative-count guard above — the boundary between "valid empty" and
    /// "invalid negative" is part of the design.
    /// </summary>
    [TestMethod]
    public void PublicConstructor_ZeroEntityChannelCount_Succeeds() {
      // Arrange / Act
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 0);

      // Assert
      Assert.AreEqual(0, field.EntityChannelCount);
      Assert.AreEqual(1, field.ChunksX);
      Assert.AreEqual(1, field.ChunksY);
    }

    /// <summary>
    /// Pins that the public constructor preserves the supplied origin
    /// coordinates verbatim (including negative values — Timberborn region
    /// bounding boxes may sit at negative tile coordinates).
    /// </summary>
    [TestMethod]
    public void PublicConstructor_OriginCoordinatesStoredVerbatim() {
      // Arrange / Act
      var field = new RegionEcologyField(originX: -7, originY: 13, chunksX: 2, chunksY: 3, entityChannelCount: 4);

      // Assert
      Assert.AreEqual(-7, field.OriginX);
      Assert.AreEqual(13, field.OriginY);
      Assert.AreEqual(2, field.ChunksX);
      Assert.AreEqual(3, field.ChunksY);
      Assert.AreEqual(4, field.EntityChannelCount);
    }

    #endregion

    #region Internal-constructor preconditions

    /// <summary>
    /// Pins the constraint that the internal (builder/test) constructor
    /// guards chunk dimensions even though callers are trusted. The same
    /// invariant the public constructor enforces — at least one chunk
    /// along each axis — applies here so the builder path can't smuggle
    /// in degenerate fields.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void InternalConstructor_ChunksXZero_Throws() {
      _ = new RegionEcologyField(
          originX: 0, originY: 0, chunksX: 0, chunksY: 1, entityChannelCount: 0,
          data: new float[0], valid: new bool[0], sampleCounts: new int[0]);
    }

    /// <summary>
    /// Companion to <see cref="InternalConstructor_ChunksXZero_Throws"/>.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void InternalConstructor_ChunksYZero_Throws() {
      _ = new RegionEcologyField(
          originX: 0, originY: 0, chunksX: 1, chunksY: 0, entityChannelCount: 0,
          data: new float[0], valid: new bool[0], sampleCounts: new int[0]);
    }

    /// <summary>
    /// Pins that the internal constructor validates the data array length
    /// against <c>(FixedChannelCount + entityChannelCount) * chunksX * chunksY</c>.
    /// A length mismatch would otherwise cause every channel offset to read
    /// stale or out-of-range memory; the explicit precondition fails loudly
    /// at construction.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void InternalConstructor_DataLengthMismatch_Throws() {
      var expected = RegionEcologyField.FixedChannelCount * 2 * 2;  // 20
      var tooShort = new float[expected - 1];
      _ = new RegionEcologyField(
          originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0,
          data: tooShort, valid: new bool[4], sampleCounts: new int[4]);
    }

    /// <summary>
    /// Pins that the internal constructor validates the valid-flag array
    /// length against <c>chunksX * chunksY</c>. Validity is the gate every
    /// bilinear sample passes through; a mis-sized array would corrupt
    /// every downstream renormalisation.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void InternalConstructor_ValidLengthMismatch_Throws() {
      var dataLen = RegionEcologyField.FixedChannelCount * 2 * 2;
      _ = new RegionEcologyField(
          originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0,
          data: new float[dataLen], valid: new bool[3], sampleCounts: new int[4]);
    }

    /// <summary>
    /// Pins that the internal constructor validates the sample-counts array
    /// length. Cluster-aggregate weighting reads these per-chunk; a length
    /// mismatch would silently misweight aggregates.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void InternalConstructor_SampleCountsLengthMismatch_Throws() {
      var dataLen = RegionEcologyField.FixedChannelCount * 2 * 2;
      _ = new RegionEcologyField(
          originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0,
          data: new float[dataLen], valid: new bool[4], sampleCounts: new int[3]);
    }

    /// <summary>
    /// Pins that the internal constructor accepts entity channels in the
    /// data-length calculation. Distinct from the no-entity path above —
    /// the formula <c>(FixedChannelCount + entityChannelCount)</c> needs
    /// to be exercised with a non-zero entity count to confirm the
    /// expected-length math.
    /// </summary>
    [TestMethod]
    public void InternalConstructor_WithEntityChannels_AcceptsCorrectLength() {
      // Arrange — 2 entity channels, 2x2 chunks → (5 + 2) * 4 = 28
      var dataLen = (RegionEcologyField.FixedChannelCount + 2) * 2 * 2;

      // Act
      var field = new RegionEcologyField(
          originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 2,
          data: new float[dataLen], valid: new bool[4], sampleCounts: new int[4]);

      // Assert
      Assert.AreEqual(2, field.EntityChannelCount);
    }

    #endregion

    #region WriteChunk coordinate and sample-count guards

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.WriteChunk"/> rejects negative
    /// chunk-X indices. The polling updater computes chunk coordinates from
    /// region geometry; a negative index here would mean a logic bug
    /// upstream, and silently writing past the start of the backing array
    /// (or no-op) would mask it.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void WriteChunk_NegativeCx_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);
      var scalars = new float[RegionEcologyField.FixedChannelCount];
      field.WriteChunk(cx: -1, cy: 0, valid: true, sampleCount: 1, scalars, ReadOnlySpan<float>.Empty);
    }

    /// <summary>
    /// Pins the upper bound on the chunk-X index: <c>cx == ChunksX</c> is
    /// out of range (the valid range is [0, ChunksX)). This is the boundary
    /// case most likely to slip past a `&lt;` vs `&lt;=` thinko.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void WriteChunk_CxEqualToChunksX_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);
      var scalars = new float[RegionEcologyField.FixedChannelCount];
      field.WriteChunk(cx: 2, cy: 0, valid: true, sampleCount: 1, scalars, ReadOnlySpan<float>.Empty);
    }

    /// <summary>
    /// Companion to <see cref="WriteChunk_NegativeCx_Throws"/> for the Y axis.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void WriteChunk_NegativeCy_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);
      var scalars = new float[RegionEcologyField.FixedChannelCount];
      field.WriteChunk(cx: 0, cy: -1, valid: true, sampleCount: 1, scalars, ReadOnlySpan<float>.Empty);
    }

    /// <summary>
    /// Companion to <see cref="WriteChunk_CxEqualToChunksX_Throws"/> for Y.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void WriteChunk_CyEqualToChunksY_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);
      var scalars = new float[RegionEcologyField.FixedChannelCount];
      field.WriteChunk(cx: 0, cy: 2, valid: true, sampleCount: 1, scalars, ReadOnlySpan<float>.Empty);
    }

    /// <summary>
    /// Pins that sample count must be non-negative. Sample counts are used
    /// as tile-weights in cluster aggregates; a negative weight would
    /// produce nonsensical weighted means that propagate silently.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void WriteChunk_NegativeSampleCount_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 0);
      var scalars = new float[RegionEcologyField.FixedChannelCount];
      field.WriteChunk(cx: 0, cy: 0, valid: true, sampleCount: -1, scalars, ReadOnlySpan<float>.Empty);
    }

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.ChunkSampleCount"/> returns
    /// exactly the value passed at the most recent <c>WriteChunk</c>. This
    /// is load-bearing for cluster-aggregate weighting where multi-Z chunks
    /// (cliff faces) intentionally count each Z surface; the value must
    /// not be silently clamped or normalised.
    /// </summary>
    [TestMethod]
    public void WriteChunk_SampleCount_RoundTripsThroughChunkSampleCount() {
      // Arrange
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 0);
      var scalars = new float[RegionEcologyField.FixedChannelCount];

      // Act
      field.WriteChunk(cx: 1, cy: 0, valid: true, sampleCount: 17, scalars, ReadOnlySpan<float>.Empty);

      // Assert
      Assert.AreEqual(17, field.ChunkSampleCount(1, 0));
      Assert.AreEqual(0, field.ChunkSampleCount(0, 0));  // never written
    }

    #endregion

    #region Entity-index bounds on sample / chunk-value / raw-grid

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.SampleEntity"/> rejects a
    /// negative entity index. The catalog hands out indices [0, N); a
    /// negative one means the consumer is looking up an entity that doesn't
    /// exist, and silently sampling fixed channel 5 (WaterContamination,
    /// at offset FixedChannelCount + (-1)) would return values from a
    /// completely unrelated channel.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void SampleEntity_NegativeIndex_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 2);
      _ = field.SampleEntity(-1, 1.5f, 1.5f);
    }

    /// <summary>
    /// Pins the upper bound on <see cref="RegionEcologyField.SampleEntity"/>:
    /// <c>entityIndex == EntityChannelCount</c> is out of range. Caught at
    /// the API boundary rather than indexing past the data array.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void SampleEntity_IndexEqualToEntityChannelCount_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 2);
      _ = field.SampleEntity(2, 1.5f, 1.5f);
    }

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.ChunkValueEntity"/> rejects a
    /// negative entity index. Tooling/debug overlays call this and would
    /// otherwise read raw bytes from a fixed channel under the wrong label.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void ChunkValueEntity_NegativeIndex_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 2);
      _ = field.ChunkValueEntity(-1, 0, 0);
    }

    /// <summary>
    /// Companion upper-bound guard for <see cref="RegionEcologyField.ChunkValueEntity"/>.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void ChunkValueEntity_IndexEqualToEntityChannelCount_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 1, chunksY: 1, entityChannelCount: 2);
      _ = field.ChunkValueEntity(2, 0, 0);
    }

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.RawGridEntity"/> rejects a
    /// negative entity index. Returning a span at an arbitrary offset
    /// would silently expose a fixed channel as "entity data" to tooling
    /// and overlays, painting the wrong field on the debug overlay.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void RawGridEntity_NegativeIndex_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 2);
      _ = field.RawGridEntity(-1).Length;
    }

    /// <summary>
    /// Companion upper-bound guard for <see cref="RegionEcologyField.RawGridEntity"/>.
    /// </summary>
    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void RawGridEntity_IndexEqualToEntityChannelCount_Throws() {
      var field = new RegionEcologyField(originX: 0, originY: 0, chunksX: 2, chunksY: 2, entityChannelCount: 2);
      _ = field.RawGridEntity(2).Length;
    }

    #endregion

    #region Raw-grid views

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.RawGrid"/> returns the
    /// per-channel flat grid in <c>cy * ChunksX + cx</c> order, of length
    /// <c>ChunksX * ChunksY</c>. Debug overlays paint chunks in this order,
    /// so any reshuffling would scramble the overlay silently.
    /// </summary>
    [TestMethod]
    public void RawGrid_ReturnsFlatChunkValuesInRowMajorOrder() {
      // Arrange — 2x2 moisture grid with distinct values per chunk.
      var moist = new[] { 0.1f, 0.2f, 0.3f, 0.4f };  // (0,0), (1,0), (0,1), (1,1)
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), moist, Filled(4, 0)),
          valid: new[] { true, true, true, true });

      // Act
      var span = field.RawGrid(EcologyChannel.Moisture);

      // Assert
      Assert.AreEqual(4, span.Length);
      Assert.AreEqual(0.1f, span[0], 1e-6f);
      Assert.AreEqual(0.2f, span[1], 1e-6f);
      Assert.AreEqual(0.3f, span[2], 1e-6f);
      Assert.AreEqual(0.4f, span[3], 1e-6f);
    }

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.RawGrid"/> isolates channels
    /// in the backing array. Distinct channels write to distinct slabs;
    /// without this, a per-channel debug overlay could leak values from
    /// a sibling channel.
    /// </summary>
    [TestMethod]
    public void RawGrid_DifferentChannelsReturnDistinctSlabs() {
      // Arrange — depth and moisture set to different values per chunk.
      var depth = new[] { 1f, 2f, 3f, 4f };
      var moist = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(depth, Filled(4, 0), moist, Filled(4, 0)),
          valid: new[] { true, true, true, true });

      // Act
      var depthSpan = field.RawGrid(EcologyChannel.WaterDepth);
      var moistSpan = field.RawGrid(EcologyChannel.Moisture);

      // Assert
      Assert.AreEqual(1f, depthSpan[0], 1e-6f);
      Assert.AreEqual(4f, depthSpan[3], 1e-6f);
      Assert.AreEqual(0.1f, moistSpan[0], 1e-6f);
      Assert.AreEqual(0.4f, moistSpan[3], 1e-6f);
    }

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.RawGridEntity"/> returns the
    /// entity channel's flat grid (and not a fixed channel's). Off-by-one
    /// in the channel-offset math would silently swap entity counts for
    /// fixed scalars, which is a particularly nasty bug because both are
    /// floats and the values look plausible.
    /// </summary>
    [TestMethod]
    public void RawGridEntity_ReturnsEntityChannelValues() {
      // Arrange — set distinguishing values: depth=99 across the board,
      // entity counts unique per chunk. The entity span must not see 99.
      var entity = new[] { 5f, 10f, 15f, 20f };
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 99f), Filled(4, 0), Filled(4, 0), Filled(4, 0)),
          valid: new[] { true, true, true, true },
          entityValues: new[] { entity });

      // Act
      var span = field.RawGridEntity(0);

      // Assert
      Assert.AreEqual(4, span.Length);
      Assert.AreEqual(5f, span[0], 1e-6f);
      Assert.AreEqual(10f, span[1], 1e-6f);
      Assert.AreEqual(15f, span[2], 1e-6f);
      Assert.AreEqual(20f, span[3], 1e-6f);
    }

    /// <summary>
    /// Pins that <see cref="RegionEcologyField.ValidFlags"/> exposes the
    /// per-chunk validity vector in the same row-major order as
    /// <see cref="RegionEcologyField.RawGrid"/>. Debug overlays cross-reference
    /// the two: a value at index <c>i</c> in <c>RawGrid</c> is meaningful
    /// iff <c>ValidFlags[i]</c> is true. The orderings must match.
    /// </summary>
    [TestMethod]
    public void ValidFlags_ReturnsRowMajorValidityVector() {
      // Arrange — distinct validity per chunk so any reordering shows up.
      var field = Build(
          chunksX: 2, chunksY: 2,
          scalarValues: Scalars(Filled(4, 0), Filled(4, 0), Filled(4, 0), Filled(4, 0)),
          valid: new[] { true, false, false, true });

      // Act
      var flags = field.ValidFlags;

      // Assert
      Assert.AreEqual(4, flags.Length);
      Assert.IsTrue(flags[0]);
      Assert.IsFalse(flags[1]);
      Assert.IsFalse(flags[2]);
      Assert.IsTrue(flags[3]);
    }

    #endregion

  }

}
