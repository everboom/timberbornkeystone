namespace Keystone.Core.Persistence {

  /// <summary>
  /// Keystone Mod 1's own value-kind constants for
  /// <see cref="RegionValueStore"/> and <see cref="ChunkValueStore"/>.
  /// External mods writing values into either store should prefix their
  /// kinds with their own mod id (both stores are publicly mutable --
  /// "use at own risk" is documented on the <c>Set</c> methods).
  /// </summary>
  public static class KnownValueKinds {

    /// <summary>
    /// Accumulator: total in-game days the region has existed, in
    /// continuous real time. Updated each tick by
    /// <c>RegionScoreTicker</c> and persisted across save/load so it
    /// survives sessions.
    /// </summary>
    public const string RegionAgeDays = "keystone.region.ageDays";

    /// <summary>
    /// Prefix for per-biome chunk Suitability values. The full key is
    /// <c>"{ChunkSuitabilityPrefix}{biome.ToString().ToLowerInvariant()}"</c>
    /// (e.g. <c>"keystone.chunk.suitability.forest"</c>). Use
    /// <see cref="Keystone.Core.Biomes.BiomeValueKinds.ForSuitability"/>
    /// to construct the key for a given
    /// <see cref="Keystone.Core.Biomes.BiomeKind"/> rather than
    /// concatenating manually.
    ///
    /// <para>This is the short-term "Suitability" channel: drifts
    /// toward a target computed from current chunk inputs on hour-scale
    /// dynamics, clamped <c>[0, 1]</c>. Drives instantaneous "are
    /// current conditions valid for this biome here" reads -- the
    /// pass/fail gate in tile-level dominance selection.</para>
    /// </summary>
    public const string ChunkSuitabilityPrefix = "keystone.chunk.suitability.";

    /// <summary>
    /// Prefix for per-biome chunk Maturity values. The full key is
    /// <c>"{ChunkMaturityPrefix}{biome.ToString().ToLowerInvariant()}"</c>
    /// (e.g. <c>"keystone.chunk.maturity.forest"</c>). Use
    /// <see cref="Keystone.Core.Biomes.BiomeValueKinds.ForMaturity"/>
    /// to construct the key rather than concatenating manually.
    ///
    /// <para>This is the long-term "Maturity" channel: integrates the
    /// Suitability channel over time with asymmetric rise / decay, in
    /// units of in-game days. Positive biomes mature slowly and decay
    /// quickly when neglected; negative biomes (Contaminated, Badwater,
    /// Dry) mature slowly and decay slowly so
    /// the scar persists. Drives long-term gameplay state (level-ladder
    /// gates, future rewards / penalties).</para>
    /// </summary>
    public const string ChunkMaturityPrefix = "keystone.chunk.maturity.";

  }

}
