using System.Collections.Generic;
using Timberborn.GameFactionSystem;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Multi-bound <see cref="ITemplateCollectionIdProvider"/> that asks
  /// <see cref="TemplateCollectionService"/> to load the *other* faction's
  /// natural-resource collection on top of whatever the active faction
  /// already loaded.
  ///
  /// <para><b>Why.</b> Without this, the active faction's
  /// <c>TemplateNameMapper</c> can't resolve names from the other faction
  /// (Ironteeth asking for <c>Maple</c> silently substitutes via
  /// <c>BackwardCompatibleTemplateNames</c>; <c>ISpecService.GetBlueprint</c>
  /// throws). Asking the collection service to ingest the missing
  /// collection at load time fixes the lookup at the source -- every
  /// downstream service (factory, mapper, blueprint store) sees the union.</para>
  ///
  /// <para><b>Why only NaturalResources.</b> Phase 1 only needs cross-faction
  /// flora; pulling in Buildings/Characters/Planes would add 300+ blueprints
  /// with no UI route to interact with them, plus more collision risk
  /// (e.g. duplicate <c>DistrictCenter</c>).</para>
  ///
  /// <para><b>Caveat.</b> Hard-coded to the vanilla faction pair. Expansion
  /// mods (e.g. Emberpelts) would need the candidate list generalised to
  /// enumerate every loaded faction's <c>NaturalResources</c> collection
  /// rather than naming the pair. Phase 1 prototype only.</para>
  /// </summary>
  public sealed class CrossFactionCollectionProvider : CrossFactionProviderBase, ITemplateCollectionIdProvider {

    #region Constants

    /// <summary>
    /// Candidate template-collection ids -- only NaturalResources.
    /// Trees and bushes from the other faction render and behave correctly
    /// after the
    /// <see cref="HarmonyPatches.TemplateCollectionServicePatch"/>
    /// strips Plantable/Gatherable. Cross-faction crops aren't covered by
    /// this path -- their visual depends on a deep chain of cross-faction
    /// dependencies (planter, good, recipe, need). For decorative crop
    /// visuals we use bare-mesh passive objects instead.
    /// </summary>
    private static readonly string[] Candidates = {
        "NaturalResources.Folktails",
        "NaturalResources.IronTeeth",
    };

    #endregion

    #region Construction

    public CrossFactionCollectionProvider(FactionService factions) : base(factions) {
    }

    #endregion

    #region ITemplateCollectionIdProvider

    /// <inheritdoc />
    public IEnumerable<string> GetTemplateCollectionIds()
        => YieldOtherFaction(Candidates, nameof(CrossFactionCollectionProvider));

    #endregion

  }

}
