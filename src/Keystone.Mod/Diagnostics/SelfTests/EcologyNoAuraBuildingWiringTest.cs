using System.Collections.Generic;
using System.Text;
using Keystone.Core.Buildings;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Verifies that every blueprint name in
  /// <see cref="BlueprintNamePolicy.NoAuraBuildingNames"/> resolves to
  /// a real blueprint in the active faction's
  /// <see cref="TemplateCollectionService.AllTemplates"/>. Detection
  /// is name-based (adapter-side, no spec injection), so a typo in
  /// the list would silently fail to apply no-aura without any
  /// runtime crash.
  ///
  /// <para>Mirrors <see cref="EcologyTransparentBuildingWiringTest"/>
  /// for the no-aura list. See its docstring for the warning levels.</para>
  /// </summary>
  internal sealed class EcologyNoAuraBuildingWiringTest : IKeystoneSelfTest {

    private readonly TemplateCollectionService _templates;

    public EcologyNoAuraBuildingWiringTest(TemplateCollectionService templates) {
      _templates = templates;
    }

    /// <inheritdoc />
    public string Name => "Ecology no-aura building names resolve";

    /// <inheritdoc />
    public string Category => "Wiring";

    /// <inheritdoc />
    public SelfTestResult Run() =>
        FactionAwareNameWiringCheck.Run(
            "ecology no-aura",
            BlueprintNamePolicy.NoAuraBuildingNames,
            _templates);

  }

}
