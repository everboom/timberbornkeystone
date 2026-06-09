using System;
using System.Collections.Immutable;
using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// A list of flourish recipes registered for spawn. Drop on a
  /// Keystone-authored "recipe book" blueprint at PostLoad;
  /// <see cref="FlourishCatalog"/> walks every instance via
  /// <c>ISpecService.GetSpecs</c> and merges all entries into the
  /// per-class indexes the handlers consume.
  ///
  /// <para><b>Decoupled from the blueprint asset.</b> The recipe
  /// names a blueprint by string; the blueprint itself carries no
  /// "what class am I" marker. The same blueprint can be referenced
  /// by multiple recipes with different <see cref="RecipeEntry.Class"/>
  /// values (Cattail-as-Class-A and Cattail-as-Class-C, sharing the
  /// same mesh). Visual lifecycle (wilting under stress) stays a
  /// property of the blueprint via <c>KeystoneFlourishSpec</c> and is
  /// independent of which class spawned the entity.</para>
  ///
  /// <para><b>Mod-extensible.</b> Faction expansion mods ship their
  /// own recipe-book blueprints; the catalog merges all of them at
  /// PostLoad. No code required from the modder.</para>
  /// </summary>
  public record KeystoneRecipeBookSpec : ComponentSpec {

    /// <summary>The recipes contributed by this book. Ordering doesn't
    /// matter; the catalog buckets by <c>(Class, Biome, Level)</c>.
    /// <para><b>Why <see cref="ImmutableArray{T}"/>, not
    /// <c>List&lt;T&gt;</c>.</b> Timberborn's blueprint deserializer
    /// (<c>BasicDeserializer</c> / <c>PrimitiveTypeSerialization</c>)
    /// rejects <c>List&lt;CustomRecord&gt;</c> at runtime ("Can't
    /// deserialize System.Object[] to type List&lt;...&gt;"). Vanilla
    /// specs that carry lists of records (e.g. <c>BlockObjectSpec.Blocks
    /// : ImmutableArray&lt;BlockSpec&gt;</c>) all use
    /// <see cref="ImmutableArray{T}"/>, which is the supported
    /// list-of-records type on a spec.</para></summary>
    [Serialize] public ImmutableArray<RecipeEntry> Recipes { get; init; } = ImmutableArray<RecipeEntry>.Empty;

    /// <summary>Attrition rules contributed by this book. Each entry
    /// attaches a destruction action to a <c>(Biome, Level)</c> bucket;
    /// <c>AttritionHandler</c> applies them per-cycle to Keystone
    /// entities in dominant-biome chunks at the active level. Spawn-
    /// shaped <see cref="Recipes"/> add content; attritions remove it.
    /// See <see cref="AttritionEntry"/>.</summary>
    [Serialize] public ImmutableArray<AttritionEntry> Attritions { get; init; } = ImmutableArray<AttritionEntry>.Empty;

    /// <summary>Overgrowth rules contributed by this book (GitHub issue
    /// #33). Each entry drapes an <i>existing</i> tree (live or dead) in
    /// a flourish composition over time; <c>OvergrowthHandler</c> applies
    /// them per-cycle in dominant-biome chunks at the active level. A new
    /// rule family alongside <see cref="Attritions"/> — not a spawn class.
    /// See <see cref="OvergrowthEntry"/>.</summary>
    [Serialize] public ImmutableArray<OvergrowthEntry> Overgrowths { get; init; } = ImmutableArray<OvergrowthEntry>.Empty;

  }

  /// <summary>One attrition rule inside a <see cref="KeystoneRecipeBookSpec"/>.
  /// Addresses a <c>(Biome, Level)</c> bucket the same way
  /// <see cref="RecipeEntry"/> does; the action applies per-cycle to
  /// every Keystone entity at every surface in a dominant-biome chunk
  /// at the active level whose content class is in <see cref="Classes"/>.</summary>
  public record AttritionEntry {

    /// <summary>Biome whose Investment + level state gates this rule.
    /// String name of a <see cref="Keystone.Core.Biomes.BiomeKind"/>.</summary>
    [Serialize] public string Biome { get; init; } = "";

    /// <summary>Level identifier (e.g. <c>"L1"</c>). Same semantics
    /// as <see cref="RecipeEntry.Level"/>.</summary>
    [Serialize] public string Level { get; init; } = "L1";

    /// <summary>Action applied to a hit entity. Recognised values:
    /// <c>"Kill"</c> (flip <c>KeystoneFlourish.LifeStatus</c> to
    /// <c>Dead</c>; visual switches to <c>#Dead</c>, entity persists)
    /// or <c>"Destroy"</c> (delete the entity). Unknown values are
    /// logged once and the entry is dropped.</summary>
    [Serialize] public string Action { get; init; } = "Kill";

    /// <summary>Target tokens the rule applies to. <c>"A"</c>/<c>"B"</c>/
    /// <c>"C"</c> match Keystone-stamped entities by
    /// <c>KeystoneVariant.Class</c>. <b>Class A is parsed but not yet
    /// wired</b>: the spawn handler reconciles every cycle, so a deleted
    /// Class A would just respawn on the next pass.
    /// <para><c>"Overgrowth"</c> is a special token (not a spawn class):
    /// it means "also terminally kill the overgrowth on the tree at this
    /// tile," on the same filter/probability. Combine freely with entity
    /// classes, e.g. <c>["B", "C", "Overgrowth"]</c> — the Dry-biome rule
    /// uses this so drought reclaims overgrowth alongside irrigated
    /// flourishes.</para>
    /// <para>Class D (vanilla flora) is addressed via
    /// <see cref="VanillaSpecies"/> instead — vanilla entities don't
    /// carry a <c>KeystoneVariant.Class</c> stamp so they can't be
    /// targeted by class.</para></summary>
    [Serialize] public ImmutableArray<string> Classes { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Vanilla blueprint names this rule destroys (Class D /
    /// unstamped flora). Empty (default) = no vanilla targets; the
    /// rule only acts on the classes in <see cref="Classes"/>. Each
    /// named blueprint at the tile gets the same per-cycle probability
    /// roll the Keystone-class targets get.
    /// <para>Per-tile player planting marks are already honoured at
    /// the dispatcher level (<c>ChunkRulesApplier</c> skips marked
    /// tiles before any handler fires), so listing a species here
    /// doesn't override that exemption -- a marked tile keeps its
    /// vanilla plants intact.</para>
    /// <para>Example: River's high-flow attrition lists
    /// <c>["Cattail", "Spadderdock"]</c> so river-grade flow scours
    /// the aquatic plants but leaves Keystone-tagged ground flora
    /// (handled via <see cref="Classes"/>) and any marked tiles
    /// alone.</para></summary>
    [Serialize] public ImmutableArray<string> VanillaSpecies { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Per-entity per-cycle Bernoulli probability in <c>[0, 1]</c>.
    /// A value of 0 means the rule never fires; 1 means it always
    /// fires (when other gates pass). Out-of-range values are clamped
    /// at parse time with a single warning.</summary>
    [Serialize] public float Probability { get; init; }

    /// <summary>Optional spatial-eligibility filter, same registry as
    /// spawn recipes (<c>"WaterEdge"</c>, <c>"RiverBank"</c>,
    /// <c>"ContaminatedTile"</c>). Empty string = no filter.</summary>
    [Serialize] public string Filter { get; init; } = "";

    /// <summary>Optional ecology channel to scale
    /// <see cref="Probability"/> by. Empty string = no scaling, use
    /// <see cref="Probability"/> as a constant. Recognised values
    /// match <c>Keystone.Core.Ecology.Fields.EcologyChannel</c>:
    /// <c>"WaterDepth"</c>, <c>"WaterFlowMagnitude"</c>,
    /// <c>"Moisture"</c>, <c>"Contamination"</c>. Case-insensitive.
    /// <para>When set, the channel is sampled at the tile and a
    /// linear ramp is built: probability is
    /// <see cref="ProbabilityAtMin"/> at <see cref="ScaleMin"/> and
    /// <see cref="Probability"/> at <see cref="ScaleMax"/>. Below
    /// <see cref="ScaleMin"/> the rule never fires; above
    /// <see cref="ScaleMax"/> the probability clamps to
    /// <see cref="Probability"/>.</para></summary>
    [Serialize] public string ScaleBy { get; init; } = "";

    /// <summary>Channel value at the low end of the scaling ramp.
    /// Ignored when <see cref="ScaleBy"/> is empty.</summary>
    [Serialize] public float ScaleMin { get; init; }

    /// <summary>Channel value at the high end of the scaling ramp.
    /// Must be strictly greater than <see cref="ScaleMin"/>.
    /// Ignored when <see cref="ScaleBy"/> is empty.</summary>
    [Serialize] public float ScaleMax { get; init; }

    /// <summary>Probability at exactly <see cref="ScaleMin"/>. Ignored
    /// when <see cref="ScaleBy"/> is empty.</summary>
    [Serialize] public float ProbabilityAtMin { get; init; }

    /// <summary>Habitat tags that disqualify an entity from this rule.
    /// Empty (default) = no exclusion gate. The only recognised value
    /// today is <c>"Dry"</c> (matches entities tagged with
    /// <c>KeystoneDryNaturalResource</c>). Unknown values are warned
    /// once and dropped — authoring <c>"Land"</c> / <c>"Aquatic"</c>
    /// today does nothing but the schema accepts them for forward
    /// compatibility.</summary>
    [Serialize] public ImmutableArray<string> ExcludeHabitats { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Positive counterpart of <see cref="ExcludeHabitats"/>:
    /// when non-empty, the rule applies only to entities carrying at
    /// least one of the listed habitat tags. Empty (default) = no
    /// inclusion gate. The two lists compose as "include AND NOT
    /// exclude" — an entity must pass both to be targeted. Same
    /// vocabulary as <see cref="ExcludeHabitats"/> (today: <c>"Dry"</c>).
    /// <para>Example: water biomes' "kill dry plants" recipe lists
    /// <c>IncludeHabitats: ["Dry"]</c> so only Dry-habitat flourishes
    /// (those carrying <c>KeystoneDryNaturalResource</c>) get the
    /// per-cycle Bernoulli roll, leaving every other Class B/C in the
    /// chunk untouched.</para></summary>
    [Serialize] public ImmutableArray<string> IncludeHabitats { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>When <c>true</c>, the rule only acts on a target whose
    /// <c>LivingNaturalResource.IsDead</c> is set; a living target is
    /// left untouched. Default <c>false</c> = no liveness gate (acts on
    /// the target regardless of state, e.g. River's reed-washout rule).
    /// <para>This is the "clean up dead clutter" switch: irrigated
    /// biomes list dead-only <c>Destroy</c> rules for vanilla bushes
    /// (<c>VanillaSpecies: ["BlueberryBush", "Dandelion"]</c>) so dead
    /// husks are reclaimed while living, productive bushes stay. A
    /// targeted entity with no <c>LivingNaturalResource</c> reads as
    /// not-dead and is skipped under a dead-only rule.</para></summary>
    [Serialize] public bool DeadOnly { get; init; }

  }

  /// <summary>One recipe entry inside a <see cref="KeystoneRecipeBookSpec"/>.</summary>
  public record RecipeEntry {

    /// <summary>Content class: <c>"A"</c>, <c>"B"</c>, <c>"C"</c>.
    /// Determines which handler picks up the recipe and what
    /// per-entity class designation gets stamped on the spawned
    /// entity's <c>KeystoneVariant</c> component. Class B's stamp
    /// drives the Harmony selection-suppression patches; Class C
    /// stamps the same component with <c>"C"</c> so the entity is
    /// selectable + demolishable, even when sharing a blueprint
    /// with a Class B recipe. Class D (vanilla flora pipeline) is
    /// not auto-spawned; recipes referencing class D are logged-and-
    /// skipped today (slot reserved for a future Class D handler).</summary>
    [Serialize] public string Class { get; init; } = "";

    /// <summary>Name of the blueprint to instantiate when the recipe
    /// fires. Resolved at PostLoad against the currently loaded blueprint
    /// set; recipes referencing missing blueprint names are logged-and-
    /// skipped. Mutually exclusive with <see cref="BlueprintNames"/>:
    /// set one or the other (or in the rare case both, the catalog
    /// expands BlueprintNames first then appends BlueprintName as a
    /// final entry).</summary>
    [Serialize] public string BlueprintName { get; init; } = "";

    /// <summary>Convenience: declare N recipes that share everything
    /// except the blueprint by listing the names here. The catalog
    /// expands one <c>ClassARecipe</c> / <c>ClassBRecipe</c> /
    /// <c>ClassCRecipe</c> / <c>ClassDRecipe</c> per name with the rest of the entry's fields shared
    /// (<see cref="Class"/>, <see cref="Biome"/>, <see cref="Level"/>,
    /// <see cref="Filter"/>, <see cref="Weight"/>). Empty (default) =
    /// use <see cref="BlueprintName"/> as the single blueprint reference.
    /// <para>Useful when a biome has many similar minis or vanilla-flora
    /// donors in the same bucket -- collapses 15 near-identical
    /// recipe-book lines into one entry with a 15-name array.</para></summary>
    [Serialize] public ImmutableArray<string> BlueprintNames { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Biome whose Investment + level state gates this recipe.
    /// String name of a <see cref="Keystone.Core.Biomes.BiomeKind"/>;
    /// catalog parses on registration.</summary>
    [Serialize] public string Biome { get; init; } = "";

    /// <summary>Level identifier (e.g. <c>"L1"</c>). The level's
    /// investment range comes from the per-biome entry in
    /// <c>BiomeLevelTable</c>; the recipe fires while the biome's
    /// Investment puts the chunk inside or past that range.</summary>
    [Serialize] public string Level { get; init; } = "L1";

    /// <summary>Optional spatial-eligibility filter applied before the
    /// per-tile activation gate. Empty string (default) = no filter;
    /// any dominant-biome tile is eligible. Currently recognised:
    /// <list type="bullet">
    ///   <item><c>"WaterEdge"</c> — only tiles whose 8-neighbour Moore
    ///         neighbourhood contains a water-bearing column are
    ///         eligible. Backed by <c>Keystone.Core.Spatial.WaterProximity</c>.</item>
    /// </list>
    /// Unknown values are logged once and treated as "always reject" so
    /// a typo can't silently behave like "no filter".</summary>
    [Serialize] public string Filter { get; init; } = "";

    /// <summary>Relative weight when the handler picks one recipe
    /// from the <c>(biome, level)</c> bucket on an activated tile.
    /// Default <c>1.0</c>: all recipes equally likely. Setting one
    /// recipe to <c>2.0</c> makes it twice as likely as the others;
    /// other recipes can use fractional values like <c>0.5</c> for
    /// "half as likely". Zero or negative values are normalised to
    /// the default <c>1.0</c> (so JSON-elision of the field works
    /// without surprise). To remove a recipe from the pool entirely,
    /// drop its entry from the recipe book. The pick is deterministic
    /// per <c>(tile, biome, level)</c> via a separate hash so the
    /// same tile always picks the same recipe across runs.</summary>
    [Serialize] public float Weight { get; init; }

    /// <summary>Class D / Class E: user-facing category this recipe
    /// belongs to. Class D values: <c>"Trees"</c>, <c>"Bushes"</c>,
    /// <c>"Crops"</c> (see <c>KeystoneFloraSettings.Categories</c>).
    /// Class E values: <c>"Deer"</c>, <c>"Cattle"</c>, <c>"Fish"</c>
    /// (see <c>KeystoneFaunaSettings.Categories</c>). Maps to the per-
    /// category slider in the matching settings owner — every recipe
    /// in a <c>(biome, levelId)</c> bucket must share the same Category
    /// so the bucket has an unambiguous multiplier. Free-form string;
    /// unknown categories pass through with a neutral 1.0× multiplier
    /// (third-party mods can mint their own categories without
    /// changing Keystone). Required for Class D and Class E (catalog
    /// warns and skips at PostLoad on missing/empty); ignored for
    /// Class A/B/C.</summary>
    [Serialize] public string Category { get; init; } = "";

    /// <summary>Vertical extent of the spawned flourish in voxels.
    /// Used by the spawner's clearance check: the placement voxel
    /// plus <see cref="Height"/> voxels above it must all be empty
    /// (no natural terrain, no <c>BlockObject</c>) for the spawn to
    /// proceed. So a <see cref="Height"/>=1 flourish needs two free
    /// voxels (placement + 1 above of headroom); a Class D tree with
    /// <see cref="Height"/>=2 needs three (placement + 2 above).
    /// <para><b>Class-specific defaults.</b> Value <c>0</c> means
    /// "unspecified, use the class default" -- 1 for Class A/B/C
    /// (single-voxel flourishes) and 2 for Class D (vanilla flora
    /// trees). Author this field explicitly only when a recipe
    /// deviates from the class default.</para>
    /// <para><b>Above-tile occupants are unconditional blockers.</b>
    /// The placement-voxel replacement rules (dead Keystone flourishes
    /// and fully-harvested stumps yield; live Class B does NOT, as of
    /// 2026-06-06) apply only to the placement voxel itself. Any occupant
    /// in the clearance voxels above -- including dead flourishes and
    /// live Class B -- blocks the spawn.</para></summary>
    [Serialize] public int Height { get; init; }

  }

  /// <summary>One overgrowth rule inside a <see cref="KeystoneRecipeBookSpec"/>
  /// (GitHub issue #33). Drapes an existing tree of the given
  /// <see cref="Target"/> state in a flourish <see cref="Composition"/>.
  /// The rate is owned by the level's <c>Mode</c> (Deterministic for Live
  /// = capped coverage; Stochastic for Dead = accumulates), so this entry
  /// carries no probability — only <see cref="Weight"/> for the pick
  /// among recipes sharing a bucket.</summary>
  public record OvergrowthEntry {

    /// <summary>Biome whose maturity + level state gates this rule.
    /// String name of a <see cref="Keystone.Core.Biomes.BiomeKind"/>
    /// (the recovery biomes — Grassland / Forest).</summary>
    [Serialize] public string Biome { get; init; } = "";

    /// <summary>Level identifier; its maturity band + <c>Mode</c> drive
    /// when and how the rule fires. Live-target rules belong on
    /// Deterministic levels, Dead-target on Stochastic levels.</summary>
    [Serialize] public string Level { get; init; } = "L1";

    /// <summary>Which tree state this rule acts on: <c>"Live"</c>,
    /// <c>"Dead"</c>, or <c>"Reseed"</c> (case-insensitive). Catalog parses
    /// to <c>OvergrowthTarget</c>; unknown values are skipped with a
    /// warning. <c>"Reseed"</c> replaces a mature overgrown dead tree with
    /// a new living seedling (see <see cref="MaturityThreshold"/> /
    /// <see cref="SourceLevel"/>).</summary>
    [Serialize] public string Target { get; init; } = "Dead";

    /// <summary>Single Keystone flourish composition blueprint to drape on
    /// the tree (the overgrowth overlay). For <c>"Reseed"</c> this is the
    /// composition carried onto the new seedling. Combine with — or replace
    /// by — <see cref="Compositions"/> for per-tile variety. At least one of
    /// <see cref="Composition"/> / <see cref="Compositions"/> must be
    /// non-empty or the catalog skips the entry.</summary>
    [Serialize] public string Composition { get; init; } = "";

    /// <summary>Multiple flourish compositions for this bucket, expanded
    /// 1:1 into recipes (mirrors <see cref="RecipeEntry.BlueprintNames"/>).
    /// Each shares this entry's Biome/Level/Target/Weight/MaturityThreshold/
    /// SourceLevel; the handler's weighted pick then chooses one per tree,
    /// so a Dead/Live overgrow — or a Reseed's carried-over overlay — draws
    /// a random mini. <see cref="Composition"/> (if non-empty) is appended
    /// as one more.</summary>
    [Serialize] public ImmutableArray<string> Compositions { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Optional spatial-eligibility filter (same registry as
    /// spawn recipes). Empty = no filter.</summary>
    [Serialize] public string Filter { get; init; } = "";

    /// <summary>Relative pick weight among overgrowth recipes sharing a
    /// <c>(biome, level)</c> bucket. Non-positive normalises to 1.0.</summary>
    [Serialize] public float Weight { get; init; }

    /// <summary><c>"Reseed"</c> only: the overgrowth's accrued maturity
    /// must reach this value before the dead tree is reseeded. Lets the
    /// reseed transition lag the overgrow transition so deadwood sits
    /// overgrown for a while before regrowing. Ignored by Live/Dead.</summary>
    [Serialize] public float MaturityThreshold { get; init; }

    /// <summary><c>"Reseed"</c> only: the level whose Class D spawn table
    /// the replacement species is drawn from (weighted by the same
    /// weights, so e.g. Grassland stays birch-heavy). Empty falls back to
    /// <see cref="Level"/>. Ignored by Live/Dead.</summary>
    [Serialize] public string SourceLevel { get; init; } = "";

  }

}
