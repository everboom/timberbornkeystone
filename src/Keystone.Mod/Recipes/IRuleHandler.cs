using System.Collections.Generic;
using Keystone.Core.Biomes;
using Keystone.Core.Tiles;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// One per-class plug-in invoked by <see cref="ChunkRulesApplier"/>
  /// during each chunk's rule-application pass. Replaces the
  /// per-class <c>RollingSweepTicker</c>-based reconcilers that
  /// previously each owned their own tile sweep — handlers are now
  /// passive participants in a single shared sweep.
  ///
  /// <para><b>Lifecycle (per cycle).</b>
  /// <list type="number">
  ///   <item><see cref="OnCycleStart"/> — once, before any
  ///         <see cref="OnUnit"/> calls. Reset per-cycle scratch
  ///         state (e.g. Class A's "seen this cycle" tracker).</item>
  ///   <item><see cref="OnUnit"/> — once per (surface, active level)
  ///         across every surface the applier visits. The applier
  ///         resolves the surface's dominant biome per tile (Score-
  ///         pass gate + max-Score among passers), so adjacent
  ///         surfaces in the same chunk can be classified into
  ///         different biomes when their bilinearly-sampled values
  ///         differ. Handlers look up their class's recipes for the
  ///         <c>(biome, level)</c> bucket and decide what to do.</item>
  ///   <item><see cref="OnCycleComplete"/> — once, after every
  ///         scheduled chunk has been processed. Per-cycle bookkeeping
  ///         (e.g. Class A's despawn-unseen pass).</item>
  /// </list></para>
  ///
  /// <para><b><see cref="ShouldRun"/>.</b> Lets a handler short-circuit
  /// when it has nothing to do (no recipes registered). The applier
  /// skips all handler dispatch when no handler returns true, avoiding
  /// per-tile work on freshly-installed worlds with no Keystone
  /// content yet.</para>
  ///
  /// <para><b>Per-surface gating already done upstream.</b> The applier
  /// has already gated the surface on settled region, ecology field
  /// presence, planting marks, biome dominance at the tile (Score-pass
  /// + max-Score winner), and "investment ≥ level lower bound" before
  /// calling <see cref="OnUnit"/>. Handlers can assume those
  /// preconditions and skip re-checking.</para>
  /// </summary>
  public interface IRuleHandler {

    /// <summary>Reset any per-cycle scratch state. Called once per
    /// cycle, before the per-unit pass begins. Default no-op
    /// implementations are fine for handlers with no per-cycle
    /// state.</summary>
    void OnCycleStart();

    /// <summary>Handle one <c>(surface, biome, level)</c> pass.
    /// The applier guarantees the level is active for this surface's
    /// dominant biome (maturity ≥ <see cref="BiomeLevel.LowerMaturity"/>)
    /// before the call. Handlers usually look up their per-class
    /// recipes in the catalog and dispatch from there.
    ///
    /// <para><paramref name="progress"/> is the level's saturation
    /// fraction in <c>[0, 1]</c>:
    /// <c>clamp01((maturity - LowerMaturity) / (UpperMaturity - LowerMaturity))</c>.
    /// Spawn handlers multiply <see cref="BiomeLevel.Density"/> by it
    /// on <see cref="LevelDispatchMode.Deterministic"/> levels so
    /// activation ramps in linearly across the maturity range, and
    /// ignore it on <see cref="LevelDispatchMode.Stochastic"/> levels
    /// (every cycle is a fresh roll at full <c>Density</c>). Handlers
    /// that aren't maturity-driven at all (attrition's per-cycle
    /// Bernoulli rolls, for example) are free to ignore it.</para></summary>
    void OnUnit(SurfaceCoord surface, BiomeKind biome, BiomeLevel level, float progress);

    /// <summary>End-of-cycle hook. Class A uses this for the
    /// despawn-unseen pass. Default no-op is fine for one-shot spawners.</summary>
    void OnCycleComplete();

    /// <summary>End-of-tick hook. Fires after the applier finishes
    /// draining its scheduled work for the current Unity-tick. Used
    /// by handlers that want to flush per-tick aggregated counters
    /// (e.g. "events this tick") so the perf window's counter rows
    /// show events-per-tick rather than the meaningless average of
    /// per-event "1" records. Default no-op is fine for handlers
    /// without per-tick aggregation needs.</summary>
    void OnTickEnd() {}

    /// <summary>Return false to skip this handler for the cycle.
    /// Typical usage: "no recipes registered for my class, nothing
    /// to do." If every handler returns false, the applier can skip
    /// the per-tile dispatch entirely.</summary>
    bool ShouldRun();

    /// <summary>The set of <c>(biome, levelId)</c> buckets this
    /// handler has recipes for. Read once by
    /// <see cref="ChunkRulesApplier"/> to build a precomputed
    /// "which handlers care about this bucket" inverse map, so the
    /// per-surface dispatch loop can skip handlers that would
    /// no-op on a given <c>(biome, level)</c>. Enumerated once after
    /// recipes have loaded; do not return changing data on
    /// subsequent reads.
    /// <para>Duplicates within the enumeration are allowed — the
    /// applier de-dupes when building the map. Handlers with no
    /// recipes return an empty sequence.</para></summary>
    IEnumerable<(BiomeKind Biome, string LevelId)> ActiveBuckets { get; }

  }

}
