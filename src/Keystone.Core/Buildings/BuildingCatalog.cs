using System.Collections.Generic;

namespace Keystone.Core.Buildings {

  /// <summary>
  /// Read-mostly registry of building blueprints discovered at mod load.
  /// Mirrors <see cref="Flora.FloraCatalog"/> on the building side. A
  /// Mod-side loader populates the catalog at <c>IPostLoadableSingleton.PostLoad</c>
  /// from <c>ISpecService.GetSpecs&lt;BuildingSpec&gt;()</c>; consumers
  /// (cursor display, future ecology rules that key off building roles)
  /// read it freely thereafter.
  ///
  /// <para><b>Why a catalog instead of inline classification.</b> Three
  /// reasons. (1) The role detection requires walking
  /// <c>TemplateModule.Decorators</c> for runtime components attached
  /// via Bindito's decorator path -- expensive to repeat per query.
  /// (2) Faction extraction and <see cref="BuildingEntry.PlantableGroup"/>
  /// lookups need a single source of truth. (3) Tests can populate the
  /// catalog with fake entries to exercise consumers without requiring
  /// a live game.</para>
  ///
  /// <para><b>Plantable cross-reference.</b> Building entries with a
  /// <see cref="BuildingEntry.PlantableGroup"/> set are planters -- their
  /// group is the join key with <c>FloraEntry.PlantableGroups</c>.
  /// <see cref="PlantersByGroup"/> exposes that index directly so
  /// "which planters can plant this flora" is a one-step lookup.</para>
  /// </summary>
  public sealed class BuildingCatalog {

    #region Fields

    private readonly Dictionary<string, BuildingEntry> _byBlueprintName = new();
    private readonly Dictionary<string, List<BuildingEntry>> _plantersByGroup = new();

    #endregion

    #region Properties

    /// <summary>Number of entries currently in the catalog.</summary>
    public int Count => _byBlueprintName.Count;

    /// <summary>Iterate every entry, in insertion order.</summary>
    public IEnumerable<BuildingEntry> Entries => _byBlueprintName.Values;

    #endregion

    #region Read API

    /// <summary>
    /// Look up a building entry by blueprint name (e.g. "BigLodge.Folktails").
    /// Returns null when no entry by that name was catalogued.
    /// </summary>
    public BuildingEntry? Get(string blueprintName) =>
        _byBlueprintName.TryGetValue(blueprintName, out var entry) ? entry : null;

    /// <summary>True iff a building with this blueprint name was discovered at load.</summary>
    public bool Contains(string blueprintName) =>
        _byBlueprintName.ContainsKey(blueprintName);

    /// <summary>
    /// All planter buildings that operate on <paramref name="group"/>.
    /// Empty when no planter targets that group. The returned collection
    /// is the catalog's own list -- treat as read-only.
    /// </summary>
    public IReadOnlyList<BuildingEntry> PlantersByGroup(string group) =>
        _plantersByGroup.TryGetValue(group, out var list)
            ? list
            : (IReadOnlyList<BuildingEntry>)System.Array.Empty<BuildingEntry>();

    /// <summary>Count entries whose <see cref="BuildingEntry.Roles"/> overlap with <paramref name="roles"/> at all.</summary>
    public int CountWithAnyRole(BuildingRoles roles) {
      var n = 0;
      foreach (var e in _byBlueprintName.Values) {
        if ((e.Roles & roles) != 0) n++;
      }
      return n;
    }

    #endregion

    #region Write API (loader use)

    /// <summary>
    /// Replace the catalog contents with <paramref name="entries"/>.
    /// Intended for one-shot population by a loader at mod-load.
    /// Re-derives the planter-by-group index. Calling after consumers
    /// have read the catalog will silently invalidate their snapshots.
    /// </summary>
    public void Populate(IEnumerable<BuildingEntry> entries) {
      _byBlueprintName.Clear();
      _plantersByGroup.Clear();
      foreach (var e in entries) {
        _byBlueprintName[e.BlueprintName] = e;
        if (e.PlantableGroup is { } group) {
          if (!_plantersByGroup.TryGetValue(group, out var list)) {
            list = new List<BuildingEntry>();
            _plantersByGroup[group] = list;
          }
          list.Add(e);
        }
      }
    }

    #endregion

  }

}
