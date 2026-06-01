using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Wellbeing {

  /// <summary>
  /// Marker spec applied to buildings that ARE settlement
  /// infrastructure (their voxel counts as settled) but whose
  /// footprint shouldn't propagate the usual 1-tile settled aura to
  /// their 8 lateral neighbors. The middle tier between the default
  /// "full settle + aura" and <see cref="KeystoneEcologyTransparentSpec"/>
  /// (which suppresses the voxel from settling entirely).
  ///
  /// <para>Used by <see cref="Adapters.BuildingQueryAdapter"/>: when a
  /// block object carries this spec, the adapter classifies the voxel
  /// as <c>BuildingKind.BuildingNoAura</c> rather than
  /// <c>BuildingKind.Building</c>. The surveyor's self-check still
  /// treats the voxel as settled; the aura check skips it.</para>
  ///
  /// <para><b>Why this exists.</b> Lanterns, scarecrows, weathervanes,
  /// beehives — small built things whose physical footprint is genuinely
  /// settled infrastructure (a player placed them, they occupy a tile)
  /// but whose 1-tile aura sterilizing a 3×3 chunk area conflicts with
  /// the player's mental model. The point-sized building should not
  /// make eight surrounding tiles ecologically inert. Applied via
  /// blueprint overlay on the listed buildings.</para>
  ///
  /// <para><b>Distinct from <see cref="KeystoneEcologyTransparentSpec"/>.</b>
  /// Transparent = "the surveyor doesn't see this building at all."
  /// NoAura = "the surveyor sees and counts the building, but it
  /// doesn't influence its neighbors." Contemplation buildings sit in
  /// the wild and should be invisible (transparent); decorations sit
  /// on player land and ARE settlement, just with a tight footprint
  /// (no-aura). Tagging a building with both is meaningless — the
  /// surveyor never reaches the no-aura check for a voxel whose
  /// transparency already excludes it.</para>
  /// </summary>
  public record KeystoneEcologyNoAuraSpec : ComponentSpec;

}
