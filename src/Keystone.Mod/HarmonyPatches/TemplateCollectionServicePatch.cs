using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Keystone.Mod.Diagnostics;
using Timberborn.BlueprintSystem;
using Timberborn.Buildings;
using Timberborn.Cutting;
using Timberborn.FactionSystem;
using Timberborn.Fields;
using Timberborn.Gathering;
using Timberborn.GoodCollectionSystem;
using Timberborn.Planting;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Postfix on <see cref="TemplateCollectionService.Load"/> that
  /// rebuilds <c>AllTemplates</c> with two passes:
  /// <list type="number">
  ///   <item><b>Dedup-first-wins.</b> Drops any blueprint whose
  ///         <c>Name</c> has already been seen earlier in
  ///         <c>AllTemplates</c>. Required because expansion factions
  ///         (Emberpelts, etc.) reference vanilla blueprint file paths
  ///         directly from their own <c>NaturalResources.&lt;Faction&gt;</c>
  ///         collection -- and <c>CrossFactionCollectionProvider</c>
  ///         then asks the service to load the vanilla collections too,
  ///         so the same file gets parsed twice.
  ///         <c>TemplateNameMapper.TryAddTemplate</c> throws on
  ///         duplicate names; without this pass the game fails to
  ///         start with any combination of expansion + Keystone.</item>
  ///   <item><b>Drop cross-faction Buildings.</b> A
  ///         <c>NaturalResources.&lt;Faction&gt;</c> collection is, by
  ///         convention, supposed to contain natural resources -- but
  ///         the convention isn't enforced. A third-party mod has been
  ///         observed appending a <see cref="BuildingSpec"/>-carrying
  ///         blueprint into <c>NaturalResources.Folktails</c> (the
  ///         "Water Purification" mod's <c>MarshwatcherHut.Folktails</c>,
  ///         intended for Folktails saves only). Once
  ///         <see cref="Debug.CrossFactionCollectionProvider"/> asks the
  ///         service to load the other faction's NaturalResources, that
  ///         building ends up in an IronTeeth save's <c>AllTemplates</c>
  ///         and the toolbox eagerly instantiates a preview whose
  ///         construction-cost good ("Paper") doesn't exist in the
  ///         active faction -- crash. We only cross-load
  ///         <c>NaturalResources.*</c>, never <c>Buildings.*</c>, so any
  ///         blueprint in <c>crossFactionBlueprintNames</c> carrying a
  ///         <see cref="BuildingSpec"/> was smuggled in via someone's
  ///         append and we drop it entirely.</item>
  ///   <item><b>Scope discipline.</b> Drop and strip operate only on
  ///         blueprints whose name appears in
  ///         <c>crossFactionBlueprintNames</c> -- i.e. blueprints that
  ///         entered <c>AllTemplates</c> via the vanilla cross-faction
  ///         NaturalResources collection we explicitly cross-loaded
  ///         (see <see cref="CrossFactionCandidateIds"/>). Expansion-
  ///         faction collections (Emberpelts, Whitepaws, etc.) are
  ///         excluded — those factions load their own content and don't
  ///         need our strip. Additionally, any blueprint that's also
  ///         in <c>nativeBlueprintNames</c> (the active faction declared
  ///         it) passes through untouched regardless of cross-faction
  ///         scope — native always trumps cross-faction.</item>
  ///   <item><b>Capability-aware strip on cross-faction blueprints.</b>
  ///         Computes three sets:
  ///         <list type="bullet">
  ///           <item><i>nativeBlueprintNames</i>: blueprint names the
  ///                 active faction declares in its own
  ///                 <c>FactionSpec.TemplateCollectionIds</c>. These
  ///                 are kept as-is -- the faction author has chosen
  ///                 to ship them and is responsible for wiring the
  ///                 supporting planters/goods.</item>
  ///           <item><i>supportedPlantableGroups</i>: the union of
  ///                 <c>PlanterBuildingSpec.PlantableResourceGroup</c>
  ///                 across every loaded planter (which by
  ///                 construction is the active faction's planter set
  ///                 -- we don't cross-load Buildings collections,
  ///                 only NaturalResources).</item>
  ///           <item><i>supportedGoodIds</i>: the union of
  ///                 <c>GoodSpec.Id</c> across every loaded good
  ///                 (active faction's good registry).</item>
  ///         </list>
  ///         For each non-native blueprint we then check:
  ///         <see cref="PlantableSpec"/> is stripped iff its
  ///         <c>ResourceGroup</c> isn't in
  ///         <i>supportedPlantableGroups</i> <b>or</b> its sibling
  ///         <see cref="GatherableSpec"/>'s yield Good isn't in
  ///         <i>supportedGoodIds</i> (no point sowing something whose
  ///         harvest will crash). <see cref="GatherableSpec"/> is
  ///         stripped iff its yield Good isn't in
  ///         <i>supportedGoodIds</i>. This keeps vanilla shared
  ///         flora like Pine/Birch/Oak/BlueberryBush plantable under
  ///         any active faction that supports their groups, while
  ///         still stripping Folktails-only crops/flowers under
  ///         IronTeeth (or Emberpelts) where the relevant planter or
  ///         harvest good is absent.
  ///
  ///         <para><b>Source-of-truth alignment.</b> Each computed
  ///         set mirrors the runtime check it's meant to predict, not
  ///         a wider view that happens to be easier to compute:
  ///         <list type="bullet">
  ///           <item><i>supportedPlantableGroups</i> walks
  ///                 <c>deduped</c> (= what <c>AllTemplates</c> will
  ///                 be after our patch), since
  ///                 <c>PlantingToolButtonFactory</c> reads via
  ///                 <c>TemplateService.GetAll&lt;PlanterBuildingSpec&gt;()</c>
  ///                 which iterates <c>AllTemplates</c>. Using the
  ///                 broader <c>ISpecService.GetSpecs</c> would
  ///                 include planters from disk that the UI never
  ///                 sees, and we'd think a group is supported when
  ///                 the UI would throw on zero matches. A group with
  ///                 <i>any</i> matching planter is considered
  ///                 supported (count &gt;= 1). An earlier version
  ///                 restricted to exactly-one (mirroring vanilla's
  ///                 <c>Single</c>), but mods that add upgraded
  ///                 planter variants (e.g. Efficient Workplaces
  ///                 adding a second Farmhouse planter) legitimately
  ///                 produce count &gt; 1; treating those groups as
  ///                 unsupported stripped Plantable/Cuttable/Crop
  ///                 from crops like Wheat, making them vanish from
  ///                 the map.</item>
  ///           <item><i>supportedGoodIds</i> replicates
  ///                 <c>GameGoodFilter.Load</c>: it unions the active
  ///                 <see cref="FactionSpec.GoodCollectionIds"/> with
  ///                 the <c>"Common"</c> collection ID
  ///                 (<c>CommonGoodCollectionIdsProvider</c>),
  ///                 then sums <c>GoodCollectionSpec.Goods</c> across
  ///                 matching collection specs. We don't query
  ///                 <c>IGoodService</c> directly because that
  ///                 service's <c>Load</c> may run after ours; the
  ///                 spec-service-based replication is correct
  ///                 regardless of load order.</item>
  ///         </list></para></item>
  /// </list>
  ///
  /// <para><b>Ambiguity warning.</b> Dedup logs a warning at the
  /// <c>UDebug</c> level if a dropped duplicate's spec composition
  /// (the sequence of <c>ComponentSpec</c> types) differs from the
  /// first-seen blueprint with that name. That preserves the safety
  /// net against a future mod that genuinely ships a divergent
  /// version of a vanilla blueprint -- silent dedup would hide it.
  /// Content-matched duplicates (the Emberpelts case) log at
  /// <c>Verbose</c>.</para>
  ///
  /// <para><b>Why basename-based name extraction works.</b> Natural-
  /// resource blueprints use the convention
  /// <c>NaturalResources/&lt;kind&gt;/&lt;Name&gt;/&lt;Name&gt;.blueprint</c>
  /// where the file basename matches <c>Blueprint.Name</c> exactly
  /// (verified against the entries in Emberpelts's own
  /// <c>NaturalResources.Emberpelts</c> collection and against
  /// Keystone's Class D recipe references like "Birch", "Mangrove",
  /// "Oak"). Faction-tagged variants like
  /// <c>Wheat.&lt;Faction&gt;.blueprint</c> aren't used at the natural-
  /// resource level in vanilla; the convention is bare names with
  /// faction selection happening at the collection level.</para>
  ///
  /// <para><b>Trees vs. crops.</b> Stripping Plantable+Gatherable is
  /// enough for trees and bushes -- they render and remain interactive
  /// (chop-able) without those specs. Crops, however, depend on the
  /// planter relationship for their visual init, so they end up
  /// invisible after this strip. We deliberately accept that:
  /// cross-faction crop visuals are handled separately via
  /// passive-mesh objects, not via the natural-resource entity
  /// path.</para>
  ///
  /// <para><b>Why a Harmony patch.</b> No Bindito extension point sits
  /// between the collection service's Load and the downstream
  /// consumers. MTB solves the same problem with a Harmony postfix on
  /// <c>TemplateCollectionService.Load</c>; we replicate that pattern
  /// rather than bringing on the full MTB dep.</para>
  /// </summary>
  [HarmonyPatch(typeof(TemplateCollectionService))]
  public static class TemplateCollectionServicePatch {

    #region Reflection

    /// <summary>
    /// Backing field for the auto-property <c>TemplateCollectionService.AllTemplates</c>.
    /// The property is get-only at the public surface, so we write the
    /// modified template list through the auto-property's compiler-generated
    /// backing field. Resolved once on first use.
    /// </summary>
    private static readonly FieldInfo AllTemplatesBackingField =
        AccessTools.Field(typeof(TemplateCollectionService), "<AllTemplates>k__BackingField")
        ?? throw new InvalidOperationException(
            "Could not locate '<AllTemplates>k__BackingField' on TemplateCollectionService -- " +
            "auto-property convention may have changed.");

    /// <summary>
    /// Backing field for the auto-property <c>Blueprint.Specs</c>. The
    /// property is get-only, so we shrink the spec list on the existing
    /// Blueprint instance via reflection rather than constructing a new
    /// Blueprint with the same <c>Name</c> -- the latter triggers an
    /// <c>InvalidCastException</c> in <c>ComponentCache.GetCachedComponents</c>
    /// under certain mod stacks (observed under Emberpelts +
    /// MoreModLogs.ComponentCachePatch). In-place mutation keeps a single
    /// canonical Blueprint instance referenced by both <c>AllTemplates</c>
    /// and <c>ISpecService</c>, so name-keyed cache layers downstream
    /// can't mis-bind one twin's component-type list onto the other.
    /// Resolved once on first use.
    /// </summary>
    private static readonly FieldInfo BlueprintSpecsBackingField =
        AccessTools.Field(typeof(Blueprint), "<Specs>k__BackingField")
        ?? throw new InvalidOperationException(
            "Could not locate '<Specs>k__BackingField' on Blueprint -- " +
            "auto-property convention may have changed.");

    #endregion

    #region Last-run state (for self-tests)

    /// <summary>
    /// Blueprint names dropped during the most recent
    /// <see cref="DedupAndStripCrossFactionTemplates"/> run, captured
    /// for inspection by <see cref="SelfTests.PatchScopeInvariantTest"/>.
    /// Replaced (not appended) on every postfix invocation. Empty list
    /// before the first run.
    /// </summary>
    public static IReadOnlyList<string> LastDroppedBuildingNames { get; private set; } =
        System.Array.Empty<string>();

    /// <summary>
    /// The <c>crossFactionBlueprintNames</c> set used by the most
    /// recent <see cref="DedupAndStripCrossFactionTemplates"/> run --
    /// i.e. the scope the patch was allowed to mutate. Captured for
    /// the self-test invariant "every dropped/stripped name was in
    /// the cross-faction scope at drop time". Empty set before the
    /// first run.
    /// </summary>
    public static IReadOnlyCollection<string> LastCrossFactionBlueprintNames { get; private set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True once the postfix has run at least once. Lets the
    /// self-test distinguish "patch ran cleanly with zero drops" from
    /// "patch never ran" (which would mean the Harmony attach failed
    /// or the host method was never called).</summary>
    public static bool HasRun { get; private set; }

    #endregion

    #region Patch

    /// <inheritdoc cref="TemplateCollectionServicePatch"/>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(TemplateCollectionService.Load))]
    public static void DedupAndStripCrossFactionTemplates(TemplateCollectionService __instance) {
      PatchInvocationLog.Once(nameof(TemplateCollectionServicePatch));

      // Phase 1: dedup (LOAD-BEARING). Runs against only AllTemplates
      // and Blueprint.Name -- nothing third-party-data-shaped can make
      // it throw. If it ever does, we have a deeper problem than just
      // this patch, but log + bail rather than masking the cause.
      //
      // The result is committed to AllTemplates BEFORE phase 2 runs.
      // That decoupling is the v0.4.5 lesson: in v0.4.4 a NRE in the
      // strip-phase scope computation discarded the dedup result, and
      // a duplicate "Corn" template then crashed TemplateNameMapper.
      // Load. Dedup is the game-boots-or-not safety net; the strip
      // phase is a gameplay-quality optimization. They must not share
      // a try/catch.
      List<Blueprint> deduped;
      int dedupCount;
      try {
        deduped = DedupTemplates(__instance.AllTemplates, out dedupCount);
      } catch (Exception ex) {
        KeystoneLog.Error($"[Keystone] TemplateCollectionServicePatch: dedup phase threw: {ex}");
        KeystoneIntegrationHealth.TryRecord(
            "Compatibility patch failed",
            $"TemplateCollectionServicePatch dedup phase: {ex.GetType().Name}");
        return;
      }
      if (dedupCount > 0) {
        AllTemplatesBackingField.SetValue(__instance, deduped.ToImmutableArray());
      }

      // Phase 2: cross-faction strip + drop (OPTIONAL). Touches
      // third-party TemplateCollectionSpec / GoodCollectionSpec data
      // shapes; can fail on malformed specs. Wrapped in its own
      // try/catch so a failure here leaves AllTemplates with the
      // already-committed deduped list rather than rolling back to
      // the pre-dedup input.
      try {
        var stripped = StripCrossFaction(
            deduped,
            out var droppedNonNativeBuildingCount,
            out var strippedPlantableCount,
            out var strippedGatherableCount,
            out var strippedCuttableCount,
            out var strippedCropCount,
            out var stripStatus,
            out var droppedBuildingNames,
            out var crossFactionScope);

        // Publish run-state for the self-test invariant check after
        // a successful strip. (If the strip threw, leaving stale
        // state is preferable to mid-mutation state.)
        LastDroppedBuildingNames = droppedBuildingNames;
        LastCrossFactionBlueprintNames = crossFactionScope;
        HasRun = true;

        // Only re-commit if strip actually changed the deduped list.
        // The strip mutates Blueprint.Specs in place, so a no-change
        // outcome means the deduped list already in AllTemplates is
        // bit-equivalent to `stripped`.
        if (droppedNonNativeBuildingCount > 0
            || strippedPlantableCount > 0
            || strippedGatherableCount > 0
            || strippedCuttableCount > 0
            || strippedCropCount > 0) {
          AllTemplatesBackingField.SetValue(__instance, stripped);
        }

        KeystoneLog.Verbose(
            $"[Keystone] TemplateCollectionServicePatch: active='{FactionIdAccessor.CurrentId ?? "(null)"}', " +
            $"strip-status={stripStatus}, " +
            $"deduped {dedupCount} duplicate-named template(s), " +
            $"dropped {droppedNonNativeBuildingCount} non-native Building template(s), " +
            $"stripped Plantable from {strippedPlantableCount} non-native template(s), " +
            $"stripped Gatherable from {strippedGatherableCount} non-native template(s), " +
            $"stripped Cuttable from {strippedCuttableCount} non-native template(s), " +
            $"stripped Crop from {strippedCropCount} non-native template(s).");
      } catch (Exception ex) {
        KeystoneLog.Error(
            $"[Keystone] TemplateCollectionServicePatch: strip phase threw: {ex}. " +
            $"AllTemplates retains the deduped list ({dedupCount} duplicate(s) removed); " +
            "cross-faction strip is skipped for this load.");
        KeystoneIntegrationHealth.TryRecord(
            "Compatibility patch failed",
            $"TemplateCollectionServicePatch strip phase: {ex.GetType().Name}");
      }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Dedup pass (LOAD-BEARING). First-wins by <see cref="Blueprint.Name"/>;
    /// drops any subsequent entry with the same name and logs a Warning
    /// if its spec composition differs from the first-seen.
    ///
    /// <para>Touches nothing third-party-data-shaped -- only
    /// <c>AllTemplates</c> (an <c>ImmutableArray&lt;Blueprint&gt;</c> the
    /// host service builds) and <c>Blueprint.Name</c>. Cannot throw on
    /// the kind of malformed-spec shapes that hurt the strip phase. The
    /// postfix commits this result to <c>AllTemplates</c> before phase
    /// 2 starts; see the postfix for the rationale on decoupling.</para>
    /// </summary>
    private static List<Blueprint> DedupTemplates(
        ImmutableArray<Blueprint> input, out int dedupCount) {
      dedupCount = 0;
      var deduped = new List<Blueprint>(input.Length);
      var seen = new Dictionary<string, Blueprint>(StringComparer.Ordinal);
      foreach (var bp in input) {
        if (seen.TryGetValue(bp.Name, out var firstSeen)) {
          LogAmbiguityIfDifferent(firstSeen, bp);
          dedupCount++;
          continue;
        }
        seen[bp.Name] = bp;
        deduped.Add(bp);
      }
      return deduped;
    }

    /// <summary>
    /// Strip + drop pass (OPTIONAL). Operates on the already-deduped
    /// list returned by <see cref="DedupTemplates"/>. Returns the post-
    /// strip <see cref="ImmutableArray{Blueprint}"/> the caller should
    /// commit to <c>AllTemplates</c> (or skip committing if all the
    /// out-counts are zero, since the in-place mutations on
    /// <c>Blueprint.Specs</c> are already visible through the deduped
    /// list the postfix wrote earlier).
    ///
    /// <para>Touches third-party <see cref="TemplateCollectionSpec"/>
    /// and <see cref="GoodCollectionSpec"/> instances. Can throw if
    /// those carry malformed shapes; the postfix wraps the call in
    /// its own try/catch so a failure here doesn't roll back the
    /// dedup commit.</para>
    /// </summary>
    private static ImmutableArray<Blueprint> StripCrossFaction(
        List<Blueprint> deduped,
        out int droppedNonNativeBuildingCount,
        out int strippedPlantableCount,
        out int strippedGatherableCount,
        out int strippedCuttableCount,
        out int strippedCropCount,
        out string stripStatus,
        out List<string> droppedBuildingNames,
        out HashSet<string> crossFactionScope) {
      droppedNonNativeBuildingCount = 0;
      strippedPlantableCount = 0;
      strippedGatherableCount = 0;
      strippedCuttableCount = 0;
      strippedCropCount = 0;
      stripStatus = "uninitialised";
      droppedBuildingNames = new List<string>();
      crossFactionScope = new HashSet<string>(StringComparer.Ordinal);

      // Pass 2: compute the scope of blueprints we're allowed to mutate
      // (cross-faction NaturalResources collections we explicitly
      // cross-loaded), the active faction's native blueprint names (for
      // a fast keep-native short-circuit log line), and the planter-
      // group / good-id gates that drive per-spec strip decisions on
      // the cross-faction set.
      //
      // The scope is the cross-faction set, NOT "everything not in
      // nativeBlueprintNames". The latter wrongly classifies vanilla
      // shared content (Path, Common-collection buildings, etc.) as
      // foreign, because those collections are loaded via other
      // ITemplateCollectionIdProvider instances and don't appear in
      // FactionSpec.TemplateCollectionIds. The cross-faction set is
      // the actual set we asked TemplateCollectionService to load on
      // top of vanilla, so it's the only set we should be allowed to
      // mutate.
      var activeFactionId = FactionIdAccessor.CurrentId;
      var specs = SpecServiceAccessor.Specs;
      var nativeBlueprintNames = TryComputeNativeBlueprintNames();
      var crossFactionBlueprintNames =
          TryComputeCrossFactionBlueprintNames(activeFactionId, specs);
      if (nativeBlueprintNames == null
          || crossFactionBlueprintNames == null
          || specs == null) {
        stripStatus = specs == null
            ? "skip(no-spec-service)"
            : (nativeBlueprintNames == null
                ? "skip(no-faction-spec-available)"
                : "skip(no-active-faction-id)");
        return deduped.ToImmutableArray();
      }
      crossFactionScope = crossFactionBlueprintNames;

      // supportedPlantableGroups: any planter group with at least one
      // matching planter in `deduped` (after cross-faction Building
      // drops). A group with zero planters means no building can sow
      // its crops — strip Plantable so the UI doesn't offer unsowable
      // content. Multiple planters for the same group is legitimate
      // (mods like Efficient Workplaces add upgraded variants).
      var planterGroupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (var bp in deduped) {
        if (crossFactionBlueprintNames.Contains(bp.Name) && HasBuildingSpec(bp)) continue;
        foreach (var spec in bp.Specs) {
          if (spec is PlanterBuildingSpec planter
              && !string.IsNullOrEmpty(planter.PlantableResourceGroup)) {
            planterGroupCounts.TryGetValue(planter.PlantableResourceGroup, out var c);
            planterGroupCounts[planter.PlantableResourceGroup] = c + 1;
          }
        }
      }
      var supportedPlantableGroups = new HashSet<string>(StringComparer.Ordinal);
      foreach (var kv in planterGroupCounts) {
        if (kv.Value >= 1) supportedPlantableGroups.Add(kv.Key);
      }

      // supportedGoodIds: replicate GameGoodFilter.Load() -- the active
      // faction's GoodCollectionIds (from FactionSpec) unioned with the
      // Common collection, mapped through GoodCollectionSpec.Goods.
      // CommonGoodCollectionIdsProvider yields "Common" verbatim;
      // GameGoodFilter sums providers, so we add "Common" explicitly.
      var supportedGoodIds = new HashSet<string>(StringComparer.Ordinal);
      var factionSpec = FactionIdAccessor.CurrentSpec;
      if (factionSpec != null) {
        var nativeGoodCollectionIds = new HashSet<string>(StringComparer.Ordinal) { "Common" };
        if (!factionSpec.GoodCollectionIds.IsDefaultOrEmpty) {
          foreach (var id in factionSpec.GoodCollectionIds) {
            if (!string.IsNullOrEmpty(id)) nativeGoodCollectionIds.Add(id);
          }
        }
        foreach (var goodCollection in specs.GetSpecs<GoodCollectionSpec>()) {
          if (!nativeGoodCollectionIds.Contains(goodCollection.CollectionId)) continue;
          if (goodCollection.Goods.IsDefaultOrEmpty) continue;
          foreach (var goodId in goodCollection.Goods) {
            if (!string.IsNullOrEmpty(goodId)) supportedGoodIds.Add(goodId);
          }
        }
      }

      stripStatus =
          $"ok({nativeBlueprintNames.Count} native names, " +
          $"{crossFactionBlueprintNames.Count} cross-faction names, " +
          $"{supportedPlantableGroups.Count}/{planterGroupCounts.Count} planter groups supported, " +
          $"{supportedGoodIds.Count} goods)";

      // Diagnostic dumps (one-shot, gated on Verbose). Goal: tell us at
      // a glance whether the gates that drive strip decisions are
      // computing what we think they are. crossFactionBlueprintNames
      // is the actual mutation scope -- anything outside it passes
      // through untouched, so it's the first thing to inspect when a
      // blueprint we expected to strip survived (or one we expected
      // untouched got modified).
      LogNamesDump("nativeBlueprintNames", nativeBlueprintNames);
      LogNamesDump("crossFactionBlueprintNames", crossFactionBlueprintNames);
      LogNamesDump("supportedGoodIds", supportedGoodIds);
      LogNamesDump("supportedPlantableGroups", supportedPlantableGroups);

      var output = ImmutableArray.CreateBuilder<Blueprint>(deduped.Count);
      foreach (var bp in deduped) {
        // Native trumps cross-faction. A blueprint declared by the
        // active faction's own TemplateCollectionIds is always kept
        // untouched, even if another faction's NaturalResources
        // collection also references it (e.g. Wheat appears in both
        // NaturalResources.Folktails and NaturalResources.Emberpelts).
        // Without this guard, such blueprints entered the strip branch
        // and could lose Plantable/Cuttable/Crop specs.
        if (nativeBlueprintNames.Contains(bp.Name)) {
          KeystoneLog.Verbose(
              $"[Keystone] TemplateCollectionServicePatch: keep-native '{bp.Name}'.");
          output.Add(bp);
          continue;
        }

        // Out-of-scope: not in any cross-faction NaturalResources
        // collection we cross-loaded. Passes through untouched --
        // vanilla shared content (Path, etc.) loaded via other
        // providers, and any third-party mod content in collections
        // we don't cross-load.
        if (!crossFactionBlueprintNames.Contains(bp.Name)) {
          KeystoneLog.Verbose(
              $"[Keystone] TemplateCollectionServicePatch: pass-through (out-of-scope) '{bp.Name}'.");
          output.Add(bp);
          continue;
        }

        // Drop cross-faction Buildings entirely. See the type docstring
        // (Pass 2, "Drop non-native Buildings") for why. Only Buildings
        // get this treatment -- cross-faction natural-resource flora is
        // the supported case, and the strip pass below handles the
        // capability mismatches that come with it.
        if (HasBuildingSpec(bp)) {
          KeystoneLog.Verbose(
              $"[Keystone] TemplateCollectionServicePatch: drop cross-faction Building '{bp.Name}' " +
              "(BuildingSpec present on a blueprint loaded via a cross-faction " +
              "NaturalResources collection -- a third-party mod appended a Building " +
              "into the wrong collection).");
          droppedNonNativeBuildingCount++;
          droppedBuildingNames.Add(bp.Name);
          continue;
        }

        PlantableSpec? plantable = null;
        GatherableSpec? gatherable = null;
        CuttableSpec? cuttable = null;
        CropSpec? crop = null;
        foreach (var spec in bp.Specs) {
          if (spec is PlantableSpec p) plantable = p;
          if (spec is GatherableSpec g) gatherable = g;
          if (spec is CuttableSpec c) cuttable = c;
          if (spec is CropSpec cr) crop = cr;
        }
        if (plantable == null && gatherable == null && cuttable == null) {
          output.Add(bp);
          continue;
        }

        var gatherableYieldId = gatherable?.Yielder?.Yield?.Id;
        var cuttableYieldId = cuttable?.Yielder?.Yield?.Id;
        var gatherableYieldMissing = gatherable != null
            && !IsYieldGoodSupported(gatherableYieldId, supportedGoodIds);
        var cuttableYieldMissing = cuttable != null
            && !IsYieldGoodSupported(cuttableYieldId, supportedGoodIds);

        var stripGatherable = gatherableYieldMissing;
        var stripCuttable = cuttableYieldMissing;

        var stripPlantable = false;
        if (plantable != null) {
          var group = plantable.ResourceGroup;
          if (string.IsNullOrEmpty(group) || !supportedPlantableGroups.Contains(group)) {
            stripPlantable = true;
          } else if (gatherableYieldMissing || cuttableYieldMissing) {
            // Planter would match for sowing, but the harvest yield is a
            // foreign good. Don't let the player sow something whose
            // gathering or cutting will crash later.
            stripPlantable = true;
          }
        }

        // Crop has an unguarded `_cuttable.WasCut +=` in Start (see
        // Timberborn.Fields.Crop.Start), so a blueprint that keeps
        // CropSpec but loses CuttableSpec NREs on spawn. If we're
        // stripping Cuttable on a crop, strip the Crop marker too --
        // the entity loses farmhouse-managed crop semantics either way
        // (no AquaticFarmhouse on IronTeeth to harvest into, etc.); the
        // GrowableSpec and visual decorators are independent and keep
        // the entity selectable and lifecycle-driven.
        var stripCrop = stripCuttable && crop != null;

        // Per-non-native trace: which P/G/C/Crop specs each cross-faction
        // blueprint had, what gates resolved to, and whether we
        // stripped. Diagnoses cases like "Cattail kept Cuttable because
        // its yield ended up in supportedGoodIds despite the active
        // faction not having that good".
        LogStripDecision(
            bp.Name, plantable, gatherable, cuttable, crop,
            gatherableYieldId, cuttableYieldId,
            gatherableYieldMissing, cuttableYieldMissing,
            stripPlantable, stripGatherable, stripCuttable, stripCrop);

        if (!stripPlantable && !stripGatherable && !stripCuttable && !stripCrop) {
          output.Add(bp);
          continue;
        }

        // In-place mutation: shrink the existing Blueprint's Specs
        // array rather than constructing a new Blueprint with the same
        // Name. See BlueprintSpecsBackingField docstring -- the
        // same-name-twin pattern blows up component caches under
        // certain mod stacks. We rebuild the ImmutableArray<ComponentSpec>
        // excluding the stripped types and write it back through the
        // auto-property's backing field. ComponentSpec.Blueprint on the
        // surviving specs already points at this Blueprint instance, so
        // the spec-to-blueprint backpointer stays consistent without
        // any cloning.
        var keptBuilder = ImmutableArray.CreateBuilder<ComponentSpec>(bp.Specs.Length);
        foreach (var spec in bp.Specs) {
          if (stripPlantable && spec is PlantableSpec) continue;
          if (stripGatherable && spec is GatherableSpec) continue;
          if (stripCuttable && spec is CuttableSpec) continue;
          if (stripCrop && spec is CropSpec) continue;
          keptBuilder.Add(spec);
        }
        BlueprintSpecsBackingField.SetValue(bp, keptBuilder.ToImmutable());

        if (stripPlantable) strippedPlantableCount++;
        if (stripGatherable) strippedGatherableCount++;
        if (stripCuttable) strippedCuttableCount++;
        if (stripCrop) strippedCropCount++;
        output.Add(bp);
      }

      return output.ToImmutable();
    }

    private static void LogNamesDump(string label, IEnumerable<string> names) {
      if (!KeystoneLog.IsVerbose) return;
      var sorted = new List<string>(names);
      sorted.Sort(StringComparer.Ordinal);
      KeystoneLog.Verbose(
          $"[Keystone] TemplateCollectionServicePatch: {label} ({sorted.Count}): " +
          string.Join(", ", sorted));
    }

    private static void LogStripDecision(
        string blueprintName,
        PlantableSpec? plantable, GatherableSpec? gatherable, CuttableSpec? cuttable,
        CropSpec? crop,
        string? gatherableYieldId, string? cuttableYieldId,
        bool gatherableYieldMissing, bool cuttableYieldMissing,
        bool stripPlantable, bool stripGatherable, bool stripCuttable, bool stripCrop) {
      if (!KeystoneLog.IsVerbose) return;
      var sb = new StringBuilder();
      sb.Append("[Keystone] TemplateCollectionServicePatch: non-native '").Append(blueprintName).Append("' ");
      sb.Append("P=").Append(plantable == null ? "-" : (plantable.ResourceGroup ?? "(null-group)"));
      sb.Append(" G=").Append(gatherable == null ? "-" : (gatherableYieldId ?? "(null-yield)"));
      sb.Append(" C=").Append(cuttable == null ? "-" : (cuttableYieldId ?? "(null-yield)"));
      sb.Append(" Crop=").Append(crop == null ? "-" : "y");
      sb.Append(" Gmiss=").Append(gatherableYieldMissing ? "1" : "0");
      sb.Append(" Cmiss=").Append(cuttableYieldMissing ? "1" : "0");
      sb.Append(" strip=");
      var any = false;
      if (stripPlantable) { sb.Append('P'); any = true; }
      if (stripGatherable) { sb.Append('G'); any = true; }
      if (stripCuttable) { sb.Append('C'); any = true; }
      if (stripCrop) { sb.Append("(Crop)"); any = true; }
      if (!any) sb.Append("none");
      KeystoneLog.Verbose(sb.ToString());
    }

    private static bool IsYieldGoodSupported(string? yieldId, HashSet<string> supportedGoodIds) {
      return !string.IsNullOrEmpty(yieldId) && supportedGoodIds.Contains(yieldId!);
    }

    private static bool HasBuildingSpec(Blueprint blueprint) {
      foreach (var spec in blueprint.Specs) {
        if (spec is BuildingSpec) return true;
      }
      return false;
    }

    /// <summary>
    /// Computes the set of blueprint names that are "native" to the
    /// active faction -- i.e. listed in any
    /// <see cref="TemplateCollectionSpec"/> whose <c>CollectionId</c>
    /// appears in the active <see cref="FactionSpec.TemplateCollectionIds"/>.
    /// Returns <c>null</c> when the active faction or spec service
    /// can't be reached, or no TemplateCollectionIds are declared; in
    /// that case the caller skips the strip rather than
    /// mis-stripping.
    ///
    /// <para>The active <see cref="FactionSpec"/> is fetched via
    /// <see cref="FactionIdAccessor.CurrentSpec"/> rather than walked
    /// out of <c>AllTemplates</c>: <c>FactionSpec</c> blueprints are
    /// loaded by <c>FactionSpecService</c>, not
    /// <c>TemplateCollectionService</c>, so they don't appear in the
    /// templates list the patch operates on.</para>
    ///
    /// <para>The <see cref="TemplateCollectionSpec"/> instances are
    /// fetched via <see cref="SpecServiceAccessor.Specs"/> rather
    /// than walked out of <c>AllTemplates</c>: collection specs are
    /// metadata read by <c>TemplateCollectionService</c> from
    /// <c>ISpecService</c>; <c>AllTemplates</c> only holds the
    /// *target* blueprints those collections reference, not the
    /// collection specs themselves.</para>
    /// </summary>
    /// <summary>
    /// The exact collection IDs that
    /// <see cref="Debug.CrossFactionCollectionProvider"/> can yield.
    /// The strip scope is restricted to blueprints from these
    /// collections — and only the one that ISN'T the active faction's.
    /// Expansion-faction collections (Emberpelts, Whitepaws, etc.) are
    /// deliberately excluded: those factions load their own content
    /// through their own providers and don't need our strip logic.
    /// An earlier convention-based matcher (any <c>NaturalResources.*</c>
    /// that isn't the active faction) was too broad — it swept in
    /// expansion-faction references to vanilla blueprints (e.g. Wheat
    /// in <c>NaturalResources.Emberpelts</c>) and incorrectly put
    /// them in strip scope.
    /// </summary>
    private static readonly string[] CrossFactionCandidateIds = {
        "NaturalResources.Folktails",
        "NaturalResources.IronTeeth",
    };

    /// <summary>
    /// Computes the set of blueprint names that live in the
    /// cross-faction NaturalResources collection we explicitly
    /// cross-loaded via
    /// <see cref="Debug.CrossFactionCollectionProvider"/>. Only the
    /// vanilla pair is in scope (see
    /// <see cref="CrossFactionCandidateIds"/>); only the non-active
    /// faction's collection is included.
    ///
    /// <para>Returns <c>null</c> when the active faction id or spec
    /// service isn't reachable; the caller skips strip/drop entirely
    /// in that case rather than mis-classifying.</para>
    /// </summary>
    private static HashSet<string>? TryComputeCrossFactionBlueprintNames(
        string? activeFactionId, ISpecService? specs) {
      if (activeFactionId == null || specs == null) return null;
      var activeSuffix = "." + activeFactionId;
      // Build the set of collection IDs we actually cross-loaded:
      // the vanilla pair minus the active faction's own.
      var crossLoadedIds = new HashSet<string>(StringComparer.Ordinal);
      foreach (var candidate in CrossFactionCandidateIds) {
        if (!candidate.EndsWith(activeSuffix, StringComparison.Ordinal)) {
          crossLoadedIds.Add(candidate);
        }
      }
      if (crossLoadedIds.Count == 0) return null;
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var collection in specs.GetSpecs<TemplateCollectionSpec>()) {
        var id = collection.CollectionId;
        if (string.IsNullOrEmpty(id)) continue;
        if (!crossLoadedIds.Contains(id)) continue;
        AccumulateBlueprintNames(collection, names);
      }
      return names;
    }

    private static HashSet<string>? TryComputeNativeBlueprintNames() {
      var factionSpec = FactionIdAccessor.CurrentSpec;
      if (factionSpec == null) return null;
      var nativeCollectionIds = factionSpec.TemplateCollectionIds;
      if (nativeCollectionIds.IsDefaultOrEmpty) return null;

      var specs = SpecServiceAccessor.Specs;
      if (specs == null) return null;

      var nativeCollectionIdSet = new HashSet<string>(nativeCollectionIds, StringComparer.Ordinal);
      var nativeBlueprintNames = new HashSet<string>(StringComparer.Ordinal);
      foreach (var collection in specs.GetSpecs<TemplateCollectionSpec>()) {
        if (string.IsNullOrEmpty(collection.CollectionId)) continue;
        if (!nativeCollectionIdSet.Contains(collection.CollectionId)) continue;
        AccumulateBlueprintNames(collection, nativeBlueprintNames);
      }
      return nativeBlueprintNames;
    }

    /// <summary>
    /// Append every blueprint name in <paramref name="collection"/> to
    /// <paramref name="sink"/>, guarding against the malformed-spec
    /// shapes third-party mods occasionally ship.
    ///
    /// <para><b>Why the guard.</b> A third-party
    /// <see cref="TemplateCollectionSpec"/> with an uninitialised
    /// <c>Blueprints</c> field (<c>default(ImmutableArray&lt;_&gt;)</c>
    /// rather than an empty array) throws <c>NullReferenceException</c>
    /// on <c>foreach</c>. v0.4.4 hit this with a user's mod stack
    /// (Badfurs faction + others) and the NRE aborted the whole patch,
    /// erasing the dedup pass's safety net and crashing
    /// <c>TemplateNameMapper.Load</c> downstream on a duplicate
    /// <c>Corn</c> template. The check is cheap; the cost of NOT
    /// having it is "the game doesn't boot."</para>
    ///
    /// <para><b>Why log the skip.</b> A malformed collection passes
    /// through silently otherwise, so future occurrences can't be
    /// traced. The collection id is enough to identify the offending
    /// mod from its file shipped at <c>TemplateCollection.&lt;Id&gt;.blueprint.json</c>.</para>
    /// </summary>
    private static void AccumulateBlueprintNames(
        TemplateCollectionSpec collection, HashSet<string> sink) {
      if (collection.Blueprints.IsDefault) {
        KeystoneLog.Warn(
            $"[Keystone] TemplateCollectionServicePatch: collection '{collection.CollectionId}' " +
            "has an uninitialised Blueprints field (third-party mod ships a malformed spec). " +
            "Skipping; the collection's blueprints won't enter our cross-faction or native sets.");
        KeystoneIntegrationHealth.TryRecord(
            "Malformed third-party spec",
            collection.CollectionId ?? "(null collection id)");
        return;
      }
      foreach (var assetRef in collection.Blueprints) {
        var name = ExtractBlueprintName(assetRef.Path);
        if (name != null) sink.Add(name);
      }
    }

    /// <summary>
    /// Convert a blueprint asset path
    /// (e.g. <c>NaturalResources/Crops/Wheat/Wheat.blueprint</c>) to
    /// its <c>Blueprint.Name</c>. Takes the last path segment and
    /// strips a trailing <c>.blueprint</c> suffix. Returns <c>null</c>
    /// for an empty path.
    /// </summary>
    private static string? ExtractBlueprintName(string path) {
      if (string.IsNullOrEmpty(path)) return null;
      var lastSlash = path.LastIndexOfAny(new[] { '/', '\\' });
      var basename = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
      const string ext = ".blueprint";
      if (basename.EndsWith(ext, StringComparison.Ordinal)) {
        basename = basename.Substring(0, basename.Length - ext.Length);
      }
      return basename.Length > 0 ? basename : null;
    }

    /// <summary>
    /// Compares a dropped duplicate to the first-seen blueprint with
    /// the same <c>Name</c>. Content-matched duplicates log at Verbose;
    /// composition-divergent duplicates log a Warning so a future mod
    /// that genuinely ships a divergent version of a vanilla blueprint
    /// surfaces rather than being silently hidden by the dedupe.
    /// </summary>
    private static void LogAmbiguityIfDifferent(Blueprint firstSeen, Blueprint duplicate) {
      if (HasSameSpecComposition(firstSeen, duplicate)) {
        KeystoneLog.Verbose(
            $"[Keystone] TemplateCollectionServicePatch: dropped duplicate '{firstSeen.Name}' " +
            "(spec composition matches first-seen).");
        return;
      }
      KeystoneLog.Warn(
          $"[Keystone] TemplateCollectionServicePatch: dropped duplicate '{firstSeen.Name}' " +
          "but its spec composition differs from first-seen. " +
          $"First-seen: [{DescribeSpecs(firstSeen.Specs)}]; " +
          $"duplicate: [{DescribeSpecs(duplicate.Specs)}]. " +
          "A mod may be shipping a divergent version of this blueprint -- " +
          "dedupe is hiding it.");
    }

    private static bool HasSameSpecComposition(Blueprint a, Blueprint b) {
      if (a.Specs.Length != b.Specs.Length) return false;
      for (var i = 0; i < a.Specs.Length; i++) {
        if (a.Specs[i].GetType() != b.Specs[i].GetType()) return false;
      }
      return true;
    }

    private static string DescribeSpecs(ImmutableArray<ComponentSpec> specs) {
      var sb = new StringBuilder();
      for (var i = 0; i < specs.Length; i++) {
        if (i > 0) sb.Append(',');
        sb.Append(specs[i].GetType().Name);
      }
      return sb.ToString();
    }

    #endregion

  }

}
