using System.Collections.Generic;
using Timberborn.GameFactionSystem;
using Timberborn.TimbermeshMaterials;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Multi-bound <see cref="IMaterialCollectionIdsProvider"/> that asks
  /// <see cref="MaterialRepository"/> to load the OTHER faction's
  /// material collection on top of the active faction's.
  ///
  /// <para><b>Why this companion to <see cref="CrossFactionCollectionProvider"/>.</b>
  /// Loading cross-faction templates makes the spec system aware of
  /// resources like Mangrove and CoffeeBush, but the prefab pipeline
  /// also needs the <i>visual assets</i> (meshes, materials) keyed by
  /// faction. Without this provider, instantiating a Mangrove on a
  /// Folktails game throws "Material Mangrove not found in repository".</para>
  ///
  /// <para><b>ID convention.</b> Material collection ids are bare faction
  /// names (<c>Folktails</c>, <c>IronTeeth</c>, plus <c>Common</c>) --
  /// different from template collections, which use
  /// <c>&lt;Category&gt;.&lt;Faction&gt;</c>. The
  /// <see cref="CrossFactionProviderBase.BelongsToFaction"/> helper
  /// handles both shapes uniformly.</para>
  ///
  /// <para><b>RAM cost.</b> Loading the entire other-faction material
  /// collection brings in ~34 materials we mostly won't use. Acceptable
  /// for prototype; production should narrow to just the materials
  /// referenced by spawned cross-faction natural resources.</para>
  /// </summary>
  public sealed class CrossFactionMaterialProvider : CrossFactionProviderBase, IMaterialCollectionIdsProvider {

    #region Constants

    private static readonly string[] Candidates = { "Folktails", "IronTeeth" };

    #endregion

    #region Construction

    public CrossFactionMaterialProvider(FactionService factions) : base(factions) {
    }

    #endregion

    #region IMaterialCollectionIdsProvider

    /// <inheritdoc />
    public IEnumerable<string> GetMaterialCollectionIds()
        => YieldOtherFaction(Candidates, nameof(CrossFactionMaterialProvider));

    #endregion

  }

}
