using System;
using System.Collections.Immutable;
using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Declares a biome's progression ladder: a list of named levels
  /// each with a maturity range. Drop on a Keystone-authored
  /// blueprint at PostLoad; <see cref="BiomeLevelCatalog"/> walks
  /// every instance via <c>ISpecService.GetSpecs</c> and populates
  /// <see cref="Keystone.Core.Biomes.BiomeLevelTable"/>.
  ///
  /// <para><b>Default ladder vs per-biome override.</b> When
  /// <see cref="Biome"/> is empty, the spec is treated as the
  /// default ladder: its level entries are applied to every
  /// <see cref="Keystone.Core.Biomes.BiomeKind"/>. When non-empty,
  /// the spec applies only to that biome and overrides matching
  /// level ids from the default ladder. The catalog's processing
  /// order is "all defaults first, then per-biome overrides," so
  /// per-biome entries always win.</para>
  ///
  /// <para><b>Override semantics: redefine, not remove.</b> A
  /// per-biome spec can change a level's range (e.g. Wetland's L1
  /// from 0.5-1.0 to 0.2-0.8) and can add levels not in the
  /// default ladder. It cannot remove default levels -- a default
  /// L1 always carries through to every biome that doesn't override
  /// L1's range. Removal isn't supported until a use case appears.</para>
  ///
  /// <para><b>What happens at each level lives elsewhere.</b> This
  /// spec only carries timeline data. Spawn actions live on
  /// <see cref="KeystoneRecipeBookSpec"/> entries and any future
  /// code-registered actions reference levels by <c>(Biome, LevelId)</c>
  /// pair.</para>
  /// </summary>
  public record KeystoneBiomeLevelsSpec : ComponentSpec {

    /// <summary>Biome this spec targets, as the name of a
    /// <see cref="Keystone.Core.Biomes.BiomeKind"/>. Leave empty to
    /// declare the default ladder applied to every biome.</summary>
    [Serialize] public string Biome { get; init; } = "";

    /// <summary>Level entries this spec contributes. Ordering doesn't
    /// matter; the catalog sorts by lower bound on read.
    /// <para><b>Why <see cref="ImmutableArray{T}"/>, not
    /// <c>List&lt;T&gt;</c>.</b> Timberborn's blueprint deserializer
    /// rejects <c>List&lt;CustomRecord&gt;</c> on a spec; vanilla
    /// specs use <see cref="ImmutableArray{T}"/> for nested-record
    /// lists (e.g. <c>BlockObjectSpec.Blocks</c>).</para></summary>
    [Serialize] public ImmutableArray<BiomeLevelEntry> Levels { get; init; } = ImmutableArray<BiomeLevelEntry>.Empty;

  }

  /// <summary>One level entry inside a <see cref="KeystoneBiomeLevelsSpec"/>.</summary>
  public record BiomeLevelEntry {

    /// <summary>Stable identifier ("L1", "L2", ...). Recipes reference
    /// this string to declare which level they fire at; case-sensitive.</summary>
    [Serialize] public string LevelId { get; init; } = "";

    /// <summary>Maturity value (game-days) at which actions begin
    /// firing for this level. Must be non-negative.</summary>
    [Serialize] public float LowerMaturity { get; init; }

    /// <summary>Maturity value (game-days) at which the level is
    /// fully saturated. Must be strictly greater than
    /// <see cref="LowerMaturity"/>.</summary>
    [Serialize] public float UpperMaturity { get; init; }

    /// <summary>Activation density in <c>[0, 1]</c>. Fraction of
    /// (dominant-biome, level-eligible) tiles at which a recipe
    /// fires when the level is active. The level owns the density;
    /// recipes in the same <c>(biome, level)</c> bucket share it --
    /// exactly one recipe is chosen per activated tile (weighted by
    /// each recipe's <c>Weight</c>).
    /// <para>Class A/B/C use the per-tile activation hash to decide
    /// which tiles activate (so the population is reproducible across
    /// reloads). Class D uses an RNG roll against this same density
    /// (so the population accumulates over real time). Both paths
    /// share the level's density value; they differ only in the
    /// activation source.</para>
    /// <para>Negative values or omitting the field are treated as
    /// "unset" and resolve to the catalog default (<c>0.10</c>).
    /// <c>0.0</c> is a valid explicit value meaning "this level
    /// activates nothing"; the sentinel default of <c>-1f</c> in the
    /// property initialiser distinguishes "unset" from "explicitly
    /// zero".</para></summary>
    [Serialize] public float Density { get; init; } = -1f;

    /// <summary>When <c>true</c>, this level is processed only by the
    /// one-shot startup pass invoked from <c>KeystoneStartupWarmup</c>
    /// on a fresh map (via <c>ChunkRulesApplier.RunCycleIncludingStartupNow</c>),
    /// not by the rolling per-day rule cycle. Use for geological /
    /// worldgen content -- rock clusters, ruins, cave dressings --
    /// that should exist at map start but shouldn't keep trying to
    /// spawn as the ecology evolves. Default <c>false</c> keeps
    /// existing levels on the regular daily cycle.</summary>
    [Serialize] public bool RunAtStartup { get; init; } = false;

    /// <summary>Tile-selection strategy for this level's recipes.
    /// Recognised values (case-insensitive): <c>"Deterministic"</c>
    /// (per-tile hash gate, ramped by maturity progress; same tiles
    /// fire each session — the default) and <c>"Stochastic"</c>
    /// (per-cycle RNG roll at full <c>Density</c>; population
    /// accumulates over real time, no ramp). Unset / empty resolves
    /// to <c>Deterministic</c>. Unknown values are logged and
    /// fall back to <c>Deterministic</c>.
    /// <para>See <c>Keystone.Core.Biomes.LevelDispatchMode</c> for
    /// the runtime enum.</para></summary>
    [Serialize] public string Mode { get; init; } = "";

    /// <summary>Class E only: maximum number of agents this
    /// <c>(biome, levelId)</c> bucket spawns into a cluster as the
    /// cluster's <c>Score</c> approaches 1. Realised capacity is
    /// <c>floor(cluster.Score × FaunaCapacityAtSaturation)</c>;
    /// each slot picks a recipe by <c>Weight</c> (preserves the
    /// 4:1 cow:bull mix without per-recipe density caps).
    /// Default <c>0</c>: no natural fauna spawn at this level.
    /// <para>Cluster <c>Score</c> is hyperbolic in the cluster's
    /// threshold-weighted tile counts, so a small fully-mature
    /// cluster and a large half-mature cluster can both saturate this
    /// cap — the "quality × quantity" trade-off. The curve is
    /// asymptotic: a cluster never quite reaches the cap, but very
    /// large or pristine ones approach
    /// <c>FaunaCapacityAtSaturation - 1</c> (floored).</para></summary>
    [Serialize] public int FaunaCapacityAtSaturation { get; init; } = 0;

    /// <summary>Class E only: cluster <c>Score</c> floor below which
    /// this bucket spawns no fauna at all (capacity forced to zero).
    /// Use for species that should only appear once the biome is
    /// genuinely well-established (e.g. cattle requiring a substantial
    /// Grassland), independent of the per-chunk
    /// <c>LowerMaturity</c> placement filter. Must be in <c>[0, 1]</c>.
    /// Default <c>0</c>: no floor, capacity ramps up with Score from
    /// zero.</summary>
    [Serialize] public float FaunaMinScore { get; init; } = 0f;

  }

}
