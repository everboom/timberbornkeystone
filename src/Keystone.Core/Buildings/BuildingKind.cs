namespace Keystone.Core.Buildings {

  /// <summary>
  /// Four-way classification for what occupies a voxel from the
  /// "settlement infrastructure" perspective. Trees, plants, crops, and
  /// other natural block objects classify as <see cref="None"/> --
  /// they're block objects but they don't make a tile part of the
  /// settled region.
  /// </summary>
  public enum BuildingKind {

    /// <summary>Empty voxel, or natural element (tree, crop, plant, gatherable, etc.).</summary>
    None,

    /// <summary>Has a <c>BuildingSpec</c> component. Includes vanilla buildings and any decorations placed via the building tool. The voxel itself settles AND propagates a 1-tile settled aura to its 8 lateral neighbors (the surveyor's halo rule).</summary>
    Building,

    /// <summary>Like <see cref="Building"/> for the voxel itself --
    /// the building IS built infrastructure -- but its presence does
    /// NOT propagate a settled aura to its 8 lateral neighbors. For
    /// small decoration / utility buildings (lanterns, scarecrows,
    /// shrubs, beehives) whose footprint is point-sized and that the
    /// player expects to coexist with surrounding wild nature. Tagged
    /// via <c>KeystoneEcologyNoAuraSpec</c>; distinct from
    /// <c>KeystoneEcologyTransparentSpec</c> which suppresses the
    /// voxel from settling entirely.</summary>
    BuildingNoAura,

    /// <summary>Path tile (registered via <c>IPathService.IsPath</c>) without a <c>BuildingSpec</c> component.</summary>
    Path,

  }

}
