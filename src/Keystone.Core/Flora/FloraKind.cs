namespace Keystone.Core.Flora {

  /// <summary>
  /// Coarse classification of a flora blueprint, derived from which
  /// marker spec it carries. Buckets reflect the game's native
  /// distinction between forester-managed (trees, bushes) and
  /// farm-managed (crops) plantables, plus passive ground cover that
  /// neither system tends. Ecology rules will eventually blur the
  /// forester/farm split deliberately, but the catalog still classifies
  /// the way the base game does -- consumers can re-bucket from there.
  /// </summary>
  public enum FloraKind {

    /// <summary>Has none of the discriminating specs. Mushrooms, decorative naturals, dandelions.</summary>
    GroundCover,

    /// <summary>Has a <c>TreeComponentSpec</c>. Cuttable, multi-voxel, canopy. Forester-tended when planted.</summary>
    Tree,

    /// <summary>Has a <c>BushSpec</c>. Single-voxel, gatherable, low-stratum. Forester-tended when planted.</summary>
    Bush,

    /// <summary>Has a <c>CropSpec</c>. Annual / seasonal field plant. Farm-tended.</summary>
    Crop,

  }

}
