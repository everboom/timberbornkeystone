namespace Keystone.Mod.Wellbeing {

  /// <summary>
  /// Mod-side configuration constants for Keystone's Nature
  /// integration. The per-faction Nature-source declarations
  /// (which buildings provide Nature, which biomes they offer)
  /// now live in
  /// <see cref="Keystone.Core.Buildings.Factions.FactionRegistry.AllNatureFactions"/>
  /// — aggregated from each <c>FactionX.NatureBuildings</c>
  /// contribution so adding a new faction is a single-file change.
  ///
  /// <para><b>Why these two constants stay in Mod.</b> They wire
  /// the Core data to Mod-specific spec types and runtime
  /// behaviour. <see cref="NeedIdPrefix"/> matches the asset names
  /// in <c>unity-assets/Keystone/Data/Needs/Need.Beaver.KeystoneNature.{biome}.blueprint.json</c>;
  /// <see cref="DefaultPointsPerHour"/> is the runtime rate that
  /// <see cref="KeystoneNatureSource"/> scales each cycle.</para>
  /// </summary>
  internal static class KeystoneNatureFactions {

    /// <summary>Need-id prefix. Each full id is formed by appending
    /// a biome name; the three resulting ids must match the asset
    /// names in
    /// <c>unity-assets/Keystone/Data/Needs/Need.Beaver.KeystoneNature.{Forest,Grassland,Wetland}.blueprint.json</c>.</summary>
    public const string NeedIdPrefix = "KeystoneNature.";

    /// <summary>Default per-hour rate at full Maturity scaling. The
    /// runtime component scales this down by the chunk's actual
    /// Maturity each cycle — see
    /// <see cref="KeystoneNatureSource"/>.</summary>
    public const float DefaultPointsPerHour = 4.0f;

  }

}
