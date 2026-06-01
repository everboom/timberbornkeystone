namespace Keystone.Core.Ecology {

  /// <summary>
  /// Per-surface structural state cached by <c>TerrainSurveyor</c>.
  /// Only fields that are <b>expensive to compute</b> and <b>change
  /// only on structural events</b> (terrain edits, building placement)
  /// live here -- those are the fields the surveyor's dirty-column
  /// mechanism actually keeps coherent.
  ///
  /// <para><b>Why not the volatile inputs.</b> Earlier versions of
  /// this record also cached the per-surface ecological inputs
  /// (water depth / flow / moisture / contamination plus their
  /// boolean predicates). Those are <i>cheap</i> port lookups against
  /// the game's continuous water / moisture / contamination
  /// simulations and they change without any event the surveyor
  /// listens to. Caching them was duplicate state that silently went
  /// stale -- e.g., a flood added water but no terrain-height /
  /// building-set event fired, so the cache never refreshed and the
  /// downstream biome ticker read game-load values forever. The
  /// volatile fields are now read directly via <c>IWaterQuery</c> /
  /// <c>IMoistureQuery</c> / <c>IContaminationQuery</c> wherever
  /// needed.</para>
  ///
  /// <para><b>Pure data, no derived classification.</b> Surfaces
  /// don't carry a single "biome tag" because that collapses
  /// orthogonal axes into one bucket and forces an arbitrary
  /// priority. Classification (biome assignment, region grouping,
  /// inter-region transition characterisation) happens at the region
  /// level on top of these raw fields plus live port queries.</para>
  /// </summary>
  /// <param name="IsCave">True if the surface has terrain voxels above it (roofed by an overhang, stacked platform, or cantilevered stack).</param>
  /// <param name="IsSettled">True if the surface is part of player-placed infrastructure: a building sits directly on it, OR a path sits on it AND a lateral neighbor surface has a building. Trees, crops, and other natural block objects do not count -- only block objects with <c>BuildingSpec</c>.</param>
  /// <param name="IsBlocked">True if a natural impassable obstacle occupies this surface voxel (natural dam, blockage, geyser, overhang, reserve pile, etc.). Blocked surfaces are excluded from region membership entirely -- they are not part of any region, do not contribute to biome scoring, and are unreachable by fauna pathfinding. The Settled halo does not extend over a blocked tile.</param>
  public readonly record struct SurfaceSurvey(
      bool IsCave,
      bool IsSettled,
      bool IsBlocked);

}
