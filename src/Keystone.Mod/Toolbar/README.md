# Keystone.Mod.Toolbar

Bottom-bar surfaces for Keystone's toolbar tools.

## Pieces

| Type | Role |
|---|---|
| `KeystoneToolGroup` | `IBottomBarElementsProvider`. Groups all five Keystone dev placement tools under a single expandable toolbar button: `ParticlePlacementTool` (Class A, atmospheric particle), `DecorationPlacementTool` (Class A, reactive plant clone), `FlourishPlacementTool` (Class B, block-object flourish), `VanillaFloraPlacementTool` (Class D, active-faction vanilla flora), `CrossFactionFloraPlacementTool` (Class D, cross-faction vanilla flora). The group's `ToolGroupSpec` is loaded from `Data/ToolGroups/ToolGroups.Keystone.blueprint.json` via `ISpecService`. All children currently share a placeholder icon. |
| `KeystoneToolDisabler` | `IToolDisabler` (MultiBind'd in `KeystoneConfigurator`). Live per-tool visibility gate for the three player-facing brushes injected into vanilla groups — mixed-crop planting, mixed-forest planting, cutting planner — driven by their `KeystoneUiSettings` on/off toggles. The engine re-evaluates each `ToolButton`'s visibility on every `ToolGroupEnteredEvent`, so toggling a setting shows/hides the button on next group open, no reload. Returns `true` (enabled) for all non-Keystone tools. See `../Planting/README.md` and `../Cutting/README.md`. |
