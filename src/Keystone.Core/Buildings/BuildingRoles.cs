using System;

namespace Keystone.Core.Buildings {

  /// <summary>
  /// Bitwise-combinable role tags for player-placed buildings. A single
  /// blueprint commonly carries several roles -- Emberpelts lodges are
  /// <c>Path | Dwelling</c>, a manufactory is <c>Workplace | Industry</c>,
  /// a water wheel is <c>Mechanical | WaterInfra</c>. Querying "is this
  /// building X" is therefore a flag test, never an enum equality.
  ///
  /// <para><b>Distinct from <see cref="BuildingKind"/>.</b>
  /// <see cref="BuildingKind"/> is the structural per-voxel signal used by
  /// the Settled-region halo (Building / Path / None). This is the
  /// per-blueprint role description used by the cursor display, ecology
  /// rules, and any consumer that wants "what role does this building
  /// fill" rather than "should this voxel anchor settlement."</para>
  ///
  /// <para><b>Detection lives in the loader, not here.</b> Mapping
  /// capability names (e.g. <c>"Manufactory"</c>, <c>"PlanterBuilding"</c>)
  /// to flags is the loader's responsibility -- Core only declares the
  /// flag set so consumers can pattern-match against it.</para>
  /// </summary>
  [Flags]
  public enum BuildingRoles {

    /// <summary>No detected roles. Equivalent to <see cref="Decoration"/> at the consumer level.</summary>
    None = 0,

    /// <summary>Beavers walk through this voxel (carries a Path component / PathSpec).</summary>
    Path = 1 << 0,

    /// <summary>Beavers live here (Dwelling component). Often co-occurs with Path on hybrid lodges.</summary>
    Dwelling = 1 << 1,

    /// <summary>Beavers work here in a generic sense (Workplace component). Industry implies Workplace, but plain Workplace ≠ Industry (foresters, gatherers).</summary>
    Workplace = 1 << 2,

    /// <summary>Recipe-driven production (Manufactory component). Always also <see cref="Workplace"/> in vanilla.</summary>
    Industry = 1 << 3,

    /// <summary>Plants and tends a flora group (PlanterBuildingSpec). Foresters, farmhouses, greenhouses.</summary>
    Farming = 1 << 4,

    /// <summary>Holds goods (StockpileSpec / FixedStockpileSpec / similar).</summary>
    Storage = 1 << 5,

    /// <summary>Touches the water sim -- pumps, sluices, water sources, water-input consumers.</summary>
    WaterInfra = 1 << 6,

    /// <summary>Connects to the mechanical-power network (MechanicalNodeSpec).</summary>
    Mechanical = 1 << 7,

    /// <summary>One of the endgame Wonder buildings (FactionWonderSpec).</summary>
    Wonder = 1 << 8,

    /// <summary>Emits an area-of-effect bonus (RangedEffectBuilding component).</summary>
    RangedEffect = 1 << 9,

    /// <summary>Settlement nucleus -- DistrictCenter, BuilderHub.</summary>
    DistrictAnchor = 1 << 10,

    /// <summary>Computed catchall: no other meaningful role detected. Set explicitly by the loader so consumers can opt in to "is this purely decorative" without re-running the all-zero check.</summary>
    Decoration = 1 << 11,

  }

}
