using System.Collections.Generic;
using Keystone.Mod.Recipes;

namespace Keystone.Mod.Diagnostics.StartupChecks {

  /// <summary>
  /// Surfaces blueprint names that recipes referenced but couldn't be
  /// found in the loaded <c>BlockObjectSpec</c> set. A name in
  /// <see cref="BlueprintResolver.MissedNames"/> means at least one
  /// recipe (typically a Class D entry like <c>BlueprintName: "Birch"</c>)
  /// will silently no-op for the whole session.
  ///
  /// <para>Reads the resolver's existing miss-list rather than
  /// re-walking the catalogs -- the resolver already logs once per
  /// missed name during its own PostLoad and the catalog draining,
  /// so by reporter time the list is final.</para>
  /// </summary>
  public sealed class BlueprintResolutionCheck : IStartupCheck {

    private readonly BlueprintResolver _resolver;

    public BlueprintResolutionCheck(BlueprintResolver resolver) {
      _resolver = resolver;
    }

    /// <inheritdoc />
    public string Category => "Blueprint resolution";

    /// <inheritdoc />
    public bool IsReady => _resolver.IsLoaded;

    /// <inheritdoc />
    public IEnumerable<StartupFinding> Run() {
      var missed = _resolver.MissedNames;
      if (missed.Count == 0) yield break;

      yield return new StartupFinding(
          StartupFindingSeverity.Warning,
          $"Keystone expected {missed.Count} piece(s) of content that " +
          "the game doesn't have. Affected ecology features won't spawn.",
          DetailedMessage:
              $"Unresolved blueprint name(s): {string.Join(", ", missed)}. " +
              "Likely a typo in a recipe or a vanilla blueprint renamed in a " +
              "Timberborn update.");
    }

  }

}
