using System.Collections.Immutable;
using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Wellbeing {

  /// <summary>
  /// Marks a building as a Nature-need source. The building polls its
  /// surrounding chunks' Maturity for each eligible biome listed in
  /// <see cref="Sources"/> and, while a beaver is inside, fills the
  /// need corresponding to whichever eligible biome scores highest at
  /// the building's location.
  ///
  /// <para><b>How the "winning biome" is chosen.</b> For each entry in
  /// <see cref="Sources"/>, the runtime component averages that biome's
  /// Maturity across the 4 chunks nearest the building's position, then
  /// scales by <c>clamp01((avgMaturity − floor) / (maxMaturity − floor))
  /// · PointsPerHour</c> where <c>floor = 5.0</c> and <c>maxMaturity</c>
  /// is the highest non-sentinel <c>UpperMaturity</c> from the biome's
  /// level table (sentinel values ≥ 1000 are skipped; fallback 30 if
  /// every level is sentinel). The source with the highest resulting
  /// rate wins; if all sources score 0, the building offers no Nature
  /// satisfaction this cycle.</para>
  ///
  /// <para><b>Save-compat philosophy.</b> This is an additive mechanism
  /// — it never modifies the building's vanilla <c>AttractionSpec</c>
  /// or the existing entertainment effects. A player loading an old
  /// save with leisure buildings in "wrong" biomes simply gets no
  /// Nature satisfaction from them; vanilla entertainment is
  /// untouched.</para>
  /// </summary>
  public record KeystoneNatureSourceSpec : ComponentSpec {

    /// <summary>The set of eligible (biome, need-id, rate) sources this
    /// building can offer. Empty array is technically valid but means
    /// the building never satisfies any Nature need — should always
    /// have at least one entry.</summary>
    [Serialize]
    public ImmutableArray<KeystoneNatureSourceEntry> Sources { get; init; }

  }

  /// <summary>
  /// A single (biome, need, rate) tuple inside a
  /// <see cref="KeystoneNatureSourceSpec"/>. Stored as a record list
  /// rather than a dictionary so the blueprint authoring is positional
  /// and re-orderable.
  /// </summary>
  public record KeystoneNatureSourceEntry {

    /// <summary>Biome name (matches <c>BiomeKind</c> enum member). The
    /// runtime parses this once at Awake; an unknown name logs and the
    /// entry is skipped.</summary>
    [Serialize]
    public string Biome { get; init; }

    /// <summary>Need id satisfied when this biome wins. Must reference
    /// a registered <c>NeedSpec</c> on the faction's beavers, or the
    /// per-tick <c>NeedManager.HasNeed</c> check silently no-ops.</summary>
    [Serialize]
    public string NeedId { get; init; }

    /// <summary>Maximum satisfaction rate at full Maturity scaling.
    /// Multiplied by the [0, 1] scaled-Maturity factor each cycle.</summary>
    [Serialize]
    public float PointsPerHour { get; init; }

  }

}
