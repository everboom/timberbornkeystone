using Timberborn.BlueprintSystem;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Static accessor for <see cref="ISpecService"/>, readable from
  /// Harmony patch contexts that can't take constructor-injected
  /// dependencies. Mirrors the <see cref="FactionIdAccessor"/> /
  /// <c>ClassBAreaQuery</c> pattern: Bindito constructs and injects
  /// normally, the ctor publishes the service statically, and patches
  /// read <see cref="Specs"/> at invocation time.
  ///
  /// <para><b>Why we need it.</b>
  /// <see cref="HarmonyPatches.TemplateCollectionServicePatch"/> needs
  /// to enumerate <c>TemplateCollectionSpec</c> instances to compute
  /// the active faction's "native blueprint name" set. Those specs
  /// aren't in <c>TemplateCollectionService.AllTemplates</c> --
  /// <c>AllTemplates</c> holds the *target* blueprints that
  /// collections reference, not the collection-spec blueprints
  /// themselves, which are metadata read by the service via
  /// <c>ISpecService.GetSingleSpec&lt;TemplateCollectionSpec&gt;</c>.
  /// So the patch needs direct <c>ISpecService</c> access to find
  /// them.</para>
  ///
  /// <para>Bindito eagerly resolves <see cref="ILoadableSingleton"/>
  /// instances during scope startup, so the static is populated via
  /// the ctor before any Harmony patch could fire on a downstream
  /// <c>Load</c>. If <see cref="Specs"/> is null at read time
  /// (genuinely too-early window), patches should fall back to a
  /// safe default rather than throw.</para>
  /// </summary>
  public sealed class SpecServiceAccessor : ILoadableSingleton {

    /// <summary>Static reference to the spec service. Populated by
    /// the ctor; null only before Bindito constructs this
    /// singleton.</summary>
    private static ISpecService? _specs;

    /// <summary>Active <see cref="ISpecService"/> at the time of
    /// read, or <c>null</c> if Bindito has not yet constructed this
    /// singleton. Callers must null-check.</summary>
    public static ISpecService? Specs => _specs;

    public SpecServiceAccessor(ISpecService specs) {
      _specs = specs;
    }

    /// <inheritdoc />
    public void Load() {
      // No-op. The constructor publishes the static; ILoadableSingleton
      // is implemented only so Bindito eagerly resolves and constructs
      // this singleton during scope startup.
    }

  }

}
