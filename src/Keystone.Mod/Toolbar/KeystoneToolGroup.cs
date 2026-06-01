using System.Collections.Generic;
using System.Linq;
using Keystone.Mod.Decoration;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Fauna;
using Keystone.Mod.Flora;
using Keystone.Mod.Flourish;
using Timberborn.BlueprintSystem;
using Timberborn.BottomBarSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;

namespace Keystone.Mod.Toolbar {

  /// <summary>
  /// Bottom-bar provider that groups all five Keystone dev placement
  /// tools under a single expandable toolbar button. One button per
  /// distinct content variant we want to demonstrate (see
  /// <c>DESIGN.md</c> § "Content classes" for taxonomy):
  /// <list type="bullet">
  ///   <item>Class A (atmospheric particle):
  ///         <see cref="ParticlePlacementTool"/> -- non-blocking
  ///         decoration carrying a Unity ParticleSystem; passive
  ///         (no controller, no per-frame reactivity).</item>
  ///   <item>Class A (reactive plant decoration):
  ///         <see cref="DecorationPlacementTool"/> -- non-blocking
  ///         decoration with a moisture-driven controller. Same
  ///         Class A bucket as the particle path; reactivity is
  ///         orthogonal.</item>
  ///   <item>Class B (block-object flourish):
  ///         <see cref="FlourishPlacementTool"/> -- block object,
  ///         persisted, displaceable by builds, custom lifecycle
  ///         driven by Watered/Floodable specs.</item>
  ///   <item>Class D (active faction): <see cref="VanillaFloraPlacementTool"/>
  ///         -- vanilla flora from the active faction.</item>
  ///   <item>Class D (cross-faction): <see cref="CrossFactionFloraPlacementTool"/>
  ///         -- vanilla flora from the OTHER faction.</item>
  /// </list>
  ///
  /// <para>The group's <see cref="ToolGroupSpec"/> is loaded from
  /// <c>Data/ToolGroups/ToolGroups.Keystone.blueprint.json</c>; we
  /// look it up by id at provider construction.</para>
  ///
  /// <para>All four child buttons currently share the placeholder
  /// <see cref="SharedIcon"/> sprite -- distinguishable in-game only
  /// by tooltip / position. Add per-class icons once the toolbar
  /// design solidifies.</para>
  /// </summary>
  public sealed class KeystoneToolGroup : IBottomBarElementsProvider {

    private const string GroupId = "Keystone";
    private const string SharedIcon = "KeystoneFlourishPlacement";

    private readonly ISpecService _specs;
    private readonly ToolGroupButtonFactory _groupFactory;
    private readonly ToolButtonFactory _buttonFactory;
    private readonly FlourishPlacementTool _flourishTool;
    private readonly DecorationPlacementTool _decorationTool;
    private readonly ParticlePlacementTool _particleTool;
    private readonly RockPlacementTool _rockTool;
    private readonly RockClusterPlacementTool _rockClusterTool;
    private readonly CrossFactionFloraPlacementTool _crossFactionFloraTool;
    private readonly VanillaFloraPlacementTool _vanillaFloraTool;
    private readonly FaunaPlacementTool _faunaTool;
    private readonly FishSmokeTestTool _fishSmokeTestTool;
    private readonly StumpPlacementTool _stumpTool;

    public KeystoneToolGroup(
        ISpecService specs,
        ToolGroupButtonFactory groupFactory,
        ToolButtonFactory buttonFactory,
        FlourishPlacementTool flourishTool,
        DecorationPlacementTool decorationTool,
        ParticlePlacementTool particleTool,
        RockPlacementTool rockTool,
        RockClusterPlacementTool rockClusterTool,
        CrossFactionFloraPlacementTool crossFactionFloraTool,
        VanillaFloraPlacementTool vanillaFloraTool,
        FaunaPlacementTool faunaTool,
        FishSmokeTestTool fishSmokeTestTool,
        StumpPlacementTool stumpTool) {
      _specs = specs;
      _groupFactory = groupFactory;
      _buttonFactory = buttonFactory;
      _flourishTool = flourishTool;
      _decorationTool = decorationTool;
      _particleTool = particleTool;
      _rockTool = rockTool;
      _rockClusterTool = rockClusterTool;
      _crossFactionFloraTool = crossFactionFloraTool;
      _vanillaFloraTool = vanillaFloraTool;
      _faunaTool = faunaTool;
      _fishSmokeTestTool = fishSmokeTestTool;
      _stumpTool = stumpTool;
    }

    public IEnumerable<BottomBarElement> GetElements() {
      // Dev-only surface: the group hosts force-placement tools that
      // bypass the recipe gates and are not intended for end users.
      // KeystoneDevMode is the same sentinel-file gate used by
      // StartupReporter.AlwaysShow; off in any clean release build.
      if (!KeystoneDevMode.IsEnabled) yield break;

      var spec = _specs.GetSpecs<ToolGroupSpec>().FirstOrDefault(s => s.Id == GroupId);
      if (spec == null) {
        KeystoneLog.Warn(
            $"[Keystone] KeystoneToolGroup: no ToolGroupSpec with Id '{GroupId}' " +
            "found via ISpecService. Tool group disabled. Confirm that " +
            "Data/ToolGroups/ToolGroups.Keystone.blueprint.json was deployed " +
            "by the SDK Mod Builder.");
        yield break;
      }

      var groupButton = _groupFactory.CreateBlue(spec);

      // Order matches the content-class hierarchy A -> B -> D so the
      // toolbar reads left-to-right in the same order as the tooltip
      // labels ("Class A: ...", "Class B: ...", "Class D: ..."). Each
      // Create call appends to the group's ToolButtonsElement;
      // AddTool registers the button with the group so visibility-on-
      // empty and tab navigation work.
      AppendChild(groupButton, _decorationTool);          // Class A reactive
      AppendChild(groupButton, _particleTool);            // Class A passive
      AppendChild(groupButton, _rockTool);                // Class A custom-mesh smoke test
      AppendChild(groupButton, _flourishTool);            // Class B
      AppendChild(groupButton, _rockClusterTool);         // Class C rock cluster (inanimate, reactive tint)
      AppendChild(groupButton, _vanillaFloraTool);        // Class D active-faction
      AppendChild(groupButton, _crossFactionFloraTool);   // Class D cross-faction
      AppendChild(groupButton, _faunaTool);               // Fauna smoke test (KeystoneDeer)
      AppendChild(groupButton, _fishSmokeTestTool);       // Fish visual smoke test (KeystoneFish1, water-gated)
      AppendChild(groupButton, _stumpTool);               // Harvested Birch stump (tests stump replacement)

      yield return BottomBarElement.CreateMultiLevel(
          groupButton.Root, groupButton.ToolButtonsElement);
    }

    private void AppendChild(ToolGroupButton groupButton, ITool tool) {
      var button = _buttonFactory.Create(tool, SharedIcon, groupButton.ToolButtonsElement);
      groupButton.AddTool(button);
    }

  }

}
