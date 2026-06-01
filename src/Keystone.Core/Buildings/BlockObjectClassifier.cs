namespace Keystone.Core.Buildings {

  /// <summary>
  /// Boolean signals about a single block object at a voxel. Derived
  /// adapter-side from component inspection on the Timberborn-side BO
  /// (which spec is attached, what its blueprint is named, whether
  /// the path service registers the voxel). Pure data — feeds
  /// <see cref="BlockObjectClassifier.Classify"/>.
  /// </summary>
  /// <param name="IsNaturalComponent">The BO carries a vanilla
  /// natural-element runtime component (NaturalResource / Crop /
  /// Gatherable / Growable / Yielder). Highest-priority skip and
  /// beats every other signal including explicit Keystone tags —
  /// the game's component classification is authoritative for real
  /// plants / crops / yielders.</param>
  /// <param name="IsTaggedTransparent">The BO is tagged for ecology
  /// transparency — either carries <c>KeystoneEcologyTransparentSpec</c>
  /// (the spec-injection path, used by Nature buildings and external
  /// mods) or its blueprint name appears in
  /// <see cref="BlueprintNamePolicy.TransparentBuildingNames"/> (the
  /// name-lookup path that keeps our footprint off vanilla
  /// blueprints). Beats every signal below — the surveyor pretends
  /// the BO isn't there at all.</param>
  /// <param name="IsTaggedNoAura">The BO is tagged no-aura — either
  /// carries <c>KeystoneEcologyNoAuraSpec</c> or its blueprint name
  /// appears in <see cref="BlueprintNamePolicy.NoAuraBuildingNames"/>.
  /// Promotes to <see cref="BlockObjectFootprint.NoAura"/> regardless
  /// of path status. Beats the no-<c>BuildingSpec</c> heuristic so
  /// third-party mod BOs that conceptually settle but ship without
  /// <c>BuildingSpec</c> (e.g. Tree of Life) classify per the tag.</param>
  /// <param name="HasEnterableSpec">The BO carries Timberborn's
  /// <c>EnterableSpec</c> — the canonical "real building"
  /// discriminator. Promotes to <see cref="BlockObjectFootprint.Building"/>.</param>
  /// <param name="LacksBuildingSpec">The BO's <c>BlockObjectSpec</c>
  /// exists but lacks a <c>BuildingSpec</c>. Heuristic
  /// inverse-discriminator for spec-only naturals (rocks, badwater
  /// residue, natural water sources, natural ramps) that don't
  /// declare any of the natural-element components. Lower precedence
  /// than the explicit Keystone tags so a tagged-but-spec-less BO
  /// classifies per the tag.</param>
  /// <param name="VoxelIsPath">The voxel is registered as a path
  /// (via Timberborn's path service or a <c>PathSpec</c>).</param>
  /// <param name="MatchesStructuralPathName">The BO's blueprint name
  /// matches the structural-path heuristic (Zipline / Tubeway /
  /// Overhang / SuspensionBridge family). Combined with
  /// <paramref name="VoxelIsPath"/>, promotes to NoAura — a visually
  /// structural path-occupier that shouldn't sterilize a 3×3 chunk
  /// area below.</param>
  public readonly record struct BlockObjectSignals(
      bool IsNaturalComponent,
      bool IsTaggedTransparent,
      bool IsTaggedNoAura,
      bool HasEnterableSpec,
      bool LacksBuildingSpec,
      bool VoxelIsPath,
      bool MatchesStructuralPathName);

  /// <summary>
  /// Per-block-object footprint policy. Pure function deciding how
  /// one BO contributes to its voxel's settlement classification,
  /// based on the boolean signals the adapter derives from the
  /// Timberborn-side components.
  ///
  /// <para><b>Priority (first match wins):</b>
  /// <list type="number">
  ///   <item><see cref="BlockObjectSignals.IsNaturalComponent"/> →
  ///         <see cref="BlockObjectFootprint.Skip"/></item>
  ///   <item><see cref="BlockObjectSignals.IsTaggedTransparent"/> →
  ///         <see cref="BlockObjectFootprint.Skip"/></item>
  ///   <item><see cref="BlockObjectSignals.IsTaggedNoAura"/> →
  ///         <see cref="BlockObjectFootprint.NoAura"/></item>
  ///   <item><see cref="BlockObjectSignals.HasEnterableSpec"/> →
  ///         <see cref="BlockObjectFootprint.Building"/></item>
  ///   <item><see cref="BlockObjectSignals.LacksBuildingSpec"/> →
  ///         <see cref="BlockObjectFootprint.Skip"/></item>
  ///   <item><see cref="BlockObjectSignals.VoxelIsPath"/> AND
  ///         <see cref="BlockObjectSignals.MatchesStructuralPathName"/>
  ///         → <see cref="BlockObjectFootprint.NoAura"/></item>
  ///   <item>NOT <see cref="BlockObjectSignals.VoxelIsPath"/> →
  ///         <see cref="BlockObjectFootprint.Building"/></item>
  ///   <item>(VoxelIsPath only, no other signal) →
  ///         <see cref="BlockObjectFootprint.Skip"/></item>
  /// </list></para>
  ///
  /// <para><b>Pattern: explicit first, then heuristics, then default.</b>
  /// Game-component facts (<c>IsNaturalComponent</c>) and explicit
  /// Keystone tags (<c>IsTaggedTransparent</c> / <c>IsTaggedNoAura</c>)
  /// are explicit overrides and run before any heuristic.
  /// Component-natural beats tag because real plants / crops can
  /// never be infrastructure and we shouldn't let a misconfigured
  /// tag turn a vanilla pine into an aura-anchor.
  /// <c>HasEnterableSpec</c> is also explicit (the game's "real
  /// building" discriminator) but goes after the Keystone tags
  /// because a Keystone tag is a more-specific override. The
  /// heuristic arms — <c>LacksBuildingSpec</c> (catches spec-only
  /// naturals) and the structural-path name substring — run next.
  /// Defaults at the end: non-path → Building, path-only → Skip.</para>
  /// </summary>
  public static class BlockObjectClassifier {

    /// <summary>Apply the per-BO policy to one set of signals.</summary>
    public static BlockObjectFootprint Classify(BlockObjectSignals s) {
      if (s.IsNaturalComponent) return BlockObjectFootprint.Skip;
      if (s.IsTaggedTransparent) return BlockObjectFootprint.Skip;
      if (s.IsTaggedNoAura) return BlockObjectFootprint.NoAura;
      if (s.HasEnterableSpec) return BlockObjectFootprint.Building;
      if (s.LacksBuildingSpec) return BlockObjectFootprint.Skip;
      if (s.VoxelIsPath && s.MatchesStructuralPathName) return BlockObjectFootprint.NoAura;
      if (!s.VoxelIsPath) return BlockObjectFootprint.Building;
      return BlockObjectFootprint.Skip;
    }

  }

}
