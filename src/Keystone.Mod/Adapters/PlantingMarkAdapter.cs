using System.Collections.Generic;
using Keystone.Core.Ports;
using Timberborn.Planting;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="IPlantingMarkQuery"/> implementation backed by
  /// Timberborn's <see cref="PlantingService"/>. Per-tile lookups
  /// (<see cref="IsMarked"/>, <see cref="MarkedSpecies"/>) dispatch
  /// straight to the service's O(1) dictionary lookups.
  /// <see cref="MarksInTileRect"/> is served from an internal spatial
  /// bucket maintained reactively via
  /// <see cref="PlantingCoordinatesSetEvent"/> /
  /// <see cref="PlantingCoordinatesUnsetEvent"/>.
  ///
  /// <para><b>Bucket grain.</b> <see cref="BucketSize"/> tiles per
  /// bucket side (currently 4, matching Keystone's chunk size). A
  /// chunk-rect query of size 4x4 maps to 1-4 buckets depending on
  /// rect alignment; each bucket holds at most 16 mark positions, so
  /// the per-call cost is bounded by a small constant regardless of
  /// the world-wide mark count. The grain isn't exposed in the port
  /// contract -- callers see only the (minX, minY, maxX, maxY) rect
  /// they ask for.</para>
  ///
  /// <para><b>Why an internal index instead of polling the service.</b>
  /// <c>PlantingService.PlantingCoordinates</c> is a flat
  /// <c>IEnumerable&lt;Vector3Int&gt;</c>. A naive "filter by rect"
  /// callsite is <c>O(world-wide marks)</c> per call; called per chunk
  /// per cycle on a large map with extensive Forester areas, that's
  /// quadratic and blows up perf. The reactive bucket trades a tiny
  /// fixed amount of work per mark-edit (1 dict + 1 list mutation)
  /// for constant-bounded per-query work in the hot ticker path.</para>
  /// </summary>
  public sealed class PlantingMarkAdapter
      : IPlantingMarkQuery, ILoadableSingleton, IUnloadableSingleton {

    #region Constants

    /// <summary>Tiles per bucket side. Power-of-2 so bucket lookup
    /// is a cheap bit-shift. 4 matches Keystone's chunk size, so a
    /// chunk-aligned rect query falls in exactly one bucket; rect
    /// queries are robust to misalignment because the inner loop
    /// filters on the exact rect anyway.</summary>
    private const int BucketSize = 4;

    /// <summary>log2(<see cref="BucketSize"/>). Used as the shift
    /// amount for tile-to-bucket key derivation. Arithmetic shift
    /// (the C# default for <c>int &gt;&gt; n</c>) rounds toward
    /// negative infinity, which is what we want for negative tile
    /// coordinates to land in the correct bucket.</summary>
    private const int BucketShift = 2;

    #endregion

    #region Fields

    private readonly PlantingService _planting;
    private readonly EventBus _eventBus;

    /// <summary>Bucket-keyed mark store. Each bucket holds every
    /// mark whose <c>(x &gt;&gt; BucketShift, y &gt;&gt; BucketShift)</c>
    /// equals the key. Z is preserved per entry (a column can carry
    /// marks at multiple Z values across stacked surfaces).</summary>
    private readonly Dictionary<(int Bx, int By), List<(int X, int Y, int Z, string Species)>> _buckets = new();

    #endregion

    #region Construction

    public PlantingMarkAdapter(PlantingService planting, EventBus eventBus) {
      _planting = planting;
      _eventBus = eventBus;
    }

    #endregion

    #region ILoadableSingleton / IUnloadableSingleton

    /// <inheritdoc />
    public void Load() {
      _eventBus.Register(this);
      // Snapshot any marks present at load time (loaded saves rehydrate
      // PlantingService at PostLoad; fresh games have none yet). After
      // this point the bucket stays in sync via the [OnEvent] hooks.
      foreach (var coord in _planting.PlantingCoordinates) {
        var species = _planting.GetResourceAt(coord);
        if (species == null) continue;
        AddToBucket(coord.x, coord.y, coord.z, species);
      }
    }

    /// <inheritdoc />
    public void Unload() {
      _eventBus.Unregister(this);
    }

    #endregion

    #region Event handlers

    [OnEvent]
    public void OnPlantingCoordinatesSet(PlantingCoordinatesSetEvent ev) {
      var c = ev.Coordinates;
      AddToBucket(c.x, c.y, c.z, ev.Resource);
    }

    [OnEvent]
    public void OnPlantingCoordinatesUnset(PlantingCoordinatesUnsetEvent ev) {
      var c = ev.Coordinates;
      RemoveFromBucket(c.x, c.y, c.z);
    }

    #endregion

    #region IPlantingMarkQuery

    /// <inheritdoc />
    public bool IsMarked(int x, int y, int z) {
      return _planting.IsResourceAt(new Vector3Int(x, y, z));
    }

    /// <inheritdoc />
    public string MarkedSpecies(int x, int y, int z) {
      return _planting.GetResourceAt(new Vector3Int(x, y, z));
    }

    /// <inheritdoc />
    public IEnumerable<(int X, int Y, int Z, string Species)> MarksInTileRect(
        int minX, int minY, int maxX, int maxY) {
      var minBx = minX >> BucketShift;
      var maxBx = maxX >> BucketShift;
      var minBy = minY >> BucketShift;
      var maxBy = maxY >> BucketShift;
      for (var bx = minBx; bx <= maxBx; bx++) {
        for (var by = minBy; by <= maxBy; by++) {
          if (!_buckets.TryGetValue((bx, by), out var bucket)) continue;
          for (var i = 0; i < bucket.Count; i++) {
            var m = bucket[i];
            if (m.X < minX || m.X > maxX) continue;
            if (m.Y < minY || m.Y > maxY) continue;
            yield return m;
          }
        }
      }
    }

    #endregion

    #region Bucket maintenance

    private void AddToBucket(int x, int y, int z, string species) {
      var key = (x >> BucketShift, y >> BucketShift);
      if (!_buckets.TryGetValue(key, out var bucket)) {
        bucket = new List<(int X, int Y, int Z, string Species)>();
        _buckets[key] = bucket;
      }
      // Set semantics: a re-set at the same (x, y, z) replaces the
      // species rather than duplicating the entry. The host's add
      // event fires on set; unset fires separately. A direct
      // species-change without an intervening unset is unusual but
      // we tolerate it here.
      for (var i = 0; i < bucket.Count; i++) {
        if (bucket[i].X == x && bucket[i].Y == y && bucket[i].Z == z) {
          bucket[i] = (x, y, z, species);
          return;
        }
      }
      bucket.Add((x, y, z, species));
    }

    private void RemoveFromBucket(int x, int y, int z) {
      var key = (x >> BucketShift, y >> BucketShift);
      if (!_buckets.TryGetValue(key, out var bucket)) return;
      for (var i = 0; i < bucket.Count; i++) {
        if (bucket[i].X == x && bucket[i].Y == y && bucket[i].Z == z) {
          bucket.RemoveAt(i);
          if (bucket.Count == 0) _buckets.Remove(key);
          return;
        }
      }
    }

    #endregion

  }

}
