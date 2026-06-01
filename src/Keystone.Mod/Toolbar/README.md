# Keystone.Mod.Toolbar

Bottom-bar surface for Keystone's dev placement tools. One file.

## Pieces

| Type | Role |
|---|---|
| `KeystoneToolGroup` | `IBottomBarElementsProvider`. Groups all five Keystone dev placement tools under a single expandable toolbar button: `ParticlePlacementTool` (Class A, atmospheric particle), `DecorationPlacementTool` (Class A, reactive plant clone), `FlourishPlacementTool` (Class B, block-object flourish), `VanillaFloraPlacementTool` (Class D, active-faction vanilla flora), `CrossFactionFloraPlacementTool` (Class D, cross-faction vanilla flora). The group's `ToolGroupSpec` is loaded from `Data/ToolGroups/ToolGroups.Keystone.blueprint.json` via `ISpecService`. All children currently share a placeholder icon. |
