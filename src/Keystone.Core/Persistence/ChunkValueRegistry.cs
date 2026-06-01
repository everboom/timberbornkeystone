using System;
using System.Collections.Generic;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Session-scoped registry mapping named chunk-value kinds to ordinal
  /// indices. The ordinals drive the parallel data layer's flat
  /// <c>float[]</c> arrays; the names are used for persistence,
  /// cross-mod interop, and debug display.
  ///
  /// <para><b>Lifecycle.</b> Registration is open during game startup
  /// (Bindito <c>Load</c> phase). Other mods inject this singleton and
  /// call <see cref="Register"/> to claim slots. Keystone's own biome
  /// slots are registered via
  /// <see cref="Biomes.BiomeValueKinds.Initialize"/>. Once gameplay
  /// begins, <see cref="Freeze"/> locks the registry — no new slots
  /// can be added. <see cref="SlotCount"/> is then fixed for the
  /// session and determines the <c>float[]</c> size for every
  /// per-chunk data array.</para>
  ///
  /// <para><b>Persistence.</b> Ordinals are session-local and may
  /// change when mods are added or removed. The save/load codec
  /// serializes values by <see cref="NameFor"/> and deserializes by
  /// <see cref="TryOrdinalFor"/>, discarding unknown names with a
  /// warning and defaulting missing slots to zero.</para>
  ///
  /// <para><b>Cross-mod API.</b> The registry is publicly injectable.
  /// External mods register slots under their own prefix (e.g.
  /// <c>"folktails.chunk.regrowth"</c>) during <c>Load</c>. Keystone
  /// reserves the <c>"keystone."</c> prefix.</para>
  /// </summary>
  public sealed class ChunkValueRegistry {

    #region Fields

    private readonly List<string> _names = new();
    private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);
    private readonly List<ChunkValueRole> _roles = new();
    private bool _frozen;

    #endregion

    #region Properties

    /// <summary>Whether registration is locked.</summary>
    public bool IsFrozen => _frozen;

    /// <summary>Total registered slots. Fixed after <see cref="Freeze"/>.</summary>
    public int SlotCount => _names.Count;

    /// <summary>All registered names in ordinal order. For persistence
    /// codec enumeration and debug display.</summary>
    public IReadOnlyList<string> AllNames => _names;

    #endregion

    #region Registration

    /// <summary>
    /// Register a named value slot and return its ordinal index.
    /// Idempotent: registering the same name twice returns the same
    /// ordinal. Throws <see cref="InvalidOperationException"/> if
    /// the registry has been frozen.
    /// </summary>
    /// <param name="name">Non-null, non-empty slot name. By
    /// convention, prefixed with the mod id (e.g.
    /// <c>"keystone.chunk.suitability.forest"</c>).</param>
    /// <param name="role">The slot's semantic channel role, declared by
    /// the registrant. Consumers read it via <see cref="RoleOf"/> instead
    /// of re-deriving meaning from the name string — e.g.
    /// <c>ChunkReconciler</c> tells "lost maturity" from "benign" drops by
    /// asking which slots are <see cref="ChunkValueRole.Maturity"/>. On
    /// re-registration of an existing name the first-registered role
    /// stands (the passed value is ignored).</param>
    /// <returns>The ordinal index for this slot.</returns>
    public int Register(string name, ChunkValueRole role = ChunkValueRole.Other) {
      if (string.IsNullOrEmpty(name))
        throw new ArgumentException("Slot name must be non-null and non-empty.", nameof(name));
      if (_frozen)
        throw new InvalidOperationException(
            $"ChunkValueRegistry is frozen ({_names.Count} slots). " +
            $"Cannot register '{name}'. Register during Load, before Freeze.");
      if (_ordinals.TryGetValue(name, out var existing))
        return existing;
      var ordinal = _names.Count;
      _names.Add(name);
      _roles.Add(role);
      _ordinals[name] = ordinal;
      return ordinal;
    }

    /// <summary>
    /// Lock the registry. No further <see cref="Register"/> calls
    /// are accepted. Called once at session start (typically from
    /// <c>KeystoneStartupWarmup.PostLoad</c>).
    /// </summary>
    public void Freeze() {
      _frozen = true;
    }

    #endregion

    #region Lookup

    /// <summary>
    /// Return the ordinal for <paramref name="name"/>.
    /// Throws <see cref="KeyNotFoundException"/> if the name was
    /// never registered.
    /// </summary>
    public int OrdinalFor(string name) {
      if (_ordinals.TryGetValue(name, out var ordinal))
        return ordinal;
      throw new KeyNotFoundException(
          $"ChunkValueRegistry: no slot registered for '{name}'.");
    }

    /// <summary>
    /// Return the ordinal for <paramref name="name"/>, or
    /// <c>null</c> if the name was never registered.
    /// </summary>
    public int? TryOrdinalFor(string name) {
      if (string.IsNullOrEmpty(name)) return null;
      return _ordinals.TryGetValue(name, out var ordinal) ? ordinal : null;
    }

    /// <summary>
    /// Return the name for <paramref name="ordinal"/>.
    /// Throws <see cref="ArgumentOutOfRangeException"/> if the
    /// ordinal is outside <c>[0, SlotCount)</c>.
    /// </summary>
    public string NameFor(int ordinal) {
      if (ordinal < 0 || ordinal >= _names.Count)
        throw new ArgumentOutOfRangeException(
            nameof(ordinal),
            $"Ordinal {ordinal} is out of range [0, {_names.Count}).");
      return _names[ordinal];
    }

    /// <summary>
    /// The semantic channel role declared for <paramref name="ordinal"/>
    /// at registration. Lets consumers act on a slot's meaning without
    /// re-deriving it from the name string. Throws
    /// <see cref="ArgumentOutOfRangeException"/> if the ordinal is outside
    /// <c>[0, SlotCount)</c>.
    /// </summary>
    public ChunkValueRole RoleOf(int ordinal) {
      if (ordinal < 0 || ordinal >= _roles.Count)
        throw new ArgumentOutOfRangeException(
            nameof(ordinal),
            $"Ordinal {ordinal} is out of range [0, {_roles.Count}).");
      return _roles[ordinal];
    }

    #endregion

  }

  /// <summary>
  /// Semantic channel role of a <see cref="ChunkValueRegistry"/> slot,
  /// declared by the registrant. Decouples "what does this slot mean" from
  /// the slot's name string, so consumers (notably <c>ChunkReconciler</c>'s
  /// lost-maturity-vs-benign drop classification) read a typed role rather
  /// than prefix-matching a kind name that could be renamed out from under
  /// them. External mods that don't care leave slots <see cref="Other"/>.
  /// </summary>
  public enum ChunkValueRole {

    /// <summary>No special role (default; external-mod slots, etc.).</summary>
    Other,

    /// <summary>Short-term, hour-scale Suitability channel.</summary>
    Suitability,

    /// <summary>Long-term, day-scale accumulated Maturity channel — the
    /// ecology history whose loss is worth alarming about.</summary>
    Maturity,

  }

}
