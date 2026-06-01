using System;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Recipe filter that admits river-bank tiles in shallow water: the
  /// surface sits at the bottom of a step-up (a neighbour rises above it
  /// -- typical of a tile against the river's wall) but is not itself a
  /// cliff top (it does not look down on a neighbour), AND sits under a
  /// water column whose voxel height is within
  /// <c>[<see cref="MinWaterVoxelsAbove"/>, <see cref="MaxWaterVoxelsAbove"/>]</c>
  /// (both 1 — exactly one voxel).
  ///
  /// <para>The bank geometry excludes waterfall edges, where the player
  /// drops water over a cliff: those tiles are both above one neighbour
  /// (the basin below) and below another (the bank wall above), which is
  /// not the visual the bank-decoration wants. Backed by
  /// <see cref="CliffProximity.IsBelowNeighbor"/> and
  /// <see cref="CliffProximity.IsAboveNeighbor"/>.</para>
  ///
  /// <para><b>Why the water-depth band.</b> The river Class B minis carry
  /// vanilla <c>FloodableNaturalResourceSpec</c> with
  /// <c>MinWaterHeight = MaxWaterHeight = 1</c>. Vanilla flips a floodable
  /// resource to its wilted <c>#Dying</c> / <c>#Dead</c> mesh the instant
  /// its water column leaves that band — too deep (<c>flooded</c>) or too
  /// shallow (<c>dry</c>) — and <c>WaterObject.WaterAboveBase</c> is
  /// <c>ceil(depth)</c>, an integer. So a bank tile under a 2-deep channel
  /// reads as flooded, and a dry bank tile (0 voxels) reads as dry; both
  /// render dead. Requiring exactly one voxel here keeps spawns inside the
  /// healthy band. This is the only consumer of this filter, so the band
  /// lives here rather than in a separate filter + blueprint re-point; if
  /// a bank-without-depth use ever appears, split it back out into its own
  /// <see cref="IRecipeFilter"/>.</para>
  ///
  /// <para><b>Spawn-time only.</b> The band is checked as the flourish
  /// spawns; it can't stop a flourish from flooding (or drying) later if
  /// the player changes the water after placement — that's the deferred
  /// Keystone-owned floodable component on the ROADMAP backlog.</para>
  /// </summary>
  public sealed class RiverBankRecipeFilter : IRecipeFilter {

    /// <summary>Inclusive water-voxel band — <c>ceil</c> of the continuous
    /// depth — the surface must sit under to be eligible. Both bounds are
    /// 1, mirroring the river minis'
    /// <c>FloodableNaturalResourceSpec.MinWaterHeight</c> /
    /// <c>MaxWaterHeight</c> of 1: vanilla considers exactly one voxel of
    /// water neither dry nor flooded. Below the min reads as dry, above
    /// the max as flooded — both wilt.</summary>
    private const int MinWaterVoxelsAbove = 1;
    private const int MaxWaterVoxelsAbove = 1;

    private readonly CliffProximity _cliffs;
    private readonly IWaterQuery _water;

    public RiverBankRecipeFilter(CliffProximity cliffs, IWaterQuery water) {
      _cliffs = cliffs;
      _water = water;
    }

    /// <inheritdoc />
    public string Name => "RiverBank";

    /// <inheritdoc />
    public bool IsEligible(SurfaceCoord surface) {
      // Bank geometry: below a neighbour, not above one.
      if (!_cliffs.IsBelowNeighbor(surface) || _cliffs.IsAboveNeighbor(surface)) {
        return false;
      }
      // Water-depth band. ceil(depth) is exactly vanilla's integer
      // WaterAboveBase for a base-level surface, so requiring it inside
      // [1, 1] is "neither dry nor flooded" under the mini's
      // MinWaterHeight = MaxWaterHeight = 1.
      var voxelsAbove = (int)Math.Ceiling(_water.WaterDepthAt(surface));
      return voxelsAbove >= MinWaterVoxelsAbove && voxelsAbove <= MaxWaterVoxelsAbove;
    }

  }

}
