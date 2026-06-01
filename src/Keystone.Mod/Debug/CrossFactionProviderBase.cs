using System;
using System.Collections.Generic;
using Keystone.Mod.Diagnostics;
using Timberborn.GameFactionSystem;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Shared scaffolding for the cross-faction collection providers. Each
  /// provider asks the relevant collection service to load the OTHER
  /// faction's variant of one or more collections; the matching logic
  /// has to handle both vanilla naming conventions:
  /// <list type="bullet">
  ///   <item>Bare faction id (<c>Folktails</c>, <c>IronTeeth</c>) — material collections.</item>
  ///   <item>Dotted suffix (<c>NaturalResources.IronTeeth</c>) — template collections.</item>
  /// </list>
  ///
  /// <para><b>Scope discipline.</b> Each subclass passes a <i>narrow</i>
  /// candidate list to <see cref="YieldOtherFaction"/>; we don't enumerate
  /// every collection in the spec system. Loading the entire other faction
  /// would balloon RAM for content we'll never use; the goal is just to
  /// reach the cross-faction natural resources we actually spawn.</para>
  ///
  /// <para><b>Active faction id.</b> Read from
  /// <c>Keystone.Mod.HarmonyPatches.FactionIdAccessor.CurrentId</c>, which
  /// resolves <c>FactionService.Current.Id</c> at call time. The previous
  /// pattern (a static field set as a side-effect of the first
  /// <see cref="YieldOtherFaction"/> call) coupled
  /// <c>TemplateCollectionServicePatch</c> to provider iteration order;
  /// the deterministic lookup avoids that.</para>
  /// </summary>
  public abstract class CrossFactionProviderBase {

    #region Fields

    private readonly FactionService _factions;

    #endregion

    #region Construction

    protected CrossFactionProviderBase(FactionService factions) {
      _factions = factions;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// True if <paramref name="collectionId"/> belongs to the faction
    /// identified by <paramref name="factionId"/>. Handles both the bare
    /// (<c>Folktails</c>) and dotted (<c>NaturalResources.Folktails</c>)
    /// naming conventions in one predicate, while staying strict enough
    /// to not match a substring like <c>"FolktailsLegacy"</c>.
    /// </summary>
    protected static bool BelongsToFaction(string collectionId, string factionId) {
      if (collectionId == factionId) return true;
      // ".Folktails" guard — substring match on a dot-prefixed suffix
      // avoids accidental hits like "FolktailsLegacy".
      return collectionId.EndsWith("." + factionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Iterate <paramref name="candidates"/> and yield those that DON'T
    /// belong to the currently active faction. Logs the active faction
    /// id once at call time; if the faction service hasn't loaded yet
    /// (shouldn't happen in practice — see
    /// <see cref="CrossFactionCollectionProvider"/> notes), yields nothing.
    /// </summary>
    protected IEnumerable<string> YieldOtherFaction(IEnumerable<string> candidates, string label) {
      var current = _factions.Current;
      if (current == null) {
        KeystoneLog.Verbose(
            $"[Keystone] {label}: FactionService.Current is null at provider-call time -- yielding nothing.");
        yield break;
      }

      KeystoneLog.Verbose(
          $"[Keystone] {label}: active faction Id='{current.Id}'; yielding non-active faction collection(s).");

      foreach (var candidate in candidates) {
        if (!BelongsToFaction(candidate, current.Id)) yield return candidate;
      }
    }

    #endregion

  }

}
