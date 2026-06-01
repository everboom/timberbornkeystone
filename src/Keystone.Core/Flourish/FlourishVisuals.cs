namespace Keystone.Core.Flourish {

  /// <summary>Life phase of a Keystone flourish. Manual axis — no
  /// auto-progression. Move via the Mod-side <c>KeystoneFlourish.SetPhase</c>.</summary>
  public enum FlourishPhase {

    /// <summary>Immature; vanilla "Seedling" stage.</summary>
    Seedling,

    /// <summary>Adult; vanilla "Mature" stage. Default.</summary>
    Mature,

    /// <summary>Special: terminal stump remains, used for trees after
    /// cutting. Has no <see cref="FlourishLifeStatus"/> /
    /// <see cref="FlourishHealth"/> sub-distinction — the blueprint
    /// exposes a single <c>#Models/Stump</c> child with no nested
    /// hierarchy.</summary>
    Stump,

  }

  /// <summary>Whether the flourish is currently alive or dead. Manual
  /// axis — the auto-wiring from vanilla Watered/Floodable does not
  /// flip this. Move via the Mod-side <c>KeystoneFlourish.SetLifeStatus</c>.</summary>
  public enum FlourishLifeStatus {

    /// <summary>Living; visuals reflect <see cref="FlourishHealth"/>.
    /// Default.</summary>
    Alive,

    /// <summary>Dead; visuals show the phase's <c>#Dead</c> leaf
    /// regardless of <see cref="FlourishHealth"/>.</summary>
    Dead,

  }

  /// <summary>Current health within an alive flourish. Auto axis on
  /// the Mod side — driven by <c>DyingNaturalResource</c> events from
  /// vanilla <c>WateredNaturalResourceSpec</c> /
  /// <c>FloodableNaturalResourceSpec</c>. Ignored when
  /// <see cref="FlourishLifeStatus"/> is
  /// <see cref="FlourishLifeStatus.Dead"/>.</summary>
  public enum FlourishHealth {

    /// <summary>Well-watered; visuals show the <c>#Alive</c> leaf.
    /// Default.</summary>
    Healthy,

    /// <summary>Drying out; visuals show the <c>#Dying</c> leaf.</summary>
    Dry,

  }

  /// <summary>
  /// The seven visual leaves a Keystone flourish blueprint can carry.
  /// Pure-data identifier consumed by Mod-side code that maps each
  /// leaf to a <c>GameObject</c> under <c>#Models</c>. Pure axis here;
  /// Mod-side hierarchy resolution lives in <c>KeystoneFlourish</c>.
  /// </summary>
  public enum FlourishVisualLeaf {

    SeedlingAlive,
    SeedlingDying,
    SeedlingDead,
    MatureAlive,
    MatureDying,
    MatureDead,

    /// <summary>Single mesh; phase=<see cref="FlourishPhase.Stump"/>
    /// ignores life-status and health.</summary>
    Stump,

  }

  /// <summary>
  /// Pure rules over the three flourish axes. Extracted from the
  /// Mod-side <c>KeystoneFlourish</c> so the visual-leaf mapping and
  /// the per-tick badwater self-kill predicate can be unit-tested
  /// without Unity / Timberborn coupling.
  /// </summary>
  public static class FlourishVisuals {

    /// <summary>Water-column contamination value above which an alive
    /// flourish standing in water self-kills on its next tick. Set
    /// just above zero so any meaningful badwater pool kills on
    /// contact, while clean water (which reads 0 here) leaves the
    /// entity unharmed.
    ///
    /// <para>Distinct from
    /// <see cref="Ecology.Fields.WaterContamination.Threshold"/>
    /// (which is the badwater-fraction cutoff for the
    /// <c>EcologyChannel.WaterContamination</c> aggregate): this
    /// constant is the per-tile self-kill threshold, intentionally
    /// stricter than the chunk-level aggregator. The per-tile signal
    /// is precise where the chunk aggregate is bilinear-sample-fuzzy
    /// near boundaries — a flourish in clearly-toxic water shouldn't
    /// survive because the chunk's average contamination dilutes it
    /// below the per-cycle Bernoulli's <c>ScaleMin</c>.</para></summary>
    public const float BadwaterContaminationThreshold = 0.1f;

    /// <summary>Map the three-axis state to the leaf the blueprint
    /// should display. Returns <c>null</c> for combinations the
    /// hierarchy doesn't enumerate (only one currently:
    /// <see cref="FlourishPhase.Stump"/> with a non-default
    /// life-status / health is undefined; Stump ignores those axes
    /// and returns <see cref="FlourishVisualLeaf.Stump"/>).
    ///
    /// <para><b>Routing rules.</b>
    /// <list type="number">
    ///   <item><see cref="FlourishPhase.Stump"/> always routes to
    ///         <see cref="FlourishVisualLeaf.Stump"/> regardless of
    ///         the other axes.</item>
    ///   <item><see cref="FlourishLifeStatus.Dead"/> routes to the
    ///         phase's <c>#Dead</c> leaf, ignoring health.</item>
    ///   <item>Otherwise (Alive), health picks between
    ///         <c>#Alive</c> (Healthy) and <c>#Dying</c> (Dry).</item>
    /// </list></para>
    /// </summary>
    public static FlourishVisualLeaf? LeafFor(
        FlourishPhase phase,
        FlourishLifeStatus lifeStatus,
        FlourishHealth health) {
      // Stump phase ignores life-status and health — single mesh.
      if (phase == FlourishPhase.Stump) return FlourishVisualLeaf.Stump;

      // Dead life-status routes to the phase's #Dead leaf regardless
      // of health. Keystone-Dead means visually-dead.
      if (lifeStatus == FlourishLifeStatus.Dead) {
        return phase switch {
          FlourishPhase.Seedling => FlourishVisualLeaf.SeedlingDead,
          FlourishPhase.Mature => FlourishVisualLeaf.MatureDead,
          _ => null,
        };
      }

      // Alive: health picks between #Alive (healthy) and #Dying (dry).
      return (phase, health) switch {
        (FlourishPhase.Seedling, FlourishHealth.Healthy) => FlourishVisualLeaf.SeedlingAlive,
        (FlourishPhase.Seedling, FlourishHealth.Dry) => FlourishVisualLeaf.SeedlingDying,
        (FlourishPhase.Mature, FlourishHealth.Healthy) => FlourishVisualLeaf.MatureAlive,
        (FlourishPhase.Mature, FlourishHealth.Dry) => FlourishVisualLeaf.MatureDying,
        _ => null,
      };
    }

    /// <summary>Per-tick badwater self-kill predicate. Returns
    /// <c>true</c> iff an alive flourish standing in water with
    /// contamination above the badwater threshold should flip to
    /// <see cref="FlourishLifeStatus.Dead"/>.
    ///
    /// <para>Dead flourishes never re-evaluate; flourishes outside
    /// water (depth ≤ 0) are unaffected; clean water (contamination
    /// ≤ threshold) leaves the flourish alive. The comparison on
    /// contamination is strict (<c>&gt;</c>) — exactly-at-threshold
    /// is treated as still survivable. Use
    /// <see cref="BadwaterContaminationThreshold"/> for the production
    /// threshold; callers can pass a different value for testing.</para></summary>
    public static bool ShouldDieFromBadwater(
        FlourishLifeStatus current,
        float waterDepth,
        float waterContamination,
        float threshold) {
      if (current == FlourishLifeStatus.Dead) return false;
      if (waterDepth <= 0f) return false;
      if (waterContamination <= threshold) return false;
      return true;
    }

  }

}
