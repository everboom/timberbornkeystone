using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Marker spec attached to aquatic fauna blueprints (e.g.
  /// <c>KeystoneFish1</c>) to wire the continuous-swim agent. The
  /// decorator binding in <c>KeystoneTemplateModuleProvider</c>
  /// attaches the <see cref="KeystoneAquaticAgent"/> component at
  /// blueprint-to-prefab time. Mirrors the
  /// <see cref="KeystoneFaunaAgentSpec"/> pattern for land fauna but
  /// drives a much simpler agent: no idle state, no clip switching,
  /// just A* between water-tile destinations with the
  /// <c>"Default"</c> swim loop on permanently.
  ///
  /// <para>Target biomes are hardcoded in the agent today (Wetland and
  /// Lake). Promote to a spec field once a fish wants different
  /// preferences (badwater dweller, river-only flow-following, etc.).</para>
  /// </summary>
  public record KeystoneAquaticAgentSpec : ComponentSpec {

    /// <summary>World-movement speed in tiles per real-time second.
    /// Default <c>0.5</c> — slower than land fauna because water reads
    /// as a more viscous medium and fish are visually smaller relative
    /// to a tile. Per-species override by setting this field on the
    /// blueprint.</summary>
    [Serialize] public float WorldSpeedTilesPerSec { get; init; } = 0.5f;

    /// <summary>Minimum water depth (in voxel units) at a tile for the
    /// fish to consider it walkable. Default <c>0.1</c> is permissive
    /// enough that even shallow Wetland tiles (the
    /// <see cref="Keystone.Core.Biomes.BiomeKind.Wetland"/> biome is
    /// "shallow low-flow water" by definition) pass the gate. Per-
    /// species can raise this for fish that need deeper water (e.g.
    /// pelagic Lake-dwellers).</summary>
    [Serialize] public float MinWaterDepth { get; init; } = 0.1f;

    /// <summary>Vertical offset from the water surface. Default
    /// <c>-0.1</c> sits the fish just below the waterline so the fin/
    /// body breaks the surface without floating clearly above it.
    /// Positive values raise the fish out of the water; more negative
    /// values submerge further.</summary>
    [Serialize] public float AboveSurfaceLift { get; init; } = -0.1f;

  }

}
