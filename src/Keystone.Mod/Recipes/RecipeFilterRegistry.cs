using System.Collections.Generic;
using Keystone.Core.Spatial;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Centralised dispatcher for per-recipe spatial-eligibility
  /// filters. Handlers call <see cref="IsEligible"/> with the
  /// recipe's <c>Filter</c> string and the candidate tile; the
  /// registry routes to the registered <see cref="IRecipeFilter"/>
  /// or returns <c>false</c> with a one-time warning if no filter
  /// matches.
  ///
  /// <para><b>Why centralised.</b> Before this registry existed,
  /// each of the four reconcilers had its own <c>switch (filter)</c>
  /// + warned-set, so adding a new filter type touched four files.
  /// Now: implement <see cref="IRecipeFilter"/> + add one
  /// <c>MultiBind</c> in the configurator. One file, no churn in
  /// handlers.</para>
  ///
  /// <para>Empty filter strings are eligible for every tile (no
  /// constraint). Unknown filter names are ineligible everywhere
  /// (defensive: a typo shouldn't silently behave like "no filter").</para>
  /// </summary>
  public sealed class RecipeFilterRegistry {

    private readonly Dictionary<string, IRecipeFilter> _filters;
    private readonly HashSet<string> _warnedNames = new();

    public RecipeFilterRegistry(IEnumerable<IRecipeFilter> filters) {
      _filters = new Dictionary<string, IRecipeFilter>();
      foreach (var filter in filters) {
        _filters[filter.Name] = filter;
      }
    }

    /// <summary>Whether <paramref name="filterName"/> recognises
    /// <paramref name="surface"/> as eligible. Empty filter = true
    /// (no constraint). Unknown filter = false, with a one-time
    /// warning so a typo surfaces visibly without spamming the log.</summary>
    public bool IsEligible(string filterName, SurfaceCoord surface) {
      if (string.IsNullOrEmpty(filterName)) return true;
      if (_filters.TryGetValue(filterName, out var filter)) {
        return filter.IsEligible(surface);
      }
      if (_warnedNames.Add(filterName)) {
        KeystoneLog.Warn(
            $"[Keystone] RecipeFilterRegistry: unknown filter '{filterName}'. " +
            "Recipe will not fire until the filter is registered. " +
            $"Known filters: \"\", {string.Join(", ", _filters.Keys)}.");
      }
      return false;
    }

  }

}
