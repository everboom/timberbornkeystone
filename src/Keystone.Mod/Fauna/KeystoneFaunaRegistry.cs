using System.Collections.Generic;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Visualization;
using Timberborn.EntitySystem;
using Timberborn.Navigation;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Singleton tracking live Keystone fauna agents. Two distinct
  /// uses:
  /// <list type="bullet">
  ///   <item>Dusk teardown — every tracked agent gets deleted in one
  ///         pass via <see cref="DespawnAll"/>.</item>
  ///   <item>Dawn capacity reconciliation — the day-cycle handler
  ///         iterates <see cref="Entries"/>, groups fauna by
  ///         (cluster, species), and culls the oldest entries via
  ///         <see cref="Despawn"/> when a group overshoots its
  ///         per-recipe capacity.</item>
  /// </list>
  ///
  /// <para>Each entry carries the blueprint name and a monotonic
  /// spawn sequence — those are the two pieces of metadata the
  /// capacity reconciler needs beyond the entity handle itself.
  /// Position is read from the agent's <see cref="IFaunaPositioning"/>
  /// implementation at query time, so the registry doesn't have to
  /// re-sync as fauna move.</para>
  ///
  /// <para><b>No save/load.</b> Live agents don't persist. A saved
  /// game comes back with an empty registry and waits for the next
  /// dawn to populate.</para>
  /// </summary>
  public sealed class KeystoneFaunaRegistry {

    /// <summary>One registered fauna entity. <see cref="Sequence"/>
    /// is monotonically increasing across the session — culling by
    /// "oldest first" sorts ascending. <see cref="Position"/> is the
    /// agent's position-reporter; may be null for entities that
    /// don't carry one of the Keystone fauna agent components (dev-
    /// placed without a spec, future test stubs, etc.).
    /// <see cref="PersistsOvernight"/> controls whether
    /// <see cref="DespawnAll"/> at dusk skips this entry; aquatic
    /// fauna set it true so fish live across day cycles, with the
    /// dawn capacity-reconcile pass managing their numbers.</summary>
    public readonly struct Entry {
      public readonly EntityComponent Entity;
      public readonly IFaunaPositioning? Position;
      public readonly string BlueprintName;
      public readonly int Sequence;
      public readonly bool PersistsOvernight;
      public Entry(EntityComponent entity, IFaunaPositioning? position, string blueprintName, int sequence, bool persistsOvernight) {
        Entity = entity;
        Position = position;
        BlueprintName = blueprintName;
        Sequence = sequence;
        PersistsOvernight = persistsOvernight;
      }
    }

    private readonly EntityService _entityService;
    private readonly KeystoneVisibilityHider _visibilityHider;
    private readonly List<Entry> _live = new();
    private int _nextSequence;

    public KeystoneFaunaRegistry(
        EntityService entityService,
        KeystoneVisibilityHider visibilityHider) {
      _entityService = entityService;
      _visibilityHider = visibilityHider;
    }

    /// <summary>Total tracked agents (including stale references if
    /// any have already been destroyed by other means).</summary>
    public int Count => _live.Count;

    /// <summary>Live snapshot suitable for iteration during the dawn
    /// reconcile pass. The handler may call <see cref="Despawn"/>
    /// while iterating a copy of this list; don't mutate the registry
    /// while iterating the underlying list directly.</summary>
    public IReadOnlyList<Entry> Entries => _live;

    /// <summary>Cumulative count of <see cref="Add"/> calls since
    /// construction. Activity-panel readers sample this against a
    /// previous snapshot to compute "fauna spawned today." Never
    /// decrements; pairs with <see cref="RemovedCount"/>.</summary>
    public long AddedCount { get; private set; }

    /// <summary>Cumulative count of entries removed via any path
    /// (<see cref="Despawn"/>, <see cref="Forget"/>,
    /// <see cref="DespawnAnyAtColumn"/>, or the bulk paths). Never
    /// decrements; pairs with <see cref="AddedCount"/>.</summary>
    public long RemovedCount { get; private set; }

    private readonly Dictionary<string, long> _addedByBlueprint = new();
    private readonly Dictionary<string, long> _removedByBlueprint = new();

    /// <summary>Per-blueprint cumulative <see cref="Add"/> counts.
    /// Used by the activity panel to show "deer spawned: 87, cattle
    /// spawned: 23, fish spawned: 41" rather than a single global
    /// fauna total. The dictionary grows by one entry the first time
    /// any given blueprint is added; species that never spawn don't
    /// appear at all.</summary>
    public IReadOnlyDictionary<string, long> AddedByBlueprint => _addedByBlueprint;

    /// <summary>Per-blueprint cumulative removal counts. Paired with
    /// <see cref="AddedByBlueprint"/>; the difference per blueprint
    /// is the current live count (also computable by walking
    /// <see cref="Entries"/> when an exact live count is needed).</summary>
    public IReadOnlyDictionary<string, long> RemovedByBlueprint => _removedByBlueprint;

    private readonly Dictionary<FaunaDespawnReason, long> _removedByReason = new();

    /// <summary>Cumulative removal counts bucketed by
    /// <see cref="FaunaDespawnReason"/>. Surfaced in the activity
    /// panel's despawn-reason histogram to answer "why are fauna
    /// disappearing" without grepping Player.log. Categories
    /// without any counts simply don't appear in the dictionary.
    /// Internal callers stamp the category through the overloads
    /// below (<see cref="Despawn(EntityComponent, FaunaDespawnReason)"/>
    /// etc.); paths that don't pass a category land under
    /// <see cref="FaunaDespawnReason.Unknown"/>.</summary>
    public IReadOnlyDictionary<FaunaDespawnReason, long> RemovedByReason => _removedByReason;

    /// <summary>Register a freshly-spawned agent. Caller is the
    /// placement tool or day-cycle spawn handler. The position
    /// reporter is optional (the dev tool may register an entity
    /// before the agent component is available); passing
    /// <c>null</c> means the dawn reconciler will skip this entry
    /// when grouping by cluster. <paramref name="persistsOvernight"/>
    /// is typically read off the agent's
    /// <see cref="BaseFaunaAgent.PersistsOvernight"/>; aquatic agents
    /// pass <c>true</c> so they survive the dusk teardown.</summary>
    public void Add(EntityComponent entity, IFaunaPositioning? position, string blueprintName, bool persistsOvernight = false) {
      _live.Add(new Entry(entity, position, blueprintName, _nextSequence++, persistsOvernight));
      AddedCount++;
      _addedByBlueprint.TryGetValue(blueprintName, out var n);
      _addedByBlueprint[blueprintName] = n + 1;
      // Honour the vertical-view cutaway. Fauna move every frame, so
      // the hider re-evaluates the coord-getter on each Tick; we
      // capture the entity's Transform here (NavigationCoordinateSystem
      // is what vanilla's CharacterModelHider uses -- it nudges the
      // world position 0.1y up so a fauna standing on a surface lands
      // in the air voxel, not in the terrain voxel below it).
      var go = entity.GameObject;
      if (go != null) {
        var transform = go.transform;
        _visibilityHider.Track(
            go,
            () => NavigationCoordinateSystem.WorldToGridInt(transform.position));
      }
    }

    /// <summary>Despawn every fauna whose current tile sits on the
    /// given column (X, Y), regardless of Z. Used by the topology-
    /// change watcher to precautionarily evict fauna from columns
    /// where the terrain just changed or a building was placed.
    /// <paramref name="reason"/> is included in the per-entry log so
    /// the cause of the cull is visible.</summary>
    /// <returns>Number of agents culled.</returns>
    public int DespawnAnyAtColumn(int columnX, int columnY, string reason)
        => DespawnAnyAtColumn(columnX, columnY, reason, FaunaDespawnReason.Unknown);

    public int DespawnAnyAtColumn(
        int columnX, int columnY, string reason, FaunaDespawnReason category) {
      var culled = 0;
      // Iterate back-to-front so RemoveAt indices stay valid.
      for (var i = _live.Count - 1; i >= 0; i--) {
        var entry = _live[i];
        if (entry.Position == null) continue;
        var tile = entry.Position.CurrentTile;
        if (tile.X != columnX || tile.Y != columnY) continue;
        _live.RemoveAt(i);
        RemovedCount++;
        _removedByBlueprint.TryGetValue(entry.BlueprintName, out var n);
        _removedByBlueprint[entry.BlueprintName] = n + 1;
        TallyReason(category);
        if ((object)entry.Entity == null) continue;
        KeystoneLog.Verbose(
            $"[Keystone] KeystoneFaunaRegistry: despawning '{entry.BlueprintName}' " +
            $"at column ({columnX},{columnY}) — {reason}.");
        _visibilityHider.Untrack(entry.Entity.GameObject);
        _entityService.Delete(entry.Entity);
        culled++;
      }
      return culled;
    }

    /// <summary>Drop <paramref name="entity"/> from the registry
    /// without deleting it. Used by the entity-deletion event handler
    /// (see <see cref="FaunaDayCycleHandler.OnEntityDeleted"/>) so the
    /// registry stays in sync with the world even when entities are
    /// destroyed via paths Keystone didn't initiate (region
    /// invalidation, vanilla cleanup, scene unload, ...). Without this
    /// the registry accumulates stale references that NRE the next
    /// time <see cref="DespawnAll"/> iterates them.</summary>
    public void Forget(EntityComponent entity)
        => Forget(entity, FaunaDespawnReason.ExternalEntityDeleted);

    public void Forget(EntityComponent entity, FaunaDespawnReason reason) {
      for (var i = 0; i < _live.Count; i++) {
        if (ReferenceEquals(_live[i].Entity, entity)) {
          var blueprintName = _live[i].BlueprintName;
          _live.RemoveAt(i);
          RemovedCount++;
          _removedByBlueprint.TryGetValue(blueprintName, out var n);
          _removedByBlueprint[blueprintName] = n + 1;
          TallyReason(reason);
          if ((object)entity != null) {
            _visibilityHider.Untrack(entity.GameObject);
          }
          return;
        }
      }
    }

    /// <summary>Delete <paramref name="entity"/> and drop it from the
    /// registry. No-op if the entity isn't registered. Logs the
    /// despawn at verbose level so dawn culling is visible in the
    /// log.</summary>
    public void Despawn(EntityComponent entity)
        => Despawn(entity, FaunaDespawnReason.Unknown);

    public void Despawn(EntityComponent entity, FaunaDespawnReason reason) {
      var index = -1;
      for (var i = 0; i < _live.Count; i++) {
        if (ReferenceEquals(_live[i].Entity, entity)) { index = i; break; }
      }
      if (index < 0) return;
      var entry = _live[index];
      _live.RemoveAt(index);
      RemovedCount++;
      _removedByBlueprint.TryGetValue(entry.BlueprintName, out var nRemoved);
      _removedByBlueprint[entry.BlueprintName] = nRemoved + 1;
      TallyReason(reason);
      if ((object)entry.Entity == null) return;
      _visibilityHider.Untrack(entry.Entity.GameObject);
      _entityService.Delete(entry.Entity);
    }

    private void TallyReason(FaunaDespawnReason reason) {
      _removedByReason.TryGetValue(reason, out var n);
      _removedByReason[reason] = n + 1;
    }

    /// <summary>Toggle Unity active state on every tracked ephemeral
    /// agent (<see cref="Entry.PersistsOvernight"/> = <c>false</c>).
    /// Used by the day-cycle handler to "hide at night" — inactive
    /// GameObjects skip ticking entirely (per
    /// <c>TickableEntity.Tick</c>'s <c>activeInHierarchy</c> gate),
    /// so a hidden agent costs nothing while it's offstage but keeps
    /// its position, animator state, and registry handle intact.
    /// Compared to the old destroy-and-respawn-at-dawn approach, this
    /// skips the expensive
    /// <see cref="EntityService.Instantiate"/> /
    /// <see cref="EntityService.Delete"/> churn entirely on steady-
    /// state days where the cluster's fauna count is unchanged.
    ///
    /// <para>Aquatic agents (PersistsOvernight=true) are left alone:
    /// fish keep swimming through the night.</para></summary>
    public void SetAllEphemeralActive(bool active) {
      for (var i = 0; i < _live.Count; i++) {
        var entry = _live[i];
        if (entry.PersistsOvernight) continue;
        if ((object)entry.Entity == null) continue;
        var go = entry.Entity.GameObject;
        if (go != null) go.SetActive(active);
      }
    }

    /// <summary>Despawn every tracked agent via
    /// <see cref="EntityService.Delete"/> and clear the registry.
    /// Idempotent; safe to call when empty.
    ///
    /// <para><b>Snapshot-then-clear pattern.</b> We move <c>_live</c>
    /// into a local array and clear the field <i>before</i> the Delete
    /// loop. Each <see cref="EntityService.Delete"/> call posts
    /// <c>EntityDeletedEvent</c>, which the subscribed
    /// <see cref="FaunaDayCycleHandler.OnEntityDeleted"/> routes to
    /// <see cref="Forget"/>. If we iterated <c>_live</c> directly,
    /// <see cref="Forget"/> would mutate the list mid-loop, shift
    /// indices, and cause us to skip every other agent — those
    /// skipped agents would persist past dusk as ghosts (no movement,
    /// no further despawn, just visual stutter at their spawn tile).
    /// Clearing up-front means <see cref="Forget"/> finds nothing to
    /// remove for each event and no-ops cleanly.</para></summary>
    public void DespawnAll() {
      var initial = _live.Count;
      if (initial == 0) {
        return;
      }
      // Partition: persistent entries stay in _live, ephemeral ones get
      // snapshotted out and deleted. Take a snapshot first so the
      // EntityDeletedEvent -> Forget feedback can't mutate _live mid-
      // loop (same reason DespawnAll already snapshots in the all-
      // ephemeral case -- see the original comment block above).
      var toDelete = new List<EntityComponent>(_live.Count);
      var persisted = 0;
      for (var i = _live.Count - 1; i >= 0; i--) {
        var entry = _live[i];
        if (entry.PersistsOvernight) {
          persisted++;
          continue;
        }
        _live.RemoveAt(i);
        RemovedCount++;
        _removedByBlueprint.TryGetValue(entry.BlueprintName, out var nRemoved);
        _removedByBlueprint[entry.BlueprintName] = nRemoved + 1;
        TallyReason(FaunaDespawnReason.Unknown);
        if ((object)entry.Entity != null) {
          toDelete.Add(entry.Entity);
        }
      }
      for (var i = 0; i < toDelete.Count; i++) {
        _visibilityHider.Untrack(toDelete[i].GameObject);
        _entityService.Delete(toDelete[i]);
      }
      KeystoneLog.Verbose(
          $"[Keystone] KeystoneFaunaRegistry: despawned {toDelete.Count} fauna agent(s) " +
          $"({persisted} persisted overnight, {initial} total before dusk).");
    }

  }

}
