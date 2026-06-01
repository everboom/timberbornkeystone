using System;
using System.Collections.Generic;

namespace Keystone.Core.Flora {

  /// <summary>
  /// Distilled per-blueprint ecological signature for one flora
  /// blueprint. Built once at mod load by walking
  /// <c>ISpecService.GetSpecs&lt;NaturalResourceSpec&gt;()</c> and
  /// projecting only the fields ecology rules will read -- keeping the
  /// catalog small (~tens of blueprints x small footprint each) and
  /// independent of the full Timberborn spec graph.
  ///
  /// <para><b>Why nullable on most fields.</b> Not every flora carries
  /// every spec. A decorative ground cover might lack a Yielder; a
  /// hardy weed might lack a moisture tolerance; a cuttable tree might
  /// lack a flood tolerance. Null = "this blueprint doesn't expose
  /// that signal" rather than zero -- which would lie to consumers
  /// computing relative scales.</para>
  ///
  /// <para><b>Two yield channels.</b> A blueprint can be both
  /// cuttable AND gatherable (Pine, Maple, ChestnutTree, Mangrove all
  /// are). The two yields are tracked separately in
  /// <see cref="CutYield"/> and <see cref="GatherYield"/> -- a Maple
  /// yields logs when felled and syrup when tapped, and ecology rules
  /// will eventually want both numbers.</para>
  ///
  /// <para><b>Plantable groups, not faction.</b>
  /// <see cref="PlantableGroups"/> captures the raw resource-group
  /// strings from <c>PlantableSpec.ResourceGroup</c> (<c>"Forester"</c>,
  /// <c>"Farmhouse"</c>, etc.) -- empty when the flora is wild-only.
  /// Faction attribution is derived by the consumer from the matching
  /// <c>PlanterBuildingSpec</c> entries on the building side, not stored
  /// here. That keeps the data faction-mod-agnostic.</para>
  ///
  /// <para><b>Values are absolute, not normalised.</b> The catalog
  /// stores raw growth-time-in-days, raw yield amounts, etc. The
  /// "compare on a relative scale" framing belongs with the
  /// consumers that read this catalog (eco-pressure rules, biome
  /// classification, ...) -- not baked into the data shape.</para>
  /// </summary>
  public sealed class FloraEntry {

    #region Properties

    /// <summary>Blueprint identifier from <c>BlockObjectSpec.Blueprint.Name</c> (e.g. "Pine.WhitePine").</summary>
    public string BlueprintName { get; }

    /// <summary>
    /// Logical template name from <c>TemplateSpec.TemplateName</c> when present;
    /// often the same across faction variants (e.g. all houses share a template).
    /// Null when the blueprint carries no <c>TemplateSpec</c>.
    /// </summary>
    public string? TemplateName { get; }

    /// <summary>
    /// Faction tag extracted from the blueprint name's last <c>.</c>-segment
    /// (e.g. <c>Carrot.Folktails</c> → <c>"Folktails"</c>). Null when the
    /// name has no <c>.</c>-segment we could split out -- shared natural
    /// content (most wild flora). The convention isn't perfect
    /// (<c>Pine.WhitePine</c> yields <c>"WhitePine"</c>, which is a
    /// variant not a faction) but consumers can refine on lookup.
    /// </summary>
    public string? Faction { get; }

    /// <summary>Coarse classification (Tree / Bush / Crop / GroundCover) -- see <see cref="FloraKind"/>.</summary>
    public FloraKind Kind { get; }

    /// <summary>
    /// Resource groups this flora belongs to, from one or more
    /// <c>PlantableSpec.ResourceGroup</c> entries. Empty when the flora
    /// is wild-only (no <c>PlantableSpec</c>). Cross-references with
    /// <c>BuildingEntry.PlantableGroup</c> on the building side.
    /// </summary>
    public IReadOnlyList<string> PlantableGroups { get; }

    /// <summary>True iff this flora can be planted by some planter (i.e. <see cref="PlantableGroups"/> is non-empty).</summary>
    public bool IsPlantable => PlantableGroups.Count > 0;

    /// <summary>Days from sapling to mature, from <c>GrowableSpec.GrowthTimeInDays</c>. Null if blueprint lacks <c>GrowableSpec</c>.</summary>
    public float? GrowthTimeInDays { get; }

    /// <summary>Days the plant survives without moisture, from <c>WateredNaturalResourceSpec.DaysToDieDry</c>. Null if not moisture-sensitive.</summary>
    public float? DaysToDieDry { get; }

    /// <summary>Lower bound (inclusive) of the water-depth tolerance window, from <c>FloodableNaturalResourceSpec.MinWaterHeight</c>. Null if not flood-sensitive.</summary>
    public int? MinWaterHeight { get; }

    /// <summary>Upper bound (inclusive) of the water-depth tolerance window, from <c>FloodableNaturalResourceSpec.MaxWaterHeight</c>. Null if not flood-sensitive.</summary>
    public int? MaxWaterHeight { get; }

    /// <summary>Days the plant survives outside its water-depth window, from <c>FloodableNaturalResourceSpec.DaysToDie</c>. Null if not flood-sensitive.</summary>
    public float? DaysToDieFlooded { get; }

    /// <summary>True iff blueprint carries <c>CuttableSpec</c> -- felled by a lumberjack.</summary>
    public bool IsCuttable { get; }

    /// <summary>
    /// From <c>CuttableSpec.RemoveOnCut</c>. <c>true</c> = felled and
    /// removed; <c>false</c> = a stump or stub entity persists after
    /// the cut. Vanilla trees universally use <c>false</c>; the flag
    /// is preserved for completeness but is not the tap discriminator
    /// (see <see cref="GatherYield"/>'s <c>ResourceGroup</c> for
    /// that). Null when not cuttable.
    /// </summary>
    public bool? RemoveOnCut { get; }

    /// <summary>True iff blueprint carries <c>GatherableSpec</c> -- harvested by a gatherer or tapper.</summary>
    public bool IsGatherable { get; }

    /// <summary>Days between successive yields on a gatherable, from <c>GatherableSpec.YieldGrowthTimeInDays</c>. Null when not gatherable.</summary>
    public float? YieldGrowthTimeInDays { get; }

    /// <summary>
    /// Yield produced by felling (<c>CuttableSpec.Yielder</c>). Null
    /// when the blueprint is not cuttable.
    /// </summary>
    public YieldInfo? CutYield { get; }

    /// <summary>
    /// Yield produced by gathering or tapping
    /// (<c>GatherableSpec.Yielder</c>). The <c>ResourceGroup</c> on
    /// the yield distinguishes <c>Tappable</c> (sap) from
    /// <c>Gatherable</c> (fruit). Null when the blueprint is not
    /// gatherable.
    /// </summary>
    public YieldInfo? GatherYield { get; }

    #endregion

    #region Construction

    /// <summary>Construct a flora entry. Loaders build these from spec data.</summary>
    public FloraEntry(
        string blueprintName,
        string? templateName,
        string? faction,
        FloraKind kind,
        IReadOnlyList<string> plantableGroups,
        float? growthTimeInDays,
        float? daysToDieDry,
        int? minWaterHeight,
        int? maxWaterHeight,
        float? daysToDieFlooded,
        bool isCuttable,
        bool? removeOnCut,
        bool isGatherable,
        float? yieldGrowthTimeInDays,
        YieldInfo? cutYield,
        YieldInfo? gatherYield) {
      BlueprintName = blueprintName;
      TemplateName = templateName;
      Faction = faction;
      Kind = kind;
      PlantableGroups = plantableGroups ?? Array.Empty<string>();
      GrowthTimeInDays = growthTimeInDays;
      DaysToDieDry = daysToDieDry;
      MinWaterHeight = minWaterHeight;
      MaxWaterHeight = maxWaterHeight;
      DaysToDieFlooded = daysToDieFlooded;
      IsCuttable = isCuttable;
      RemoveOnCut = removeOnCut;
      IsGatherable = isGatherable;
      YieldGrowthTimeInDays = yieldGrowthTimeInDays;
      CutYield = cutYield;
      GatherYield = gatherYield;
    }

    #endregion

  }

}
