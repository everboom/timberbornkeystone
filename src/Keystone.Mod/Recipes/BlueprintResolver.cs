using System.Collections.Generic;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Diagnostics.SelfTests;
using Timberborn.BlueprintSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Resolves blueprint names to <see cref="Blueprint"/> references via
  /// <see cref="TemplateNameMapper"/>. Lazy: a name is looked up the
  /// first time <see cref="Resolve"/> is called for it, and the result
  /// (hit or miss) is cached for subsequent calls in this session.
  ///
  /// <para><b>Why <see cref="TemplateNameMapper"/> rather than
  /// <c>ISpecService.GetSpecs&lt;BlockObjectSpec&gt;</c>.</b> The mapper
  /// is built from
  /// <c>TemplateService.GetAll&lt;TemplateSpec&gt;()</c>, which only
  /// walks blueprints registered in active <c>TemplateCollections</c>
  /// — the active faction's templates plus the other-faction
  /// natural-resource collection that
  /// <see cref="Debug.CrossFactionCollectionProvider"/> opts us into.
  /// Other mods' bundles that the player has installed but that we
  /// don't subscribe to (random building mods, faction-specific content
  /// we don't need) are never deserialized via this path. The previous
  /// <c>GetSpecs&lt;BlockObjectSpec&gt;()</c> prewarm fired every
  /// bundle's deserialize lazy that contained a <c>BlockObjectSpec</c>
  /// at root — i.e. nearly every loaded blueprint in the game — which
  /// turned us into the trigger point for any third-party mod that
  /// happened to ship a broken nested-blueprint reference.</para>
  ///
  /// <para><b>What this covers.</b> Every blueprint Keystone needs to
  /// instantiate carries a <see cref="TemplateSpec"/> at root —
  /// the Keystone-authored flourish blueprints (Class B/C) and the
  /// vanilla flora donors referenced by Class D recipes. A blueprint
  /// without a <c>TemplateSpec</c> isn't directly instantiable as an
  /// entity, so dropping the broader <c>BlockObjectSpec</c> walk loses
  /// nothing the spawn handlers actually use.</para>
  ///
  /// <para><b>Resolution failures are logged once.</b> A lookup of a
  /// name the mapper doesn't know logs a single warning and caches a
  /// null result. Subsequent lookups for the same missing name return
  /// the cached null silently, so repeated cycles don't spam the log.</para>
  /// </summary>
  public sealed class BlueprintResolver : IPostLoadableSingleton, IKeystoneLoadStatus {

    private readonly TemplateNameMapper _nameMapper;
    private readonly Dictionary<string, Blueprint?> _cache = new();
    private readonly List<string> _missed = new();

    /// <summary>True once <see cref="PostLoad"/> has run. Read by the
    /// startup self-check to defer until the resolver is ready, and
    /// by <see cref="LoaderSurvivalTest"/> to flag silent load
    /// failures. The resolver itself does no eager work; the flag
    /// exists for the existing self-check contract.</summary>
    public bool IsLoaded { get; private set; }

    /// <inheritdoc />
    public string LoaderName => nameof(BlueprintResolver);

    public BlueprintResolver(TemplateNameMapper nameMapper) {
      _nameMapper = nameMapper;
    }

    /// <summary>
    /// Names that resolution has been asked for and failed to find
    /// via the <see cref="TemplateNameMapper"/>. Surfaced in the debug
    /// panel so silent recipe-resolution failures stay visible during
    /// a session. Ordered by first-miss time.
    /// </summary>
    public IReadOnlyList<string> MissedNames => _missed;

    /// <inheritdoc />
    public void PostLoad() {
      // Outermost try/catch is defensive overkill for a body this
      // small (two Clear calls), but kept for parity with every other
      // host-called method so the audit-grep finds a uniform shape
      // across the codebase.
      try {
        // No eager work. TemplateNameMapper is an ILoadableSingleton and
        // is fully initialised before PostLoad runs; we just clear our
        // session-local cache so a mod reload starts fresh.
        _cache.Clear();
        _missed.Clear();
        IsLoaded = true;
      } catch (System.Exception ex) {
        Diagnostics.LifecycleGuard.HandleError("BlueprintResolver.PostLoad", "Subsystem failed", ex);
      }
    }

    /// <summary>Find the blueprint with the given template name.
    /// Returns <c>null</c> (logged once, name added to
    /// <see cref="MissedNames"/>) when no loaded template carries that
    /// name.</summary>
    public Blueprint? Resolve(string blueprintName) {
      if (_cache.TryGetValue(blueprintName, out var cached)) return cached;
      if (_nameMapper.TryGetTemplate(blueprintName, out var templateSpec)) {
        var bp = templateSpec.Blueprint;
        _cache[blueprintName] = bp;
        return bp;
      }
      KeystoneLog.Warn(
          $"[Keystone] BlueprintResolver: '{blueprintName}' not found via " +
          "TemplateNameMapper. The blueprint is either not part of any active " +
          "TemplateCollection (active faction + cross-faction NaturalResources) " +
          "or not deployed. Recipes referencing it will be inert.");
      _cache[blueprintName] = null;
      _missed.Add(blueprintName);
      return null;
    }

  }

}
