using System.Collections.Generic;
using Timberborn.TimbermeshMaterials;

namespace Keystone.Mod.Materials {

  /// <summary>
  /// Multi-bound <see cref="IMaterialCollectionIdsProvider"/> that
  /// registers Keystone's own <c>MaterialCollection</c> with
  /// Timberborn's <c>MaterialRepository</c>.
  ///
  /// <para><b>Why.</b> <c>.timbermesh</c> files encode only the
  /// material's name string; at runtime the <c>IMaterialRepository</c>
  /// looks that name up across every loaded MaterialCollection.
  /// Keystone-authored meshes reference Keystone-original materials
  /// (e.g. <c>KeystoneRock</c>) that aren't part of any vanilla
  /// faction collection -- without this provider, <c>GetMaterial</c>
  /// returns null and the mesh renders pink or empty.</para>
  ///
  /// <para><b>Pairing.</b> Loaded together with the
  /// <c>MaterialCollection.Keystone</c> blueprint at
  /// <c>Data/Materials/MaterialCollection.Keystone.blueprint.json</c>,
  /// which lists every material asset in this collection by bundle
  /// path. Add new <c>.mat</c> assets there <i>and</i> drop them
  /// somewhere reachable in the asset bundle when extending.</para>
  ///
  /// <para><b>ID convention.</b> Material collection ids are bare
  /// names; this one is <c>Keystone</c>. Matches the
  /// <c>CollectionId</c> field in the blueprint JSON.</para>
  /// </summary>
  public sealed class KeystoneMaterialProvider : IMaterialCollectionIdsProvider {

    #region Constants

    private const string CollectionId = "Keystone";

    #endregion

    #region IMaterialCollectionIdsProvider

    /// <inheritdoc />
    public IEnumerable<string> GetMaterialCollectionIds() {
      yield return CollectionId;
    }

    #endregion

  }

}
