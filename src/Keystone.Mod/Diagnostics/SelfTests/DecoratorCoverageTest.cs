using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Keystone.Mod.Fauna;
using Keystone.Mod.Flourish;
using Keystone.Mod.Recipes;
using Keystone.Mod.Wellbeing;
using Timberborn.BlueprintSystem;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// For every <c>AddDecorator&lt;TSpec, TComponent&gt;</c> pairing
  /// registered by <c>KeystoneTemplateModuleProvider</c>, asserts that
  /// at least one currently-loaded blueprint actually carries
  /// <c>TSpec</c>. A pairing with zero matching blueprints is dead
  /// wiring — the decorator framework will never attach
  /// <c>TComponent</c> to anything, but the pairing keeps existing in
  /// the codebase as a maintenance burden.
  ///
  /// <para><b>Why this matters.</b> Drift here is silent: a spec
  /// stops being authored on any blueprint (recipe-book rewrites,
  /// asset removals, spec rename), and the pairing in
  /// <c>KeystoneConfigurator</c> is left orphaned. Nothing fails. The
  /// developer eventually realises a feature stopped working and
  /// hunts for it through the wiring layers. This test surfaces
  /// orphaned pairings on demand.</para>
  ///
  /// <para><b>Hardcoded list of pairings.</b> Bindito doesn't expose
  /// the <see cref="Timberborn.TemplateInstantiation.TemplateModule"/>
  /// decorator registrations in a way we can iterate, so the spec-type
  /// list here mirrors <c>KeystoneTemplateModuleProvider.Get()</c>
  /// manually. Adding a new <c>AddDecorator</c> there and forgetting
  /// to add it here is a category of drift this test can't catch by
  /// itself — but the converse (removing a pairing without removing
  /// the test entry) lights up loudly with a compile error.</para>
  /// </summary>
  internal sealed class DecoratorCoverageTest : IKeystoneSelfTest {

    /// <summary>The spec types we expect at least one blueprint to
    /// carry. Mirrors the <c>AddDecorator&lt;TSpec, _&gt;</c> calls in
    /// <c>KeystoneTemplateModuleProvider.Get()</c>. Keep in sync.</summary>
    private static readonly Type[] DecoratorSpecTypes = {
        typeof(KeystoneFlourishSpec),
        typeof(KeystoneVariantSpec),
        typeof(KeystoneBiomeLevelsSpec),
        typeof(KeystoneDryNaturalResourceSpec),
        typeof(KeystoneRockTintSpec),
        typeof(KeystoneFaunaAnimatorSpec),
        typeof(KeystoneFaunaAgentSpec),
        typeof(KeystoneAquaticAgentSpec),
        typeof(KeystoneNatureSourceSpec),
    };

    private readonly ISpecService _specs;
    private readonly TemplateCollectionService _templates;

    public DecoratorCoverageTest(ISpecService specs, TemplateCollectionService templates) {
      _specs = specs;
      _templates = templates;
    }

    /// <inheritdoc />
    public string Name => "Decorator coverage";

    /// <inheritdoc />
    public string Category => "Wiring";

    /// <inheritdoc />
    public SelfTestResult Run() {
      var counts = new List<(string Spec, int Count)>(DecoratorSpecTypes.Length);
      var orphaned = new List<string>();

      // Resolve the generic ISpecService.GetSpecs<T>() once; we
      // re-bind to each spec type below. One reflection call per
      // spec type at click-time is negligible.
      var getSpecsMethod = typeof(ISpecService).GetMethod(nameof(ISpecService.GetSpecs));
      if (getSpecsMethod == null) {
        return SelfTestResult.Fail(
            "ISpecService.GetSpecs<T>() not found via reflection — API change?");
      }
      // HasSpec<T>() on Blueprint, same reflection treatment. Used to
      // count specs attached via IBlueprintModifierProvider — those
      // don't appear in ISpecService's spec-type cache because that
      // cache is built from the raw JSON before modifiers run, so
      // GetSpecs<TModifierInjected>() returns zero. HasSpec on the
      // post-deserialize Blueprint sees them.
      var hasSpecMethod = typeof(Blueprint).GetMethod(nameof(Blueprint.HasSpec));
      if (hasSpecMethod == null) {
        return SelfTestResult.Fail(
            "Blueprint.HasSpec<T>() not found via reflection — API change?");
      }

      foreach (var specType in DecoratorSpecTypes) {
        // Path 1: ISpecService.GetSpecs<T>(). Covers natively-JSON-
        // declared specs on both block-object and non-block-object
        // blueprints (e.g. KeystoneBiomeLevelsSpec on the standalone
        // biome-data blueprint).
        var typedGet = getSpecsMethod.MakeGenericMethod(specType);
        var enumerable = (System.Collections.IEnumerable?)typedGet.Invoke(_specs, null);
        var nativeCount = 0;
        if (enumerable != null) {
          foreach (var _ in enumerable) nativeCount++;
        }

        // Path 2: walk TemplateCollectionService.AllTemplates and
        // call bp.HasSpec<T>() per blueprint. Covers specs attached
        // by a modifier provider (e.g. KeystoneNatureSourceSpec on
        // ContemplationSpot/Lido via KeystoneNatureModifierProvider).
        // Only block-object templates; non-block-object carriers are
        // caught by Path 1.
        var typedHas = hasSpecMethod.MakeGenericMethod(specType);
        var injectedCount = 0;
        foreach (var bp in _templates.AllTemplates) {
          var has = (bool)(typedHas.Invoke(bp, null) ?? false);
          if (has) injectedCount++;
        }

        // Take the max — a spec covered by Path 1 returns 0 from
        // Path 2 if its carrier isn't a block-object template, and
        // vice versa for modifier-injected specs. Sum would over-
        // count when a block-object template's spec is reachable by
        // both paths, which is the common case for natively-declared
        // block-object specs (KeystoneFlourishSpec etc.).
        var count = Math.Max(nativeCount, injectedCount);
        counts.Add((specType.Name, count));
        if (count == 0) {
          orphaned.Add(specType.Name);
        }
      }

      if (orphaned.Count > 0) {
        var detail = new StringBuilder();
        detail.AppendLine("Per-spec attachment counts:");
        foreach (var (name, count) in counts.OrderBy(c => c.Count)) {
          detail.Append("  ").Append(name).Append(": ").Append(count).AppendLine();
        }
        // Warning, not Fail: a zero-coverage pairing is dead wiring
        // (maintenance concern) but not an integration error. The
        // active faction may legitimately not exercise every spec
        // we've registered decorators for -- some pairings target
        // content shipped only by specific expansion factions.
        return SelfTestResult.Warn(
            $"{orphaned.Count} decorator pairing(s) with zero matching blueprints: " +
            string.Join(", ", orphaned),
            detail.ToString());
      }

      // Show the histogram on a clean pass too — surfaces "barely used"
      // pairings (count == 1) that might be worth attention without
      // failing the test.
      var summary = new StringBuilder();
      summary.AppendLine($"{DecoratorSpecTypes.Length} decorator pairings, all attached:");
      foreach (var (name, count) in counts.OrderBy(c => c.Count)) {
        summary.Append("  ").Append(name).Append(": ").Append(count).AppendLine();
      }
      return new SelfTestResult(
          SelfTestStatus.Pass,
          $"{DecoratorSpecTypes.Length}/{DecoratorSpecTypes.Length} decorator pairings have blueprints",
          summary.ToString());
    }

  }

}
