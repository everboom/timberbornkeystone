using Keystone.Core.Buildings;
using Keystone.Mod.Wellbeing;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.Fields;
using Timberborn.Gathering;
using Timberborn.Growing;
using Timberborn.NaturalResources;
using Timberborn.Yielding;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// Shared block-object predicates used by the
  /// <see cref="BuildingQueryAdapter"/>, the event-driven region updater,
  /// and the diagnostic dump in <c>KeystoneSurveyor</c>. Centralising the
  /// natural-element check keeps the three callers from drifting apart.
  ///
  /// <para><b>Two-arm natural classification.</b> The pre-refactor
  /// single <c>IsNatural</c> method folded two distinct signals — a
  /// component-based fact ("this BO has a NaturalResource / Crop /
  /// Gatherable / Growable / Yielder component") and a heuristic
  /// inverse-discriminator ("this BO's BlockObjectSpec lacks a
  /// BuildingSpec") — into one boolean. The component arm is
  /// authoritative (a vanilla tree is unambiguously a tree); the
  /// heuristic arm catches spec-only naturals (rocks, badwater
  /// residue, natural water sources, natural ramps) by inference,
  /// and is the one that misfires for third-party mod BOs that are
  /// conceptually buildings but ship without a <c>BuildingSpec</c>
  /// (e.g. Tree of Life). They're split here so the Core classifier
  /// can let an explicit Keystone tag override the heuristic arm
  /// while the component arm stays unconditional.</para>
  /// </summary>
  internal static class BlockObjectClassification {

    /// <summary>
    /// True iff <paramref name="bo"/> carries a vanilla natural-element
    /// runtime component (<c>NaturalResource</c>, <c>Crop</c>,
    /// <c>Gatherable</c>, <c>Growable</c>, <c>Yielder</c>). These are
    /// real plants / crops / yielders — the component is the game's
    /// authoritative classification and beats every other signal
    /// (including explicit Keystone tags) in
    /// <see cref="BlockObjectClassifier.Classify"/>. The intent: a
    /// modder who accidentally attaches <c>KeystoneEcologyNoAuraSpec</c>
    /// to a tree blueprint shouldn't promote that tree to settlement
    /// infrastructure — the component fact wins.
    /// </summary>
    public static bool IsNaturalComponent(BlockObject bo) {
      if (bo.HasComponent<NaturalResource>()) return true;
      if (bo.HasComponent<Crop>()) return true;
      if (bo.HasComponent<Gatherable>()) return true;
      if (bo.HasComponent<Growable>()) return true;
      if (bo.HasComponent<Yielder>()) return true;
      return false;
    }

    /// <summary>
    /// True iff <paramref name="bo"/>'s <see cref="BlockObjectSpec"/>
    /// exists but lacks a <see cref="BuildingSpec"/>. Inverse-
    /// discriminator heuristic for spec-only naturals (water obstacles,
    /// dry objects, natural water sources, natural ramps) that don't
    /// declare any of the natural-element components. Distinct from
    /// <see cref="IsNaturalComponent"/> because this arm is heuristic
    /// rather than authoritative — explicit Keystone tags
    /// (<see cref="KeystoneEcologyTransparentSpec"/>,
    /// <see cref="KeystoneEcologyNoAuraSpec"/>,
    /// <see cref="KeystoneNatureSourceSpec"/>, or a name in
    /// <see cref="BlueprintNamePolicy.TransparentBuildingNames"/> /
    /// <see cref="BlueprintNamePolicy.NoAuraBuildingNames"/>) take
    /// precedence over it in the Core classifier.
    /// </summary>
    public static bool LacksBuildingSpec(BlockObject bo) {
      var spec = bo.GetComponent<BlockObjectSpec>();
      return spec != null && !spec.HasSpec<BuildingSpec>();
    }

    /// <summary>
    /// True iff the BO would always classify as
    /// <see cref="BlockObjectFootprint.Skip"/> regardless of voxel
    /// state — i.e. its placement / removal doesn't change any
    /// surface's settled classification, so
    /// <c>RegionUpdater</c> can short-circuit the dirty-set entry.
    /// Mirrors the "always Skip" cases in
    /// <see cref="BlockObjectClassifier.Classify"/>:
    /// component-natural (always Skip), or no-BuildingSpec heuristic
    /// when not explicitly Keystone-tagged. Blocking naturals (natural
    /// dams, blockages, geysers, overhangs) are excluded — those DO
    /// change region structure and need the dirty-set entry.
    /// </summary>
    public static bool IsSkippableForRegions(BlockObject bo) {
      if (IsBlocking(bo)) return false;
      if (IsNaturalComponent(bo)) return true;
      if (LacksBuildingSpec(bo) && !HasAnyKeystoneFootprintTag(bo)) return true;
      return false;
    }

    /// <summary>
    /// True iff <paramref name="bo"/> is one of the natural impassables
    /// in <see cref="BlueprintNamePolicy.BlockingNaturalNames"/> (natural
    /// dam, blockage, geyser, overhang variants). Distinct from
    /// <see cref="IsNaturalComponent"/>: most naturals are passable
    /// (trees, crops, gatherables) and only the curated impassable
    /// subset counts as blocking. Used by <c>RegionUpdater</c> to
    /// decide that such a BO's placement / removal DOES warrant a
    /// dirty-set entry, despite being a natural — because the region
    /// graph actually changes structurally when a blockage appears or
    /// disappears.
    /// </summary>
    public static bool IsBlocking(BlockObject bo) {
      var spec = bo.GetComponent<BlockObjectSpec>();
      if (spec == null) return false;
      return BlueprintNamePolicy.IsBlockingNatural(spec.Blueprint.Name);
    }

    /// <summary>True iff the BO carries any Keystone footprint-affecting
    /// tag — either a spec attached at load time, or a name on the
    /// transparent / no-aura whitelist. Used to gate the
    /// no-<c>BuildingSpec</c> heuristic: an explicitly-tagged BO that
    /// happens to lack <c>BuildingSpec</c> is still a Keystone-
    /// classifiable building, not a heuristic-natural.</summary>
    private static bool HasAnyKeystoneFootprintTag(BlockObject bo) {
      var spec = bo.GetComponent<BlockObjectSpec>();
      if (spec == null) return false;
      if (spec.HasSpec<KeystoneEcologyTransparentSpec>()) return true;
      if (spec.HasSpec<KeystoneEcologyNoAuraSpec>()) return true;
      if (spec.HasSpec<KeystoneNatureSourceSpec>()) return true;
      var name = spec.Blueprint?.Name ?? string.Empty;
      if (BlueprintNamePolicy.IsTransparentByName(name)) return true;
      if (BlueprintNamePolicy.IsNoAuraByName(name)) return true;
      return false;
    }

  }

}
