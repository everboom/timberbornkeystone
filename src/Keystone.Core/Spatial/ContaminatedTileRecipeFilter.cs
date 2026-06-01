using Keystone.Core.Ports;
using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Recipe filter that admits only tiles whose surface is itself
  /// contaminated (the per-voxel "is this surface in the contamination
  /// plume" predicate from <see cref="IContaminationQuery.IsContaminatedAt"/>).
  ///
  /// <para>Used to differentiate "tile is within a contaminated chunk"
  /// (chunk-level rule, no filter) from "tile is itself contaminated"
  /// (this filter). The Contaminated biome attrition stacks two rules
  /// so contaminated tiles get a 100% kill chance while non-
  /// contaminated tiles in the same dominant-Contaminated chunk get
  /// a lower base chance.</para>
  /// </summary>
  public sealed class ContaminatedTileRecipeFilter : IRecipeFilter {

    private readonly IContaminationQuery _contamination;

    public ContaminatedTileRecipeFilter(IContaminationQuery contamination) {
      _contamination = contamination;
    }

    /// <inheritdoc />
    public string Name => "ContaminatedTile";

    /// <inheritdoc />
    public bool IsEligible(SurfaceCoord surface) {
      return _contamination.IsContaminatedAt(surface);
    }

  }

}
