using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Recipe filter that admits water tiles within Chebyshev distance 2
  /// of land (the shore band). Used by Wetland's mangrove-containing
  /// flourishes which should cluster at the water's edge rather than
  /// filling the entire wetland.
  /// </summary>
  public sealed class NearShoreRecipeFilter : IRecipeFilter {

    private readonly WaterProximity _waterProximity;

    public NearShoreRecipeFilter(WaterProximity waterProximity) {
      _waterProximity = waterProximity;
    }

    /// <inheritdoc />
    public string Name => "NearShore";

    /// <inheritdoc />
    public bool IsEligible(SurfaceCoord surface) {
      return _waterProximity.IsNearShore(surface, 2);
    }

  }

}
