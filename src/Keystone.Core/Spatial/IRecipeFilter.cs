using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Spatial-eligibility predicate consulted by the flourish
  /// handlers before the activation gate. A recipe with
  /// <c>Filter == "WaterEdge"</c> is only eligible at tiles where
  /// the registered filter named <c>"WaterEdge"</c> returns true.
  ///
  /// <para>Implementations bind via
  /// <c>MultiBind&lt;IRecipeFilter&gt;().To&lt;...&gt;().AsSingleton()</c>;
  /// the <c>Keystone.Mod.Recipes.RecipeFilterRegistry</c> indexes them
  /// by <see cref="Name"/> so adding a new filter type is one new file
  /// plus a binding line, not edits across every handler.</para>
  /// </summary>
  public interface IRecipeFilter {

    /// <summary>The string name a recipe entry uses to opt in to
    /// this filter (the value of <c>RecipeEntry.Filter</c>).
    /// Case-sensitive.</summary>
    string Name { get; }

    /// <summary>True if a recipe carrying this filter is eligible to
    /// fire at <paramref name="surface"/>.</summary>
    bool IsEligible(SurfaceCoord surface);

  }

}
