namespace Keystone.Core.Buildings {

  /// <summary>
  /// What a single block object contributes to its voxel's settlement
  /// classification. The adapter inspects Timberborn-side components
  /// on the BO, derives the boolean signals in
  /// <see cref="BlockObjectSignals"/>, and asks
  /// <see cref="BlockObjectClassifier.Classify"/> which of these
  /// three buckets the BO falls into. The voxel-level fold then
  /// collects the per-BO results: any <see cref="Building"/> sets
  /// <c>hasBuilding</c>; any <see cref="NoAura"/> sets
  /// <c>hasNoAuraBuilding</c>; <see cref="Skip"/> contributes nothing.
  /// The aggregated flags + the per-voxel <c>isPath</c> signal feed
  /// <see cref="BuildingClassifier.Classify"/> for the voxel's final
  /// <see cref="BuildingKind"/>.
  /// </summary>
  public enum BlockObjectFootprint {

    /// <summary>The block object doesn't anchor settlement — natural
    /// elements (trees, crops, gatherables), Keystone-tagged
    /// transparent buildings, or path-only structures that aren't
    /// promoted by the structural-path heuristic.</summary>
    Skip,

    /// <summary>The block object is normal settlement infrastructure —
    /// settles its own voxel and propagates the 1-tile aura through
    /// the surveyor's neighbor check.</summary>
    Building,

    /// <summary>The block object settles its own voxel but does NOT
    /// propagate aura. Keystone-tagged no-aura buildings, or path-
    /// occupying buildings caught by the structural-path name
    /// heuristic (Zipline / Tubeway / Overhang / SuspensionBridge).</summary>
    NoAura,

  }

}
