# Biome → Flourish mapping

Working draft of which content belongs with which biome at which class.
Table is the authoritative form; notes below capture caveats and open
questions. Edit freely.

Excluded from this pass: Cave, Contaminated, Badwater
(deferred until later phase or until we have the right visual variants).

## Classes

The doc splits Class A into two design tiers because they're authored
differently even though the *code* treats them as one class (Class A is
defined by "non-`BlockObject`" — reactivity is orthogonal at every level
and isn't class-discriminating).

- **Class A — atmospheric** — purely passive, *not* tied to biome state.
  Particle effects (mist, dust, pollen, heat shimmer) plus ambient
  fly-through fauna that isn't simulation-backed (a flock of birds,
  rats darting through grass, fish flickers in water — distinct from
  Class E's tracked-population fauna). Appears because the *map setting*
  calls for it, not because a chunk Suitability crossed a threshold. Cheap;
  little or no per-frame cost.
- **Class A — ambient** — eco-sensitive non-`BlockObject` decorations
  whose presence comes and goes with biome Suitability. Coexists on the same
  tile with other content (Class A's superpower). The
  `KeystoneAmbientFlourish*` system today, plus the
  `KeystoneDecorationRegistry` reactive variants. May or may not have
  a per-decoration controller polling environment state.
- **Class B — flourish** — eco-sensitive `BlockObject` flourishes.
  Claims a tile, persisted, displaceable by builds, no Plantable /
  Cuttable / Gatherable. The "your stewardship is paying off" mid-tier
  marker. Vanilla ships few suitable donors; most Class B is Keystone-
  authored or stripped variants of existing flora. The
  `KeystoneFlourishTest` blueprint demonstrates the pipeline.
- **Class D — harvestable** — full vanilla flora,
  plantable/gatherable/cuttable. Persisted by Timberborn's vanilla
  machinery. Counted by `EcologyFieldUpdater`'s entity census, so they
  feed back into biome Suitability. The "earned reward" tier. C/D split
  (own-faction vs other-faction) is design-equivalent and collapsed
  into one column here.

## Mapping

| Biome | Class A — atmospheric | Class A — ambient | Class B — flourish | Class D — harvestable |
|---|---|---|---|---|
| **Forest** | forest mist, dust motes, distant bird calls, insect fly-through | coffee, ground-cover clones, _mushroom_ | fallen logs, stumps _(custom assets needed)_ | pine, maple, chestnut, oak |
| **Grassland** | butterflies, pollen drift, breeze particles | dandelion, sunflower (sparse wild) | sapling birch, stripped blueberry _(non-interactive variants)_ | birch, blueberry |
| **River** | water spray, fish darts, dragonflies | cattail clones (sparse, single) | mini-flourishes on bank tiles (`RiverBank` filter; shares assets with Wetland today, may diverge) | — |
| **Lake** | mist, water bugs, occasional fish | spadderdock clones (sparse) | — | spadderdock |
| **Wetland** | dragonflies, frogs (audio/visual flicker), mist | — | mini-flourishes across the chunk (no filter) | cattail, spadderdock (L2); mangrove (L3) |
| **Riparian** | dragonflies, water birds fly-by | cattail at the saturated edge | mangrove (edge variant)? | — |
| **Dry** | rock, _heat shimmer, dust devils, occasional tumbleweed_ | _no vanilla fit_ | — | cassava, canola _(borderline — read agricultural)_ |
| **Monoculture** | _none_ | — | — | — |

## Notes / caveats

### Class A's two design tiers, one code class

The atmospheric / ambient split is a design distinction (different
authoring shapes, different content vocabularies) but in code they share
the `KeystoneDecorationRegistry` machinery: both spawn cloned prefabs (or
runtime-built objects like `ParticleSystem`s) without entity registration.
Atmospheric flourishes spawn at region/setting scope and don't react to
biome state once placed; ambient flourishes spawn through the biome-
Suitability-driven handler and come and go with Suitability drift. The reactivity
axis is opt-in for both via `IDecorationController`.

### Class A overlap with Class E

Both can host fauna. Class A — atmospheric fauna are pure visual filler:
a fly-through flock with no simulation backing. Class E fauna (Phase 2)
are tracked populations with biome-driven carrying capacity, where the
visible representatives are spawned ephemerally to reflect persisted
counts. A bird could be either depending on intent: ambient flock = A;
songbird population census visualisation = E.

### Class A — ambient vs Class D for water crops

Cattail / spadderdock at low Maturity (Class A — ambient sparse clone) is
the *same plant* as cattail at high Maturity (Class D), just sparse
cosmetic clones early on vs. real harvestable clusters at full
maturity. Two recipes pointing at the same donor blueprint at different
`Level` ids (e.g. `"L1"` vs `"L3"`), distinct `RecipeKey`s for tile-hash
decorrelation. Neither is Class A — atmospheric (those don't react to
biome state).

### Class B is sparse in vanilla

Most vanilla flora is Plantable + Gatherable (Class D). We don't get
many "ornamental block-objects" for free. The Class B column is where
Keystone / faction-mod-authored content lives — sapling-sized variants,
stripped-of-interaction clones, custom dead trees. The
`KeystoneFlourishTest` blueprint demonstrates the pipeline.

### Trees in Class B need to read as "not cuttable"

Affordance must match expectation: a normal-sized tree in Class B will
read as "broken cuttable" to the player. So Class B "trees" are viable
only as either sapling-sized (clearly young, eventually convert to
Class D), or as overlapping clusters where the silhouette doesn't
read as a single discrete tree (mangrove pneumatophores at the water
edge mixed with cattail clumps reading as "wetland thicket").

### Monoculture: intentionally empty

Player-managed state. Keystone doesn't auto-decorate it; the player's
choices determine what's there.

### Dry biome has no vanilla flora donors that fit

Crops read agricultural, vanilla trees read wrong, and the only
"withered" visuals belong to lifecycle states of healthy plants (which
we don't want to abuse). Likely needs custom assets early. Class A —
atmospheric content (heat shimmer, dust) is more accessible since it's
pure particles.

### Mangrove placement

Wetland L3 (Class D, vanilla mangrove) ships today — mangrove literally
grows in shallow water and reads as the wetland canopy. Riparian as an
edge variant remains a possible later addition; not currently shipped.

### Coffee as Forest Class A — ambient

Assumes coffee reads as understory rather than full-canopy. If vanilla
coffee is a big bush, it'd overpower the role and feel more like its
own biome marker than Forest decoration.

### Cassava / canola for Dry

These are crops, not wild flora. Including them blurs the wild-vs-managed
line that Monoculture is meant to keep clean. Open question whether to
keep them, replace with custom Dry-biome assets, or just leave Dry's
Class D empty.
