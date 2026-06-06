using System.Collections.Generic;
using Keystone.Core.Flourish;
using Keystone.Core.Sweep;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Sweep;
using Timberborn.EntitySystem;

namespace Keystone.Mod.Overgrowth {

  /// <summary>
  /// Rolling sweep that clears <b>dead</b> overgrowth out of the world —
  /// the overgrowth counterpart of <c>KeystoneFlourishDecayTicker</c>,
  /// reusing the same per-day deletion chance
  /// (<see cref="DeadFlourishDecay.DefaultDailyDeleteChance"/>, ~10%).
  /// Once per game-day every dead <see cref="KeystoneOvergrowth"/> rolls
  /// for removal; on a hit the overgrowth is <see cref="KeystoneOvergrowth.Clear"/>ed
  /// (decoration despawned, tree back to <b>barren</b>) — the host tree is
  /// NOT deleted. A barren tree can overgrow again later if biome maturity
  /// recovers (cyclical recovery).
  ///
  /// <para>Enumeration via <see cref="EntityComponentRegistry.GetEnabled{T}"/>
  /// (<see cref="KeystoneOvergrowth"/> is an
  /// <see cref="IRegisteredComponent"/>); work is amortised across the
  /// day's ticks by the <see cref="RollingSweepTicker{TUnit}"/> base.</para>
  /// </summary>
  public sealed class KeystoneOvergrowthDecayTicker
      : RollingSweepTicker<KeystoneOvergrowth> {

    /// <summary>Decay cycle length in game-days. 1.0 so the daily delete
    /// chance reads directly.</summary>
    private const float CycleDays = 1f;

    private readonly EntityComponentRegistry _registry;
    private readonly System.Random _rng = new();

    public KeystoneOvergrowthDecayTicker(
        IClock clock,
        PerfTracker perfTracker,
        EntityComponentRegistry registry)
        : base(clock, perfTracker, CycleDays) {
      _registry = registry;
    }

    /// <inheritdoc />
    protected override void BuildSchedule(List<KeystoneOvergrowth> schedule) {
      foreach (var overgrowth in _registry.GetEnabled<KeystoneOvergrowth>()) {
        if (overgrowth.IsDead) {
          schedule.Add(overgrowth);
        }
      }
    }

    /// <inheritdoc />
    protected override void ProcessUnit(KeystoneOvergrowth overgrowth) {
      // Held for up to a game-day of ticks; another path may have cleared
      // or revived it. Re-check before rolling.
      if (overgrowth.GameObject == null) return;
      if (!overgrowth.IsDead) return;

      var probability = DeadFlourishDecay.PerCycleProbability(
          DeadFlourishDecay.DefaultDailyDeleteChance, CurrentCycleDt);
      if (probability <= 0f) return;
      if (_rng.NextDouble() >= probability) return;

      // Remove the overgrowth only — the tree stays (returns to barren).
      overgrowth.Clear();
    }

  }

}
