using Timberborn.FactionSystem;
using Timberborn.GameFactionSystem;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Static accessor for the active faction id, readable from Harmony
  /// patch contexts that can't take constructor-injected dependencies.
  /// Mirrors the <see cref="ClassBAreaQuery"/> pattern: Bindito
  /// constructs and injects normally, the ctor publishes the
  /// <see cref="FactionService"/> reference statically, and patches
  /// read <see cref="CurrentId"/> at invocation time.
  ///
  /// <para><b>Why a singleton and not a static <see cref="FactionService"/>
  /// reference set elsewhere.</b> Replaces the prior side-channel on
  /// <c>CrossFactionProviderBase.ActiveFactionId</c>, which was only
  /// populated the first time a cross-faction provider was iterated.
  /// That introduced an ordering coupling between Mechanistry's
  /// provider iteration and <c>TemplateCollectionService.Load</c> —
  /// a coupling that worked empirically but was never an API
  /// contract. Reading <c>FactionService.Current.Id</c> directly at
  /// patch time removes the coupling entirely.</para>
  ///
  /// <para>Bindito eagerly resolves <see cref="ILoadableSingleton"/>
  /// instances during Game scope startup so it can call
  /// <see cref="Load"/> on them; the static is populated via the ctor
  /// before any Harmony patch could fire on a downstream Load. If
  /// <see cref="FactionService.Current"/> is still null at read time
  /// (genuinely too-early window), <see cref="CurrentId"/> returns
  /// null and the patch logs and skips — same defensive shape as the
  /// prior code.</para>
  /// </summary>
  public sealed class FactionIdAccessor : ILoadableSingleton {

    /// <summary>Static reference to the game's faction service.
    /// Populated by the ctor; null only before Bindito constructs
    /// this singleton (i.e. before the Game scope is wired).</summary>
    private static FactionService? _factions;

    /// <summary>Active faction id at the time of read, or
    /// <c>null</c> if either Bindito has not yet constructed this
    /// singleton or the game has not loaded a faction. Read at
    /// invocation time by Harmony patches; callers must null-check.</summary>
    public static string? CurrentId => _factions?.Current?.Id;

    /// <summary>Active <see cref="FactionSpec"/> at the time of read,
    /// or <c>null</c> under the same too-early conditions as
    /// <see cref="CurrentId"/>. Exposes the full spec (incl.
    /// <see cref="FactionSpec.TemplateCollectionIds"/> and friends) so
    /// patches that need more than the id can avoid walking
    /// <c>AllTemplates</c> for it (FactionSpec blueprints are loaded
    /// by <c>FactionSpecService</c>, not <c>TemplateCollectionService</c>,
    /// so they wouldn't be there anyway).</summary>
    public static FactionSpec? CurrentSpec => _factions?.Current;

    public FactionIdAccessor(FactionService factions) {
      _factions = factions;
    }

    /// <inheritdoc />
    public void Load() {
      // No-op. The constructor publishes the static; ILoadableSingleton
      // is implemented only so Bindito eagerly resolves and constructs
      // this singleton during Game scope startup.
    }

  }

}
