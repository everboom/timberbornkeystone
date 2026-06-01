using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Spatial {

  /// <summary>
  /// Pins <see cref="ContaminatedTileRecipeFilter"/>'s contract: it
  /// delegates the eligibility predicate to
  /// <see cref="IContaminationQuery.IsContaminatedAt"/>, with the
  /// filter name <c>"ContaminatedTile"</c>. This filter is
  /// load-bearing for the two-rule attrition stack on the Contaminated
  /// biome — a regression that broke the predicate would silently
  /// stop the higher-probability rule from firing on contaminated
  /// tiles.
  /// </summary>
  [TestClass]
  public class ContaminatedTileRecipeFilterTests {

    #region Helpers

    private sealed class FakeContaminationQuery : IContaminationQuery {

      public HashSet<SurfaceCoord> ContaminatedSurfaces { get; } = new();
      public Dictionary<TileCoord, float> ColumnContamination { get; } = new();

      public float ContaminationAt(TileCoord column)
          => ColumnContamination.TryGetValue(column, out var v) ? v : 0f;

      public bool IsContaminatedAt(SurfaceCoord surface)
          => ContaminatedSurfaces.Contains(surface);

    }

    #endregion

    #region Name

    [TestMethod]
    public void Name_IsContaminatedTile() {
      // The filter name is the key the recipe-book JSON uses to
      // reference the filter. Renaming it would silently break every
      // recipe that lists "ContaminatedTile" as its Filter.
      var filter = new ContaminatedTileRecipeFilter(new FakeContaminationQuery());
      Assert.AreEqual("ContaminatedTile", filter.Name);
    }

    #endregion

    #region Predicate

    [TestMethod]
    public void IsEligible_ContaminatedSurface_True() {
      var query = new FakeContaminationQuery();
      var coord = new SurfaceCoord(3, 5, 7);
      query.ContaminatedSurfaces.Add(coord);
      var filter = new ContaminatedTileRecipeFilter(query);

      Assert.IsTrue(filter.IsEligible(coord));
    }

    [TestMethod]
    public void IsEligible_CleanSurface_False() {
      var query = new FakeContaminationQuery();
      // No surfaces added.
      var filter = new ContaminatedTileRecipeFilter(query);

      Assert.IsFalse(filter.IsEligible(new SurfaceCoord(0, 0, 0)));
    }

    [TestMethod]
    public void IsEligible_StackedSurfaces_DistinctPredicate() {
      // The docstring explicitly notes that stacked surfaces in one
      // column can return different IsContaminatedAt answers. Pin
      // that the filter forwards per-surface, not per-column.
      var query = new FakeContaminationQuery();
      var lower = new SurfaceCoord(5, 5, 2);
      var upper = new SurfaceCoord(5, 5, 6);
      query.ContaminatedSurfaces.Add(lower);
      // upper is clean.
      var filter = new ContaminatedTileRecipeFilter(query);

      Assert.IsTrue(filter.IsEligible(lower));
      Assert.IsFalse(filter.IsEligible(upper));
    }

    [TestMethod]
    public void IsEligible_DoesNotConsultColumnContaminationFloat() {
      // The filter must use IsContaminatedAt (per-voxel predicate),
      // not the ContaminationAt(column) float. A column with a non-
      // zero float value but no contaminated voxel must not register
      // — this is the same trace-vs-real distinction the water-side
      // 0.05 threshold guards against.
      var query = new FakeContaminationQuery();
      query.ColumnContamination[new TileCoord(2, 3)] = 0.9f;
      // ContaminatedSurfaces left empty.
      var filter = new ContaminatedTileRecipeFilter(query);

      Assert.IsFalse(filter.IsEligible(new SurfaceCoord(2, 3, 0)),
          "Filter must consult the per-voxel predicate, not the column float.");
    }

    #endregion

  }

}
