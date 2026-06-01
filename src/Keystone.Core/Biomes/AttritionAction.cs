namespace Keystone.Core.Biomes {

  /// <summary>
  /// Action an <see cref="AttritionRecipe"/> applies to a hit entity.
  ///
  /// <para>The Kill / Destroy split is deliberate: killed-but-not-
  /// destroyed flourishes are intentional ambient flavour. A
  /// contamination biome that wipes out an area should leave visible
  /// dead plants behind, not vanish them. Destroy is for cases where
  /// the design wants the tile freed (e.g. river current sweeping
  /// debris away).</para>
  /// </summary>
  public enum AttritionAction {

    /// <summary>
    /// Flip <c>KeystoneFlourish.LifeStatus</c> to <c>Dead</c>. Entity
    /// stays in the world; visual switches to the blueprint's
    /// <c>#Dead</c> leaf. No-op on entities without a
    /// <c>KeystoneFlourish</c> component (a recipe targeting Class A
    /// decorations, for example -- Class A has no life-status axis).
    /// </summary>
    Kill,

    /// <summary>
    /// Remove the entity entirely via <c>EntityService.Delete</c>.
    /// </summary>
    Destroy,

  }

}
