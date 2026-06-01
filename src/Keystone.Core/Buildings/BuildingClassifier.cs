namespace Keystone.Core.Buildings {

  /// <summary>
  /// Voxel-level fold: given three booleans about a voxel — does any
  /// BO classify it as Building, does any classify it as NoAura, and
  /// is the voxel registered as a path — decide the voxel's final
  /// <see cref="BuildingKind"/>. Lives in Core (not the Mod adapter)
  /// so the precedence rules can be unit-tested without any
  /// Timberborn dependencies.
  ///
  /// <para><b>Where the inputs come from.</b> The Mod-side adapter
  /// walks every block object at the voxel and asks
  /// <see cref="BlockObjectClassifier.Classify"/> what each one
  /// contributes — Skip, Building, or NoAura. The OR of those
  /// per-BO results becomes <see cref="Classify"/>'s
  /// <c>hasBuilding</c> / <c>hasNoAuraBuilding</c> inputs. The
  /// per-BO classifier is where the natural / spec / EnterableSpec
  /// / name-heuristic precedence lives; this fold only handles the
  /// final voxel-wide aggregation.</para>
  ///
  /// <para><b>Precedence at the voxel level:</b>
  /// <list type="number">
  ///   <item>Any BO contributes Building → <see cref="BuildingKind.Building"/>.
  ///         Beats a co-located NoAura tag — a normal building's aura
  ///         claim dominates a tagged-no-aura sibling on the same
  ///         voxel.</item>
  ///   <item>Any BO contributes NoAura (and no Building) →
  ///         <see cref="BuildingKind.BuildingNoAura"/>.</item>
  ///   <item>Voxel is a path (and no building of either kind) →
  ///         <see cref="BuildingKind.Path"/>.</item>
  ///   <item>Otherwise → <see cref="BuildingKind.None"/>.</item>
  /// </list></para>
  /// </summary>
  public static class BuildingClassifier {

    #region Public API

    /// <summary>
    /// Classify a voxel from three signals.
    /// </summary>
    /// <param name="hasBuilding">
    /// True iff at least one block object at the voxel is a "building-like"
    /// player-placed structure with normal aura semantics -- i.e., not
    /// a natural element (tree, crop, gatherable, growable, natural
    /// resource), not exclusively a path, and not flagged as no-aura.
    /// </param>
    /// <param name="hasNoAuraBuilding">
    /// True iff at least one block object at the voxel is a building
    /// tagged with <c>KeystoneEcologyNoAuraSpec</c> -- still
    /// infrastructure, but doesn't propagate a settled aura. Lower
    /// precedence than <paramref name="hasBuilding"/>: a voxel
    /// hosting both a normal building and a no-aura one (a fence next
    /// to a house, somehow stacked) resolves to <c>Building</c>, since
    /// the normal building's aura claim dominates.
    /// </param>
    /// <param name="isPath">
    /// True iff the voxel has a path on it (registered via
    /// <c>IPathService.IsPath</c> or carrying a <c>PathSpec</c>
    /// component).
    /// </param>
    public static BuildingKind Classify(bool hasBuilding, bool hasNoAuraBuilding, bool isPath) {
      if (hasBuilding) {
        // Pure building or dual (building + path). Either way: Building.
        // Normal building dominates a co-located no-aura tag.
        return BuildingKind.Building;
      }
      if (hasNoAuraBuilding) {
        return BuildingKind.BuildingNoAura;
      }
      if (isPath) {
        return BuildingKind.Path;
      }
      return BuildingKind.None;
    }

    #endregion

  }

}
