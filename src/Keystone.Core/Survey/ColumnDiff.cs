using System.Collections.Generic;
using Keystone.Core.Tiles;

namespace Keystone.Core.Survey {

  /// <summary>
  /// Result of <see cref="TerrainSurveyor.ResurveyColumn"/>: the set of
  /// surfaces that need region-level detach (because they disappeared or
  /// their <c>IsCave</c> flipped) and the set that need region-level
  /// attach (because they appeared or their <c>IsCave</c> flipped).
  ///
  /// <para>A surface whose <c>IsCave</c> changed in place appears in
  /// <b>both</b> collections -- the region-level effect of "detach from
  /// old, attach to new" is identical to a remove + re-add.</para>
  ///
  /// <para>Surfaces whose <c>(Z, IsCave)</c> are unchanged but whose
  /// pollable data (moisture, contamination, water, flow) changed do
  /// not appear at all -- those updates apply directly to
  /// <c>SurfaceSurvey</c> in place and don't affect region structure.</para>
  /// </summary>
  /// <param name="Detached">Surfaces to detach from their current region (gone, or IsCave flipped).</param>
  /// <param name="Attached">Surfaces to attach to a region (new, or IsCave flipped).</param>
  public readonly record struct ColumnDiff(
      IReadOnlyCollection<SurfaceCoord> Detached,
      IReadOnlyCollection<SurfaceCoord> Attached) {

    /// <summary>True if neither collection has any entries.</summary>
    public bool IsEmpty => Detached.Count == 0 && Attached.Count == 0;

    /// <summary>An empty diff -- nothing to do.</summary>
    public static readonly ColumnDiff Empty =
        new(System.Array.Empty<SurfaceCoord>(), System.Array.Empty<SurfaceCoord>());

  }

}
