using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Recipe filter that admits tiles within Chebyshev distance 2 of
  /// water (the 5×5 neighbourhood minus the tile itself). Used by
  /// Grassland's riparian mini-flourishes which should appear in the
  /// near-water band, not just at the immediate shoreline.
  /// </summary>
  public sealed class NearWaterRecipeFilter : IRecipeFilter {

    private readonly WaterProximity _waterProximity;

    public NearWaterRecipeFilter(WaterProximity waterProximity) {
      _waterProximity = waterProximity;
    }

    /// <inheritdoc />
    public string Name => "NearWater";

    /// <inheritdoc />
    public bool IsEligible(SurfaceCoord surface) {
      return _waterProximity.IsNearWater(surface, 2);
    }

  }

}
