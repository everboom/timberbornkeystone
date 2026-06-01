using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Recipe filter that admits only tiles whose 8-neighbour Moore
  /// neighbourhood contains water (Chebyshev distance 1). Backed by
  /// <see cref="WaterProximity.BordersWater"/>.
  /// </summary>
  public sealed class WaterEdgeRecipeFilter : IRecipeFilter {

    private readonly WaterProximity _waterProximity;

    public WaterEdgeRecipeFilter(WaterProximity waterProximity) {
      _waterProximity = waterProximity;
    }

    /// <inheritdoc />
    public string Name => "WaterEdge";

    /// <inheritdoc />
    public bool IsEligible(SurfaceCoord surface) {
      return _waterProximity.BordersWater(surface);
    }

  }

}
