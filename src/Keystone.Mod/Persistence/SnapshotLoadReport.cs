namespace Keystone.Mod.Persistence {

  /// <summary>
  /// What <see cref="KeystonePersistence.Load"/> and
  /// <see cref="KeystonePersistence.PostLoad"/> observed when reading
  /// and rehydrating the saved Keystone blob. Exposed via
  /// <see cref="KeystonePersistence.LoadReport"/> so the startup
  /// self-check can surface both "save came back surprisingly empty"
  /// and "rehydration silently discarded N entries" cases.
  /// </summary>
  /// <param name="HasSnapshot">True iff a Keystone singleton blob was
  ///   present in the save. False for new games and for saves written
  ///   before Keystone was installed.</param>
  /// <param name="SchemaVersion">The version stamp the blob carried.
  ///   Compare against <see cref="Keystone.Core.Persistence.SnapshotCodec.CurrentSchemaVersion"/>
  ///   for drift detection.</param>
  /// <param name="RegionCount">Number of region records decoded.</param>
  /// <param name="RegionValueCount">Number of region-value entries decoded.</param>
  /// <param name="ChunkValueCount">Number of chunk-value entries decoded.</param>
  /// <param name="MatchedRegionStamps">Region records whose
  ///   representative surface resolved to a live region with the same
  ///   <c>RegionId</c> as was saved. The canonical-ID save was right
  ///   by coincidence; nothing changed in the world topology. The
  ///   common case for an in-place save→load with no terrain or
  ///   mod-set delta.</param>
  /// <param name="RecoveredRegionStamps">Region records whose
  ///   representative surface resolved to a live region with a
  ///   <i>different</i> <c>RegionId</c> than was saved. The
  ///   canonical-ID drifted between save and load (terrain edit,
  ///   blockage placement, mod-set change introducing new region
  ///   splits, etc.), and the spatial anchor corrected the binding.
  ///   No data loss; the save self-normalises on next write.</param>
  /// <param name="DroppedRegionStamps">Region records that couldn't
  ///   be anchored at all -- either the saved record has no
  ///   representative (v1 saves) or the representative surface no
  ///   longer exists in the live state (terrain was edited externally
  ///   between save and load). These records' creation stamps are
  ///   permanently lost; the live region's CreatedAt defaults to
  ///   "now."</param>
  /// <param name="DroppedRegionValues">Region-value entries (per-region
  ///   accumulators) whose <c>RegionId</c> couldn't be remapped to a
  ///   live region. Sum of translation-time drops and post-rehydration
  ///   prunes (the latter should be zero in normal flow now that the
  ///   remap pre-filters).</param>
  /// <param name="DroppedChunkValues">Chunk-value entries (per-chunk
  ///   per-biome Maturity, etc.) for which spatial rescue found no
  ///   live region in the chunk's footprint -- every voxel in the
  ///   chunk's tile-bounds is either out of bounds or blocked. The
  ///   player's per-chunk biome accumulation is silently reset to
  ///   fresh for these chunks.</param>
  /// <param name="RescuedChunkValues">Chunk-value entries whose saved
  ///   <c>RegionId</c> either didn't remap to a live region, or
  ///   remapped to a different region than the chunk's spatial
  ///   footprint actually overlaps. These chunks were re-bound via
  ///   majority-owner spatial lookup -- without that path they would
  ///   have been silently dropped or misrouted to a region they don't
  ///   physically belong to. High counts on first load after a
  ///   topology-changing version bump (blockages introduced, mod-set
  ///   change splitting old regions) are expected and benign.</param>
  public readonly record struct SnapshotLoadReport(
      bool HasSnapshot,
      int SchemaVersion,
      int RegionCount,
      int RegionValueCount,
      int ChunkValueCount,
      int MatchedRegionStamps,
      int RecoveredRegionStamps,
      int DroppedRegionStamps,
      int DroppedRegionValues,
      int DroppedChunkValues,
      int RescuedChunkValues) {

    /// <summary>Sentinel: no snapshot present (new game, save without
    /// Keystone state, or pre-Load construction).</summary>
    public static readonly SnapshotLoadReport Empty = default;

  }

}
