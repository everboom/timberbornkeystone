using System.Collections.Generic;
using Keystone.Core.Flourish;
using Keystone.Core.Sweep;
using Keystone.Core.Time;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Sweep;
using Timberborn.EntitySystem;

namespace Keystone.Mod.Flourish {

  /// <summary>
  /// Rolling sweep that rots dead flourishes out of the world. Once per
  /// decay cycle (<see cref="CycleDays"/> = one game-day) every live
  /// <see cref="KeystoneFlourish"/> in the <see cref="FlourishLifeStatus.Dead"/>
  /// state gets a <see cref="DeadFlourishDecay.DefaultDailyDeleteChance"/>
  /// (~10%) chance to be deleted. The work is amortised across the day's
  /// ticks by the <see cref="RollingSweep{TUnit}"/> base, so a map full of
  /// dead remains never spikes a single frame. (<see cref="RollingSweep{TUnit}"/>
  /// is the Core base; <see cref="RollingSweepTicker{TUnit}"/> adds the
  /// tick marker.)
  ///
  /// <para><b>Why this exists.</b> Attrition kills and badwater self-kills
  /// flip a flourish to Dead but leave its dead visual standing
  /// indefinitely; it only disappears if a spawn handler later reclaims
  /// the tile (see <see cref="KeystoneFlourish.IsDeadFlourish"/>). When the
  /// habitat stays unsuitable no respawn is coming, so dead growth would
  /// accumulate forever. A steady per-day deletion chance gives each dead
  /// flourish a finite half-life and lets marginal land clear itself.</para>
  ///
  /// <para><b>Enumeration.</b> <see cref="BuildSchedule"/> reads every
  /// flourish from <see cref="EntityComponentRegistry.GetEnabled{T}"/>
  /// (cheap; <see cref="KeystoneFlourish"/> is an
  /// <see cref="IRegisteredComponent"/>) and schedules only the dead ones.
  /// Flourishes that die mid-cycle are caught on the next cycle.</para>
  ///
  /// <para><b>RNG.</b> The per-flourish Bernoulli roll uses a
  /// non-deterministic <see cref="System.Random"/>, matching
  /// <c>AttritionHandler</c>: decay is stochastic cosmetic cleanup, not
  /// gameplay-critical state, so reload reproducibility isn't required.
  /// (The base sweep's schedule shuffle uses its own seeded RNG.)</para>
  /// </summary>
  public sealed class KeystoneFlourishDecayTicker
      : RollingSweepTicker<KeystoneFlourish> {

    #region Constants

    /// <summary>Decay cycle length in game-days. 1.0 = each dead
    /// flourish is rolled once per game-day, so
    /// <see cref="DeadFlourishDecay.DefaultDailyDeleteChance"/> reads
    /// directly as the per-day deletion chance.</summary>
    private const float CycleDays = 1f;

    #endregion

    #region Dependencies

    private readonly EntityComponentRegistry _registry;
    private readonly EntityService _entityService;
    private readonly System.Random _rng = new();

    #endregion

    #region Construction

    public KeystoneFlourishDecayTicker(
        IClock clock,
        PerfTracker perfTracker,
        EntityComponentRegistry registry,
        EntityService entityService)
        : base(clock, perfTracker, CycleDays) {
      _registry = registry;
      _entityService = entityService;
    }

    #endregion

    #region Sweep hooks

    /// <inheritdoc />
    protected override void BuildSchedule(List<KeystoneFlourish> schedule) {
      foreach (var flourish in _registry.GetEnabled<KeystoneFlourish>()) {
        if (flourish.CurrentLifeStatus == FlourishLifeStatus.Dead) {
          schedule.Add(flourish);
        }
      }
    }

    /// <inheritdoc />
    protected override void ProcessUnit(KeystoneFlourish flourish) {
      // The flourish reference is held for the whole cycle (up to a
      // game-day of ticks); another path can delete it in the interim —
      // a spawn handler reclaiming the tile is the common one. A
      // destroyed entity's GameObject reads Unity-null, so skip it.
      if (flourish.GameObject == null) return;
      // Re-check: nothing resurrects a dead flourish today, but guard
      // anyway so a future revive path can't be undone by a stale roll.
      if (flourish.CurrentLifeStatus != FlourishLifeStatus.Dead) return;

      var probability = DeadFlourishDecay.PerCycleProbability(
          DeadFlourishDecay.DefaultDailyDeleteChance, CurrentCycleDt);
      if (probability <= 0f) return;
      if (_rng.NextDouble() >= probability) return;

      _entityService.Delete(flourish);
    }

    #endregion

  }

}
