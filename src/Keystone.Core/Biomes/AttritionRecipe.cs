using System.Collections.Generic;
using Keystone.Core.Ecology.Fields;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Level-driven attrition rule: when the named biome is dominant at
  /// a chunk and the named level is active, each cycle every Keystone
  /// entity in the chunk whose content class is in
  /// <see cref="TargetClasses"/> is subject to a Bernoulli roll on
  /// <see cref="Probability"/>; on hit, <see cref="Action"/> is
  /// applied.
  ///
  /// <para><b>Top-down by design.</b> The rule lives with the biome,
  /// not the entity (contamination kills plants, not the plants
  /// knowing about contamination). Multiple recipes in the same
  /// <c>(biome, level)</c> bucket roll independently — they stack.</para>
  ///
  /// <para><b>Independent of physiological transitions.</b> The
  /// <c>#Dying</c> visual switch driven by
  /// <c>WateredNaturalResource</c> / <c>FloodableNaturalResource</c>
  /// remains a property of the entity. Attrition is a separate axis:
  /// the visual transition can fire without attrition ever applying,
  /// and attrition can fire on a perfectly healthy entity in a
  /// hostile biome.</para>
  /// </summary>
  /// <param name="Biome">Dominant biome that gates this rule.</param>
  /// <param name="LevelId">Level identifier the rule attaches to.
  /// Investment range inherits from the biome's entry in
  /// <c>BiomeLevelTable</c>.</param>
  /// <param name="Action">Kill vs. Destroy.</param>
  /// <param name="TargetClasses">Content classes this rule applies to
  /// for Keystone-stamped entities. Subset of <c>{"A", "B", "C"}</c>.
  /// Class D (vanilla flora) is identified separately by blueprint name
  /// through <see cref="VanillaSpecies"/> — vanilla entities don't carry
  /// a <c>KeystoneVariant.Class</c> stamp so the class-string mechanism
  /// can't address them.</param>
  /// <param name="Probability">Per-entity per-cycle Bernoulli, in
  /// <c>[0, 1]</c>. When <see cref="ScaleBy"/> is set, this is the
  /// probability at <see cref="ScaleMax"/> (and above).</param>
  /// <param name="Filter">Optional spatial-eligibility filter
  /// (mirrors <c>RecipeEntry.Filter</c>). Empty string = no filter.</param>
  /// <param name="ScaleBy">Optional ecology channel to scale the
  /// probability by. <c>null</c> = no scaling, use
  /// <see cref="Probability"/> as a constant. When set, the channel
  /// is sampled bilinearly at the tile; the resulting value drives a
  /// linear interpolation between
  /// <see cref="ProbabilityAtMin"/> at <see cref="ScaleMin"/> and
  /// <see cref="Probability"/> at <see cref="ScaleMax"/>. Below
  /// <see cref="ScaleMin"/> the rule skips entirely (probability 0);
  /// above <see cref="ScaleMax"/> the probability clamps to
  /// <see cref="Probability"/>.</param>
  /// <param name="ScaleMin">Channel value at the low end of the
  /// scaling ramp. Ignored when <see cref="ScaleBy"/> is null.</param>
  /// <param name="ScaleMax">Channel value at the high end. Must be
  /// strictly greater than <see cref="ScaleMin"/>. Ignored when
  /// <see cref="ScaleBy"/> is null.</param>
  /// <param name="ProbabilityAtMin">Probability at exactly
  /// <see cref="ScaleMin"/>. Ignored when <see cref="ScaleBy"/> is
  /// null.</param>
  /// <param name="ExcludeHabitats">Habitat tags that disqualify an
  /// entity from this rule. Empty list (default) = no exclusion
  /// gate. The only habitat tag recognised today is <c>"Dry"</c>
  /// (entity has <c>KeystoneDryNaturalResource</c>); land/aquatic
  /// detection isn't wired yet, so those names would be authored-
  /// but-inert.</param>
  /// <param name="IncludeHabitats">Positive counterpart of
  /// <see cref="ExcludeHabitats"/>: when non-empty, the rule applies
  /// only to entities carrying at least one of the listed habitat
  /// tags. Empty list (default) = no inclusion gate. The two lists
  /// compose as "include AND NOT exclude" — an entity must pass both
  /// to be targeted. Same vocabulary as <see cref="ExcludeHabitats"/>
  /// (today: <c>"Dry"</c>).</param>
  /// <param name="VanillaSpecies">Vanilla blueprint names this rule
  /// applies to (Class D / unstamped flora). Empty list = no vanilla
  /// targets; the rule only acts on the classes in
  /// <see cref="TargetClasses"/>. Identified by the entity's
  /// <c>BlockObjectSpec.Blueprint.Name</c>; case-sensitive. Each named
  /// blueprint at the tile is subject to the same per-cycle probability
  /// roll the Keystone-class targets get. Player-marked tiles are
  /// already exempt at the dispatcher level (the per-surface marked
  /// skip in <c>ChunkRulesApplier</c> fires before any handler),
  /// so listing a species here doesn't override that protection.</param>
  /// <param name="DeadOnly">When <c>true</c>, the rule only acts on
  /// entities whose <c>LivingNaturalResource.IsDead</c> is set — living
  /// targets are left alone. Default <c>false</c> = no liveness gate
  /// (acts on a target regardless of state, e.g. River's reed-washout
  /// rule). Used for "clean up dead clutter" rules that should leave a
  /// thriving plant standing. The gate is enforced Mod-side in the
  /// handler (Core has no entity-state access); a targeted entity with
  /// no <c>LivingNaturalResource</c> at all reads as not-dead and is
  /// skipped under a dead-only rule.</param>
  public sealed record AttritionRecipe(
      BiomeKind Biome,
      string LevelId,
      AttritionAction Action,
      IReadOnlyList<string> TargetClasses,
      float Probability,
      string Filter,
      EcologyChannel? ScaleBy,
      float ScaleMin,
      float ScaleMax,
      float ProbabilityAtMin,
      IReadOnlyList<string> ExcludeHabitats,
      IReadOnlyList<string> IncludeHabitats,
      IReadOnlyList<string> VanillaSpecies,
      bool DeadOnly = false) {

    /// <summary>
    /// Per-tile probability for this recipe given the channel sample
    /// at the tile. Returns:
    /// <list type="bullet">
    ///   <item><see cref="Probability"/> when <see cref="ScaleBy"/> is
    ///         null (no scaling configured; <paramref name="channelSample"/>
    ///         is ignored).</item>
    ///   <item><c>0</c> when the sample is below <see cref="ScaleMin"/>
    ///         (rule never fires).</item>
    ///   <item><see cref="Probability"/> when the sample is at or above
    ///         <see cref="ScaleMax"/> (clamped to the high end).</item>
    ///   <item>Linear interpolation between
    ///         <see cref="ProbabilityAtMin"/> at <see cref="ScaleMin"/>
    ///         and <see cref="Probability"/> at <see cref="ScaleMax"/>
    ///         in between.</item>
    /// </list>
    /// Pure function: no I/O, no allocation. Assumes the parser's
    /// invariant <c>ScaleMax &gt; ScaleMin</c> when scaling is set.
    /// </summary>
    public float EffectiveProbability(float channelSample) {
      if (ScaleBy == null) return Probability;
      if (channelSample < ScaleMin) return 0f;
      if (channelSample >= ScaleMax) return Probability;
      var t = (channelSample - ScaleMin) / (ScaleMax - ScaleMin);
      return ProbabilityAtMin + t * (Probability - ProbabilityAtMin);
    }

  }

}
