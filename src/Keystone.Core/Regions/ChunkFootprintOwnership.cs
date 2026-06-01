namespace Keystone.Core.Regions {

  /// <summary>
  /// Which regions own surfaces in one chunk's <c>(X, Y, Z)</c> footprint:
  /// the majority owner (most surfaces, lowest-id tiebreak — matching
  /// <see cref="RegionService.FindRegionByChunkFootprint"/>) plus the full
  /// set of regions present. Produced in bulk by
  /// <see cref="RegionService.BuildChunkFootprintOwnerIndex"/>.
  ///
  /// <para>The <see cref="Present"/> set is what lets the chunk reconciler
  /// distinguish "this chunk's keyed region still legitimately owns it
  /// (even as a minority co-owner of a boundary-straddling chunk) — keep
  /// it" from "the keyed region is gone from this footprint — it's
  /// stranded, re-home to the majority or drop." Present sets are tiny
  /// (typically 1; 2–3 where regions share a chunk boundary), so a linear
  /// <see cref="Contains"/> scan is cheaper than a hash set.</para>
  /// </summary>
  public readonly struct ChunkFootprintOwnership {

    /// <summary>The majority-owner region (most surfaces in the footprint;
    /// ties broken by lowest <see cref="RegionId"/>).</summary>
    public RegionId Majority { get; }

    /// <summary>Every region with at least one surface in the footprint.
    /// Includes <see cref="Majority"/>. Never empty (the key wouldn't exist
    /// otherwise).</summary>
    public RegionId[] Present { get; }

    public ChunkFootprintOwnership(RegionId majority, RegionId[] present) {
      Majority = majority;
      Present = present;
    }

    /// <summary>True if <paramref name="region"/> has any surface in this
    /// footprint.</summary>
    public bool Contains(RegionId region) {
      var present = Present;
      for (var i = 0; i < present.Length; i++) {
        if (present[i].Value == region.Value) return true;
      }
      return false;
    }

  }

}
