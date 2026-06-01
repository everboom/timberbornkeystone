using System;
using System.Collections.Generic;

namespace Keystone.Core.Spatial {

  /// <summary>
  /// Session-scoped registry mapping named per-tile value slots to
  /// ordinal indices. Same lifecycle as <see cref="Persistence.ChunkValueRegistry"/>:
  /// register during Load, freeze before first tick, use ordinals
  /// on the hot path.
  /// </summary>
  public sealed class TileSlotRegistry {

    #region Fields

    private readonly List<string> _names = new();
    private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);
    private bool _frozen;

    #endregion

    #region Properties

    /// <summary>Total registered slots. Fixed after <see cref="Freeze"/>.</summary>
    public int SlotCount => _names.Count;

    /// <summary>True after <see cref="Freeze"/> has been called.</summary>
    public bool IsFrozen => _frozen;

    #endregion

    #region Registration

    /// <summary>Register a named slot and return its ordinal. Idempotent:
    /// same name always returns the same ordinal. Throws after
    /// <see cref="Freeze"/>.</summary>
    public int Register(string name) {
      if (name == null) throw new ArgumentNullException(nameof(name));
      if (name.Length == 0) throw new ArgumentException("Name must not be empty.", nameof(name));
      if (_frozen) throw new InvalidOperationException(
          $"TileSlotRegistry is frozen. Cannot register '{name}'.");
      if (_ordinals.TryGetValue(name, out var existing)) return existing;
      var ordinal = _names.Count;
      _names.Add(name);
      _ordinals[name] = ordinal;
      return ordinal;
    }

    /// <summary>Lock the registry. No new slots after this.</summary>
    public void Freeze() => _frozen = true;

    #endregion

    #region Lookup

    /// <summary>Get the ordinal for a registered name. Throws if
    /// unknown.</summary>
    public int OrdinalFor(string name) {
      if (_ordinals.TryGetValue(name, out var ordinal)) return ordinal;
      throw new KeyNotFoundException($"No tile slot registered with name '{name}'.");
    }

    /// <summary>Try to get the ordinal for a name. Returns null if
    /// not registered.</summary>
    public int? TryOrdinalFor(string? name) {
      if (name == null || name.Length == 0) return null;
      return _ordinals.TryGetValue(name, out var ordinal) ? ordinal : (int?)null;
    }

    /// <summary>Get the name for an ordinal. Throws if out of range.</summary>
    public string NameFor(int ordinal) {
      if (ordinal < 0 || ordinal >= _names.Count)
        throw new ArgumentOutOfRangeException(nameof(ordinal));
      return _names[ordinal];
    }

    #endregion

  }

}
