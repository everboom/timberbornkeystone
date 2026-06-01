using System;
using Keystone.Core.Regions;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Composite key for an entry in <see cref="ChunkValueStore"/>: a
  /// region id, the chunk's global lattice coordinates, and a string
  /// discriminator (the "kind"). Mod 1's own kinds are namespaced
  /// under <c>"keystone."</c>; external mods are strongly recommended
  /// to prefix with their own mod id.
  ///
  /// <para>Chunk coordinates are global, not bbox-relative: <c>chunkX =
  /// absoluteTileX / RegionEcologyField.ChunkSize</c> (and same for Y).
  /// Producers that have a region's <c>RegionEcologyField</c> in hand
  /// translate field-local <c>(cx, cy)</c> to global by adding
  /// <c>field.OriginX/Y / ChunkSize</c> -- field origins are
  /// chunk-aligned by construction (see <c>RegionScratch.ChunkAlignedBbox</c>).
  /// Including <see cref="RegionId"/> in the key keeps Z-stacked
  /// regions that share an XY footprint insulated from each other.</para>
  ///
  /// <para><b>Z invariant (load-bearing).</b> A region's identity is
  /// <c>(Z, IsCave, IsSettled)</c>: every surface in a region shares
  /// the region's Z, and two regions at the same <c>(X, Y)</c> but
  /// different Z always have distinct <see cref="RegionId"/>s. So
  /// <i>two <see cref="ChunkValueKey"/>s with different
  /// <see cref="RegionId"/>s implicitly refer to different Z layers
  /// even when their <see cref="ChunkX"/> and <see cref="ChunkY"/>
  /// coincide.</i></para>
  ///
  /// <para><b>Never silently merge chunks across Z.</b> A value
  /// recorded at <c>(R_a, x, y)</c> where <c>R_a.Z == 5</c> must not
  /// end up at <c>(R_b, x, y)</c> where <c>R_b.Z == 10</c>. Concretely:
  /// any code that maps a saved or live <see cref="ChunkValueKey"/>
  /// to a different <see cref="RegionId"/> must verify the destination
  /// region's Z matches the source's. The two places this risk
  /// surfaces today:
  /// <list type="bullet">
  ///   <item><b>Persistence load rescue.</b> When a saved
  ///         <see cref="RegionId"/> doesn't match a live region,
  ///         <c>RegionService.FindRegionByChunkFootprint</c> resolves
  ///         the chunk's footprint to a live region. The call site in
  ///         <c>KeystonePersistence.PostLoadInner</c> MUST pass the
  ///         saved region's Z as <c>targetZ</c> so stacked regions at
  ///         the same <c>(X, Y)</c> can't compete and steal each
  ///         other's chunk data. A post-rescue assertion in
  ///         PostLoadInner reinforces this at runtime.</item>
  ///   <item><b>Mid-game reconciliation.</b> <c>ChunkReconciler</c>
  ///         re-binds each chunk to the region owning its footprint after
  ///         a topology change, querying <c>IChunkOwnerQuery</c> (over
  ///         <c>RegionService.FindRegionByChunkFootprint</c>) with the
  ///         chunk's carried <see cref="Keystone.Core.Persistence.ChunkData.Z"/>.
  ///         Z-strict by construction, so a chunk never re-homes across Z.</item>
  ///   <item><b>External-mod store API.</b> <c>ChunkValueStore.Inherit</c> /
  ///         <c>MergeFrom</c> (and the <c>ChunkDataStore</c> equivalents)
  ///         move chunk values between <see cref="RegionId"/>s with NO Z
  ///         check. They have no Mod 1 caller — Mod 1's re-binding goes
  ///         through the Z-strict reconciler above — but remain public for
  ///         external mods, which MUST verify the destination region's Z
  ///         matches the source's themselves.</item>
  /// </list>
  /// Losing a chunk's data when the destination Z doesn't exist (i.e.
  /// no live region at the saved Z in the footprint) is the correct
  /// behaviour: the player's per-chunk Maturity gets rebuilt over the
  /// next few game-days. Silently misapplying the data to a different
  /// Z layer is not -- the wrong layer accrues bogus history that
  /// never decays.</para>
  /// </summary>
  public readonly record struct ChunkValueKey {

    #region Properties

    /// <summary>The region this value is attached to.</summary>
    public RegionId RegionId { get; }

    /// <summary>Global chunk X (absolute tile X / chunk size).</summary>
    public int ChunkX { get; }

    /// <summary>Global chunk Y (absolute tile Y / chunk size).</summary>
    public int ChunkY { get; }

    /// <summary>The value's discriminator. Non-empty.</summary>
    public string Kind { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Construct a chunk-value key. <paramref name="kind"/> must be
    /// non-null and non-empty -- empty kinds would make the value
    /// store impossible to debug ("which value is the empty-kind
    /// one?") so we fail loudly at the boundary rather than silently
    /// accept them.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="kind"/> is null or empty.</exception>
    public ChunkValueKey(RegionId regionId, int chunkX, int chunkY, string kind) {
      if (string.IsNullOrEmpty(kind)) {
        throw new ArgumentException("ChunkValueKey.Kind must be non-empty.", nameof(kind));
      }
      RegionId = regionId;
      ChunkX = chunkX;
      ChunkY = chunkY;
      Kind = kind;
    }

    #endregion

  }

}
