using Keystone.Core.Persistence;
using Keystone.Core.Regions;
using Keystone.Mod.Diagnostics;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.Persistence {

  /// <summary>
  /// Forwards <see cref="RegionService"/> lifecycle events into
  /// <see cref="RegionValueStore"/> so accumulated per-region values
  /// follow topology changes.
  ///
  /// <para><b>Region-level only — chunk stores are NOT handled here.</b>
  /// Per-chunk data (<see cref="ChunkValueStore"/> /
  /// <see cref="ChunkDataStore"/>) used to be moved here too, via the
  /// same Inherit/MergeFrom/Remove calls, but that event-driven approach
  /// operated on whole regions at once and could strand data (split copied
  /// the parent's chunks wholesale onto the orphan) or wipe it (removal
  /// dropped every chunk a dying region held). Chunk data is now keyed by
  /// its <c>(X, Y, Z)</c> footprint and re-bound by
  /// <c>Keystone.Core.Persistence.ChunkReconciler</c> after each topology
  /// flush (see <c>RegionUpdater.Flush</c>): each chunk follows whichever
  /// region physically owns its footprint, which is strictly more correct.
  /// Region values have no footprint to reconcile against, so they remain
  /// event-driven here.</para>
  ///
  /// <para>On split, the orphan inherits the parent's region values
  /// (<c>Inherit</c>) — accumulated history carries forward instead of
  /// resetting. On merge, the loser's region values move onto the survivor
  /// (<c>MergeFrom</c>, survivor-wins). On removal, the dead region's
  /// region values are dropped so they don't leak in the save or
  /// re-activate under a recycled id.</para>
  ///
  /// <para><b>Merge policy is provisional.</b> Survivor-wins is the
  /// simplest non-destructive choice; per-kind semantics (max, sum,
  /// size-weighted average) may suit some accumulators better.
  /// Revisit when concrete value producers have opinions.</para>
  ///
  /// <para>Lives in the Mod project rather than Core because Bindito
  /// wiring (the <see cref="ILoadableSingleton.Load"/> hook) is the
  /// natural place to subscribe. The actual subscription targets
  /// (the store's <c>Inherit</c> and <c>MergeFrom</c> methods) are in
  /// Core and are independently testable.</para>
  /// </summary>
  public sealed class RegionValueLifecycleHandler : ILoadableSingleton {

    #region Fields

    private readonly RegionService _regions;
    private readonly RegionValueStore _regionValues;

    #endregion

    #region Construction

    public RegionValueLifecycleHandler(
        RegionService regions,
        RegionValueStore regionValues) {
      _regions = regions;
      _regionValues = regionValues;
    }

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      try {
        _regions.RegionSplit += OnRegionSplit;
        _regions.RegionMerged += OnRegionMerged;
        _regions.RegionRemoved += OnRegionRemoved;
      } catch (System.Exception ex) {
        Diagnostics.LifecycleGuard.HandleError("RegionValueLifecycleHandler.Load", "Subsystem failed", ex);
      }
    }

    private void OnRegionSplit(RegionId parent, RegionId orphan) {
      _regionValues.Inherit(parent, orphan);
    }

    private void OnRegionMerged(RegionId loser, RegionId survivor) {
      _regionValues.MergeFrom(loser, survivor);
    }

    private void OnRegionRemoved(RegionId id) {
      _regionValues.RemoveAllValuesFor(id);
    }

    #endregion

  }

}
