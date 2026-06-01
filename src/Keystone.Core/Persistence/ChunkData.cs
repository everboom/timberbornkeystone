using System;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Per-chunk value array for the parallel data layer. Holds one
  /// <c>float</c> per registered <see cref="ChunkValueRegistry"/> slot,
  /// indexed by ordinal. Created once per chunk when the chunk enters
  /// the store; the array is not resized — slot count is fixed for the
  /// session after <see cref="ChunkValueRegistry.Freeze"/>.
  ///
  /// <para><b>Thread safety.</b> During the parallel sweep phase,
  /// exactly one worker thread writes to a given <see cref="ChunkData"/>
  /// instance. The main thread reads from other instances whose sweep
  /// batch has already completed. No two threads access the same
  /// instance concurrently.</para>
  /// </summary>
  public sealed class ChunkData {

    #region Fields

    private readonly float[] _values;

    /// <summary>
    /// Top biomes by Suitability for this chunk, ordered descending.
    /// Up to 3 entries; <c>-1</c> marks "no biome at this rank" (chunk
    /// has fewer than 3 non-zero biomes). Layout is domain-neutral:
    /// the consumer interprets ints as <c>BiomeKind</c> ordinals.
    ///
    /// <para>Maintained by <c>BiomeSuitabilityUpdater</c> after each
    /// Suitability write so the per-chunk top-3 stays consistent with
    /// the chunk's value slots. Read by <c>ChunkBiomeSampler</c> to
    /// shrink the per-tile bilinear argmax candidate set from "all 10
    /// biomes" to "the union of the 4 corner chunks' top-3 lists" --
    /// typically 1–4 candidates in real terrain.</para>
    ///
    /// <para>Top-3 is a small-sized correctness compromise: a biome
    /// that's #4 at every corner with uniform values can technically
    /// win a tile's argmax in pathological neighborhoods. In real
    /// Timberborn terrain that doesn't happen meaningfully -- if it
    /// becomes an issue we widen to top-4 or store a sparse non-zero
    /// list.</para>
    /// </summary>
    private readonly int[] _topBiomes = { -1, -1, -1 };

    #endregion

    #region Construction

    /// <summary>
    /// Allocate a chunk data array with <paramref name="slotCount"/>
    /// slots, all initialized to zero.
    /// </summary>
    public ChunkData(int slotCount) {
      if (slotCount < 0)
        throw new ArgumentOutOfRangeException(nameof(slotCount));
      _values = new float[slotCount];
    }

    #endregion

    #region Properties

    /// <summary>Number of value slots.</summary>
    public int SlotCount => _values.Length;

    /// <summary>Game-day timestamp of the last time this chunk was
    /// processed by the biome ticker. Set by the ticker after both
    /// updaters have written; read by the debug panel to show data
    /// age. Zero means "never processed this session".</summary>
    public float LastUpdatedDay { get; set; }

    /// <summary>The Z layer this chunk's data belongs to — the
    /// <see cref="Keystone.Core.Regions.Region.Z"/> of the region that
    /// owned the chunk when its data was written. Stamped by the writer
    /// at creation; copied by <see cref="CopyFrom"/>.
    ///
    /// <para><b>Why carried explicitly.</b> A chunk's owning region can
    /// die (terrain edit, building) while its accumulated data still
    /// needs to find a new home at the same physical footprint. Once the
    /// region is gone its Z is unrecoverable from the region graph, so
    /// the only way reconciliation can re-bind the chunk to whichever
    /// live region now owns <c>(X, Y, Z)</c> is for the chunk to remember
    /// its own Z. See <c>ChunkReconciler</c>. Z is identity, not a value
    /// slot, so <see cref="Clear"/> leaves it untouched.</para></summary>
    public int Z { get; set; }

    #endregion

    #region Access

    /// <summary>Read the value at <paramref name="ordinal"/>.</summary>
    public float Get(int ordinal) => _values[ordinal];

    /// <summary>Write <paramref name="value"/> at <paramref name="ordinal"/>.</summary>
    public void Set(int ordinal, float value) => _values[ordinal] = value;

    /// <summary>Direct access to the backing array. Use for bulk
    /// reads/writes where per-element method-call overhead matters
    /// (e.g. the parallel sweep's inner loop).</summary>
    public float[] Values => _values;

    /// <summary>Direct access to the top-biomes array (length 3,
    /// <c>-1</c> = empty rank). See <see cref="_topBiomes"/> for
    /// semantics. Returned as the live array so the sampler can
    /// iterate without copying.</summary>
    public int[] TopBiomes => _topBiomes;

    /// <summary>Write the top-3 biomes (BiomeKind ordinals; pass
    /// <c>-1</c> for empty ranks). Called by
    /// <c>BiomeSuitabilityUpdater</c> after each Suitability write.
    /// Order is descending by Suitability — <paramref name="first"/>
    /// is the chunk's current top biome.</summary>
    public void SetTopBiomes(int first, int second, int third) {
      _topBiomes[0] = first;
      _topBiomes[1] = second;
      _topBiomes[2] = third;
    }

    #endregion

    #region Bulk

    /// <summary>Copy all values from <paramref name="source"/> into
    /// this instance. Both must have the same slot count.</summary>
    public void CopyFrom(ChunkData source) {
      if (source._values.Length != _values.Length)
        throw new ArgumentException(
            $"Slot count mismatch: source has {source._values.Length}, " +
            $"this has {_values.Length}.");
      Array.Copy(source._values, _values, _values.Length);
      Array.Copy(source._topBiomes, _topBiomes, _topBiomes.Length);
      Z = source.Z;
    }

    /// <summary>Reset all slots to zero.</summary>
    public void Clear() {
      Array.Clear(_values, 0, _values.Length);
      _topBiomes[0] = -1;
      _topBiomes[1] = -1;
      _topBiomes[2] = -1;
    }

    #endregion

  }

}
