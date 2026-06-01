using System.Collections.Generic;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.SingletonSystem;
using Timberborn.TerrainSystem;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Precautionary watcher: when terrain is dug or filled at a column,
  /// or a <see cref="BlockObject"/> is placed on one, despawn any
  /// fauna whose current tile is on that column. Bridges the gap
  /// between the once-per-hour cluster self-check on
  /// <see cref="BaseFaunaAgent"/> and the once-per-day dawn reconcile:
  /// without this, a deer can briefly walk through air or a building
  /// can be raised over a cow without immediate cleanup.
  ///
  /// <para><b>Why column-only, not full coord.</b> Fauna live on a
  /// single surface within a region; their tile coord is
  /// 2D-effective. Any change to the column they're standing on —
  /// regardless of which Z the terrain or block ended up at — is
  /// grounds for caution because the surface they were on may have
  /// shifted or disappeared. The hourly cluster check (or next
  /// dawn) re-confirms whether a fauna actually belongs where it
  /// is, so over-eviction here is harmless: at worst we cull a
  /// fauna that the system would have culled anyway.</para>
  ///
  /// <para><b>What's not handled.</b> <c>BlockObjectUnsetEvent</c>
  /// is intentionally ignored — fauna shouldn't have been on a
  /// tile while a building was there in the first place, so a
  /// building's disappearance doesn't put a fauna at risk.</para>
  /// </summary>
  public sealed class FaunaTopologyChangeWatcher : ILoadableSingleton {

    private readonly EventBus _eventBus;
    private readonly ITerrainService _terrainService;
    private readonly KeystoneFaunaRegistry _registry;

    public FaunaTopologyChangeWatcher(
        EventBus eventBus,
        ITerrainService terrainService,
        KeystoneFaunaRegistry registry) {
      _eventBus = eventBus;
      _terrainService = terrainService;
      _registry = registry;
    }

    /// <inheritdoc />
    public void Load() {
      try {
        _eventBus.Register(this);
        _terrainService.TerrainHeightChanged += OnTerrainHeightChanged;
        KeystoneLog.Verbose(
            "[Keystone] FaunaTopologyChangeWatcher: subscribed to " +
            "BlockObjectSetEvent + ITerrainService.TerrainHeightChanged.");
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleError(
            "FaunaTopologyChangeWatcher.Load", "Subsystem failed", ex);
      }
    }

    /// <summary>Building placed: evict any fauna standing on a tile
    /// the building now occupies. Uses <see cref="PositionedBlocks.GetOccupiedCoordinates"/>
    /// to enumerate the building's footprint; multi-tile buildings
    /// cull at every column they cover.</summary>
    [OnEvent]
    public void OnBlockObjectSet(BlockObjectSetEvent e) {
      var blockObject = e.BlockObject;
      if (blockObject == null) return;
      // GetOccupiedCoordinates returns 3D coords; project to the
      // column set and cull once per unique (X, Y). A 4x4 building
      // would otherwise call DespawnAnyAtColumn 16 times with the
      // same column.
      var seen = new HashSet<(int, int)>();
      foreach (var coord in blockObject.PositionedBlocks.GetOccupiedCoordinates()) {
        if (!seen.Add((coord.x, coord.y))) continue;
        _registry.DespawnAnyAtColumn(coord.x, coord.y,
            $"BlockObject placed on column ({coord.x},{coord.y})",
            FaunaDespawnReason.BlockObjectPlaced);
      }
    }

    /// <summary>Terrain height changed at a column (player dug or
    /// filled). Cull regardless of direction: a fauna on a column
    /// whose surface just moved is suspect.</summary>
    private void OnTerrainHeightChanged(object sender, TerrainHeightChangeEventArgs args) {
      var change = args.Change;
      _registry.DespawnAnyAtColumn(change.Coordinates.x, change.Coordinates.y,
          $"terrain height changed at column ({change.Coordinates.x},{change.Coordinates.y})",
          FaunaDespawnReason.TerrainEdited);
    }

  }

}
