namespace Keystone.Core.Ports {

  /// <summary>
  /// Read-side port over the host's planting-designation system: the
  /// per-tile marks the player draws via Plantable buildings (Forester,
  /// Farmhouse, ...) before beavers actually plant anything.
  /// Engine-agnostic counterpart of Timberborn's
  /// <c>Timberborn.Planting.PlantingService</c>; the Mod layer supplies an
  /// adapter wrapping that singleton.
  ///
  /// <para><b>Why Keystone reads this.</b> Marks are a strong "the player
  /// has committed to managing this tile" signal. Two consumers:
  /// <list type="bullet">
  ///   <item><b>Handler eligibility.</b> No handler should place
  ///         on a marked tile -- doing so would interfere with the
  ///         player's planting intent (and on Class D's stochastic path,
  ///         could race the planter's own spawn). Marks override
  ///         per-recipe filters and biome activation.</item>
  ///   <item><b>Monoculture detection.</b> A marked tile counts toward
  ///         the chunk's plantable-count + species-count exactly the
  ///         way an actual planted entity does -- the player's intent
  ///         is treated as already realised for biome-classification
  ///         purposes. Lets a freshly-drawn Forester area read as
  ///         monoculture immediately, before any sapling sprouts.</item>
  /// </list></para>
  ///
  /// <para><b>Lifecycle.</b> Marks persist independently of plants: the
  /// player has to explicitly clear them. A "marked-and-planted" tile
  /// reads marked AND has the entity. After the entity is cut, the
  /// mark may persist (planter will replant) -- it stays counted by
  /// either path.</para>
  /// </summary>
  public interface IPlantingMarkQuery {

    /// <summary>True if the tile at <c>(x, y, z)</c> currently carries
    /// a planting mark. Cheap (O(1) on the host).</summary>
    bool IsMarked(int x, int y, int z);

    /// <summary>The species (vanilla resource name like
    /// <c>"Pine"</c>) that the mark designates, or <c>null</c> if the
    /// tile isn't marked. Used by the chunk aggregator for species-
    /// count purposes.</summary>
    string MarkedSpecies(int x, int y, int z);

    /// <summary>
    /// Enumerate the marks whose <c>(X, Y)</c> lies inside the closed
    /// rect <c>[minX, maxX] × [minY, maxY]</c>, as
    /// <c>(x, y, z, species)</c>. Z is unconstrained -- marks at any Z
    /// in an in-range column are yielded.
    ///
    /// <para><b>Cost.</b> The adapter is expected to maintain a
    /// spatial index (kept in sync with the host's mark-add / mark-
    /// remove events) so the per-call cost scales with marks inside
    /// the rect, not with the total world-wide mark count. Callers
    /// that walk every chunk per cycle therefore stay
    /// <c>O(total marks)</c> per cycle rather than
    /// <c>O(chunks * total marks)</c>.</para>
    ///
    /// <para><b>Contract.</b> Returned marks have <c>X</c> in
    /// <c>[minX, maxX]</c> and <c>Y</c> in <c>[minY, maxY]</c>. Order
    /// is unspecified.</para>
    /// </summary>
    System.Collections.Generic.IEnumerable<(int X, int Y, int Z, string Species)> MarksInTileRect(
        int minX, int minY, int maxX, int maxY);

  }

}
