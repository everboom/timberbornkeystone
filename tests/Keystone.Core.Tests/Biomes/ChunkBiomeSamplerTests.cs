using Keystone.Core.Biomes;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Biomes {

  [TestClass]
  public class ChunkBiomeSamplerTests {

    private static readonly RegionId Region = new(1);
    private const BiomeKind Biome = BiomeKind.Forest;

    /// <summary>Build a chunk score store seeded with the given Score
    /// values for <see cref="Biome"/>. Each entry is a (cx, cy, score)
    /// triple. Other biomes' channels left untouched.</summary>
    private static ChunkValueStore Store(params (int cx, int cy, float score)[] entries) {
      var store = new ChunkValueStore();
      var kind = BiomeValueKinds.ForSuitability(Biome);
      foreach (var (cx, cy, score) in entries) {
        store.Set(Region, cx, cy, kind, score);
      }
      return store;
    }

    private static float Sample(ChunkValueStore store, float tileX, float tileY,
                                int chunksX = 2, int chunksY = 2,
                                int originTileX = 0, int originTileY = 0) {
      return ChunkBiomeSampler.SampleSuitability(
          new ChunkValueStoreReader(store), Region, Biome,
          originTileX, originTileY,
          chunksX, chunksY,
          tileX, tileY);
    }

    #region Centre / lerp

    [TestMethod]
    public void Sample_AtChunkCentre_ReturnsChunkScore() {
      // Chunk (0,0) covers tiles 0..3; centre at tile (1.5, 1.5).
      var store = Store((0, 0, 0.7f));
      Assert.AreEqual(0.7f, Sample(store, 1.5f, 1.5f, chunksX: 1, chunksY: 1), 1e-4f);
    }

    [TestMethod]
    public void Sample_AtMidpointOfTwoChunks_LerpsHalfAndHalf() {
      // Two chunks horizontally; centres at tile x=1.5 and x=5.5.
      // Midpoint x=3.5 should give the average of the two scores.
      var store = Store(
          (0, 0, 1f),
          (1, 0, 0f));
      Assert.AreEqual(0.5f, Sample(store, 3.5f, 1.5f), 1e-4f);
    }

    [TestMethod]
    public void Sample_AtThreeQuarters_LerpsProportionally() {
      // Three quarters from chunk (0,0)'s centre toward chunk (1,0)'s centre.
      // Linear weight: 0.25 * 1 + 0.75 * 0 = 0.25.
      var store = Store(
          (0, 0, 1f),
          (1, 0, 0f));
      Assert.AreEqual(0.25f, Sample(store, 4.5f, 1.5f), 1e-4f);
    }

    [TestMethod]
    public void Sample_FullBilinear_BlendsFourCorners() {
      // 2x2 chunks, distinct scores per corner. Sample at the centre
      // of the 2x2 (tile 3.5, 3.5) -> equal-weighted average.
      var store = Store(
          (0, 0, 0f),
          (1, 0, 1f),
          (0, 1, 1f),
          (1, 1, 0f));
      // 0.25 * (0 + 1 + 1 + 0) = 0.5
      Assert.AreEqual(0.5f, Sample(store, 3.5f, 3.5f), 1e-4f);
    }

    #endregion

    #region Edge clamp

    [TestMethod]
    public void Sample_OutsideBbox_ClampsToNearestChunkCentre() {
      // Tile at (-10, -10) should clamp to chunk (0,0)'s centre value.
      var store = Store((0, 0, 0.4f));
      Assert.AreEqual(0.4f, Sample(store, -10f, -10f, chunksX: 1, chunksY: 1), 1e-4f);
    }

    [TestMethod]
    public void Sample_PastFarEdge_ClampsToLastChunk() {
      // Tile at tile (100, 100) clamps to chunk (1,1) in a 2x2 grid.
      var store = Store(
          (0, 0, 0f),
          (1, 1, 0.8f));
      Assert.AreEqual(0.8f, Sample(store, 100f, 100f), 1e-4f);
    }

    #endregion

    #region Missing corners

    [TestMethod]
    public void Sample_OneMissingCorner_RenormalisesOverRemaining() {
      // 2x2 grid; chunk (1,1) is missing from the store. At the centre
      // of the 2x2, equal weights would be 0.25 each. With 3 valid
      // corners and the (1,1) weight removed, total weight = 0.75 and
      // each corner's contribution is renormalised by /0.75.
      var store = Store(
          (0, 0, 0f),
          (1, 0, 1f),
          (0, 1, 1f));
      // (0.25*0 + 0.25*1 + 0.25*1 + 0) / 0.75 = 0.5 / 0.75 = 0.6667.
      Assert.AreEqual(2f / 3f, Sample(store, 3.5f, 3.5f), 1e-4f);
    }

    [TestMethod]
    public void Sample_AllFourCornersMissing_ReturnsZero() {
      var store = new ChunkValueStore();
      // Empty store, sample anywhere -> 0.
      Assert.AreEqual(0f, Sample(store, 3.5f, 3.5f));
    }

    #endregion

    #region Origin offset

    [TestMethod]
    public void Sample_NonZeroOrigin_RespectsBboxShift() {
      // Region origin at tile (100, 100). Chunk (25, 25) global coords
      // covers tiles 100..103.
      var store = new ChunkValueStore();
      store.Set(Region, 25, 25, BiomeValueKinds.ForSuitability(Biome), 0.6f);
      Assert.AreEqual(0.6f,
          Sample(store, tileX: 101.5f, tileY: 101.5f,
                 chunksX: 1, chunksY: 1, originTileX: 100, originTileY: 100),
          1e-4f);
    }

    #endregion

    #region Multi-biome isolation

    [TestMethod]
    public void Sample_DifferentBiome_ReadsThatBiomesScore() {
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.9f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.2f);

      var reader = new ChunkValueStoreReader(store);
      Assert.AreEqual(0.9f, ChunkBiomeSampler.SampleSuitability(
          reader, Region, BiomeKind.Forest, 0, 0, 1, 1, 1.5f, 1.5f), 1e-4f);
      Assert.AreEqual(0.2f, ChunkBiomeSampler.SampleSuitability(
          reader, Region, BiomeKind.Grassland, 0, 0, 1, 1, 1.5f, 1.5f), 1e-4f);
    }

    [TestMethod]
    public void Sample_OtherChannelsForSameBiomeIgnored() {
      // Investment channel for Forest at the same chunk should not
      // bleed into the Score sample.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.3f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Forest), 12.5f);

      Assert.AreEqual(0.3f, ChunkBiomeSampler.SampleSuitability(
          new ChunkValueStoreReader(store), Region, BiomeKind.Forest, 0, 0, 1, 1, 1.5f, 1.5f), 1e-4f);
    }

    #endregion

    #region Investment channel

    [TestMethod]
    public void SampleInvestment_ReadsInvestmentChannelNotScore() {
      // Same chunk has both Score and Investment for Forest. The
      // Investment sampler must read the Investment value, not the
      // Score value.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.3f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Forest), 12.5f);

      Assert.AreEqual(12.5f, ChunkBiomeSampler.SampleMaturity(
          new ChunkValueStoreReader(store), Region, BiomeKind.Forest, 0, 0, 1, 1, 1.5f, 1.5f), 1e-4f);
    }

    [TestMethod]
    public void SampleInvestment_BilinearLerp_SameMathAsScore() {
      // Two chunks horizontally with distinct Investment values.
      // Midpoint should give the average.
      var store = new ChunkValueStore();
      var kind = BiomeValueKinds.ForMaturity(Biome);
      store.Set(Region, 0, 0, kind, 30f);
      store.Set(Region, 1, 0, kind, 0f);

      // Two chunks horizontally; centres at tile x=1.5 and x=5.5.
      // Midpoint x=3.5.
      Assert.AreEqual(15f, ChunkBiomeSampler.SampleMaturity(
          new ChunkValueStoreReader(store), Region, Biome, 0, 0, 2, 2, 3.5f, 1.5f), 1e-4f);
    }

    [TestMethod]
    public void SampleInvestment_NoEntries_ReturnsZero() {
      var store = new ChunkValueStore();
      Assert.AreEqual(0f, ChunkBiomeSampler.SampleMaturity(
          new ChunkValueStoreReader(store), Region, Biome, 0, 0, 2, 2, 3.5f, 3.5f));
    }

    #endregion

    #region Dominant biome

    [TestMethod]
    public void SampleDominantBiome_HighestSuitabilityWins() {
      // Highest Suitability wins regardless of Maturity magnitudes.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.7f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.9f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Wetland), 0.6f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Forest), 30f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Grassland), 5f);

      var (biome, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.AreEqual(BiomeKind.Grassland, biome);
      Assert.AreEqual(5f, maturity, 1e-4f);
    }

    [TestMethod]
    public void SampleDominantBiome_WeakSuitability_StillWinsIfHighest() {
      // No gate -- a weakly-positive Suitability still wins when it's
      // the max. Forest=0.3, Grassland=0.4 (both well below any old
      // threshold); Grassland still becomes dominant.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.3f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.4f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Grassland), 2f);

      var (biome, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.AreEqual(BiomeKind.Grassland, biome);
      Assert.AreEqual(2f, maturity, 1e-4f);
    }

    [TestMethod]
    public void SampleDominantBiome_AllSuitabilitiesZero_ReturnsNull() {
      // No biome has positive Suitability -- nothing to be dominant,
      // even if Maturity has been accumulated in some past life.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Forest), 30f);

      var (biome, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.IsNull(biome);
      Assert.AreEqual(0f, maturity);
    }

    [TestMethod]
    public void SampleDominantBiome_EmptyStore_ReturnsNull() {
      var store = new ChunkValueStore();

      var (biome, investment) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.IsNull(biome);
      Assert.AreEqual(0f, investment);
    }

    [TestMethod]
    public void SampleDominantBiome_BilinearInterp_PicksWinnerAtTile() {
      // Two chunks horizontally. c0: Forest Score=0.9, Grassland=0.6.
      // c1: Forest=0.6, Grassland=0.9. Tile in c0's centre -> Forest.
      // Tile in c1's centre -> Grassland. Both pass the gate.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.9f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.6f);
      store.Set(Region, 1, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.6f);
      store.Set(Region, 1, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.9f);

      var reader = new ChunkValueStoreReader(store);
      var (b0, _) = ChunkBiomeSampler.SampleDominantBiome(
          reader, Region, 0, 0, 2, 1, 1.5f, 1.5f);
      Assert.AreEqual(BiomeKind.Forest, b0);

      var (b1, _) = ChunkBiomeSampler.SampleDominantBiome(
          reader, Region, 0, 0, 2, 1, 5.5f, 1.5f);
      Assert.AreEqual(BiomeKind.Grassland, b1);
    }

    [TestMethod]
    public void SampleDominantBiome_InvestmentChannelDoesntDetermineWinner() {
      // High Investment alone -- with no passing Score -- is ignored.
      // The "old forest that's currently flooded" case.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Forest), 30f);
      // Forest Score not set (defaults to 0); no biome passes the gate.

      var (biome, investment) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.IsNull(biome);
      Assert.AreEqual(0f, investment);
    }

    [TestMethod]
    public void SampleDominantBiome_ReturnsWinnersInvestment_NotItsScore() {
      // Sanity: the second tuple element is the winner's Investment,
      // not its Score. Catches a wrong-channel-read regression.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.8f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Forest), 17f);

      var (biome, investment) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.AreEqual(BiomeKind.Forest, biome);
      Assert.AreEqual(17f, investment, 1e-4f);
    }

    [TestMethod]
    public void SampleDominantBiome_BadwaterWinsTieOverContaminated_AggressorTiebreak() {
      // Fully-badwater chunk: Contaminated Suitability stacks to the
      // same value as Badwater (both cover the contaminated-water
      // area). Aggressor tier breaks the tie in Badwater's favour so
      // the chunk reads as Badwater dominant; downstream the matrix's
      // BW column (0.5d half-life) destroys neighbouring biomes'
      // Maturity in ~12h rather than the Con column's 1d.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Contaminated), 1f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Badwater), 1f);

      var (biome, _) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.AreEqual(BiomeKind.Badwater, biome);
    }

    [TestMethod]
    public void SampleDominantBiome_ContaminatedWinsWhenStrictlyHigher() {
      // Mixed chunk where contamination covers more area than badwater
      // fluid: Contaminated Suitability strictly exceeds Badwater, so
      // it wins dominance.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Contaminated), 0.8f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Badwater), 0.3f);

      var (biome, _) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.AreEqual(BiomeKind.Contaminated, biome);
    }

    #endregion

    #region Dominant by maturity

    [TestMethod]
    public void SampleDominantByMaturity_HighestMaturityWins_IgnoringSuitability() {
      // The suitability winner (Forest, suit 0.9) is NOT the maturity winner:
      // Grassland has lower suitability (0.1) but the highest Maturity (30).
      // SampleDominantByMaturity must return Grassland — the opposite of what
      // SampleDominantBiome returns for the same store.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.9f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Forest), 5f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.1f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Grassland), 30f);
      var reader = new ChunkValueStoreReader(store);

      var (suitBiome, _) = ChunkBiomeSampler.SampleDominantBiome(
          reader, Region, 0, 0, 1, 1, 1.5f, 1.5f);
      var (matBiome, matMaturity) = ChunkBiomeSampler.SampleDominantByMaturity(
          reader, Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.AreEqual(BiomeKind.Forest, suitBiome, "suitability winner");
      Assert.AreEqual(BiomeKind.Grassland, matBiome, "maturity winner");
      Assert.AreEqual(30f, matMaturity, 1e-4f);
    }

    [TestMethod]
    public void SampleDominantByMaturity_LowSuitabilityHighMaturity_StillWins() {
      // A biome whose Suitability has collapsed to 0 (flooded forest) but
      // still holds Maturity must remain the maturity winner -- the case the
      // suitability top-3 cache would have evicted.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Forest), 25f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Wetland), 0.8f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Wetland), 1f);

      var (matBiome, matMaturity) = ChunkBiomeSampler.SampleDominantByMaturity(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.AreEqual(BiomeKind.Forest, matBiome);
      Assert.AreEqual(25f, matMaturity, 1e-4f);
    }

    [TestMethod]
    public void SampleDominantByMaturity_AllMaturityZero_ReturnsNull() {
      // Positive Suitability but no accumulated Maturity anywhere -> nothing
      // is "established," so the maturity winner is null.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.9f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.4f);

      var (matBiome, matMaturity) = ChunkBiomeSampler.SampleDominantByMaturity(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f);

      Assert.IsNull(matBiome);
      Assert.AreEqual(0f, matMaturity);
    }

    [TestMethod]
    public void SampleDominantByMaturity_RiparianMaturity_OutweighsPerChunk() {
      // Per-tile riparian Maturity exceeds every per-chunk biome's Maturity,
      // so Riparian wins -- and carries its own per-tile R back.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.9f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Grassland), 8f);

      var (matBiome, matMaturity) = ChunkBiomeSampler.SampleDominantByMaturity(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f,
          riparianMaturity: 20f);

      Assert.AreEqual(BiomeKind.Riparian, matBiome);
      Assert.AreEqual(20f, matMaturity, 1e-4f);
    }

    [TestMethod]
    public void SampleDominantByMaturity_RiparianMaturityLower_PerChunkWins() {
      // Riparian R below the per-chunk maturity winner -> per-chunk wins,
      // riparian does not displace it.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Grassland), 18f);

      var (matBiome, matMaturity) = ChunkBiomeSampler.SampleDominantByMaturity(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f,
          riparianMaturity: 4f);

      Assert.AreEqual(BiomeKind.Grassland, matBiome);
      Assert.AreEqual(18f, matMaturity, 1e-4f);
    }

    #endregion

    #region Fast path vs full-scan fallback

    [TestCleanup]
    public void ResetBiomeOrdinals() => BiomeValueKinds.ResetOrdinals();

    /// <summary>Set a chunk's per-biome Suitability in BOTH the ordinal
    /// ChunkData layer (which drives the fast path and its top-3) and the
    /// string-keyed ChunkValueStore (which drives the fallback), so the two
    /// readers see identical data.</summary>
    private static void SetChunkSuitabilities(
        ChunkDataStore data, ChunkValueStore values, int cx, int cy,
        params (BiomeKind biome, float suit)[] biomes) {
      var d = data.GetOrCreate(Region, cx, cy);
      foreach (var (biome, suit) in biomes) {
        d.Set(BiomeValueKinds.SuitabilityOrdinal(biome), suit);
        values.Set(Region, cx, cy, BiomeValueKinds.ForSuitability(biome), suit);
      }
      BiomeValueKinds.RecomputeTopBiomes(d);
    }

    [TestMethod]
    public void SampleDominantBiome_FastPath_PicksHandVerifiedWinnerAcrossTiles() {
      // Pins the SHIPPING per-tile dominance path (fast: top-3-shrunk over
      // real ChunkData) against hand-computed bilinear winners. Every other
      // dominance test here drives the string-keyed FALLBACK path, never the
      // one production runs -- the same hide-in-the-untested-path gap that
      // let the original per-chunk dominance regression sit unnoticed.
      //
      // NB: the fallback is NOT a valid oracle to compare against. It
      // renormalizes a biome's bilinear blend over only the corners whose
      // store HAS an entry for it (it can't tell "chunk absent" from "biome
      // absent in this chunk"), so a biome present in few corners is inflated
      // at a partial-weight stencil. The dense-ChunkData fast path 0-fills
      // and is correct; we assert against ground truth, not the fallback.
      var registry = new ChunkValueRegistry();
      BiomeValueKinds.Initialize(registry);
      var data = new ChunkDataStore(registry);
      var values = new ChunkValueStore();

      // 2x2 region; each corner <=3 active biomes.
      SetChunkSuitabilities(data, values, 0, 0,
          (BiomeKind.Forest, 0.8f), (BiomeKind.Grassland, 0.3f));
      SetChunkSuitabilities(data, values, 1, 0,
          (BiomeKind.Wetland, 0.9f), (BiomeKind.River, 0.4f));
      SetChunkSuitabilities(data, values, 0, 1,
          (BiomeKind.Dry, 0.7f), (BiomeKind.Forest, 0.2f));
      SetChunkSuitabilities(data, values, 1, 1,
          (BiomeKind.Grassland, 0.6f), (BiomeKind.Lake, 0.5f), (BiomeKind.Wetland, 0.1f));

      var fast = new ChunkDataStoreReader(data);
      BiomeKind? Dom(float tx, float ty) =>
          ChunkBiomeSampler.SampleDominantBiome(fast, Region, 0, 0, 2, 2, tx, ty).Biome;

      // Chunk centres (weight 1 on that corner) -> that chunk's max biome.
      Assert.AreEqual(BiomeKind.Forest, Dom(1.5f, 1.5f), "(0,0): Forest 0.8 > Grassland 0.3");
      Assert.AreEqual(BiomeKind.Wetland, Dom(5.5f, 1.5f), "(1,0): Wetland 0.9 > River 0.4");
      Assert.AreEqual(BiomeKind.Dry, Dom(1.5f, 5.5f), "(0,1): Dry 0.7 > Forest 0.2");
      Assert.AreEqual(BiomeKind.Grassland, Dom(5.5f, 5.5f), "(1,1): Grassland 0.6 > Lake 0.5");

      // Midpoint of (0,0)|(1,0) at row 0 (0.5/0.5 blend): Forest 0.40,
      // Grassland 0.15, Wetland 0.45, River 0.20 -> Wetland.
      Assert.AreEqual(BiomeKind.Wetland, Dom(3.5f, 1.5f), "blend favours Wetland 0.45 over Forest 0.40");

      // Edge clamp below the bbox (y<0 -> row 0), mostly under chunk (0,0):
      // Forest 0.7 vs Wetland ~0.11 -> Forest (the fallback wrongly says Wetland).
      Assert.AreEqual(BiomeKind.Forest, Dom(2f, -1f), "edge clamp stays under chunk (0,0) -> Forest");
    }

    #endregion

    #region Riparian fold (per-tile dominance)

    [TestMethod]
    public void SampleDominantBiome_RiparianBeatsGrasslandWhereQualified() {
      // Per-chunk dominant is Grassland; riparian qualifies (suit 1) so
      // riparian wins, and it carries its own per-tile maturity (4),
      // NOT grassland's per-chunk maturity (12).
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 1f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Grassland), 12f);

      var (biome, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f,
          riparianSuitability: 1f, riparianMaturity: 4f);

      Assert.AreEqual(BiomeKind.Riparian, biome);
      Assert.AreEqual(4f, maturity, 1e-4f);
    }

    [TestMethod]
    public void SampleDominantBiome_RiparianDoesNotQualify_PerChunkWinnerStands() {
      // riparianSuitability 0 (default-equivalent) -> riparian out of the
      // argmax; grassland keeps dominance and its own maturity.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 1f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForMaturity(BiomeKind.Grassland), 12f);

      var (biome, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f,
          riparianSuitability: 0f, riparianMaturity: 4f);

      Assert.AreEqual(BiomeKind.Grassland, biome);
      Assert.AreEqual(12f, maturity, 1e-4f);
    }

    [TestMethod]
    public void SampleDominantBiome_RiparianLosesToWetland_OnSuitabilityTie() {
      // Wetland out-ranks riparian: an exact suitability tie at 1 goes
      // to wetland (the water family wins genuinely-wet tiles).
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Wetland), 1f);

      var (biome, _) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f,
          riparianSuitability: 1f, riparianMaturity: 4f);

      Assert.AreEqual(BiomeKind.Wetland, biome);
    }

    [TestMethod]
    public void SampleDominantBiome_RiparianWinsByMagnitude_OverWeakerHigherTier() {
      // Riparian suit 1 strictly exceeds a weak wetland suit 0.5, so it
      // wins by magnitude -- magnitude beats tier; the tier rule only
      // settles exact ties.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Wetland), 0.5f);

      var (biome, _) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f,
          riparianSuitability: 1f, riparianMaturity: 4f);

      Assert.AreEqual(BiomeKind.Riparian, biome);
    }

    [TestMethod]
    public void SampleDominantBiome_RiparianClaimsOtherwiseEmptyTile() {
      // No per-chunk biome scores positive; riparian still claims the
      // tile when it qualifies.
      var store = new ChunkValueStore();

      var (biome, maturity) = ChunkBiomeSampler.SampleDominantBiome(
          new ChunkValueStoreReader(store), Region, 0, 0, 1, 1, 1.5f, 1.5f,
          riparianSuitability: 1f, riparianMaturity: 4f);

      Assert.AreEqual(BiomeKind.Riparian, biome);
      Assert.AreEqual(4f, maturity, 1e-4f);
    }

    #endregion

    #region DominantAtChunk (chunk-resolution dominance)

    [TestMethod]
    public void DominantAtChunk_HighestSuitabilityWins() {
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0.7f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Grassland), 0.9f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Wetland), 0.6f);

      var dominant = ChunkBiomeSampler.DominantAtChunk(new ChunkValueStoreReader(store), Region, 0, 0);

      Assert.AreEqual(BiomeKind.Grassland, dominant);
    }

    [TestMethod]
    public void DominantAtChunk_BadwaterWinsTieOverContaminated() {
      // Chunk-resolution counterpart of the SampleDominantBiome test.
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Contaminated), 0.6f);
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Badwater), 0.6f);

      var dominant = ChunkBiomeSampler.DominantAtChunk(new ChunkValueStoreReader(store), Region, 0, 0);

      Assert.AreEqual(BiomeKind.Badwater, dominant);
    }

    [TestMethod]
    public void DominantAtChunk_AllSuitabilitiesZero_ReturnsNull() {
      var store = new ChunkValueStore();
      store.Set(Region, 0, 0, BiomeValueKinds.ForSuitability(BiomeKind.Forest), 0f);

      Assert.IsNull(ChunkBiomeSampler.DominantAtChunk(new ChunkValueStoreReader(store), Region, 0, 0));
    }

    [TestMethod]
    public void DominantAtChunk_EmptyStore_ReturnsNull() {
      var store = new ChunkValueStore();
      Assert.IsNull(ChunkBiomeSampler.DominantAtChunk(new ChunkValueStoreReader(store), Region, 0, 0));
    }

    #endregion

  }

}
