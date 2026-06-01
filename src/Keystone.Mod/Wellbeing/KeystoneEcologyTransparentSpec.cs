using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Wellbeing {

  /// <summary>
  /// Marker spec applied to vanilla buildings that should be invisible
  /// to Keystone's ecology survey -- their tiles do not count as
  /// "settled" for the purpose of region indexing and per-chunk biome
  /// scoring.
  ///
  /// <para>Used by <see cref="Adapters.BuildingQueryAdapter"/>: when a
  /// block object carries this spec, the adapter skips it during
  /// classification, exactly like it already skips natural elements.
  /// The building still exists from the player's and the game's
  /// perspective; only Keystone's surveyor treats the voxel as
  /// unoccupied.</para>
  ///
  /// <para><b>Why this exists.</b> Contemplation Spot and Lido sit
  /// inside (or at the edge of) preserved nature and contribute to
  /// beaver wellbeing based on the surrounding biome. Without this
  /// exemption, placing one would flip the chunk to settled, freeze
  /// biome Suitability/Maturity accrual, and break the very feedback
  /// the building is meant to surface. Applied via blueprint overlay
  /// on the vanilla building blueprints.</para>
  /// </summary>
  public record KeystoneEcologyTransparentSpec : ComponentSpec;

}
