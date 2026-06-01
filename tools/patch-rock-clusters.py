"""
One-off patch: adds DemolishableFromTopSpec at the top level and CollidersSpec
on every TimbermeshSpec-bearing child of the 8 KeystoneRockCluster*.blueprint.json
files. Decompile work on docs/timberborn-specs.md identifies these as the missing
pieces preventing player selection and mark-for-destruction on Class C clusters.

Run from repo root: python tools/patch-rock-clusters.py
"""

import json
from collections import OrderedDict
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
CLUSTER_DIR = REPO_ROOT / "unity-assets" / "Keystone" / "Data" / "NaturalResources"
CLUSTER_NAMES = [f"KeystoneRockCluster{i}" for i in range(1, 9)]


def make_collider_spec():
    # Loose 1x1x1 box centered on the rock's local origin. Each rock's child
    # carries a TransformSpec that scales by ~0.8, so the effective collider
    # is ~0.8 units cubed at the rock's tile-space position. Generous enough
    # for cursor pickup; tight enough that two rocks in one tile don't
    # collapse to one overlapping click target.
    return OrderedDict([
        ("BoxColliders", [
            OrderedDict([
                ("Center", OrderedDict([("X", 0.0), ("Y", 0.5), ("Z", 0.0)])),
                ("Size", OrderedDict([("X", 1.0), ("Y", 1.0), ("Z", 1.0)])),
            ])
        ]),
        ("SphereColliders", []),
        ("CapsuleColliders", []),
    ])


def insert_after_key(d: OrderedDict, anchor: str, new_key: str, new_value):
    """Insert (new_key, new_value) into ordered dict right after `anchor`."""
    if new_key in d:
        return  # already present
    if anchor not in d:
        d[new_key] = new_value
        return
    rebuilt = OrderedDict()
    for k, v in d.items():
        rebuilt[k] = v
        if k == anchor:
            rebuilt[new_key] = new_value
    d.clear()
    d.update(rebuilt)


def add_colliders_recursively(node):
    if not isinstance(node, dict):
        return
    if "TimbermeshSpec" in node and "CollidersSpec" not in node:
        insert_after_key(node, "TransformSpec", "CollidersSpec", make_collider_spec())
    children = node.get("Children")
    if isinstance(children, dict):
        for child in children.values():
            add_colliders_recursively(child)


def make_labeled_entity_spec():
    # Placeholder loc keys (Keystone-namespaced; will fall back to the
    # literal key text via the loc system if not present in enUS.csv -- no
    # NRE). Icon points at the vanilla Pine sprite as a placeholder so
    # LabeledEntity.Image isn't null (EntityPanel.UpdateEntityBadge derefs
    # it during Show()). Swap to a Keystone-authored sprite once the
    # custom-asset pipeline lands an icon in the bundle.
    return OrderedDict([
        ("DisplayNameLocKey", "NaturalResource.KeystoneRockCluster.DisplayName"),
        ("DescriptionLocKey", ""),
        ("FlavorDescriptionLocKey", ""),
        ("Icon", "NaturalResources/Trees/Pine/PineIcon"),
    ])


def force_overridable_true(data: OrderedDict):
    # Class B = yielding to construction. Mirrors KeystoneFlourishTest and
    # the mini-flourish family. Class C (blocking, markable for beaver
    # demolition) was attempted but the mark-for-destruction tool never
    # picked the cluster up even with the full vanilla decorator chain +
    # EntityComponent init-lifecycle push -- see docs/timberborn-specs.md
    # case study for what we tried and what's left to investigate.
    spec = data.get("BlockObjectSpec")
    if isinstance(spec, dict):
        spec["Overridable"] = True


def force_blocks_occupy_all(data: OrderedDict):
    # Bump every BlockObjectSpec.Blocks[i].Occupations to "All". Without "Top"
    # in the occupations, another BlockObject can stack on the tile -- and
    # vanilla markable natural objects (Pine, Blockage, NaturalDam) all use
    # "All". The default the generator emitted ("Floor, Bottom, Corners,
    # Path, Middle") was leftover from a different content shape and let
    # the dev tool place two clusters on the same tile.
    spec = data.get("BlockObjectSpec")
    if not isinstance(spec, dict):
        return
    blocks = spec.get("Blocks")
    if not isinstance(blocks, list):
        return
    for block in blocks:
        if isinstance(block, dict):
            block["Occupations"] = "All"


def patch(path: Path) -> bool:
    with path.open("r", encoding="utf-8") as f:
        data = json.load(f, object_pairs_hook=OrderedDict)
    force_overridable_true(data)
    force_blocks_occupy_all(data)
    insert_after_key(data, "DemolishableSpec", "DemolishableFromTopSpec", OrderedDict())
    insert_after_key(data, "DemolishableFromTopSpec", "LabeledEntitySpec", make_labeled_entity_spec())
    add_colliders_recursively(data)
    with path.open("w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")
    return True


def main():
    for name in CLUSTER_NAMES:
        path = CLUSTER_DIR / name / f"{name}.blueprint.json"
        if not path.exists():
            print(f"missing: {path}")
            continue
        patch(path)
        print(f"patched: {name}")


if __name__ == "__main__":
    main()
