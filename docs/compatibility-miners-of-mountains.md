# Optional ground excavation compatibility (Miners of Mountains)

## Problem

Keystone places natural flourishes and decorative rocks above ordinary terrain. Miners of Mountains (MoM) and other excavation tools can reject terrain removal when a supported natural resource lacks a ground-invalidation marker. Earlier broad workarounds changed `MatterBelow` and allowed decorations to remain suspended over water.

## Fix

- Make `KeystoneVariant` implement the **native Timberborn** `IGroundMatterBelowInvalidator` marker. Existing Keystone spawn recipes already attach `KeystoneVariantSpec`.
- Add `KeystoneVariantSpec` to the two standalone blueprints that lacked it: `Rock_medium_1` and `KeystoneFlourishTest`.
- Retain every existing `MatterBelow` value, including `Ground`, as well as all occupancy and placement rules.
- **No MoM dependency:** no referenced MoM assembly, required-mod entry, Harmony patch, optional-mod loader, or MoM-specific calls. Keystone is expected to keep functioning if MoM is absent or disabled.

## Scope

No changes to MoM itself, general demolition job handling, global terrain validation, or Keystone placement rules. The game's own terrain-removal path determines whether a supported resource is invalidated. This is a first-party component change, not a separate runtime patch.

## Regression checks for the maintainer (pending in-game verification)

1. **Without MoM installed:** launch Keystone, load an existing save and start a new colony; verify normal decoration placement and no type-load errors.
2. **MoM installed but disabled:** verify the same behavior and no implicit MoM dependency.
3. **MoM enabled:** excavate under ground-supported Keystone flourishes and stones; verify terrain removal succeeds and decorations disappear.
4. **Save/load:** reload a colony with existing Keystone decorations and repeat excavation; verify the spec-driven component is restored.
5. **Water/unsupported terrain:** verify unsupported decorations do not linger floating above water; ensure normal placement and occupancy constraints remain intact.
6. **Other terrain/demolition tools:** watch for unexpected invalidations or job stalls.

Ported from the locally collected `Keystone_1.3.3_MOM_Compat_v7_Clean` binary, whose `Code.dll` contains the native marker reference. The collected Timberborn 1.1 game DLL also contains the marker. The **exact source revision in this branch has not been compiled or gameplay-tested here**; do not treat source/binary inspection as a passed game test.
