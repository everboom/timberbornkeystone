using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Core.Regions;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Runs a map-wide <see cref="ChunkReconciler"/> sweep as a DRY RUN and
  /// reports what a real sweep would do. Manual, dev-facing — the backstop
  /// to the automatic per-flush sweep in <c>RegionUpdater.Flush</c> (which
  /// is scoped to the regions a topology change touched).
  ///
  /// <para><b>Read-only.</b> Runs <c>dryRun: true</c>, so it counts without
  /// re-keying or dropping anything — a diagnostic must not mutate game
  /// state, and re-running it can't churn data. Logs the counts to
  /// Player.log so the result survives the session for later diagnosis.</para>
  ///
  /// <para><b>Result interpretation.</b>
  /// <list type="bullet">
  ///   <item><b>Fail</b> — only when a chunk holding accumulated Maturity
  ///   would be dropped (no live region at its footprint+Z). That is the
  ///   sole real ecology-history loss.</item>
  ///   <item><b>Pass</b> — otherwise. A non-zero re-home count is normal:
  ///   genuinely stranded chunks (keyed region gone from the footprint) get
  ///   re-homed to their footprint owner. (Valid minority co-owners of a
  ///   boundary-straddling chunk are KEPT by the reconciler, so they no
  ///   longer show up here.) Empty drops are benign churn likewise.</item>
  /// </list></para>
  /// </summary>
  internal sealed class ChunkReconciliationSelfTest : IKeystoneSelfTest {

    private readonly ChunkReconciler _reconciler;
    private readonly RegionService _regions;

    public ChunkReconciliationSelfTest(ChunkReconciler reconciler, RegionService regions) {
      _reconciler = reconciler;
      _regions = regions;
    }

    /// <inheritdoc />
    public string Name => "Chunk reconciliation (full sweep)";

    /// <inheritdoc />
    public string Category => "Persistence";

    /// <inheritdoc />
    public SelfTestResult Run() {
      // Precompute the (chunkXY, z) -> owner map in one surface pass so the
      // map-wide sweep is O(1) per chunk instead of a footprint walk each —
      // the per-chunk walk is what made this self-test slow on a developed
      // map. The index is a point-in-time snapshot, built immediately
      // before the single sweep and discarded after.
      var ownerIndex = _regions.BuildChunkFootprintOwnerIndex(RegionEcologyField.ChunkSize);
      var r = _reconciler.ReconcileFromDataStore(
          scope: null, ownerOverride: new PrecomputedChunkOwnerQuery(ownerIndex),
          dryRun: true);
      var detail =
          $"  scanned {r.Scanned}, kept {r.Kept}, re-homed {r.Rehomed} (stranded), " +
          $"dropped {r.HomelessDropped} ({r.HomelessDroppedWithMaturity} with maturity, " +
          $"{r.HomelessDroppedEmpty} empty), collisions {r.CollisionsResolved}";

      // Log so the result lands in Player.log, not just the panel.
      KeystoneLog.Info($"[Keystone] Chunk reconciliation self-test (dry run): {detail.Trim()}.");

      // Only real ecology loss fails. Re-homes are expected (genuinely
      // stranded chunks moving to their footprint owner); empty drops are
      // benign churn. Valid minority co-owners are KEPT, not re-homed.
      if (r.HomelessDroppedWithMaturity > 0) {
        return SelfTestResult.Fail(
            $"{r.HomelessDroppedWithMaturity} chunk(s) holding accumulated maturity would be dropped — real ecology history lost",
            detail);
      }
      if (r.Rehomed > 0 || r.HomelessDroppedEmpty > 0) {
        return SelfTestResult.Pass(
            $"{r.Scanned} chunk(s); {r.Rehomed} stranded to re-home, " +
            $"{r.HomelessDroppedEmpty} empty to sweep (no maturity lost)");
      }
      return SelfTestResult.Pass($"{r.Scanned} chunk(s), all correctly bound");
    }

  }

}
