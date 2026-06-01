using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Keystone.Core.Buildings;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Diagnostics.SelfTests;
using Timberborn.BlueprintSystem;
using Timberborn.Buildings;
using Timberborn.Planting;
using Timberborn.SingletonSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.TemplateSystem;
using Timberborn.Workshops;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Buildings {

  /// <summary>
  /// Mod-side loader that fills <see cref="BuildingCatalog"/> at
  /// <see cref="PostLoad"/>. Walks every <see cref="BuildingSpec"/>
  /// blueprint, derives a per-blueprint <see cref="BuildingRoles"/>
  /// flag set from spec presence + decorator-attached components,
  /// extracts faction + plantable group, and publishes the result.
  ///
  /// <para><b>How roles are detected.</b> The capability set for a
  /// blueprint is the union of:
  /// <list type="number">
  ///   <item><b>Specs</b> -- everything in <see cref="Blueprint.Specs"/>,
  ///         labelled by trimmed type name (e.g. <c>BuildingSpec</c> →
  ///         <c>"Building"</c>). String-discriminating values from a
  ///         couple of specs (recipe count, planter resource group) are
  ///         folded in as <c>Label[value]</c>.</item>
  ///   <item><b>Runtime components</b> -- looked up via the global
  ///         decorator map aggregated from every injected
  ///         <see cref="TemplateModule"/>. Each
  ///         <c>AddDecorator&lt;TSpec, TComponent&gt;</c> the game has
  ///         registered tells us "if blueprint has TSpec, instances
  ///         have TComponent". Components contributed this way appear
  ///         with a <c>"+"</c> prefix initially, then collapse to the
  ///         bare name.</item>
  /// </list>
  /// We then map each (bracket-stripped) capability name to a
  /// <see cref="BuildingRoles"/> flag via <see cref="CapabilityToRoles"/>.
  /// Anything with no mapped role becomes <see cref="BuildingRoles.Decoration"/>.</para>
  ///
  /// <para><b>Diagnostic dump retained but gated.</b> The grouped
  /// capability dump (universals, presentation suppression, fingerprint
  /// grouping) lives behind <see cref="EnableDump"/>. Useful when a new
  /// faction mod arrives and we want to audit which capabilities show
  /// up; off by default to keep <c>player.log</c> clean.</para>
  ///
  /// <para><b>Limitations.</b> Only components attached via
  /// <c>TemplateModule.AddDecorator</c> are visible. Components placed
  /// directly on the prefab GameObject (Unity-side, not via the Bindito
  /// decorator path) won't show up. So far every ecology-relevant
  /// runtime class we've seen (<c>RangedEffectBuilding</c>,
  /// <c>MechanicalBuilding</c>, <c>Hive</c>, etc.) is decorator-attached.</para>
  /// </summary>
  public sealed class BuildingCatalogLoader : IPostLoadableSingleton, IKeystoneLoadStatus {

    #region Toggles

    /// <summary>
    /// When true, prints the full grouped capability dump to
    /// <c>player.log</c> after population (universals + presentation
    /// suppression + per-fingerprint grouping). Off by default; flip on
    /// when auditing a new faction mod or investigating a missed role.
    /// </summary>
    private const bool EnableDump = false;

    #endregion

    #region Fields

    private readonly TemplateService _templateService;
    private readonly IEnumerable<TemplateModule> _templateModules;
    private readonly BuildingCatalog _catalog;

    /// <summary>
    /// Aggregated spec → component map. Built once at <see cref="PostLoad"/>
    /// from every <see cref="TemplateModule"/>'s <c>Decorators</c>
    /// dictionary. Multiple TemplateModules may contribute decorators
    /// for the same spec; we union them.
    /// </summary>
    private readonly Dictionary<Type, List<Type>> _specToComponents = new();

    /// <summary>True once <see cref="PostLoad"/> has finished
    /// populating <see cref="BuildingCatalog"/>. Read by the startup
    /// self-check to defer until the catalog is ready, and by
    /// <see cref="LoaderSurvivalTest"/> to flag silent load
    /// failures.</summary>
    public bool IsLoaded { get; private set; }

    /// <inheritdoc />
    public string LoaderName => nameof(BuildingCatalogLoader);

    #endregion

    #region Construction

    public BuildingCatalogLoader(
        TemplateService templateService,
        IEnumerable<TemplateModule> templateModules,
        BuildingCatalog catalog) {
      _templateService = templateService;
      _templateModules = templateModules;
      _catalog = catalog;
    }

    #endregion

    #region Role mapping

    /// <summary>
    /// Capability-name → role-flag table. Capability names come from the
    /// <c>SpecType.Name</c> with the conventional <c>"Spec"</c> suffix
    /// trimmed (or matching component name after the <c>"+"</c>-prefix
    /// collapse). Bracketed values (<c>Manufactory[3]</c>,
    /// <c>PlanterBuilding[Forester]</c>) are stripped before lookup.
    ///
    /// <para>We do <b>not</b> infer combined flags here -- a manufactory
    /// is mapped to <see cref="BuildingRoles.Industry"/> only, even
    /// though it's universally also a workplace. The
    /// <c>WorkplaceSpec</c> on the same blueprint contributes
    /// <see cref="BuildingRoles.Workplace"/> independently, so the union
    /// produces the right answer without us pretending to know.</para>
    /// </summary>
    private static readonly Dictionary<string, BuildingRoles> CapabilityToRoles = new(StringComparer.Ordinal) {
        { "Path", BuildingRoles.Path },
        { "Dwelling", BuildingRoles.Dwelling },
        { "Workplace", BuildingRoles.Workplace },
        { "Manufactory", BuildingRoles.Industry },
        { "PlanterBuilding", BuildingRoles.Farming },
        { "Stockpile", BuildingRoles.Storage },
        { "FixedStockpile", BuildingRoles.Storage },
        { "WaterInput", BuildingRoles.WaterInfra },
        { "WaterWheel", BuildingRoles.WaterInfra },
        { "WaterSource", BuildingRoles.WaterInfra },
        { "WaterSourceDischarger", BuildingRoles.WaterInfra },
        { "HazardousWeatherWaterSource", BuildingRoles.WaterInfra },
        { "UndergroundWaterSource", BuildingRoles.WaterInfra },
        { "MechanicalNode", BuildingRoles.Mechanical },
        { "FactionWonder", BuildingRoles.Wonder },
        { "RangedEffectBuilding", BuildingRoles.RangedEffect },
        { "DistrictCenter", BuildingRoles.DistrictAnchor },
        { "BuilderHub", BuildingRoles.DistrictAnchor },
    };

    /// <summary>
    /// Union the role flags contributed by each capability in the list.
    /// Empty union becomes <see cref="BuildingRoles.Decoration"/> so
    /// "no detected role" is queryable as a positive flag, not just
    /// the absence of others.
    /// </summary>
    private static BuildingRoles MapRoles(IReadOnlyList<string> capabilities) {
      var roles = BuildingRoles.None;
      foreach (var cap in capabilities) {
        var bare = StripBracket(cap);
        if (CapabilityToRoles.TryGetValue(bare, out var r)) {
          roles |= r;
        }
      }
      return roles == BuildingRoles.None ? BuildingRoles.Decoration : roles;
    }

    private static string StripBracket(string capability) {
      var bracket = capability.IndexOf('[');
      return bracket > 0 ? capability.Substring(0, bracket) : capability;
    }

    /// <summary>
    /// Pull the faction tag from the last <c>.</c>-segment of the
    /// blueprint name (e.g. <c>BigLodge.Folktails</c> → <c>"Folktails"</c>).
    /// Null when the name has no <c>.</c> -- shared / faction-neutral
    /// content. Mirrors the same heuristic used by
    /// <c>FloraCatalogLoader.ExtractFaction</c> so cross-references stay
    /// consistent.
    /// </summary>
    private static string? ExtractFaction(string blueprintName) {
      var dot = blueprintName.LastIndexOf('.');
      return (dot > 0 && dot < blueprintName.Length - 1)
          ? blueprintName.Substring(dot + 1)
          : null;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      // Outermost try/catch around the whole PostLoad body. The
      // per-blueprint try/catch in ScanAll only protects the inner
      // walk -- a throw outside (ComputeUniversals on a degenerate
      // capability list, FilterEntries, _catalog.Populate, the
      // catalog-entry build loop) would otherwise escape to Bindito
      // with IsLoaded never set, and CatalogStartupCheck would
      // silently defer forever rather than fire the dialog.
      try {
        var sw = Stopwatch.StartNew();
        BuildDecoratorMap();
        var decoratorMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var rawEntries = ScanAll();
        var scanMs = sw.ElapsedMilliseconds;

        var universals = ComputeUniversals(rawEntries, UniversalCoverageThreshold);
        var (presentation, filtered) = FilterEntries(rawEntries, universals);

        sw.Restart();
        var catalogEntries = new List<BuildingEntry>(filtered.Count);
        foreach (var raw in filtered) {
          catalogEntries.Add(new BuildingEntry(
              blueprintName: raw.BlueprintName,
              templateName: raw.TemplateName,
              faction: ExtractFaction(raw.BlueprintName),
              roles: MapRoles(raw.Capabilities),
              plantableGroup: raw.PlantableGroup,
              rawCapabilities: raw.Capabilities.ToArray()));
        }
        _catalog.Populate(catalogEntries);
        var publishMs = sw.ElapsedMilliseconds;

        LogPopulateSummary(catalogEntries, decoratorMs, scanMs, publishMs);

#pragma warning disable CS0162 // Unreachable code -- gate is intentionally a const so the diagnostic dump can be re-enabled by a one-line change.
        if (EnableDump) {
          LogDumpHeader(filtered, universals, presentation);
          LogCapabilityInventory(filtered);
          LogGrouped(filtered);
        }
#pragma warning restore CS0162
        IsLoaded = true;
      } catch (Exception ex) {
        LifecycleGuard.HandleError("BuildingCatalogLoader.PostLoad", "Subsystem failed", ex);
      }
    }

    #endregion

    #region Filter constants

    /// <summary>
    /// Coverage fraction at which a capability is treated as "universal"
    /// and dropped from per-blueprint capability lists. ≥95% means any
    /// capability seen on basically every blueprint contributes nothing
    /// to discrimination -- e.g. <c>Building</c>, <c>BlockObject</c>,
    /// <c>InstantiatedTemplate</c>.
    /// </summary>
    private const double UniversalCoverageThreshold = 0.95;

    /// <summary>
    /// Suffix denylist for presentation-layer capability names (visual,
    /// sound, animation, particle, decal, status-icon). Any capability
    /// whose name ends in one of these is considered noise for ecology
    /// classification and dropped from per-blueprint capability lists.
    /// </summary>
    private static readonly string[] PresentationSuffixes = {
        "Model", "Sound", "Sounds", "Animator", "AnimationController",
        "Drawer", "Visualizer", "Highlighter", "Describer", "Illuminator",
        "Cache", "Cycler", "Hider", "Badge", "Icon", "Outfit", "Texture",
        "Updater", "Renderer",
    };

    /// <summary>
    /// Substring denylist -- capabilities whose name contains any of
    /// these are considered presentation-layer regardless of suffix.
    /// Smaller list because substring matches are coarser; keep narrow.
    /// </summary>
    private static readonly string[] PresentationSubstrings = {
        "Particle", "Decal",
    };

    #endregion

    #region Decorator map

    /// <summary>
    /// Walk every injected <see cref="TemplateModule"/> and union its
    /// <c>Decorators</c> dictionary into <see cref="_specToComponents"/>.
    /// The result is queried per-blueprint during <see cref="ScanAll"/>.
    /// </summary>
    private void BuildDecoratorMap() {
      _specToComponents.Clear();
      foreach (var module in _templateModules) {
        foreach (var kv in module.Decorators) {
          if (!_specToComponents.TryGetValue(kv.Key, out var list)) {
            list = new List<Type>();
            _specToComponents[kv.Key] = list;
          }
          foreach (var dec in kv.Value) {
            // De-dupe per spec key in case multiple modules register
            // the same decorator -- shouldn't happen but defensive.
            if (!list.Contains(dec.DecoratorType)) {
              list.Add(dec.DecoratorType);
            }
          }
        }
      }
    }

    #endregion

    #region Scan

    /// <summary>
    /// One pass over every <see cref="BuildingSpec"/> blueprint reachable
    /// via <see cref="TemplateService.GetAll{T}"/>. For each, derive the
    /// capability set (specs + decorator-attached components), and
    /// capture the planter resource group when present.
    ///
    /// <para><b>Why <see cref="TemplateService"/> rather than
    /// <c>ISpecService.GetSpecs&lt;BuildingSpec&gt;</c>.</b> Same reason
    /// <see cref="Recipes.BlueprintResolver"/> uses
    /// <see cref="TemplateNameMapper"/>: the spec-service walk fires
    /// every bundle's deserialize lazy that contains a
    /// <c>BuildingSpec</c> at root -- i.e. every loaded building
    /// blueprint across every installed mod -- which turns us into the
    /// trigger point for any third-party mod that ships a broken
    /// nested-blueprint reference (observed: a Whitepaws building with
    /// a "Timberborn U6.1/Resources U6.1/..." nested path that doesn't
    /// resolve at runtime). <see cref="TemplateService.GetAll{T}"/>
    /// iterates only already-materialised blueprints in
    /// <c>TemplateCollectionService.AllTemplates</c>, so foreign-mod
    /// blueprints that failed to deserialize at their own faction's
    /// Load are simply absent from the enumeration rather than
    /// re-attempted (and thrown on) by us.</para>
    /// </summary>
    private List<Entry> ScanAll() {
      var result = new List<Entry>();
      var skipped = 0;
      foreach (var building in _templateService.GetAll<BuildingSpec>()) {
        // Per-blueprint isolation: a single malformed third-party
        // blueprint (default Specs ImmutableArray, unexpected spec
        // shape, accessor on a misconfigured field) shouldn't take
        // out the whole catalog. Log the blueprint name + exception
        // type so the offending mod is identifiable, then skip and
        // continue. The rest of the catalog still populates.
        try {
          var bp = building.Blueprint;
          if (bp == null) continue;
          var caps = BuildCapabilitySet(bp);
          var template = bp.HasSpec<TemplateSpec>()
              ? bp.GetSpec<TemplateSpec>().TemplateName
              : null;
          var plantableGroup = bp.HasSpec<PlanterBuildingSpec>()
              ? NullIfEmpty(bp.GetSpec<PlanterBuildingSpec>().PlantableResourceGroup)
              : null;
          result.Add(new Entry(
              BlueprintName: bp.Name,
              TemplateName: template,
              Capabilities: caps,
              Fingerprint: caps.Count == 0 ? "(empty)" : string.Join(",", caps),
              PlantableGroup: plantableGroup));
        } catch (Exception ex) {
          skipped++;
          var name = building.Blueprint?.Name ?? "(no blueprint)";
          KeystoneLog.Warn(
              $"[Keystone] BuildingCatalogLoader: skipping blueprint '{name}' " +
              $"-- threw {ex.GetType().Name}: {ex.Message}. " +
              "Catalog will be missing this entry; downstream consumers won't see it.");
          KeystoneIntegrationHealth.TryRecord("Skipped blueprints (buildings)", name);
        }
      }
      if (skipped > 0) {
        KeystoneLog.Warn(
            $"[Keystone] BuildingCatalogLoader: skipped {skipped} malformed blueprint(s); " +
            $"catalog contains {result.Count} entries.");
      }
      ReportCasingDrift(result);
      return result;
    }

    /// <summary>
    /// Dev-mode diagnostic: flag any loaded building whose runtime
    /// <c>Blueprint.Name</c> matches a no-aura / transparent classification
    /// entry ONLY via the case-insensitive fallback — i.e. our
    /// hand-maintained list's casing has drifted from the mod's actual
    /// name. The classification still works (the fallback catches it), but
    /// the list entry should be corrected to the exact runtime spelling so
    /// the exact path hits and the fallback isn't masking a stale entry.
    /// Runs once over the catalog at load, off the per-block-object
    /// classification hot path. See
    /// <see cref="Keystone.Core.Buildings.Factions.FactionRegistry.FindCasingDrift"/>.
    /// </summary>
    private static void ReportCasingDrift(List<Entry> entries) {
      if (!KeystoneDevMode.IsEnabled) return;
      var names = new List<string>(entries.Count);
      foreach (var e in entries) names.Add(e.BlueprintName);
      foreach (var (actual, listed, list) in
               Keystone.Core.Buildings.Factions.FactionRegistry.FindCasingDrift(names)) {
        KeystoneLog.Warn(
            $"[Keystone] {list} classification: list entry '{listed}' matched runtime " +
            $"blueprint '{actual}' only via case-insensitive fallback — the match works, " +
            "but update the list entry to the exact runtime casing.");
      }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// Compute the sorted capability list for one blueprint. Combines
    /// spec labels + component labels (from the decorator map), then
    /// normalizes by dropping the spec/component <c>"+"</c> prefix
    /// distinction and deduping the resulting names.
    ///
    /// <para>Prefix collapse is what makes the dump readable: nearly
    /// every spec has a matching same-named component (the decorator
    /// is 1:1), so without collapsing we'd see <c>Workplace,+Workplace</c>
    /// on most blueprints. After collapse, label distinctions only
    /// survive when the spec and component genuinely have different
    /// names (e.g. <c>MechanicalConnectorTargetSpec</c> →
    /// <c>MechanicalConnectors</c> component) or when a spec carries a
    /// discriminating value in brackets (<c>Manufactory[1]</c> stays
    /// distinct from <c>Manufactory</c>).</para>
    /// </summary>
    private List<string> BuildCapabilitySet(Blueprint bp) {
      var raw = new List<string>();
      var componentSeen = new HashSet<Type>();
      foreach (var spec in bp.Specs) {
        var specType = spec.GetType();
        raw.Add(LabelForSpec(specType, spec));
        if (_specToComponents.TryGetValue(specType, out var components)) {
          foreach (var c in components) {
            if (componentSeen.Add(c)) {
              raw.Add("+" + c.Name);
            }
          }
        }
      }
      // Normalize: drop the '+' prefix, dedupe.
      var seen = new HashSet<string>(StringComparer.Ordinal);
      var normalized = new List<string>(raw.Count);
      foreach (var label in raw) {
        var canonical = label.StartsWith("+", StringComparison.Ordinal)
            ? label.Substring(1)
            : label;
        if (seen.Add(canonical)) normalized.Add(canonical);
      }
      normalized.Sort(StringComparer.Ordinal);
      return normalized;
    }

    /// <summary>
    /// Trim the conventional <c>Spec</c> suffix and fold in
    /// discriminating string values from the small set of specs that
    /// expose them (recipe count, planter resource group).
    /// </summary>
    private static string LabelForSpec(Type specType, ComponentSpec spec) {
      var name = TrimSuffix(specType.Name, "Spec");
      return spec switch {
          ManufactorySpec m => $"{name}[{m.ProductionRecipeIds.Length}]",
          PlanterBuildingSpec p when !string.IsNullOrEmpty(p.PlantableResourceGroup) =>
              $"{name}[{p.PlantableResourceGroup}]",
          _ => name,
      };
    }

    private static string TrimSuffix(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length
            ? s.Substring(0, s.Length - suffix.Length)
            : s;

    #endregion

    #region Filtering

    /// <summary>
    /// Compute the set of "universal" capabilities -- those present on
    /// at least <paramref name="threshold"/> fraction of blueprints.
    /// These are dropped from per-blueprint capability lists because they
    /// don't discriminate.
    /// </summary>
    private static HashSet<string> ComputeUniversals(List<Entry> entries, double threshold) {
      if (entries.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
      var counts = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (var e in entries) {
        foreach (var c in e.Capabilities) {
          counts.TryGetValue(c, out var n);
          counts[c] = n + 1;
        }
      }
      var minCount = (int)Math.Ceiling(threshold * entries.Count);
      var result = new HashSet<string>(StringComparer.Ordinal);
      foreach (var kv in counts) {
        if (kv.Value >= minCount) result.Add(kv.Key);
      }
      return result;
    }

    /// <summary>
    /// True iff <paramref name="capability"/> matches the presentation-
    /// layer denylist (suffix or substring). Used to suppress visual /
    /// sound / particle / animation / decal / status-icon capabilities
    /// that don't help with ecology classification. Inputs come in
    /// already-normalised form (no <c>"+"</c> prefix after the collapse
    /// step in <see cref="BuildCapabilitySet"/>), but we tolerate the
    /// prefix defensively in case the call order shifts.
    /// </summary>
    private static bool IsPresentationName(string capability) {
      var bare = capability.StartsWith("+", StringComparison.Ordinal)
          ? capability.Substring(1)
          : capability;
      var bracket = bare.IndexOf('[');
      if (bracket > 0) bare = bare.Substring(0, bracket);

      foreach (var s in PresentationSuffixes) {
        if (bare.EndsWith(s, StringComparison.Ordinal)) return true;
      }
      foreach (var s in PresentationSubstrings) {
        if (bare.IndexOf(s, StringComparison.Ordinal) >= 0) return true;
      }
      return false;
    }

    /// <summary>
    /// Build new entries with capability lists filtered down to
    /// "discriminating, non-presentation" labels, with regenerated
    /// fingerprints. Returns the suppressed presentation labels too,
    /// so the diagnostic dump can report what was dropped.
    /// </summary>
    private static (HashSet<string> presentation, List<Entry> filtered) FilterEntries(
        List<Entry> rawEntries, HashSet<string> universals) {
      var presentation = new HashSet<string>(StringComparer.Ordinal);
      var filtered = new List<Entry>(rawEntries.Count);
      foreach (var raw in rawEntries) {
        var newCaps = new List<string>(raw.Capabilities.Count);
        foreach (var c in raw.Capabilities) {
          if (universals.Contains(c)) continue;
          if (IsPresentationName(c)) {
            presentation.Add(c);
            continue;
          }
          newCaps.Add(c);
        }
        filtered.Add(raw with {
            Capabilities = newCaps,
            Fingerprint = newCaps.Count == 0 ? "(filtered to nothing)" : string.Join(",", newCaps),
        });
      }
      return (presentation, filtered);
    }

    #endregion

    #region Logging

    /// <summary>One-line publish summary -- always logged so we can confirm catalog populated.</summary>
    private void LogPopulateSummary(
        List<BuildingEntry> entries, long decoratorMs, long scanMs, long publishMs) {
      var roleCounts = new Dictionary<BuildingRoles, int>();
      foreach (var role in (BuildingRoles[])Enum.GetValues(typeof(BuildingRoles))) {
        if (role == BuildingRoles.None) continue;
        var n = 0;
        foreach (var e in entries) {
          if ((e.Roles & role) != 0) n++;
        }
        if (n > 0) roleCounts[role] = n;
      }
      var summary = string.Join(", ",
          roleCounts.OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{kv.Value} {kv.Key}"));
      KeystoneLog.Verbose($"[Keystone] Building catalog: {entries.Count} blueprints. " +
                 $"Roles: {summary}. " +
                 $"Decorator map {decoratorMs} ms, scan {scanMs} ms, publish {publishMs} ms.");
    }

    /// <summary>Diagnostic-dump header (only emitted when <see cref="EnableDump"/> is true).</summary>
    private void LogDumpHeader(
        List<Entry> entries, HashSet<string> universals, HashSet<string> presentation) {
      var distinctFingerprints = entries.Select(e => e.Fingerprint).Distinct().Count();
      var decoratorPairs = _specToComponents.Sum(kv => kv.Value.Count);
      KeystoneLog.Verbose($"[Keystone] Building catalog dump: {entries.Count} blueprints, " +
                 $"{distinctFingerprints} distinct capability-fingerprints, " +
                 $"{_specToComponents.Count} specs map to {decoratorPairs} components " +
                 $"via {_templateModules.Count()} TemplateModules.");

      LogFilteredList("universal (≥95% coverage)", universals);
      LogFilteredList("presentation-layer (suffix/substring match)", presentation);
    }

    /// <summary>
    /// Print a filtered-out capability set with count + alphabetised
    /// listing. Cap at 30 names inline so the summary stays readable;
    /// the user can read the source if they need to know about a
    /// specific suppressed capability.
    /// </summary>
    private static void LogFilteredList(string label, HashSet<string> set) {
      if (set.Count == 0) return;
      var sorted = set.OrderBy(s => s, StringComparer.Ordinal).ToList();
      const int maxInline = 30;
      var head = string.Join(", ", sorted.Take(maxInline));
      var tail = sorted.Count > maxInline ? $", ...and {sorted.Count - maxInline} more" : "";
      KeystoneLog.Verbose($"[Keystone] Suppressed {set.Count} {label}: {head}{tail}");
    }

    /// <summary>
    /// Inventory of capabilities across all (filtered) blueprints,
    /// sorted by occurrence count descending. Restricted to count
    /// strictly between 1 and total -- universals are already filtered
    /// out of <see cref="Entry.Capabilities"/>, but singletons (count=1)
    /// add little signal so we drop them too.
    /// </summary>
    private static void LogCapabilityInventory(List<Entry> entries) {
      var counts = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (var e in entries) {
        foreach (var c in e.Capabilities) {
          counts.TryGetValue(c, out var n);
          counts[c] = n + 1;
        }
      }
      var total = entries.Count;
      var discriminating = counts.Where(kv => kv.Value > 1 && kv.Value < total).ToList();
      KeystoneLog.Verbose($"[Keystone] Capability inventory: {discriminating.Count} discriminating " +
                 $"(of {counts.Count} after filters; suppressed singletons + universals).");
      foreach (var kv in discriminating
                   .OrderByDescending(kv => kv.Value)
                   .ThenBy(kv => kv.Key, StringComparer.Ordinal)) {
        KeystoneLog.Verbose($"[Keystone]   {kv.Value,4}x {kv.Key}");
      }
    }

    private static void LogGrouped(List<Entry> entries) {
      var groups = entries
          .GroupBy(e => e.Fingerprint)
          .OrderByDescending(g => g.Count())
          .ThenBy(g => g.Key, StringComparer.Ordinal);

      var sb = new StringBuilder();
      foreach (var g in groups) {
        KeystoneLog.Verbose($"[Keystone] {g.Count()}x [{g.Key}]");
        foreach (var e in g.OrderBy(e => e.BlueprintName, StringComparer.Ordinal)) {
          sb.Length = 0;
          sb.Append("[Keystone]   ").Append(e.BlueprintName);
          if (e.TemplateName != null && e.TemplateName != e.BlueprintName) {
            sb.Append(" tmpl=").Append(e.TemplateName);
          }
          if (e.PlantableGroup != null) {
            sb.Append(" plants=").Append(e.PlantableGroup);
          }
          KeystoneLog.Verbose(sb.ToString());
        }
      }
    }

    #endregion

    #region Internal record

    private sealed record Entry(
        string BlueprintName,
        string? TemplateName,
        List<string> Capabilities,
        string Fingerprint,
        string? PlantableGroup);

    #endregion

  }

}
