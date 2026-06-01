using System.Collections.Generic;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Multi-bound <see cref="ITemplateCollectionIdProvider"/> that
  /// teaches Timberborn's <c>TemplateCollectionService</c> about the
  /// Keystone-authored blueprint collection
  /// (<c>KeystoneNaturalResources</c>).
  ///
  /// <para><b>Why.</b> Blueprints dropped into the asset bundle are
  /// discovered by <c>ISpecService</c> and visible to
  /// <c>BlueprintResolver</c>, but they don't enter
  /// <c>TemplateCollectionService.AllTemplates</c> until some collection
  /// names them. <c>TemplateNameMapper.GetTemplate</c> queries
  /// <c>AllTemplates</c>; on save+load, every Keystone entity with a
  /// <c>TemplateName</c> not in <c>AllTemplates</c> is silently dropped
  /// with the log line <c>"Object had unknown type and was deleted:
  /// &lt;name&gt;"</c>. Without this provider, persisted Class B / Class C
  /// flourishes don't survive a save→load round-trip.</para>
  ///
  /// <para><b>Pairing.</b> Loaded together with the
  /// <c>KeystoneNaturalResources</c> <c>TemplateCollectionSpec</c>
  /// blueprint, which lists every Keystone-authored blueprint by name.
  /// Update both sides when adding new biome blueprints.</para>
  /// </summary>
  public sealed class KeystoneNaturalResourceCollectionProvider : ITemplateCollectionIdProvider {

    #region Constants

    /// <summary>Collection id matching the <c>CollectionId</c> field in
    /// <c>KeystoneNaturalResources.blueprint.json</c>. Faction-agnostic
    /// — both Folktails and Ironteeth load this collection on top of
    /// their faction-specific natural-resource sets.</summary>
    private const string CollectionId = "KeystoneNaturalResources";

    #endregion

    #region ITemplateCollectionIdProvider

    /// <inheritdoc />
    public IEnumerable<string> GetTemplateCollectionIds() {
      yield return CollectionId;
    }

    #endregion

  }

}
