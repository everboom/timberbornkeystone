namespace Keystone.Core.Biomes {

  /// <summary>
  /// Snapshot of a chunk's current ecological state, fed to
  /// <see cref="BiomeTargets"/> to compute per-biome target scores.
  /// Pure value object: no history, no behavior. The Mod layer
  /// populates this each tick from per-surface data.
  ///
  /// <para><b>Land-state fractions are mutually exclusive.</b>
  /// <see cref="DryLandFraction"/>, <see cref="IrrigatedFraction"/>,
  /// <see cref="WaterFraction"/>, and <see cref="CaveFraction"/>
  /// partition the chunk's surfaces: each surface is assigned to
  /// exactly one category and the four fractions sum to 1 (within
  /// float tolerance).</para>
  ///
  /// <para><b>Water sub-fractions partition <see cref="WaterFraction"/>
  /// across depth × flow-speed.</b> The four sub-fractions
  /// (<see cref="ShallowSlowWaterFraction"/>,
  /// <see cref="ShallowHighFlowWaterFraction"/>,
  /// <see cref="DeepSlowWaterFraction"/>,
  /// <see cref="DeepHighFlowWaterFraction"/>) sum to
  /// <see cref="WaterFraction"/>, not to 1. Computed properties
  /// (<see cref="HighFlowWaterFraction"/>, <see cref="SlowWaterFraction"/>)
  /// are aggregates over those.</para>
  ///
  /// <para><b>Contamination is a modifier, not exclusive.</b>
  /// <see cref="ContaminatedFraction"/> overlaps the land-state
  /// fractions: a contaminated tile is also still an irrigated /
  /// water / etc. tile. <see cref="ContaminatedWaterFraction"/> is
  /// the subset of contamination on water surfaces (i.e. the
  /// fraction that drives <see cref="BiomeKind.Badwater"/>).</para>
  /// </summary>
  public readonly struct ChunkBiomeInputs {

    #region Land state (mutually exclusive, sum to ~1)

    /// <summary>Surfaces that are dry land -- no irrigation, no
    /// water, no saturation, no overhang.</summary>
    public float DryLandFraction { get; init; }

    /// <summary>Surfaces that are irrigated land -- moist enough
    /// to support flora life.</summary>
    public float IrrigatedFraction { get; init; }

    /// <summary>Surfaces with standing water on top, of any depth or
    /// flow. Sum of the four water sub-fractions.</summary>
    public float WaterFraction { get; init; }

    /// <summary>Surfaces beneath an overhang.</summary>
    public float CaveFraction { get; init; }

    #endregion

    #region Water sub-state (partition WaterFraction across depth × flow-speed)

    /// <summary>Shallow + low-flow water. Drives
    /// <see cref="BiomeTargets.Wetland"/>. "Shallow" uses a soft
    /// depth factor (full at depth &lt; 0.65, linearly fading to 0
    /// at depth &gt;= 1.0); "low flow" includes stagnant water and
    /// flow magnitudes below the high-flow threshold.</summary>
    public float ShallowSlowWaterFraction { get; init; }

    /// <summary>Shallow + high-flow water. Part of
    /// <see cref="BiomeTargets.River"/>. Soft-shallow factor; flow
    /// magnitude above the high-flow threshold.</summary>
    public float ShallowHighFlowWaterFraction { get; init; }

    /// <summary>Deep + low-flow water. Drives
    /// <see cref="BiomeTargets.Lake"/>. Soft-deep factor; flow
    /// magnitude below the high-flow threshold.</summary>
    public float DeepSlowWaterFraction { get; init; }

    /// <summary>Deep + high-flow water. Part of
    /// <see cref="BiomeTargets.River"/>. Soft-deep factor; flow
    /// magnitude above the high-flow threshold.</summary>
    public float DeepHighFlowWaterFraction { get; init; }

    /// <summary>Aggregate: any high-flow water (shallow + deep).
    /// Drives <see cref="BiomeTargets.River"/>.</summary>
    public float HighFlowWaterFraction =>
        ShallowHighFlowWaterFraction + DeepHighFlowWaterFraction;

    /// <summary>Aggregate: any low-flow water (shallow + deep).</summary>
    public float SlowWaterFraction =>
        ShallowSlowWaterFraction + DeepSlowWaterFraction;

    #endregion

    #region Contamination (modifier, overlaps land/water)

    /// <summary>Fraction of all surfaces that are contaminated.</summary>
    public float ContaminatedFraction { get; init; }

    /// <summary>Fraction of all surfaces that are contaminated water.
    /// Subset of both <see cref="ContaminatedFraction"/> and
    /// <see cref="WaterFraction"/>.</summary>
    public float ContaminatedWaterFraction { get; init; }

    #endregion

    #region Biotic counts (per chunk)

    /// <summary>Tree-kind entities in the chunk. Drives
    /// <see cref="BiomeTargets.Forest"/>'s density factor and
    /// <see cref="BiomeTargets.Grassland"/>'s tree-presence
    /// suppression -- conceptually about canopy, not all plant life,
    /// so trees specifically (not bushes, crops, or marks).</summary>
    public int TreeCount { get; init; }

    /// <summary>Distinct tree species. Drives
    /// <see cref="BiomeTargets.Forest"/>'s diversity factor.</summary>
    public int TreeSpeciesCount { get; init; }

    /// <summary>Mature (fully-grown) tree-kind entities in the chunk --
    /// the subset of <see cref="TreeCount"/> whose <c>Growable</c>
    /// reports grown (seedlings excluded). Drives
    /// <see cref="BiomeTargets.Forest"/>'s mature-canopy gate via
    /// <see cref="MatureTreeFraction"/>, so a chunk freshly carpeted
    /// with saplings doesn't read as established forest. Always
    /// <c>&lt;= TreeCount</c>.</summary>
    public int MatureTreeCount { get; init; }

    /// <summary>Fraction of the chunk's trees that are mature, in
    /// <c>[0, 1]</c>: <see cref="MatureTreeCount"/> / <see cref="TreeCount"/>.
    /// 0 when the chunk has no trees (no canopy to be established, and
    /// avoids a divide-by-zero). Read by
    /// <see cref="BiomeTargets.Forest"/>'s mature-canopy gate.</summary>
    public float MatureTreeFraction =>
        TreeCount > 0 ? (float)MatureTreeCount / TreeCount : 0f;

    /// <summary>Combined count of plantable entities (trees, bushes,
    /// crops -- not GroundCover) AND player-drawn planting marks
    /// within the chunk. A tile that's both marked and has a
    /// plantable entity counts once. Player-drawn marks count toward
    /// this from the moment the player commits, well before any
    /// actual sapling sprouts -- intentional, since the chunk's
    /// "managed by the player" state is determined by intent, not
    /// by realised growth. Drives <see cref="BiomeTargets.Monoculture"/>'s
    /// saturation signal.</summary>
    public int PlantableCount { get; init; }

    /// <summary>Distinct plantable species (entities + marks) in the
    /// chunk. Marked tiles contribute their designated species (e.g.
    /// a Forester set to "Pine" contributes "Pine"). Useful for
    /// diagnostics; <see cref="BiomeTargets.Monoculture"/>'s actual
    /// dominance signal lives in <see cref="PlantableDominance"/>.</summary>
    public int PlantableSpeciesCount { get; init; }

    /// <summary>Simpson's diversity index over the chunk's per-species
    /// plantable counts: <c>Σ (count_i / total)²</c>. Reads as
    /// "probability that two random plantables in this chunk are the
    /// same species." 1.0 = pure single-species monoculture; 1/N for
    /// a perfectly-even N-species mix. Distribution-sensitive —
    /// 14:2 reads higher than 8:8, even though both are 2-species,
    /// because one species dominates. Drives
    /// <see cref="BiomeTargets.Monoculture"/>'s dominance signal.</summary>
    public float PlantableDominance { get; init; }


    #endregion

  }

}
