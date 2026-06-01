using System.Collections.Generic;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Multi-bound <see cref="ITemplateCollectionIdProvider"/> that
  /// teaches Timberborn's <c>TemplateCollectionService</c> about the
  /// <c>KeystoneFauna</c> collection — the registry for Keystone-
  /// authored fauna blueprints (deer today; future fish/foxes/etc.).
  ///
  /// <para><b>Why.</b> Same reason as
  /// <see cref="KeystoneNaturalResourceCollectionProvider"/>:
  /// <c>TemplateCollectionService.Load</c> only walks
  /// <c>TemplateCollectionSpec</c>s whose <c>CollectionId</c> is
  /// listed by some registered provider. Without this provider, the
  /// <c>KeystoneFauna.blueprint.json</c> file would deserialise but
  /// its blueprint entries would never reach
  /// <c>AllTemplates</c>.</para>
  ///
  /// <para><b>Pairing.</b> Loaded together with
  /// <c>Data/TemplateCollections/KeystoneFauna/KeystoneFauna.blueprint.json</c>.
  /// Update both sides when adding new fauna species.</para>
  /// </summary>
  public sealed class KeystoneFaunaCollectionProvider : ITemplateCollectionIdProvider {

    private const string CollectionId = "KeystoneFauna";

    /// <inheritdoc />
    public IEnumerable<string> GetTemplateCollectionIds() {
      yield return CollectionId;
    }

  }

}
