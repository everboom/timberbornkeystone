using System.Collections.Generic;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Sweep;

namespace Keystone.Mod.Persistence {

  /// <summary>
  /// Per-region accumulator for <see cref="KnownValueKinds.RegionAgeDays"/>.
  /// Once per game-hour, walks every live region and adds the cycle's
  /// dt to its age value. Built on
  /// <see cref="RollingSweepTicker{TUnit}"/> so the work is amortised
  /// across the cycle's ticks (negligible at typical region counts,
  /// but consistent with the rest of the periodic ecology pipeline).
  ///
  /// <para><b>Hourly cadence vs continuous.</b> Region age has no
  /// meaningful sub-hour resolution -- nothing in the design reads
  /// it at finer granularity than that. The visible difference
  /// versus the previous per-tick model is that the displayed value
  /// updates in 1-game-hour increments instead of smoothly. Long-
  /// run accumulation is mathematically identical.</para>
  ///
  /// <para><b>Accumulator vs derivation.</b> The persisted value is
  /// the accumulator itself -- on reload, the cycle continues from
  /// whatever the value store was rehydrated to (it doesn't
  /// recompute from <c>now - region.TotalDaysAtCreation</c>). That
  /// means region age survives terrain edits cleanly: a split
  /// inherits the parent's accumulated age via the kept-id piece's
  /// existing entry; the orphan piece's entry is unset until next
  /// cycle adds dt for it -- which is fine, the orphan's
  /// <c>TotalDaysAtCreation</c> is also inherited so its "age since
  /// creation" is well-defined. A pure derivation would jump on every
  /// split as the orphans are reborn at "now", which is wrong.</para>
  /// </summary>
  public sealed class RegionValueTicker : RollingSweepTicker<RegionId> {

    /// <summary>Once per game-hour. Region age has no meaningful
    /// sub-hour resolution and aligning with the chunk biome ticker's
    /// cadence keeps the periodic-ecology pipeline uniform.</summary>
    private const float CycleDays = 1f / 24f;

    private readonly RegionService _regions;
    private readonly RegionValueStore _values;

    public RegionValueTicker(
        RegionService regions,
        RegionValueStore values,
        IClock clock,
        PerfTracker perf)
        : base(clock, perf, CycleDays) {
      _regions = regions;
      _values = values;
    }

    /// <inheritdoc />
    protected override void BuildSchedule(List<RegionId> schedule) {
      foreach (var region in _regions.All) {
        schedule.Add(region.Id);
      }
    }

    /// <inheritdoc />
    protected override void ProcessUnit(RegionId regionId) {
      var existing = _values.Get(regionId, KnownValueKinds.RegionAgeDays) ?? 0f;
      _values.Set(regionId, KnownValueKinds.RegionAgeDays, existing + CurrentCycleDt);
    }

  }

}
