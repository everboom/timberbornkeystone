using System.Collections.Generic;
using System.Text;
using Keystone.Mod.Fauna;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// For every currently-spawned fauna entity carrying a
  /// <see cref="KeystoneFaunaAnimator"/>, asserts that its underlying
  /// <c>IAnimator</c> resolved successfully (i.e. the prefab's
  /// <c>.timbermesh</c> exported with "Use vertex animations" enabled).
  /// A resolution failure shows up at runtime as a one-time warning in
  /// <c>Player.log</c> plus a silent T-pose — easy to miss for fauna
  /// the developer hasn't been actively watching.
  ///
  /// <para><b>Coverage caveat.</b> Only verifies the species currently
  /// present in the world. A new fauna recipe that has never spawned in
  /// this session (e.g. blocked by biome state) doesn't appear in the
  /// registry, so its animator can't be checked here. The skipped count
  /// surfaces that gap — re-run after fast-forwarding through a few
  /// dawn cycles to broaden coverage.</para>
  /// </summary>
  internal sealed class FaunaAnimatorResolutionTest : IKeystoneSelfTest {

    private readonly KeystoneFaunaRegistry _registry;

    public FaunaAnimatorResolutionTest(KeystoneFaunaRegistry registry) {
      _registry = registry;
    }

    /// <inheritdoc />
    public string Name => "Fauna animator resolved";

    /// <inheritdoc />
    public string Category => "Fauna";

    /// <inheritdoc />
    public SelfTestResult Run() {
      var checkedCount = 0;
      var resolved = 0;
      var unresolvedBySpecies = new Dictionary<string, int>();
      var distinctSpecies = new HashSet<string>();

      foreach (var entry in _registry.Entries) {
        var animator = entry.Entity.GetComponent<KeystoneFaunaAnimator>();
        if (animator == null) continue;  // not a Keystone-managed animator
        checkedCount++;
        distinctSpecies.Add(entry.BlueprintName);
        if (animator.AnimatorResolved) {
          resolved++;
        } else {
          unresolvedBySpecies.TryGetValue(entry.BlueprintName, out var n);
          unresolvedBySpecies[entry.BlueprintName] = n + 1;
        }
      }

      if (checkedCount == 0) {
        return SelfTestResult.Skipped(
            "No spawned fauna with KeystoneFaunaAnimator. Fast-forward through " +
            "a few dawn cycles (or place fauna via the dev tool) and re-run.");
      }

      if (unresolvedBySpecies.Count > 0) {
        var detail = new StringBuilder();
        detail.Append(checkedCount).Append(" fauna entities checked across ")
              .Append(distinctSpecies.Count).AppendLine(" species");
        foreach (var (species, count) in unresolvedBySpecies) {
          detail.Append("  ").Append(species).Append(": ").Append(count)
                .AppendLine(" entity(ies) with no resolved IAnimator " +
                            "(timbermesh exported without 'Use vertex animations'?)");
        }
        return SelfTestResult.Fail(
            $"{unresolvedBySpecies.Count} species missing IAnimator resolution",
            detail.ToString());
      }

      return SelfTestResult.Pass(
          $"{resolved}/{checkedCount} fauna entities have resolved IAnimator " +
          $"({distinctSpecies.Count} species)");
    }

  }

}
