using System;
using System.Collections.Generic;

namespace Keystone.Core.Buildings {

  /// <summary>
  /// Distilled per-blueprint description for one player-placeable
  /// building. Built once at mod load by the Mod-side loader walking
  /// <c>ISpecService.GetSpecs&lt;BuildingSpec&gt;()</c>; consumers
  /// (cursor display, ecology rules, future placement queries) read it
  /// freely thereafter.
  ///
  /// <para><b>Roles, not category.</b> Buildings commonly fill multiple
  /// roles (a lodge that's also a path, a manufactory that's also a
  /// workplace, a water wheel that's both mechanical and water infra).
  /// We capture all detected roles as a bitwise flag set rather than
  /// picking one canonical "category." Querying becomes a flag test;
  /// consumers that want a single label can pick by priority.</para>
  ///
  /// <para><b>Faction is a string, not an enum.</b> Faction-mod authors
  /// can ship any suffix; we accept it verbatim. <c>null</c> = the
  /// blueprint name has no <c>.</c>-segment we could extract from.</para>
  ///
  /// <para><b>Raw capabilities preserved for diagnostics.</b> The full
  /// capability list (specs + decorator-attached components, post
  /// presentation/universal filtering) is kept on the entry for
  /// debug queries and future role-mapping audits. Cheap relative to
  /// the rest of the catalog (a few hundred blueprints, ~10 strings each).</para>
  /// </summary>
  public sealed class BuildingEntry {

    #region Properties

    /// <summary>Blueprint identifier from <c>BlockObjectSpec.Blueprint.Name</c> (e.g. "BigLodge.Folktails").</summary>
    public string BlueprintName { get; }

    /// <summary>Logical template name from <c>TemplateSpec.TemplateName</c> when present; null otherwise.</summary>
    public string? TemplateName { get; }

    /// <summary>
    /// Faction tag extracted from the blueprint name's last <c>.</c>-segment
    /// (e.g. <c>BigLodge.Folktails</c> → <c>"Folktails"</c>). Null when the
    /// name has no <c>.</c> at all -- shared / faction-neutral content.
    /// We accept any string; faction-mod authors define their own labels.
    /// </summary>
    public string? Faction { get; }

    /// <summary>Bitwise role tags. <see cref="BuildingRoles.Decoration"/> when no other role matched.</summary>
    public BuildingRoles Roles { get; }

    /// <summary>
    /// Resource group this planter operates on, from
    /// <c>PlanterBuildingSpec.PlantableResourceGroup</c>. Null when the
    /// blueprint isn't a planter. Cross-references with
    /// <c>FloraEntry.PlantableGroups</c> on the flora side.
    /// </summary>
    public string? PlantableGroup { get; }

    /// <summary>
    /// Sorted, deduped capability strings the loader saw on this
    /// blueprint -- specs (with the <c>"Spec"</c> suffix trimmed) plus
    /// decorator-attached components. Held for diagnostic queries; not
    /// load-bearing for role classification (that uses <see cref="Roles"/>).
    /// </summary>
    public IReadOnlyList<string> RawCapabilities { get; }

    #endregion

    #region Construction

    /// <summary>Construct a building entry. Loaders build these from spec + decorator data.</summary>
    public BuildingEntry(
        string blueprintName,
        string? templateName,
        string? faction,
        BuildingRoles roles,
        string? plantableGroup,
        IReadOnlyList<string> rawCapabilities) {
      BlueprintName = blueprintName;
      TemplateName = templateName;
      Faction = faction;
      Roles = roles;
      PlantableGroup = plantableGroup;
      RawCapabilities = rawCapabilities ?? Array.Empty<string>();
    }

    #endregion

  }

}
