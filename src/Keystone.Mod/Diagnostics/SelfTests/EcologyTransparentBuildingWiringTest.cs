using System.Collections.Generic;
using System.Text;
using Keystone.Core.Buildings;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Verifies that every blueprint name in
  /// <see cref="BlueprintNamePolicy.TransparentBuildingNames"/> resolves
  /// to a real blueprint in the active faction's
  /// <see cref="TemplateCollectionService.AllTemplates"/>. Detection
  /// is name-based (adapter-side, no spec injection), so a typo in
  /// the list would silently fail to apply transparency without any
  /// runtime crash.
  ///
  /// <para><b>Outcome levels:</b>
  /// <list type="bullet">
  ///   <item><b>Pass</b> — every name that's <i>expected to belong</i>
  ///         to this faction (suffix match) resolves. Other-faction
  ///         names are counted as expected absences.</item>
  ///   <item><b>Warning (zero coverage)</b> — no listed name resolves
  ///         in the active faction. Could mean the faction has no
  ///         transparent-tagged buildings (legitimate — e.g. a future
  ///         minimalist faction mod) OR that every faction-suffix
  ///         match silently failed (typos at scale).</item>
  ///   <item><b>Warning (partial)</b> — at least one name whose suffix
  ///         appears to target this faction failed to resolve.
  ///         Probably a typo. Detail lists the absent names.</item>
  /// </list>
  /// All states are advisory — there are legitimate reasons for any
  /// of them, so we never fail this test.</para>
  /// </summary>
  internal sealed class EcologyTransparentBuildingWiringTest : IKeystoneSelfTest {

    private readonly TemplateCollectionService _templates;

    public EcologyTransparentBuildingWiringTest(TemplateCollectionService templates) {
      _templates = templates;
    }

    /// <inheritdoc />
    public string Name => "Ecology-transparent building names resolve";

    /// <inheritdoc />
    public string Category => "Wiring";

    /// <inheritdoc />
    public SelfTestResult Run() =>
        FactionAwareNameWiringCheck.Run(
            "ecology-transparent",
            BlueprintNamePolicy.TransparentBuildingNames,
            _templates);

  }

}
