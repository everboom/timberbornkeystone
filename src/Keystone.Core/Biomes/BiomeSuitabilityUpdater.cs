using System;
using Keystone.Core.Persistence;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Writes a chunk's per-biome Suitability values into a
  /// <see cref="ChunkData"/> array based on current chunk state.
  /// Suitability is <b>stateless</b> -- recomputed each tick directly
  /// from <see cref="ChunkBiomeInputs"/> via
  /// <see cref="BiomeTargets.Compute"/>, no drift, no history.
  /// All biomes are written every call so the snapshot stays complete.
  ///
  /// <para><b>Why stateless.</b> Earlier iterations drifted Suitability
  /// toward a target with proportional rise/drop dynamics, plus a
  /// "stress as deep negative target" trick to produce fast crashes.
  /// Both predate the Suitability/Maturity split. With Maturity now
  /// owning every time-axis dynamic (accrue, decay, the matrix,
  /// scar gate), Suitability has no temporal job to do -- it's just
  /// "is this chunk acting like X right now," answered fresh each
  /// tick. The negative-target trick is also unnecessary: contamination
  /// cancellation lives inside <see cref="BiomeTargets.Compute"/> as
  /// a multiplicative factor on positive predicates, and drought /
  /// inundation are enforced naturally by the positive predicates
  /// (their irrigation/water inputs fall to 0).</para>
  ///
  /// <para>Suitability written here drives:</para>
  /// <list type="bullet">
  /// <item>Dominance argmax (<see cref="ChunkBiomeSampler.DominantAtChunk"/>
  /// and <see cref="ChunkBiomeSampler.SampleDominantBiome"/>).</item>
  /// <item>Maturity's asymptote and decay-mode gate
  /// (<see cref="BiomeMaturityUpdater.Tick"/>).</item>
  /// <item>Per-tile bilinear reads from
  /// <see cref="ChunkBiomeSampler.SampleSuitability"/>.</item>
  /// </list>
  /// </summary>
  public sealed class BiomeSuitabilityUpdater {

    /// <summary>
    /// Recompute every biome's Suitability from
    /// <paramref name="inputs"/> and write the values into
    /// <paramref name="data"/> at the corresponding ordinal slots.
    /// Suitability is stateless; previously-stored values are
    /// overwritten without being read. Result is clamped to
    /// <c>[0, 1]</c> defensively.
    /// </summary>
    public void Tick(ChunkData data, in ChunkBiomeInputs inputs) {
      if (data == null) throw new ArgumentNullException(nameof(data));
      var values = data.Values;
      foreach (var biome in BiomeValueKinds.AllBiomes) {
        var value = BiomeTargets.Compute(biome, inputs);
        if (value < 0f) value = 0f;
        else if (value > 1f) value = 1f;
        values[BiomeValueKinds.SuitabilityOrdinal(biome)] = value;
      }
      // Refresh the per-chunk top-3 cache that the sampler reads.
      // One extra pass over the just-written values (~10 array reads
      // + ~30 compares); shared with the seed/rehydrate path so the
      // two writers can't drift in how they pick the top three.
      BiomeValueKinds.RecomputeTopBiomes(data);
    }

  }

}
