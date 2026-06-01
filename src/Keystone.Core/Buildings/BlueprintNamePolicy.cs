using System;
using System.Collections.Generic;

namespace Keystone.Core.Buildings {

  /// <summary>
  /// Name-based classification policies for vanilla and mod block
  /// objects. Two parallel predicates that both decide ecology
  /// behaviour from the blueprint name alone — kept together so the
  /// full set of name-driven policy lives in one file, can be unit
  /// tested without a Unity host, and surfaces collisions (e.g. a
  /// name appearing in both sets, which would indicate a categorisation
  /// bug) by inspection.
  ///
  /// <list type="bullet">
  ///   <item><see cref="IsBlockingNatural"/> — natural-element block
  ///         objects whose tiles must be excluded from region
  ///         membership ("blocked"). Used by the blocking adapter to
  ///         set <c>SurfaceSurvey.IsBlocked</c>; surfaces flow into
  ///         <c>(Z, IsCave, IsSettled, IsBlocked)</c> as the fourth
  ///         component of region identity.</item>
  ///   <item><see cref="IsStructuralPath(string)"/> — path-occupying buildings
  ///         (Zipline / Tubeway / Overhang / SuspensionBridge) that
  ///         should classify as
  ///         <see cref="BlockObjectFootprint.NoAura"/> instead of
  ///         falling through to <see cref="BuildingKind.Path"/>.
  ///         Used by <c>BuildingQueryAdapter</c> to set
  ///         <see cref="BlockObjectSignals.MatchesStructuralPathName"/>.</item>
  /// </list>
  /// </summary>
  public static class BlueprintNamePolicy {

    #region Blocking-natural whitelist

    /// <summary>
    /// Hardcoded whitelist of natural-element block objects whose
    /// tiles must be excluded from region membership ("blocked").
    /// Verified via <c>BlockingCandidateProbe</c> against the live
    /// <c>TemplateCollectionService.AllTemplates</c> on the target
    /// Timberborn version; entries are the literal
    /// <c>Blueprint.Name</c> strings.
    ///
    /// <para><b>Why a whitelist, not a spec-based predicate.</b> The
    /// base game doesn't have a single spec or component that cleanly
    /// identifies "natural impassable obstacle" (the
    /// <c>WaterObstacleSpec</c> / <c>LayeredBlockObstacleSpec</c> /
    /// <c>BlockOccupierSpec</c> family covers only the water side, and
    /// other modders may legitimately attach those specs to non-natural
    /// content). An explicit name list is stable, easy to read, and
    /// won't silently widen if another mod reuses the underlying specs.</para>
    ///
    /// <para><b>Excluded on purpose.</b> Relics
    /// (Large/Medium/SmallRelic), slopes, thorns, and reserve piles /
    /// tanks / warehouses are intentionally NOT in the whitelist --
    /// per design discussion, those entities are passable, walkable
    /// around, or removed quickly enough that splitting the region on
    /// them would be more disruptive than helpful. Ruins (RuinColumn,
    /// UndergroundRuins) are also out: those will eventually anchor a
    /// dedicated Ruins biome and need to stay inside regions for that
    /// to work.</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> BlockingNaturalNames =
        new HashSet<string>(StringComparer.Ordinal) {
          "Blockage",
          "NaturalDam",
          "NaturalOverhang2x1",
          "NaturalOverhang3x1",
          "NaturalOverhang4x1",
          "UnstableCore",
          "GeothermalField",
        };

    // Backing set for fast Contains lookups (the read-only collection
    // above wraps the same instance).
    private static readonly HashSet<string> _blockingNaturalSet =
        (HashSet<string>)BlockingNaturalNames;

    /// <summary>True iff the named blueprint represents a blocking
    /// natural element.</summary>
    public static bool IsBlockingNatural(string blueprintName) =>
        _blockingNaturalSet.Contains(blueprintName);

    #endregion

    #region Keystone-tagged transparent / no-aura name whitelists

    /// <summary>
    /// Union of every faction's transparent-building names. The
    /// per-faction contributions live in
    /// <c>Keystone.Core.Buildings.Factions.Faction*</c> files;
    /// <see cref="Factions.FactionRegistry"/> aggregates them at
    /// type-init.
    ///
    /// <para>Detected adapter-side by name rather than via a spec
    /// attached to the vanilla blueprint, to keep Keystone's footprint
    /// on vanilla content minimal. The
    /// <c>KeystoneEcologyTransparentSpec</c> type still exists for
    /// external mods that want to opt their own content in.</para>
    /// </summary>
    public static IReadOnlyCollection<string> TransparentBuildingNames =>
        Factions.FactionRegistry.AllTransparent;

    /// <summary>True iff the named blueprint is in the aggregated
    /// transparent-buildings whitelist. Adapter consumers OR this
    /// with the <c>KeystoneEcologyTransparentSpec</c> attachment
    /// check.</summary>
    public static bool IsTransparentByName(string blueprintName) =>
        Factions.FactionRegistry.IsTransparent(blueprintName);

    /// <summary>
    /// Union of every faction's no-aura-building names. The
    /// per-faction contributions live in
    /// <c>Keystone.Core.Buildings.Factions.Faction*</c> files.
    ///
    /// <para>Used for small built things — point-sized production,
    /// designation flags, decoration props, automation sensors —
    /// where the building's footprint shouldn't sterilize a 3×3
    /// ecology block around itself.</para>
    /// </summary>
    public static IReadOnlyCollection<string> NoAuraBuildingNames =>
        Factions.FactionRegistry.AllNoAura;

    /// <summary>True iff the named blueprint is in the aggregated
    /// no-aura whitelist. Adapter consumers OR this with the
    /// <c>KeystoneEcologyNoAuraSpec</c> attachment check.</summary>
    public static bool IsNoAuraByName(string blueprintName) =>
        Factions.FactionRegistry.IsNoAura(blueprintName);

    #endregion

    #region Structural-path substring heuristic

    /// <summary>
    /// Substring tokens (case-insensitive) that mark a path-occupying
    /// blueprint as visibly structural — should classify as
    /// <see cref="BlockObjectFootprint.NoAura"/> instead of being
    /// treated as a flat path. Covers Zipline (Beam / Pylon /
    /// Station), Tubeway (Tubeway / TubewayStation / VerticalTubeway),
    /// Overhang (2x1..6x1), SuspensionBridge (1x1..6x1), and
    /// LeafCoats' Branch.Bridge (1x1..6x1 + Stairs) tree-bridge
    /// variants — plus any future variants whose name contains one
    /// of these tokens.
    ///
    /// <para>Substring instead of exact match: the family naming
    /// convention is stable in vanilla and any future Mechanistry
    /// content following it will be picked up automatically; mod
    /// content that follows the same convention will too. If a
    /// future blueprint matches accidentally, add an explicit opt-
    /// out to <see cref="StructuralPathExclusions"/>.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> StructuralPathTokens =
        new[] {
          "Zipline",
          "Tube",
          "Overhang",
          "SuspensionBridge",
          "Branch.Bridge",
        };

    /// <summary>Explicit opt-out list for the structural-path
    /// heuristic. Names that match one of
    /// <see cref="StructuralPathTokens"/> but should be treated as
    /// ordinary path tiles instead. Currently empty — the heuristic
    /// is tight enough that no false-positive has appeared. Entries
    /// here override the substring match before
    /// <see cref="IsStructuralPath(string)"/> returns true.
    ///
    /// <para>If you find a mod blueprint whose name accidentally
    /// matches (e.g. a "TubeFactory" production building, or a
    /// "Suspension" themed mod that ships passable platforms), add
    /// it here rather than tightening the substring tokens — the
    /// tokens are deliberately permissive to catch vanilla family
    /// variants and mod-content following the same naming
    /// convention.</para></summary>
    public static readonly IReadOnlyCollection<string> StructuralPathExclusions =
        new HashSet<string>(StringComparer.Ordinal) {
          // Emberpelts variants of the district crossing / district
          // gate that include a tube-passage section. They contain
          // "Tubeway" in the name and would otherwise auto-promote
          // to BuildingNoAura via the structural-path heuristic, but
          // they're functionally district-gate buildings with full
          // settle semantics.
          "DistrictCrossingTubeway.Emberpelts",
          "DistrictGateTubeway.Emberpelts",
        };

    /// <summary>True iff the named blueprint matches the
    /// structural-path heuristic AND is not in
    /// <see cref="StructuralPathExclusions"/>. Caller is responsible
    /// for also verifying the voxel is registered as a path — the
    /// heuristic only fires for path-occupying buildings.
    ///
    /// <para>This is the production hot-path overload — called once
    /// per BO during surveying. Inlines the (empty in production)
    /// exclusion check so the common path doesn't allocate an
    /// enumerator through the <c>IReadOnlyCollection&lt;string&gt;</c>
    /// interface. Tests that exercise non-empty exclusion sets use
    /// the explicit <see cref="IsStructuralPath(string, IReadOnlyCollection{string})"/>
    /// overload.</para></summary>
    public static bool IsStructuralPath(string blueprintName) {
      if (string.IsNullOrEmpty(blueprintName)) return false;
      // Fast-path: production exclusion set is empty. The
      // foreach-allocates-enumerator concern only applies to the
      // explicit-overload path; here we just check the substring
      // tokens directly.
      for (var i = 0; i < StructuralPathTokens.Count; i++) {
        if (blueprintName.IndexOf(StructuralPathTokens[i],
                                  StringComparison.OrdinalIgnoreCase) >= 0) {
          // Only consult exclusions if the substring matched AND the
          // set is non-empty — keeps the empty-set production path
          // allocation-free.
          if (StructuralPathExclusions.Count > 0
              && ContainsOrdinal(StructuralPathExclusions, blueprintName)) {
            return false;
          }
          return true;
        }
      }
      return false;
    }

    /// <summary>Testable variant of <see cref="IsStructuralPath(string)"/>
    /// that accepts an explicit exclusion set. Used in unit tests to
    /// verify the opt-out mechanism without mutating the static
    /// exclusion list. Production code uses the parameterless
    /// overload.</summary>
    public static bool IsStructuralPath(
        string blueprintName, IReadOnlyCollection<string> exclusions) {
      if (string.IsNullOrEmpty(blueprintName)) return false;
      if (exclusions.Count > 0 && ContainsOrdinal(exclusions, blueprintName)) {
        return false;
      }
      for (var i = 0; i < StructuralPathTokens.Count; i++) {
        if (blueprintName.IndexOf(StructuralPathTokens[i],
                                  StringComparison.OrdinalIgnoreCase) >= 0) {
          return true;
        }
      }
      return false;
    }

    // Ordinal exact-match Contains that doesn't depend on the
    // collection being a HashSet — keeps the parameterized overload
    // testable with arbitrary IReadOnlyCollection<string> inputs.
    // Allocates one enumerator (via the interface), which is fine on
    // a path that's only walked when the set is known non-empty.
    private static bool ContainsOrdinal(
        IReadOnlyCollection<string> set, string value) {
      foreach (var item in set) {
        if (string.Equals(item, value, StringComparison.Ordinal)) return true;
      }
      return false;
    }

    #endregion

  }

}
