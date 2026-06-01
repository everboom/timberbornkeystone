#!/usr/bin/env python
"""
Generate N Keystone flourish blueprints with pseudorandom asymmetric plant compositions.

Bakes in the conventions established through in-game calibration:

- TransformSpec coordinates are tile-centred. Range roughly [-0.5, +0.5];
  (0, 0, 0) places the mesh's visible centroid at tile centre. Vanilla
  flora meshes use centre pivots regardless of mesh type (crop / bush /
  tree all verified empirically 2026-05-10).
- Plant positions stay in [-0.40, +0.40] to avoid bounding-box overhang.
  Vanilla mesh boxes are wide enough that more extreme placements push
  visible geometry into the next tile.
- Plants are >= 0.20 apart from each other. Closer than that reads as
  one stacked mesh, not two distinct plants.
- Cluster centre of gravity sits 0.10 to 0.25 from origin -- always
  noticeably off-centre, never extreme. No preferred direction.
- Each plant gets a random per-plant scale in [0.70, 1.00] (uniform).
  Same scale applies across the plant's three lifecycle phases so the
  mesh doesn't visibly resize when wilting.
- Rotations stay at (0, 0, 0). Mathematically the centre pivot makes
  rotation predictable, but every wide-mesh test (cassava especially)
  has shown rotated geometry clipping past tile edges.
- Each blueprint gets the standard spec set (Watered + Floodable +
  Demolishable + KeystoneFlourishSpec + KeystoneVariantSpec) and three
  lifecycle leaves wired to <stem>Seedling, <stem>SeedlingDry,
  <stem>SeedlingDead (or Mature stage with --stage Mature).

These conventions came from many rounds of in-game tuning -- the
authoritative project memory entries are
feedback_transformspec_pivot.md and feedback_no_transformspec_rotations.md.
The mesh catalog below mirrors dump/mesh-paths.csv (regenerate that by
running Keystone with the MeshPathProbe singleton).

Examples:
    # 10 Riparian-style minis: cattail + sunflower + at most one cassava per blueprint
    python tools/generate-flourish-blueprints.py \\
        --prefix KeystoneRiparianMini \\
        --plants Cattail Sunflower Cassava:1 \\
        --count 10 --seed 42

    # 25 grassland-style minis: 5 plants, 2 or 3 of them per blueprint
    python tools/generate-flourish-blueprints.py \\
        --prefix KeystoneGrasslandMini \\
        --plants BlueberryBush Dandelion Sunflower Eggplant Corn \\
        --count 25 --plants-per-blueprint 2-3 --seed 1

    # Grassland minis with mossy pebble decorations scattered alongside the plants
    python tools/generate-flourish-blueprints.py \\
        --prefix KeystoneGrasslandMini \\
        --plants BlueberryBush Dandelion Sunflower Eggplant Corn \\
        --decorations Pebble_Round_1 Pebble_Round_2 Pebble_Square_1 \\
        --decoration-variant Mossy \\
        --decorations-per-blueprint 1-2 \\
        --count 25 --plants-per-blueprint 2-3 --seed 1 --force

    # Dry biome minis with dry pebble decorations (variant defaults to Dry under --dry)
    python tools/generate-flourish-blueprints.py \\
        --prefix KeystoneDryMini \\
        --plants Cassava Eggplant Corn \\
        --decorations Pebble_Square_2 Pebble_Square_3 \\
        --decorations-per-blueprint 1-3 \\
        --dry --count 10 --seed 7 --force
"""

import argparse
import json
import math
import random
import string
import sys
from pathlib import Path

# Embedded mesh catalog. Each value is the path "stem"; append the stage
# ("Seedling" / "Mature") and lifecycle suffix ("" / "Dry" / "Dead") to
# get the full TimbermeshSpec.Model path. Source: dump/mesh-paths.csv.
CATALOG = {
    "Cattail":       "NaturalResources/Crops/Cattail/Cattail",
    "Sunflower":     "NaturalResources/Crops/Sunflower/Sunflower",
    "Cassava":       "NaturalResources/Crops/Cassava/Cassava",
    "Spadderdock":   "NaturalResources/Crops/Spadderdock/Spadderdock",
    "Corn":          "NaturalResources/Crops/Corn/Corn",
    "Eggplant":      "NaturalResources/Crops/Eggplant/Eggplant",
    "BlueberryBush": "NaturalResources/Bushes/Blueberry/BlueberryBush",
    "CoffeeBush":    "NaturalResources/Bushes/Coffee/CoffeeBush",
    "Dandelion":     "NaturalResources/Bushes/Dandelion/Dandelion",
    "Mangrove":      "NaturalResources/Trees/Mangrove/Mesh/Mangrove",
    "Birch":         "NaturalResources/Trees/Birch/Mesh/Birch",
    "Maple":         "NaturalResources/Trees/Maple/Mesh/Maple",
    "Pine":          "NaturalResources/Trees/Pine/Mesh/Pine",
}

# Tunable constraints. Adjust here if a specific biome needs different feel.
MIN_PLANT_DISTANCE = 0.20
MIN_COG_OFFSET = 0.10
MAX_COG_OFFSET = 0.25
POSITION_RANGE = 0.40
MIN_PLANT_SCALE = 0.70
MAX_PLANT_SCALE = 1.00

# Decoration catalog: Keystone-original mesh entries. Each value is a
# dict with the path "stem" (script appends `_{variant}` suffix when
# constructing the TimbermeshSpec.Model path) and an approximate
# tile-local radius -- the half-extent of the mesh's visible bounding
# circle in the XZ plane. Radius drives pair-distance constraints so
# small pebbles can sit close together while larger rocks get a wider
# berth automatically.
DECORATIONS_CATALOG = {
    "Pebble_Round_1":  {"path": "NaturalResources/KeystoneRocks/Pebble_Round_1",  "radius": 0.10},
    "Pebble_Round_2":  {"path": "NaturalResources/KeystoneRocks/Pebble_Round_2",  "radius": 0.10},
    "Pebble_Round_3":  {"path": "NaturalResources/KeystoneRocks/Pebble_Round_3",  "radius": 0.10},
    "Pebble_Round_4":  {"path": "NaturalResources/KeystoneRocks/Pebble_Round_4",  "radius": 0.10},
    "Pebble_Round_5":  {"path": "NaturalResources/KeystoneRocks/Pebble_Round_5",  "radius": 0.10},
    "Pebble_Square_1": {"path": "NaturalResources/KeystoneRocks/Pebble_Square_1", "radius": 0.10},
    "Pebble_Square_2": {"path": "NaturalResources/KeystoneRocks/Pebble_Square_2", "radius": 0.10},
    "Pebble_Square_3": {"path": "NaturalResources/KeystoneRocks/Pebble_Square_3", "radius": 0.10},
    "Pebble_Square_4": {"path": "NaturalResources/KeystoneRocks/Pebble_Square_4", "radius": 0.10},
    "Pebble_Square_5": {"path": "NaturalResources/KeystoneRocks/Pebble_Square_5", "radius": 0.10},
    "Pebble_Square_6": {"path": "NaturalResources/KeystoneRocks/Pebble_Square_6", "radius": 0.10},
    # Larger rocks intended as Class C standalone cluster content;
    # used with --no-lifecycle. Radius ~0.25 makes the script
    # automatically widen spacing when these mix with pebbles.
    "Rock_Medium_1":   {"path": "NaturalResources/KeystoneRocks/Rock_Medium_1",   "radius": 0.25},
    "Rock_Medium_2":   {"path": "NaturalResources/KeystoneRocks/Rock_Medium_2",   "radius": 0.25},
    "Rock_Medium_3":   {"path": "NaturalResources/KeystoneRocks/Rock_Medium_3",   "radius": 0.25},
}

# Decoration placement constraints, separate from plants so pebbles can
# sit close to plant bases (they wouldn't compete for space the way two
# wide-canopy plants would). No COG check on decorations -- they're
# details, not the visual centre of mass.
#
# Pair distance between two decorations is computed from their radii:
# (r_a + r_b - OVERLAP_BUDGET), floored at MIN_PAIR_DISTANCE. The
# overlap budget allows modest edge overlap (visually preferable for
# rock clusters -- meshes nesting against each other reads as natural).
MIN_DECORATION_TO_PLANT = 0.10
DECORATION_PAIR_OVERLAP_BUDGET = 0.05
DECORATION_PAIR_MIN_DISTANCE = 0.05
DECORATION_POSITION_RANGE = 0.35
MIN_DECORATION_SCALE = 0.70
MAX_DECORATION_SCALE = 1.00


def _required_pair_distance(radius_a, radius_b):
    """Center-to-center minimum distance between two decorations of the
    given radii. Allows edge overlap up to DECORATION_PAIR_OVERLAP_BUDGET
    tile units, floored so two zero-radius entries still get separated."""
    return max(DECORATION_PAIR_MIN_DISTANCE,
               radius_a + radius_b - DECORATION_PAIR_OVERLAP_BUDGET)

# Decoration pivot offset: added to every decoration's TransformSpec.Position
# before serialisation. Required because Keystone-original decoration meshes
# (pebbles) are authored with their visible centre at the Blender origin
# (0, 0), whereas vanilla flora is authored with visible centre at (-0.5,
# -0.5) and the game's blueprint→prefab converter compensates for that.
# Without this offset, decorations land at the SW corner of the tile rather
# than centered. Plants are NOT offset -- they're vanilla and already have
# the (-0.5, -0.5) authoring baked in.
DECORATION_PIVOT_OFFSET_X = 0.5
DECORATION_PIVOT_OFFSET_Z = 0.5


def parse_plant_spec(spec, default_max):
    """Parse a plant spec: 'Name[:Max][@Stage][^Y][#x,z]'.

    All four optional suffixes follow the name in this fixed order:
    - ':Max' caps how many times the plant may appear in one blueprint
      (default: --plants-per-blueprint upper bound).
    - '@Stage' overrides the global --stage for this plant
      ('Seedling' or 'Mature'). Useful for mixed-stage compositions
      like "Mature corn alongside seedling cassava."
    - '^Y' applies a vertical offset to the mesh position (e.g.
      '^-0.1' sinks the mesh 0.1 units into the ground). Useful for
      scaled-down mature trees whose root geometry floats above the
      terrain.
    - '#x,z' anchors the plant to roughly the named tile-local position
      (with a small random jitter so 10 blueprints don't look identical).
      Other plants in the composition will be scattered randomly,
      avoiding the anchor via MIN_PLANT_DISTANCE.

    Returns a dict with keys: name, max, stage (str or None),
    y_offset (float), anchor (tuple or None).
    """
    rest = spec.strip()

    anchor = None
    if "#" in rest:
        rest, anchor_str = rest.split("#", 1)
        try:
            ax_str, az_str = anchor_str.split(",", 1)
            anchor = (float(ax_str), float(az_str))
        except ValueError:
            raise SystemExit(
                f"error: bad #x,z anchor in --plants '{spec}'. "
                "Format: '#0.0,0.0'.")

    y_offset = 0.0
    if "^" in rest:
        rest, y_str = rest.split("^", 1)
        try:
            y_offset = float(y_str)
        except ValueError:
            raise SystemExit(
                f"error: bad ^Y offset in --plants '{spec}'. "
                "Format: '^-0.1' or '^0.05'.")

    stage = None
    if "@" in rest:
        rest, stage_str = rest.split("@", 1)
        stage = stage_str.strip()
        if stage not in ("Seedling", "Mature"):
            raise SystemExit(
                f"error: bad @Stage '{stage}' in --plants '{spec}'. "
                "Must be 'Seedling' or 'Mature'.")

    max_val = default_max
    if ":" in rest:
        rest, max_str = rest.split(":", 1)
        try:
            max_val = int(max_str)
        except ValueError:
            raise SystemExit(
                f"error: bad :Max cap in --plants '{spec}'. Must be an integer.")

    return {"name": rest.strip(), "max": max_val, "stage": stage,
            "y_offset": y_offset, "anchor": anchor}


def parse_decoration_spec(spec, default_max):
    """Parse a decoration spec: 'Name[:Max]'. Simpler than plant specs
    (no @Stage — decorations don't lifecycle; no #x,z — no reason to
    anchor pebbles to a particular tile position). Catalog radius is
    looked up at parse time and carried through so the position
    generator can compute per-pair distance without round-tripping.

    Returns a dict with keys: name, max, radius.
    """
    rest = spec.strip()
    max_val = default_max
    if ":" in rest:
        rest, max_str = rest.split(":", 1)
        try:
            max_val = int(max_str)
        except ValueError:
            raise SystemExit(
                f"error: bad :Max cap in --decorations '{spec}'. Must be an integer.")
    name = rest.strip()
    # Radius lookup is deferred to the caller's catalog validation --
    # parse_decoration_spec is called before the unknown-name check.
    # Use 0.10 (pebble-equivalent) as a safe default if the name isn't
    # in the catalog; the unknown-name check will raise SystemExit
    # before generation actually runs.
    entry = DECORATIONS_CATALOG.get(name)
    radius = entry["radius"] if entry is not None else 0.10
    return {"name": name, "max": max_val, "radius": radius}


def parse_plants_per_blueprint(value):
    """'3' -> (3, 3); '2-3' -> (2, 3). The blueprint loop picks uniformly in [lo, hi]
    so a range produces a mix of compositions (some pairs, some triples, etc.)."""
    s = str(value).strip()
    if "-" in s:
        lo_str, hi_str = s.split("-", 1)
        lo, hi = int(lo_str), int(hi_str)
        if lo < 1 or hi < lo:
            raise ValueError(f"Bad --plants-per-blueprint range '{value}': need 1 <= lo <= hi.")
        return lo, hi
    n = int(s)
    if n < 1:
        raise ValueError(f"Bad --plants-per-blueprint '{value}': must be >= 1.")
    return n, n


def generate_composition(plant_specs, total, rng):
    """Pick `total` plant specs (full dicts, not just names), never
    exceeding any spec's max-per-blueprint cap. Returning the full
    spec keeps per-plant stage and anchor info available downstream."""
    used = {s["name"]: 0 for s in plant_specs}
    composition = []
    for _ in range(total):
        candidates = [s for s in plant_specs if used[s["name"]] < s["max"]]
        if not candidates:
            raise RuntimeError(
                f"Ran out of plants generating composition (need {total} entries; "
                f"caps: {[(s['name'], s['max']) for s in plant_specs]})."
            )
        picked = rng.choice(candidates)
        composition.append(picked)
        used[picked["name"]] += 1
    return composition


# Anchor jitter: anchored plants get this much random offset around
# their specified (x, z) so 10 blueprints with the same anchor don't
# look identical. Small enough that the plant still reads as "at the
# named position."
ANCHOR_JITTER = 0.05


def generate_positions(composition, rng, max_attempts=1000):
    """Generate per-plant (x, z) tuples respecting MIN_PLANT_DISTANCE,
    COG offset window, and per-plant anchors.

    Anchored plants (those with a non-None `anchor` in their spec) are
    placed at their anchor coord plus a small ANCHOR_JITTER. Free
    plants get random positions in [-POSITION_RANGE, +POSITION_RANGE],
    avoiding all already-placed positions. The COG check covers the
    combined set.

    Returns (positions, cog_x, cog_z) where positions is in the same
    order as `composition`.
    """
    total = len(composition)

    for _ in range(max_attempts):
        positions = [None] * total
        valid = True

        # Pass 1: anchored plants first with jitter. Anchors with
        # overlapping jitter circles can occasionally violate
        # MIN_PLANT_DISTANCE; treat that as a failed attempt.
        for i, plant in enumerate(composition):
            if plant["anchor"] is None:
                continue
            ax, az = plant["anchor"]
            x = ax + rng.uniform(-ANCHOR_JITTER, ANCHOR_JITTER)
            z = az + rng.uniform(-ANCHOR_JITTER, ANCHOR_JITTER)
            ok = True
            for p in positions:
                if p is None:
                    continue
                if math.hypot(x - p[0], z - p[1]) < MIN_PLANT_DISTANCE:
                    ok = False
                    break
            if not ok:
                valid = False
                break
            positions[i] = (x, z)
        if not valid:
            continue

        # Pass 2: free plants with inner retry per slot. Without an
        # inner retry, a single failed roll would discard the whole
        # outer attempt — wasteful when anchors are already placed.
        for i, plant in enumerate(composition):
            if plant["anchor"] is not None:
                continue
            placed = False
            for _ in range(50):
                x = rng.uniform(-POSITION_RANGE, POSITION_RANGE)
                z = rng.uniform(-POSITION_RANGE, POSITION_RANGE)
                ok = True
                for p in positions:
                    if p is None:
                        continue
                    if math.hypot(x - p[0], z - p[1]) < MIN_PLANT_DISTANCE:
                        ok = False
                        break
                if ok:
                    positions[i] = (x, z)
                    placed = True
                    break
            if not placed:
                valid = False
                break
        if not valid:
            continue

        # COG check over the combined set.
        cog_x = sum(p[0] for p in positions) / total
        cog_z = sum(p[1] for p in positions) / total
        cog_dist = math.hypot(cog_x, cog_z)
        if MIN_COG_OFFSET <= cog_dist <= MAX_COG_OFFSET:
            return positions, cog_x, cog_z

    raise RuntimeError(
        f"Failed to satisfy positional constraints after {max_attempts} attempts. "
        "Likely causes: anchors that pin too many plants near origin (COG can't "
        "reach MIN_COG_OFFSET), anchors that conflict with each other's jitter, "
        "or MIN_PLANT_DISTANCE too tight for the chunk count."
    )


def generate_scales(total, rng):
    """Pick `total` per-plant scales uniformly in [MIN_PLANT_SCALE, MAX_PLANT_SCALE].
    Each plant's scale is shared across its three lifecycle phases so the mesh
    doesn't visibly resize when wilting."""
    return [round(rng.uniform(MIN_PLANT_SCALE, MAX_PLANT_SCALE), 2) for _ in range(total)]


def generate_decoration_composition(decoration_specs, total, rng):
    """Pick `total` decoration specs, never exceeding any spec's
    max-per-blueprint cap. Mirrors generate_composition for plants but
    returns decoration dicts (just name + max), not full plant specs."""
    if total == 0 or not decoration_specs:
        return []
    used = {s["name"]: 0 for s in decoration_specs}
    composition = []
    for _ in range(total):
        candidates = [s for s in decoration_specs if used[s["name"]] < s["max"]]
        if not candidates:
            # Caller asked for more decorations than caps allow; that's
            # fine -- truncate to what's achievable rather than failing.
            break
        picked = rng.choice(candidates)
        composition.append(picked)
        used[picked["name"]] += 1
    return composition


def generate_decoration_positions(decoration_composition, plant_positions, rng, max_attempts=200):
    """Generate (x, z) for each decoration, avoiding plants by
    MIN_DECORATION_TO_PLANT and avoiding other decorations by a
    per-pair distance computed from radii via _required_pair_distance.
    No COG constraint (decorations are details, not the visual anchor).

    Returns a list of (x, z) tuples in the same order as
    decoration_composition. May return fewer than requested if the
    constraints can't be satisfied (caller decides whether to warn);
    in practice the constraints are loose enough that this rarely
    fires for 1-3 decorations per tile.
    """
    # Tracks (x, z, radius) so per-pair distance is correctly sized
    # against the radius of each already-placed decoration.
    placed = []
    for spec in decoration_composition:
        my_radius = spec["radius"]
        chosen = None
        for _ in range(max_attempts):
            x = rng.uniform(-DECORATION_POSITION_RANGE, DECORATION_POSITION_RANGE)
            z = rng.uniform(-DECORATION_POSITION_RANGE, DECORATION_POSITION_RANGE)
            ok = True
            for p in plant_positions:
                if math.hypot(x - p[0], z - p[1]) < MIN_DECORATION_TO_PLANT:
                    ok = False
                    break
            if ok:
                for (dx, dz, d_radius) in placed:
                    required = _required_pair_distance(my_radius, d_radius)
                    if math.hypot(x - dx, z - dz) < required:
                        ok = False
                        break
            if ok:
                chosen = (x, z, my_radius)
                break
        if chosen is None:
            # Failed to place this decoration; skip rather than failing
            # the whole blueprint. Pebbles are optional flavour.
            continue
        placed.append(chosen)
    # Drop the radius from the return shape so callers (build_blueprint,
    # logging) keep working unchanged.
    return [(x, z) for (x, z, _r) in placed]


def generate_decoration_scales(total, rng):
    """Pick `total` per-decoration scales uniformly in
    [MIN_DECORATION_SCALE, MAX_DECORATION_SCALE]. Same shape as
    generate_scales for plants but uses decoration-specific bounds."""
    return [round(rng.uniform(MIN_DECORATION_SCALE, MAX_DECORATION_SCALE), 2)
            for _ in range(total)]


def generate_decoration_rotations(total, mode, rng):
    """Pick `total` per-decoration Y-rotation values (degrees).

    `mode` is the string from --decoration-rotation:
      "0"        -> all zeros (no rotation).
      "random"   -> uniform [0, 360) per decoration.
      "<int>"    -> the same fixed value for every decoration.

    Plants never go through this path -- their Rotation always stays
    at (0, 0, 0). See the per-mesh `Rotation` block in `phase_children`.
    """
    if total == 0:
        return []
    if mode == "random":
        return [round(rng.uniform(0.0, 360.0), 2) for _ in range(total)]
    try:
        fixed = float(mode)
    except ValueError:
        raise SystemExit(
            f"error: bad --decoration-rotation '{mode}'. Must be '0', "
            "'random', or a numeric value in degrees.")
    return [fixed] * total


def build_blueprint(name, composition, positions, scales,
                    decoration_composition, decoration_positions, decoration_scales,
                    decoration_rotations, decoration_variant,
                    default_stage, aquatic, dry, no_lifecycle):
    """Assemble the full blueprint dict for one mini.

    Habitat is one of three mutually exclusive flags (defaults: land):

    aquatic=True: water-based. Watered+Floodable mirror vanilla aquatic
    crops (Cattail, Spadderdock): lives in water at height 1. Watered
    spec retained so the Dying visual still fires if the surface dries
    up. Used for Wetland minis.

    dry=True: dry-loving. NO WateredNaturalResourceSpec (so the plant
    never fires Dying when soil moisture drops — that's its preferred
    state). FloodableNaturalResourceSpec retained at height 0 so it
    still reacts to actual standing water. Carries the
    KeystoneDryNaturalResourceSpec marker so consumers can recognise
    the habitat.

    aquatic=False and dry=False (default): land-based. Watered+
    Floodable use "lives on dry/irrigated land, dies if flooded" config
    matching Riparian / Grassland minis.

    Per-plant stage: each entry in `composition` carries an optional
    `stage` override; entries without one fall back to `default_stage`.

    Mesh-variant treatment for dry blueprints: the visible #Alive leaf
    uses the {stem}{stage}Dry mesh (the dried/withered variant) so the
    plant reads as naturally desert-adapted. The #Dying leaf uses the
    same mesh (no visible transition during the alive->dying state
    change, since the plant already looks stressed). #Dead uses the
    distinct Dead mesh so attrition Kill produces a visible change.
    """
    if aquatic and dry:
        raise SystemExit("error: --aquatic and --dry are mutually exclusive.")
    if no_lifecycle and len(composition) > 0:
        raise SystemExit(
            "error: --no-lifecycle requires --plants to be empty. "
            "Inanimate blueprints contain decorations only (rocks etc); "
            "plants without lifecycle is incoherent.")
    if no_lifecycle and len(decoration_positions) == 0:
        raise SystemExit(
            "error: --no-lifecycle with no decorations placed produces an "
            "empty blueprint. Pass --decorations and --decorations-per-blueprint.")
    # Disambiguating per-plant labels: CattailA, CattailB, etc.
    label_counts = {}
    entries = []
    for plant_spec, (x, z), scale in zip(composition, positions, scales):
        plant = plant_spec["name"]
        stage = plant_spec["stage"] or default_stage
        label_counts[plant] = label_counts.get(plant, 0) + 1
        suffix = string.ascii_uppercase[label_counts[plant] - 1]
        label = f"{plant}{suffix}"
        stem = CATALOG[plant]
        # Dry biome: alive leaf already shows the dried variant.
        alive_mesh = f"{stem}{stage}Dry" if dry else f"{stem}{stage}"
        entries.append({
            "label": label,
            "alive": alive_mesh,
            "dry":   f"{stem}{stage}Dry",
            "dead":  f"{stem}{stage}Dead",
            "x": round(x, 2),
            "y": round(plant_spec.get("y_offset", 0.0), 2),
            "z": round(z, 2),
            "scale": scale,
        })

    # Decoration entries: single mesh path (variant suffix already
    # applied), no lifecycle variation. Same Position/Scale appears in
    # all three phase_children outputs since decorations don't change
    # with lifecycle. Truncate scales/labels to the number of
    # decorations that actually got placed (generate_decoration_positions
    # may have dropped some).
    decoration_entries = []
    deco_label_counts = {}
    placed_count = len(decoration_positions)
    for deco_spec, (x, z), scale, rot_y in zip(
            decoration_composition[:placed_count],
            decoration_positions,
            decoration_scales[:placed_count],
            decoration_rotations[:placed_count]):
        deco_name = deco_spec["name"]
        deco_label_counts[deco_name] = deco_label_counts.get(deco_name, 0) + 1
        suffix = string.ascii_uppercase[deco_label_counts[deco_name] - 1]
        label = f"{deco_name}{suffix}"
        stem = DECORATIONS_CATALOG[deco_name]["path"]
        mesh = f"{stem}_{decoration_variant}" if decoration_variant else stem
        decoration_entries.append({
            "label": label,
            "mesh": mesh,
            "x": round(x, 2),
            "z": round(z, 2),
            "scale": scale,
            "rot_y": rot_y,
        })

    def phase_children(mesh_key):
        children = {
            e["label"]: {
                "TimbermeshSpec": {"Model": e[mesh_key]},
                "TransformSpec": {
                    "Position": {"X": e["x"], "Y": e["y"], "Z": e["z"]},
                    "Rotation": {"X": 0.0,        "Y": 0.0,        "Z": 0.0},
                    "Scale":    {"X": e["scale"], "Y": e["scale"], "Z": e["scale"]},
                },
            }
            for e in entries
        }
        # Decorations: same mesh + transform in every lifecycle state.
        # Y-rotation is decoration-only -- plants (above) always stay at
        # rotation (0,0,0) because vanilla flora has baked-in directional
        # features that break under rotation. Symmetric authored
        # decorations (rocks) don't have that constraint.
        # Position is offset by DECORATION_PIVOT_OFFSET_{X,Z} to compensate
        # for the Keystone-original vs. vanilla authoring-pivot mismatch.
        for d in decoration_entries:
            children[d["label"]] = {
                "TimbermeshSpec": {"Model": d["mesh"]},
                "TransformSpec": {
                    "Position": {
                        "X": round(d["x"] + DECORATION_PIVOT_OFFSET_X, 2),
                        "Y": 0.0,
                        "Z": round(d["z"] + DECORATION_PIVOT_OFFSET_Z, 2),
                    },
                    "Rotation": {"X": 0.0,        "Y": d["rot_y"],  "Z": 0.0},
                    "Scale":    {"X": d["scale"], "Y": d["scale"],  "Z": d["scale"]},
                },
            }
        return children

    # Children block: state-machine for animate blueprints (plants drive
    # the lifecycle, decorations come along for the ride); flat for
    # inanimates (decorations live directly under #Models, no Seedling/
    # Mature/Alive/Dying/Dead). The "alive" key is arbitrary in the
    # inanimate branch -- plant composition is empty, so phase_children
    # produces a dict containing only decorations regardless of key.
    if no_lifecycle:
        children_block = {
            "#Models": {
                "Children": phase_children("alive"),
            }
        }
    else:
        children_block = {
            "#Models": {
                "Children": {
                    "Mature": {
                        "Children": {
                            "#Alive": {"Children": phase_children("alive")},
                            "#Dying": {"Children": phase_children("dry")},
                            "#Dead":  {"Children": phase_children("dead")},
                        }
                    }
                }
            }
        }

    blueprint = {
        "TemplateSpec": {
            "TemplateName": name,
            "BackwardCompatibleTemplateNames": [],
            "RequiredFeatureToggle": "",
            "DisablingFeatureToggle": "",
        },
        "KeystoneFlourishSpec": {},
        "KeystoneVariantSpec": {},
        "BlockObjectSpec": {
            "Size": {"X": 1, "Y": 1, "Z": 1},
            "Blocks": [
                {
                    "MatterBelow": "Ground",
                    "Occupations": "Floor, Bottom, Corners, Path, Middle",
                    "Stackable": "None",
                    "OccupyAllBelow": False,
                    "Underground": False,
                }
            ],
            "Entrance": {"HasEntrance": False, "Coordinates": {"X": 0, "Y": 0, "Z": 0}},
            "BaseZ": 0,
            "Overridable": True,
            "Flippable": False,
        },
        "NaturalResourceSpec": {"Order": 100},
        # Keystone owns the kill decision (attrition recipes on a biome's
        # level table), not vanilla's timer. Setting DaysToDie* to a
        # sentinel-large value disables timer-driven death while keeping
        # DyingNaturalResource.StartedDying / StoppedDying firing on
        # condition change -- the visual #Dying transition still works,
        # but the entity persists until Keystone explicitly destroys it.
        "FloodableNaturalResourceSpec":
            {"MinWaterHeight": 1, "MaxWaterHeight": 1, "DaysToDie": 1e9}
            if aquatic else
            {"MinWaterHeight": 0, "MaxWaterHeight": 0, "DaysToDie": 1e9},
        "DemolishableSpec": {"DemolishTimeInHours": 0.5, "ShowDemolishButtonInEntityPanel": False},
        # Required even though flourishes are non-selectable: vanilla's
        # BlockObject.AddToServiceAfterLoad cleanup path dereferences
        # GetComponent<LabeledEntitySpec>().DisplayNameLocKey when a loaded
        # block object fails validation (stale tile after terrain change,
        # cross-mod validator, etc.). Without this spec that cleanup NREs
        # and aborts the whole entity batch load -> the save won't open.
        # No collider on flourishes, so this adds a name string only; it
        # does not make the entity selectable. See docs/timberborn-specs.md
        # and the LabeledEntity decorator chain (LabeledEntity + Namer).
        "LabeledEntitySpec": {
            "DisplayNameLocKey": "NaturalResource.KeystoneFlourish.DisplayName",
            "DescriptionLocKey": "",
            "FlavorDescriptionLocKey": "",
            "Icon": "NaturalResources/Trees/Pine/PineIcon",
        },
        "BlockObjectModelSpec": {
            "FullModelName": "#Models",
            "UncoveredModelName": "",
            "UndergroundModelName": "",
            "UndergroundModelDepth": 0,
        },
        "Children": children_block,
    }

    # Lifecycle-related specs: only emitted for animate blueprints. For
    # inanimates we drop KeystoneFlourishSpec (no state machine to drive)
    # and skip the Watered / Dry markers (no moisture-driven dying;
    # rocks don't withhold based on soil dryness). Inanimates also
    # opt into runtime biome-driven material swapping via
    # KeystoneRockTintSpec -- every Class C rock cluster wants this
    # under the current design (rocks must visually match the tile
    # biome).
    if no_lifecycle:
        del blueprint["KeystoneFlourishSpec"]
        blueprint["KeystoneRockTintSpec"] = {}
    else:
        # Habitat-specific specs. Watered is omitted for dry plants so
        # they don't fire Dying when soil is dry (their preferred
        # state). Dry marker is emitted only for the dry habitat.
        if dry:
            blueprint["KeystoneDryNaturalResourceSpec"] = {}
        else:
            blueprint["WateredNaturalResourceSpec"] = {"DaysToDieDry": 1e9}

    return blueprint


def main():
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--prefix", required=True,
                        help="Blueprint name prefix, e.g. 'KeystoneRiparianMini'.")
    parser.add_argument("--plants", nargs="*", default=[],
                        help='Plant specs: "Name[:Max][@Stage][#x,z]". '
                             ':Max caps occurrences per blueprint; @Stage '
                             '("Seedling" or "Mature") overrides --stage for '
                             'this plant; #x,z anchors the plant near that '
                             'tile-local position. Examples: '
                             '"Cattail", "Cassava:1", "Corn@Mature", '
                             '"Cassava:1@Seedling#0,0". Optional (omit '
                             'entirely for a decoration-only cluster, '
                             'e.g. rocks-only Class C blueprints).')
    parser.add_argument("--count", type=int, default=10,
                        help="Number of blueprints to generate (default: 10).")
    parser.add_argument("--output-dir", default="unity-assets/Keystone/Data/NaturalResources",
                        help="Base directory for blueprint folders.")
    parser.add_argument("--stage", choices=["Seedling", "Mature"], default="Seedling",
                        help="Vanilla mesh stage to compose (default: Seedling).")
    parser.add_argument("--plants-per-blueprint", default="3",
                        help="Plants per blueprint as a fixed int ('3') or an inclusive "
                             "range ('2-3'). Range picks uniformly per blueprint. Default: 3.")
    parser.add_argument("--seed", type=int, default=None,
                        help="Random seed (default: random).")
    parser.add_argument("--start-index", type=int, default=1,
                        help="Numerical suffix for the first blueprint (default: 1).")
    parser.add_argument("--force", action="store_true",
                        help="Overwrite existing blueprint files.")
    parser.add_argument("--dry", action="store_true",
                        help="Dry-loving plants for the Dry biome: omits "
                             "WateredNaturalResourceSpec (so they don't fire Dying "
                             "when soil dries up — that's their preferred state), "
                             "keeps FloodableNaturalResourceSpec at height 0 so they "
                             "still react to standing water, and emits "
                             "KeystoneDryNaturalResourceSpec as a habitat marker. "
                             "Mutually exclusive with --aquatic.")
    parser.add_argument("--aquatic", action="store_true",
                        help="Use aquatic Watered+Floodable specs "
                             "(MinWaterHeight=1, MaxWaterHeight=1, DaysToDie=1.0) "
                             "matching vanilla Cattail/Spadderdock. Use for Wetland "
                             "minis. Default land-based.")
    parser.add_argument("--decorations", nargs="*", default=[],
                        help='Optional Keystone-original decoration meshes to scatter '
                             'alongside plants (e.g. small rocks for visual flavour). '
                             'Format: "Name[:Max]". Names must appear in '
                             'DECORATIONS_CATALOG. Decorations appear identically in '
                             'all three lifecycle states (they don\'t lifecycle). '
                             'Example: "Pebble_Round_1 Pebble_Square_2:1".')
    parser.add_argument("--decoration-variant", default=None,
                        help='Variant suffix appended to each decoration\'s mesh stem '
                             '(e.g. "Mossy" -> "Pebble_Round_1_Mossy"). If omitted, '
                             'defaults to "Dry" when --dry is set, otherwise "Mossy". '
                             'Match this to the .timbermesh files you exported.')
    parser.add_argument("--decorations-per-blueprint", default="0",
                        help="Decorations per blueprint as a fixed int or inclusive "
                             "range. Default '0' (none, preserving legacy behaviour). "
                             "Suggested values: '1-2' or '1-3' for subtle scattering.")
    parser.add_argument("--decoration-rotation", default="0",
                        help='Y-axis rotation applied to each decoration\'s '
                             'TransformSpec. "0" (default) -> no rotation. '
                             '"random" -> uniform [0, 360) per pebble. A number '
                             'like "90" -> fixed N degrees for every pebble. '
                             '*Only affects decorations*: plants stay at rotation '
                             '(0,0,0) regardless (vanilla flora has directional '
                             'features that break under rotation; symmetric '
                             'authored meshes don\'t). Requires center-pivot '
                             'decoration meshes -- corner-pivot pebbles will '
                             'orbit instead of rotating in place.')
    parser.add_argument("--no-lifecycle", action="store_true",
                        help='Emit an INANIMATE blueprint shape: no '
                             'KeystoneFlourishSpec, no Watered spec, no Dry '
                             'natural-resource marker, and a flat Children '
                             'block with all meshes directly under #Models '
                             '(no Seedling/Mature/#Alive/#Dying/#Dead state '
                             'machine). Intended for Class C rock clusters '
                             'and other inanimates. Requires --plants to be '
                             'empty (plants without lifecycle is incoherent); '
                             '--decorations carries the entire content. '
                             'KeystoneVariantSpec is still emitted so spawn '
                             'handlers can stamp Class="C"; AttritionHandler '
                             '"Kill" no-ops on inanimates, "Destroy" still '
                             'works.')
    args = parser.parse_args()

    pmin, pmax = parse_plants_per_blueprint(args.plants_per_blueprint)
    # When a plant has no explicit cap, default to the per-blueprint upper bound:
    # otherwise a 2-3 range with one plant would cap at 2 even on triple-blueprints.
    plant_specs = [parse_plant_spec(s, pmax) for s in args.plants]
    unknown = sorted({s["name"] for s in plant_specs} - set(CATALOG))
    if unknown:
        sys.exit(f"Unknown plant(s): {unknown}. Known: {sorted(CATALOG)}")

    # --no-lifecycle and --plants are mutually exclusive: inanimate
    # blueprints have no lifecycle for plants to participate in.
    if args.no_lifecycle and plant_specs:
        sys.exit(
            "error: --no-lifecycle is for inanimate blueprints; pass --plants "
            "empty (or omit). Decorations carry the entire content.")

    # Decoration parsing — same shape as plants but minimal (no @Stage, no #x,z).
    # parse_plants_per_blueprint accepts '0' as a valid no-decorations case
    # (lo == hi == 0). When 0 is rolled in the loop, generate_decoration_*
    # returns empty lists and the blueprint has no decoration children.
    try:
        dmin, dmax = parse_plants_per_blueprint(args.decorations_per_blueprint)
    except ValueError:
        # Allow '0' as the explicit "no decorations" default; reuse
        # parse_plants_per_blueprint validation logic for ranges.
        if args.decorations_per_blueprint.strip() == "0":
            dmin = dmax = 0
        else:
            raise
    decoration_specs = [parse_decoration_spec(s, dmax if dmax > 0 else 1)
                        for s in args.decorations]
    unknown_decos = sorted({s["name"] for s in decoration_specs} - set(DECORATIONS_CATALOG))
    if unknown_decos:
        sys.exit(f"Unknown decoration(s): {unknown_decos}. "
                 f"Known: {sorted(DECORATIONS_CATALOG)}")
    if decoration_specs and dmax == 0:
        sys.exit("error: --decorations specified but --decorations-per-blueprint is 0. "
                 "Set --decorations-per-blueprint to a positive value (e.g. '1-2').")

    # Variant: explicit override > biome-default fallback. The fallback
    # picks based on --dry so the common cases (mossy-for-wet,
    # dry-for-arid) don't need an extra flag.
    if args.decoration_variant is not None:
        decoration_variant = args.decoration_variant
    else:
        decoration_variant = "Dry" if args.dry else "Mossy"

    rng = random.Random(args.seed)
    seed_label = "random" if args.seed is None else str(args.seed)
    range_label = f"{pmin}" if pmin == pmax else f"{pmin}-{pmax}"
    deco_range_label = f"{dmin}" if dmin == dmax else f"{dmin}-{dmax}"
    print(f"Seed:        {seed_label}")
    print(f"Plants:      {[(s['name'], s['max'], s['stage'], s['anchor']) for s in plant_specs]}")
    print(f"Per blueprint: {range_label}")
    print(f"Stage:       {args.stage}")
    print(f"Aquatic:     {args.aquatic}")
    print(f"Dry:         {args.dry}")
    if decoration_specs:
        print(f"Decorations: {[(s['name'], s['max']) for s in decoration_specs]}")
        print(f"  Variant:   {decoration_variant}")
        print(f"  Per blueprint: {deco_range_label}")
    print(f"Output:      {args.output_dir}")
    print(f"Generating {args.count} blueprint(s) with prefix '{args.prefix}'...")
    print()

    output_root = Path(args.output_dir)
    for i in range(args.count):
        idx = args.start_index + i
        name = f"{args.prefix}{idx}"
        folder = output_root / name
        target = folder / f"{name}.blueprint.json"
        if target.exists() and not args.force:
            sys.exit(f"Blueprint already exists: {target}. Use --force to overwrite.")

        # Plant generation is conditional on having any plant specs.
        # Inanimate blueprints (no plants) skip the whole composition/
        # positions/scales path -- decoration generation below carries
        # the entire content. cog_x/cog_z are still computed below for
        # the print line; with no plants they default to 0.
        if plant_specs:
            per_blueprint = rng.randint(pmin, pmax)
            composition = generate_composition(plant_specs, per_blueprint, rng)
            positions, cog_x, cog_z = generate_positions(composition, rng)
            scales = generate_scales(per_blueprint, rng)
        else:
            composition = []
            positions = []
            scales = []
            cog_x = cog_z = 0.0

        # Decorations placed after plants so plant positions stay
        # deterministic for a given seed regardless of decoration count.
        deco_count = rng.randint(dmin, dmax) if decoration_specs else 0
        deco_composition = generate_decoration_composition(decoration_specs, deco_count, rng)
        deco_positions = generate_decoration_positions(deco_composition, positions, rng)
        deco_scales = generate_decoration_scales(len(deco_positions), rng)
        deco_rotations = generate_decoration_rotations(
            len(deco_positions), args.decoration_rotation, rng)

        blueprint = build_blueprint(
            name, composition, positions, scales,
            deco_composition, deco_positions, deco_scales, deco_rotations,
            decoration_variant,
            args.stage, args.aquatic, args.dry, args.no_lifecycle)

        folder.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(blueprint, indent=2), encoding="utf-8")

        comp_str = " + ".join(p["name"] for p in composition) or "(inanimate)"
        deco_str = f"  +deco {len(deco_positions)}" if deco_positions else ""
        cog_str = (f"COG ({cog_x:+.2f}, {cog_z:+.2f})"
                   if composition else "                 ")
        print(f"  {name:32s} {comp_str:32s} {cog_str}{deco_str}")

    print()
    print(f"Done. {args.count} blueprint(s) under {output_root}")


if __name__ == "__main__":
    main()
