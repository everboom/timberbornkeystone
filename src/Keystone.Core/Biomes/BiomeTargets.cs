namespace Keystone.Core.Biomes {

  /// <summary>
  /// Per-biome Suitability functions: given current chunk state
  /// (<see cref="ChunkBiomeInputs"/>), return a value in <c>[0, 1]</c>
  /// answering "how strongly does this chunk look like that biome
  /// right now." Suitability is stateless -- recomputed each tick
  /// directly from inputs by <see cref="BiomeSuitabilityUpdater"/>.
  /// Maturity (in <see cref="BiomeMaturityUpdater"/>) handles all
  /// time-axis dynamics; nothing here drifts.
  ///
  /// <para><b>Two layers.</b> The per-biome <c>Forest</c>,
  /// <c>Grassland</c>, etc. methods are pure positive-evidence
  /// functions -- no awareness of cross-biome cancellation. The
  /// dispatcher <see cref="Compute"/> multiplies each by a
  /// contamination cancellation factor (see
  /// <see cref="ContaminationFactor"/>) so contaminated chunks
  /// correctly read as Contaminated / Badwater rather than
  /// erroneously also reading as Forest etc. -- contaminated land
  /// is still "irrigated land" from the moisture channel's
  /// perspective, so the positive predicates alone can't tell.</para>
  ///
  /// <para><b>Cancellation lattice.</b> Within the Suitability
  /// layer the priority order is:</para>
  /// <list type="number">
  /// <item><b>Badwater</b> cancels every other biome (mediated via
  /// Contaminated, since badwater contributes to
  /// <see cref="ChunkBiomeInputs.ContaminatedFraction"/>).</item>
  /// <item><b>Contamination</b> cancels every biome except
  /// <see cref="BiomeKind.Contaminated"/> and
  /// <see cref="BiomeKind.Badwater"/>.</item>
  /// <item><b>Drought</b> cancels irrigation- and water-dependent
  /// biomes -- enforced naturally by their positive predicates
  /// (<see cref="ChunkBiomeInputs.IrrigatedFraction"/> and
  /// <see cref="ChunkBiomeInputs.WaterFraction"/> both fall to 0 in
  /// drought conditions), no explicit multiplier needed.</item>
  /// <item><b>Inundation</b> cancels irrigation-dependent land
  /// biomes -- same mechanism (IrrigatedFraction falls to 0 when
  /// the chunk floods), no explicit multiplier needed.</item>
  /// </list>
  ///
  /// <para><b>Independent accumulation across biomes.</b> Beyond
  /// the cancellation lattice, the per-biome positive predicates
  /// don't gate on each other. A chunk with shallow flowing water
  /// that is also 30% contaminated has a positive River target
  /// <i>and</i> a positive Badwater target -- both stay in the
  /// Suitability record; aggressor tiebreak in
  /// <see cref="ChunkBiomeSampler"/> resolves dominance.</para>
  /// </summary>
  public static class BiomeTargets {

    #region Tunable thresholds

    private const float ForestSpeciesSaturation = 2f;
    private const float ForestDensitySaturation = 5f;

    /// <summary>Mature-tree fraction at which Forest's mature-canopy
    /// gate reaches full strength. The gate is
    /// <c>saturate(MatureTreeFraction / ForestMatureFractionSaturation)</c> —
    /// a linear ramp from 0 (no mature trees) to 1 (this fraction or
    /// more of the chunk's trees are fully grown). At <c>0.25</c>, a
    /// chunk needs a quarter of its trees mature for full Forest credit;
    /// a pure-seedling carpet (0% mature) reads as 0 Forest regardless
    /// of how dense or diverse the saplings are, which is the point —
    /// the biome rewards genuinely established woodland and can't be
    /// gamed by mass-planting seedlings.
    /// <para>Smooth ramp rather than a hard threshold, matching the rest
    /// of <see cref="BiomeTargets"/>'s multiplicative-factor style (see
    /// <see cref="Grassland"/>'s note on avoiding chunk-border wave
    /// artifacts from binary gates).</para></summary>
    private const float ForestMatureFractionSaturation = 0.25f;

    /// <summary>Below this many plantables (entities + marks) in a
    /// chunk, monoculture cannot trigger regardless of how
    /// homogeneous the species are. Prevents a partial-chunk with a
    /// single tile of one plant from reading as "fully managed".</summary>
    private const int MonocultureMinCount = 3;

    /// <summary>Tile-count denominator for Monoculture's saturation
    /// factor: <c>saturation = saturate(PlantableCount / 16)</c>. A
    /// fully populated 4×4 chunk (every tile carries a plantable or a
    /// mark) saturates at 1.0; 3 plantables in an otherwise-empty
    /// chunk read as <c>~0.19</c> saturated. Matches
    /// <c>RegionEcologyField.ChunkSize²</c> but inlined here to keep
    /// <see cref="BiomeTargets"/> free of layout dependencies.</summary>
    private const float MonocultureChunkTileCount = 16f;

    /// <summary>Simpson's-D threshold below which a chunk reads as 0
    /// monoculture. Chosen as <c>1/3</c> so a perfectly-even 3-species
    /// mix (D = 1/3) lands at exactly 0; D values above are mapped
    /// through <c>sqrt((D - 1/3) / (2/3))</c>, a concave curve that
    /// reaches 1 at D=1 (pure single-species). The concave shape pulls
    /// the upper half of the range up so heavily-skewed mixes read
    /// strongly as Monoculture: a 14:2 distribution (D = 0.781) lands
    /// at <c>~0.82</c>, and 13:3 (D = 0.695) lands at <c>~0.74</c>.
    /// Cost: an evenly-split 2-species mix (D = 1/2) lands at
    /// <c>~0.50</c>, higher than the previous linear shape's 0.25 --
    /// 8:8 chunks now read as half-Monoculture rather than mostly-
    /// Forest. Considered acceptable: a balanced 2-species canopy is
    /// only weakly diverse in practice and the player's intent in
    /// that case is closer to "managed area" than "natural forest".</summary>
    private const float MonocultureDominanceThreshold = 1f / 3f;

    /// <summary>Steepness of the contamination cancellation curve.
    /// At <c>fraction × scale &gt;= 1</c> the affected biome's
    /// Suitability is fully cancelled. With <c>scale = 20</c>, 5%
    /// of the chunk being contaminated (or contaminated water for
    /// water biomes) is enough to fully cancel -- 1 contaminated
    /// tile out of 16 cancels the chunk. Below the kill threshold
    /// the multiplier ramps linearly from 1 (no contamination) to
    /// 0 (kill threshold).
    /// <para>Matches the design intent "any contamination is bad
    /// enough to fully cancel affected biomes" while remaining
    /// smooth (not a discontinuous step function). Adjust if
    /// playtest wants gentler partial-contamination behavior.</para></summary>
    private const float ContaminationCancellationScale = 20f;

    #endregion

    #region Land -- irrigated (no contamination gate)

    public static float Forest(in ChunkBiomeInputs i) {
      // Forest = positive-evidence score (irrigation × diversity × density,
      // suppressed by monoculture) GATED by the mature-canopy ramp, so a
      // field of seedlings reads as 0 Forest however dense or diverse until
      // its trees establish. Factored into ForestUngated × MatureCanopyGate
      // so consumers can ask "would this read as Forest once the canopy
      // matures?" (see ForestLimitedByImmaturity) without the gate masking
      // the answer. The product is identical to the inlined form.
      return ForestUngated(i) * MatureCanopyGate(i);
    }

    /// <summary>Forest's positive-evidence score <i>before</i> the
    /// mature-canopy gate: <c>irrigation × species-diversity × density ×
    /// (1 - Monoculture)</c>. This is what Forest would score if its trees
    /// were already established. Not a biome target on its own — exposed so
    /// <see cref="ForestLimitedByImmaturity"/> can tell "young woodland that
    /// will become Forest" apart from "genuine low-diversity monoculture."</summary>
    public static float ForestUngated(in ChunkBiomeInputs i) {
      var diversity = Saturate(i.TreeSpeciesCount / ForestSpeciesSaturation);
      var density = Saturate(i.TreeCount / ForestDensitySaturation);
      var monoSuppression = 1f - Monoculture(i);
      return i.IrrigatedFraction * diversity * density * monoSuppression;
    }

    /// <summary>The mature-canopy gate <see cref="Forest"/> multiplies in:
    /// a linear ramp on the share of the chunk's trees that are fully grown
    /// (<see cref="ChunkBiomeInputs.MatureTreeFraction"/>), reaching full
    /// strength at <see cref="ForestMatureFractionSaturation"/>. 0 for an
    /// all-seedling chunk (no established canopy), 1 once a quarter of the
    /// trees are mature. See the field docstring for the rationale (the
    /// biome rewards genuinely established woodland and can't be gamed by
    /// mass-planting seedlings).</summary>
    public static float MatureCanopyGate(in ChunkBiomeInputs i) =>
        Saturate(i.MatureTreeFraction / ForestMatureFractionSaturation);

    /// <summary>True when Forest is out-scored by <see cref="Monoculture"/>
    /// <i>solely</i> because its canopy hasn't matured yet — i.e. the chunk
    /// would read as Forest once its trees establish. Defined as: the
    /// mature-canopy gate is below full strength
    /// (<see cref="MatureCanopyGate"/> &lt; 1) <b>and</b>
    /// <see cref="ForestUngated"/> already out-scores
    /// <see cref="Monoculture"/>. When this holds a "lacks species
    /// diversity" message is misleading — the planting is diverse/dense
    /// enough, it is merely young; the honest signal is "still maturing."
    /// When it does not hold (canopy already established, or the un-gated
    /// score still loses to Monoculture) the chunk is a genuine
    /// low-diversity planting and the diversity message is correct.
    /// <para><b>Forest-specific.</b> Grassland has no maturity gate — it
    /// yields to <i>mature</i> trees, not seedlings — so its Monoculture
    /// competition is never "just immature"; callers must not apply this to
    /// the Grassland/crop path.</para></summary>
    public static bool ForestLimitedByImmaturity(in ChunkBiomeInputs i) {
      if (MatureCanopyGate(i) >= 1f) return false;
      return ForestUngated(i) > Monoculture(i);
    }

    /// <summary>Grassland: irrigated land scaled down by mature-canopy
    /// presence (yields to established Forest) and monoculture (yields
    /// to the player-managed area). Each competitor reduces Grassland
    /// multiplicatively rather than gating with a hard threshold, so
    /// partial overlaps still accumulate a partial Grassland score
    /// alongside their other classifications -- avoiding the
    /// wave-along-chunk-border artifacts a binary gate produces.
    ///
    /// <para><b>Yields to mature trees, not raw tree presence.</b> The
    /// canopy term keys off <see cref="ChunkBiomeInputs.MatureTreeCount"/>,
    /// the same mature signal <see cref="Forest"/>'s mature-canopy gate
    /// uses. A chunk freshly planted with seedlings doesn't yet read as
    /// Forest (the gate holds Forest at ~0), so Grassland correctly
    /// holds the chunk until the trees establish -- then Grassland
    /// recedes as Forest rises. Keying Grassland off raw
    /// <c>TreeCount</c> instead would leave a seedling field suppressed
    /// on both axes (not-Forest, not-Grassland) in a low-everything
    /// limbo.</para>
    ///
    /// <para>Near-water tiles spawn riparian-style decorations via the
    /// <c>WaterEdge</c> recipe filter; the biome scoring itself is
    /// water-distance-agnostic.</para></summary>
    public static float Grassland(in ChunkBiomeInputs i) {
      var matureCanopyPresence = Saturate(i.MatureTreeCount / ForestDensitySaturation);
      var monoSuppression = 1f - Monoculture(i);
      return i.IrrigatedFraction
          * (1f - matureCanopyPresence)
          * monoSuppression;
    }

    /// <summary>Monoculture: a chunk where the player has cultivated
    /// densely with low species diversity. Used as a "punishment"
    /// signal that suppresses Forest and Grassland progression --
    /// the player gets the area they're managing
    /// without Keystone-driven ecology gameplay interfering.
    /// <para>Two signals combined multiplicatively:</para>
    /// <list type="bullet">
    ///   <item><i>Saturation</i> -- linear in the chunk's plantable
    ///         count over a 16-tile denominator, so 3 plantables in
    ///         an otherwise empty chunk read as substantially less
    ///         monoculture than 16 plantables filling the chunk.</item>
    ///   <item><i>Dominance</i> -- driven by
    ///         <see cref="ChunkBiomeInputs.PlantableDominance"/>
    ///         (Simpson's D, distribution-sensitive), thresholded so
    ///         a perfectly-even 3-species mix lands at 0 and mapped
    ///         through a concave (sqrt) curve to 1 at single-species.
    ///         The concave shape makes heavily-skewed mixes read
    ///         strongly as monoculture even though Forest's diversity
    ///         and density factors both saturate at low thresholds:
    ///         <c>14:2</c> lands at <c>~0.82</c>, comfortably ahead
    ///         of the corresponding Forest score.</item>
    /// </list>
    /// <para>Hard floor at <see cref="MonocultureMinCount"/> total
    /// plantables -- a partial-chunk with one tile of one species
    /// shouldn't trigger anything regardless of dominance.</para>
    /// <para>Plantable counts include player-drawn marks so a
    /// freshly-designated Forester area reads as monoculture
    /// immediately, before any sapling actually sprouts -- catching
    /// the player's intent the moment it's expressed.</para></summary>
    public static float Monoculture(in ChunkBiomeInputs i) {
      if (i.PlantableCount < MonocultureMinCount) return 0f;
      var saturation = Saturate(i.PlantableCount / MonocultureChunkTileCount);
      var dominanceLinear = Saturate(
          (i.PlantableDominance - MonocultureDominanceThreshold)
          / (1f - MonocultureDominanceThreshold));
      if (dominanceLinear <= 0f) return 0f;
      var dominance = (float)System.Math.Sqrt(dominanceLinear);
      return i.IrrigatedFraction * saturation * dominance;
    }

    #endregion

    #region Water -- shape-only (contamination accumulates separately as Badwater)

    /// <summary>River: any depth of water with flow above the
    /// high-flow threshold. The main-channel current is strong
    /// enough to scour aquatic plant life; River reads as
    /// destructive-water in the design, not productive-water.</summary>
    public static float River(in ChunkBiomeInputs i) {
      return i.HighFlowWaterFraction;
    }

    /// <summary>Lake: deep water with low flow. Dam reservoirs and
    /// natural ponds. Passive biome -- no Keystone-driven flora at
    /// this time.</summary>
    public static float Lake(in ChunkBiomeInputs i) {
      return i.DeepSlowWaterFraction;
    }

    /// <summary>Wetland: shallow water with low flow. The mod's
    /// primary productive water biome -- hosts cattail and spadderdock
    /// (L1) and mangrove (L2). The "low flow" qualifier means
    /// dam-stilled water and slow side-channels both count; only
    /// high-flow rivers exclude.</summary>
    public static float Wetland(in ChunkBiomeInputs i) {
      return i.ShallowSlowWaterFraction;
    }

    #endregion

    #region Structural

    public static float Cave(in ChunkBiomeInputs i) {
      return i.CaveFraction;
    }

    #endregion

    #region Negative -- moisture deficit (no contamination gate)

    public static float Dry(in ChunkBiomeInputs i) {
      return i.DryLandFraction;
    }

    #endregion

    #region Negative -- contamination

    /// <summary>Contaminated tracks <i>any</i> contamination on the
    /// chunk: contaminated land plus contaminated water (badwater).
    /// Stacks with <see cref="Badwater"/> -- both targets are positive
    /// simultaneously when badwater fluid is present, because badwater
    /// is functionally "contaminated land plus open water." Dominance
    /// between the two is broken in Badwater's favour by the aggressor
    /// tiebreak in <see cref="ChunkBiomeSampler"/> when they tie at
    /// equal Suitability.</summary>
    public static float Contaminated(in ChunkBiomeInputs i) {
      return Saturate(i.ContaminatedFraction);
    }

    /// <summary>Badwater tracks contaminated water specifically. The
    /// underlying contamination signal is captured by
    /// <see cref="Contaminated"/>, which counts the same area
    /// (water-contamination is a subset of total contamination); the
    /// two channels stack on a badwater chunk.</summary>
    public static float Badwater(in ChunkBiomeInputs i) {
      return Saturate(i.ContaminatedWaterFraction);
    }

    #endregion

    #region Dispatcher

    /// <summary>Suitability for <paramref name="biome"/> on the chunk
    /// described by <paramref name="inputs"/>. Always returns a value
    /// in <c>[0, 1]</c>. Computed as <c>positive × contamination
    /// cancellation factor</c>: the per-biome positive predicate
    /// answers "how strongly does this chunk look like that biome
    /// from the inputs alone," and the cancellation factor reduces
    /// the result toward 0 when the chunk is contaminated (or
    /// contains badwater, since badwater is contained in the
    /// contaminated-fraction signal). Contaminated and Badwater
    /// receive no cancellation -- they <i>are</i> the contamination
    /// state, and stack with each other on badwater chunks.</summary>
    public static float Compute(BiomeKind biome, in ChunkBiomeInputs inputs) {
      var positive = ComputePositive(biome, inputs);
      if (positive <= 0f) return 0f;
      return positive * ContaminationFactor(biome, in inputs);
    }

    private static float ComputePositive(BiomeKind biome, in ChunkBiomeInputs inputs) {
      return biome switch {
          BiomeKind.Forest => Forest(inputs),
          BiomeKind.Grassland => Grassland(inputs),
          BiomeKind.Monoculture => Monoculture(inputs),
          BiomeKind.River => River(inputs),
          BiomeKind.Lake => Lake(inputs),
          BiomeKind.Wetland => Wetland(inputs),
          BiomeKind.Cave => Cave(inputs),
          BiomeKind.Dry => Dry(inputs),
          BiomeKind.Contaminated => Contaminated(inputs),
          BiomeKind.Badwater => Badwater(inputs),
          _ => 0f,
      };
    }

    /// <summary>Cancellation multiplier in <c>[0, 1]</c>: 1 = no
    /// contamination penalty, 0 = fully cancelled. Land biomes use
    /// <see cref="ChunkBiomeInputs.ContaminatedFraction"/> (which
    /// includes the contaminated-water portion -- so badwater
    /// cancels land biomes too); water biomes use
    /// <see cref="ChunkBiomeInputs.ContaminatedWaterFraction"/>
    /// (land contamination on the shoreline doesn't make the river
    /// dirty). Contaminated and Badwater return 1 -- they
    /// <i>are</i> the stress, so they don't suffer from it.</summary>
    private static float ContaminationFactor(BiomeKind biome, in ChunkBiomeInputs inputs) {
      switch (biome) {
        case BiomeKind.Contaminated:
        case BiomeKind.Badwater:
          return 1f;
        case BiomeKind.River:
        case BiomeKind.Lake:
        case BiomeKind.Wetland:
          return CancellationFactor(inputs.ContaminatedWaterFraction);
        default:
          return CancellationFactor(inputs.ContaminatedFraction);
      }
    }

    private static float CancellationFactor(float fraction) {
      if (fraction <= 0f) return 1f;
      var killAmount = ContaminationCancellationScale * fraction;
      if (killAmount >= 1f) return 0f;
      return 1f - killAmount;
    }

    #endregion

    private static float Saturate(float v) => System.Math.Clamp(v, 0f, 1f);

  }

}
