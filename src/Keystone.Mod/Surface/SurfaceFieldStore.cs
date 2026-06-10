using System;
using System.Collections.Generic;
using Timberborn.MapIndexSystem;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.TerrainSystem;
using Timberborn.TickSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace Keystone.Mod.Surface {

  /// <summary>
  /// Map-global, per-surface (per-voxel) persisted float storage for
  /// Keystone ecology layers -- the per-tile counterpart to the
  /// per-chunk <c>ChunkValueStore</c>. Modelled directly on vanilla
  /// <c>SoilMoistureSimulator</c>, which solves the same problem
  /// (accumulated per-surface state on a fixed-extent but
  /// mutable-composition map).
  ///
  /// <para><b>Storage.</b> One dense <see cref="float"/> array per
  /// <see cref="SurfaceField"/> layer, sized
  /// <c>VerticalStride * MaxColumnCount</c> and indexed in the column
  /// terrain map's per-surface index space (a column's stack ordinal
  /// times <c>VerticalStride</c> plus the 2D index -- the same space its
  /// cleanup events carry, NOT <see cref="MapIndexService"/>'s
  /// height-based 3D index). Resolve every read/write through
  /// <see cref="TryResolveSurfaceIndex"/> so stored values stay aligned
  /// with cleanup; that helper mirrors how vanilla
  /// <c>SoilMoistureService</c> maps a surface to its moisture index.
  /// Dense, not sparse: the canonical consumer fills toward the whole
  /// map as the player spreads water, so the dense case is the success
  /// case (and the game stores moisture exactly this way).</para>
  ///
  /// <para><b>Terrain composition changes.</b> The map extent is fixed
  /// for a save, but surfaces are created, destroyed, and shifted as
  /// terrain is dug and raised. We subscribe to
  /// <see cref="IThreadSafeColumnTerrainMap"/> and drain its events on
  /// the main thread in <see cref="Tick"/>:
  /// <list type="bullet">
  ///   <item><c>ColumnMovedUp/Down</c> -- a surface that *moved* (terrain
  ///   inserted/removed below it). Copy its value to the shifted index;
  ///   it is NOT a new surface, so its accumulated maturity must follow
  ///   it. Zeroing here would silently wipe maturity on every surface
  ///   above any dig elsewhere in the column.</item>
  ///   <item><c>ColumnReset</c> -- a surface destroyed or newly created.
  ///   Zero it.</item>
  ///   <item><c>MaxTerrainColumnCountChanged</c> -- resize all arrays;
  ///   <see cref="Array.Resize{T}"/> zero-fills the new tail, so fresh
  ///   surfaces default to zero.</item>
  /// </list></para>
  ///
  /// <para><b>Persistence.</b> Each layer is packed under its own key via
  /// <c>MapIndexService.Pack</c>. Adding a layer is additive: old
  /// saves lack the key and load it as all-zero, so there is no
  /// migration. The fixed map extent makes a flat index stable across
  /// save/load -- no reprojection.</para>
  ///
  /// <para><b>Threading.</b> Plain arrays, mutated only on the main
  /// thread (the <see cref="Tick"/> cleanup drain, and -- once it lands
  /// -- the accrual sweep). Reads by consumers are main-thread too. If a
  /// future consumer needs to read these during a parallel sweep, switch
  /// the backing to <c>TickOnlyArray</c> as the vanilla simulators do.</para>
  /// </summary>
  public sealed class SurfaceFieldStore
      : ILoadableSingleton, ISaveableSingleton, ITickableSingleton {

    #region Save keys

    private static readonly SingletonKey StoreKey = new("Keystone.SurfaceFields");
    private static readonly PropertyKey<int> SchemaVersionKey = new("SchemaVersion");
    private static readonly PropertyKey<int> SizeKey = new("Size");
    private const int CurrentSchemaVersion = 1;

    #endregion

    #region Pending terrain actions

    private enum ActionType {
      ResizeToMaxColumnCount,
      ColumnMovedUp,
      ColumnMovedDown,
      ColumnReset,
    }

    private readonly struct PendingAction {
      public ActionType Type { get; }
      public int Value { get; }
      public PendingAction(ActionType type, int value) {
        Type = type;
        Value = value;
      }
    }

    private readonly List<PendingAction> _actions = new();

    #endregion

    #region Fields

    private readonly ISingletonLoader _singletonLoader;
    private readonly MapIndexService _mapIndexService;
    private readonly IThreadSafeColumnTerrainMap _columnTerrainMap;
    private readonly FloatPackedListSerializer _floatPackedListSerializer;

    private readonly SurfaceField[] _kinds;
    private readonly PropertyKey<PackedList<float>>[] _fieldKeys;
    private float[][] _fields;
    private int _verticalStride;

    #endregion

    #region Construction

    public SurfaceFieldStore(
        ISingletonLoader singletonLoader,
        MapIndexService mapIndexService,
        IThreadSafeColumnTerrainMap columnTerrainMap,
        FloatPackedListSerializer floatPackedListSerializer) {
      _singletonLoader = singletonLoader;
      _mapIndexService = mapIndexService;
      _columnTerrainMap = columnTerrainMap;
      _floatPackedListSerializer = floatPackedListSerializer;

      _kinds = (SurfaceField[])Enum.GetValues(typeof(SurfaceField));
      _fields = new float[_kinds.Length][];
      _fieldKeys = new PropertyKey<PackedList<float>>[_kinds.Length];
      for (var i = 0; i < _kinds.Length; i++) {
        if ((int)_kinds[i] != i) {
          throw new InvalidOperationException(
              "SurfaceField values must be contiguous and zero-based; "
              + $"'{_kinds[i]}' = {(int)_kinds[i]} at position {i}.");
        }
        _fieldKeys[i] = new PropertyKey<PackedList<float>>(
            "Field." + SurfaceFieldMeta.SaveId(_kinds[i]));
      }
    }

    #endregion

    #region Properties

    /// <summary>
    /// True if a saved store singleton was present at <see cref="Load"/>
    /// -- i.e. this save was written after the per-surface store shipped.
    /// False for a new game or a save from before it existed (post-ship
    /// saves always write the singleton, even all-zero, so the signal is
    /// unambiguous). The startup warmup reads this to decide whether to
    /// seed maturity, so a pre-store save migrates to a non-zero head
    /// start instead of starting at 0. Set once in <see cref="Load"/>.
    /// </summary>
    public bool HadPersistedData { get; private set; }

    #endregion

    #region Access

    /// <summary>
    /// Resolve the surface at column <c>(x, y)</c> whose ceiling is at
    /// height <paramref name="z"/> to its backing index, via the column
    /// terrain map -- the same resolution vanilla
    /// <c>SoilMoistureService.SoilIsMoist(Vector3Int)</c> uses
    /// (<c>TryGetIndexAtCeiling(CellToIndex(xy), z)</c>). Returns the
    /// column-stack index space the cleanup events operate on, so values
    /// written through it survive terrain edits. False when the column
    /// has no surface with that ceiling (caller should skip).
    /// </summary>
    public bool TryResolveSurfaceIndex(int x, int y, int z, out int index3D) {
      var index2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
      return _columnTerrainMap.TryGetIndexAtCeiling(index2D, z, out index3D);
    }

    /// <summary>
    /// Read a layer's value at an index from
    /// <see cref="TryResolveSurfaceIndex"/>. Out-of-range reads return 0:
    /// the column map can report a freshly-created surface one tick
    /// before <see cref="Tick"/> grows the arrays to fit it, and that
    /// new surface is correctly "no maturity yet" -- an anticipated,
    /// transient state, not a masked error.
    /// </summary>
    public float GetAt(SurfaceField field, int index3D) {
      var array = _fields[(int)field];
      return (uint)index3D < (uint)array.Length ? array[index3D] : 0f;
    }

    /// <summary>
    /// Write a layer's value at an index from
    /// <see cref="TryResolveSurfaceIndex"/>. Out-of-range writes are
    /// dropped for the same resize-lag reason as <see cref="GetAt"/>; the
    /// surface re-accrues next cycle once the arrays have grown.
    /// </summary>
    public void SetAt(SurfaceField field, int index3D, float value) {
      var array = _fields[(int)field];
      if ((uint)index3D < (uint)array.Length) {
        array[index3D] = value;
      }
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public void Load() {
      _verticalStride = _mapIndexService.VerticalStride;
      var maxColumnCount = _columnTerrainMap.MaxColumnCount;
      var size = _verticalStride * maxColumnCount;
      for (var i = 0; i < _fields.Length; i++) {
        _fields[i] = new float[size];
      }

      if (_singletonLoader.TryGetSingleton(StoreKey, out var loader)) {
        HadPersistedData = true;
        if (loader.Has(SchemaVersionKey)) {
          var version = loader.Get(SchemaVersionKey);
          if (version > CurrentSchemaVersion) {
            UnityEngine.Debug.LogWarning(
                $"[Keystone] SurfaceFieldStore save schema v{version} is newer than "
                + $"supported v{CurrentSchemaVersion}; loading best-effort.");
          }
        }
        // Saved Size is the number of levels actually packed at save time
        // (the store array's column extent -- equal to the max column count
        // in steady state; see Save). Unpack only the levels both saves agree
        // on; the rest stay zero.
        var savedColumnCount = loader.Has(SizeKey) ? loader.Get(SizeKey) : 1;
        var levels = Math.Min(savedColumnCount, maxColumnCount);
        for (var i = 0; i < _fields.Length; i++) {
          if (loader.Has(_fieldKeys[i])) {
            var packed = loader.Get(_fieldKeys[i], _floatPackedListSerializer);
            _mapIndexService.Unpack(packed, _fields[i].AsSpan(), levels);
          }
        }
      }

      _columnTerrainMap.MaxTerrainColumnCountChanged += OnMaxColumnCountChanged;
      _columnTerrainMap.ColumnMovedUp += OnColumnMovedUp;
      _columnTerrainMap.ColumnMovedDown += OnColumnMovedDown;
      _columnTerrainMap.ColumnReset += OnColumnReset;
    }

    /// <inheritdoc />
    public void Save(ISingletonSaver singletonSaver) {
      var saver = singletonSaver.GetSingleton(StoreKey);
      saver.Set(SchemaVersionKey, CurrentSchemaVersion);
      // Pack reads inputArray[startingIndex + (levels-1)*VerticalStride + ...],
      // so it requires inputArray.Length >= levels * VerticalStride. Derive the
      // level count from the array we actually hold rather than a live
      // _columnTerrainMap.MaxColumnCount read: a terrain edit (our own digging,
      // or another mod such as More Mines stacking ore) grows the column count
      // and queues a resize that Tick() drains on the next sim tick, but a save
      // landing in that same frame (GameSaver runs on LateUpdate) would pass the
      // already-grown live count against the not-yet-grown array and index off
      // the end -> IndexOutOfRangeException mid-save. The columns the live count
      // is ahead by carry no Keystone state yet (their accrual hasn't run; the
      // pending ColumnReset would zero them anyway), so the array's own extent
      // is the correct -- and only safe -- thing to persist.
      var columns = _fields[0].Length / _verticalStride;
      saver.Set(SizeKey, columns);
      for (var i = 0; i < _fields.Length; i++) {
        saver.Set(
            _fieldKeys[i],
            _mapIndexService.Pack(_fields[i], columns),
            _floatPackedListSerializer);
      }
    }

    /// <inheritdoc />
    public void Tick() {
      if (_actions.Count == 0) return;
      for (var a = 0; a < _actions.Count; a++) {
        ProcessAction(_actions[a]);
      }
      _actions.Clear();
    }

    #endregion

    #region Terrain change handling

    private void OnMaxColumnCountChanged(object sender, int maxColumnCount) =>
        _actions.Add(new PendingAction(ActionType.ResizeToMaxColumnCount, maxColumnCount));

    private void OnColumnMovedUp(object sender, int index3D) =>
        _actions.Add(new PendingAction(ActionType.ColumnMovedUp, index3D));

    private void OnColumnMovedDown(object sender, int index3D) =>
        _actions.Add(new PendingAction(ActionType.ColumnMovedDown, index3D));

    private void OnColumnReset(object sender, int index3D) =>
        _actions.Add(new PendingAction(ActionType.ColumnReset, index3D));

    private void ProcessAction(PendingAction action) {
      switch (action.Type) {
        case ActionType.ResizeToMaxColumnCount:
          var newSize = action.Value * _verticalStride;
          for (var i = 0; i < _fields.Length; i++) {
            Array.Resize(ref _fields[i], newSize);
          }
          break;
        case ActionType.ColumnMovedUp:
          MoveColumn(action.Value, action.Value - _verticalStride);
          break;
        case ActionType.ColumnMovedDown:
          MoveColumn(action.Value, action.Value + _verticalStride);
          break;
        case ActionType.ColumnReset:
          ResetColumn(action.Value);
          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(action));
      }
    }

    /// <summary>Copy every layer's value from <paramref name="source"/>
    /// to <paramref name="target"/> -- the surface moved, not vanished,
    /// so its accumulated state follows it.</summary>
    private void MoveColumn(int target, int source) {
      for (var i = 0; i < _fields.Length; i++) {
        _fields[i][target] = _fields[i][source];
      }
    }

    /// <summary>Zero every layer's value at <paramref name="index3D"/> --
    /// a surface was destroyed or freshly created there.</summary>
    private void ResetColumn(int index3D) {
      for (var i = 0; i < _fields.Length; i++) {
        _fields[i][index3D] = 0f;
      }
    }

    #endregion

  }

}
