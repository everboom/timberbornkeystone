using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// For every bound <see cref="IKeystoneLoadStatus"/>, asserts that
  /// the loader's final initialisation step ran to completion (i.e.
  /// <see cref="IKeystoneLoadStatus.IsLoaded"/> is <c>true</c>).
  ///
  /// <para><b>Why this matters.</b> A loader whose <c>PostLoad</c> (or
  /// equivalent) throws gets the exception logged to
  /// <c>Player.log</c> -- but downstream consumers just see an empty
  /// catalog or a null cache and quietly do nothing useful. Symptoms
  /// surface much later, usually when the player notices something is
  /// missing in-game and the developer goes hunting. This test makes
  /// "loader X never finished initialising" an immediate, named row
  /// on the Test tab.</para>
  ///
  /// <para><b>What it caught.</b> The 0.4.3 regression where
  /// <see cref="Buildings.BuildingCatalogLoader"/> threw at
  /// <c>PostLoad</c> on a third-party mod's broken nested-blueprint
  /// reference. The exception was logged but the loader's
  /// <see cref="IKeystoneLoadStatus.IsLoaded"/> stayed
  /// <c>false</c>; nothing else surfaced the failure to the developer
  /// until the player reported a runtime symptom hours later. With
  /// this test in place, the same condition would have shown up
  /// immediately the next time the self-test battery ran.</para>
  ///
  /// <para><b>Coverage discipline.</b> Every loader that exposes
  /// <c>IsLoaded</c> should also implement
  /// <see cref="IKeystoneLoadStatus"/> and get multi-bound in
  /// <c>KeystoneConfigurator</c>. The test iterates the multi-bind
  /// directly, so adding a new loader requires no change to this
  /// file. Conversely, dropping a loader from the multi-bind without
  /// removing its <c>IKeystoneLoadStatus</c> implementation silently
  /// loses coverage -- the only guard against that is the
  /// configurator review at commit time.</para>
  /// </summary>
  internal sealed class LoaderSurvivalTest : IKeystoneSelfTest {

    private readonly IEnumerable<IKeystoneLoadStatus> _loaders;

    public LoaderSurvivalTest(IEnumerable<IKeystoneLoadStatus> loaders) {
      _loaders = loaders;
    }

    /// <inheritdoc />
    public string Name => "Loader survival";

    /// <inheritdoc />
    public string Category => "Wiring";

    /// <inheritdoc />
    public SelfTestResult Run() {
      var rows = _loaders
          .Select(l => (Name: l.LoaderName, Loaded: l.IsLoaded))
          .OrderBy(r => r.Name, System.StringComparer.Ordinal)
          .ToList();

      if (rows.Count == 0) {
        // No multi-bind registered, or container reset. Fail rather
        // than pass-with-zero-coverage; a zero-row "pass" would
        // silently mask the regression this test was added to catch.
        return SelfTestResult.Fail(
            "No IKeystoneLoadStatus implementations are bound -- " +
            "the multi-bind in KeystoneConfigurator is missing or the " +
            "container hasn't constructed any loader yet.");
      }

      var unloaded = rows.Where(r => !r.Loaded).ToList();
      var detail = new StringBuilder();
      foreach (var r in rows) {
        detail.Append("  ").Append(r.Loaded ? "[OK]" : "[FAIL]")
              .Append(' ').Append(r.Name).AppendLine();
      }

      if (unloaded.Count > 0) {
        return SelfTestResult.Fail(
            $"{unloaded.Count}/{rows.Count} loader(s) failed to complete: " +
            string.Join(", ", unloaded.Select(r => r.Name)) +
            ". Check Player.log for the exception each one threw.",
            detail.ToString());
      }

      return new SelfTestResult(
          SelfTestStatus.Pass,
          $"{rows.Count}/{rows.Count} loader(s) initialised cleanly",
          detail.ToString());
    }

  }

}
