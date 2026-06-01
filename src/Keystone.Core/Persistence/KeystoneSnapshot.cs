using System.Collections.Generic;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Mutable buffer used by <c>KeystonePersistence</c> to hand
  /// persisted state from <c>Load()</c> (no terrain access yet) into
  /// <c>PostLoad()</c> (terrain is loaded; regions are freshly
  /// indexed). The Mod-side persistence singleton populates this in
  /// Load and drains it in PostLoad; nothing else should hold a
  /// reference past PostLoad.
  ///
  /// <para>Not the over-the-wire shape -- that's
  /// <see cref="SnapshotPayload"/>, produced by
  /// <see cref="SnapshotCodec"/>. This buffer just gives the
  /// load/postload split a typed scratch space without leaking
  /// Timberborn loader objects past the Load phase.</para>
  /// </summary>
  public sealed class KeystoneSnapshot {

    #region Properties

    /// <summary>Per-region clock-stamp records to rehydrate.</summary>
    public List<RegionPersistedRecord> Regions { get; } = new();

    /// <summary>Per-region named values (region-scope value store)
    /// to rehydrate.</summary>
    public Dictionary<RegionValueKey, float> RegionValues { get; } = new();

    /// <summary>Per-chunk named values (chunk-scope value store) to
    /// rehydrate.</summary>
    public Dictionary<ChunkValueKey, float> ChunkValues { get; } = new();

    /// <summary>True when no regions or values are populated.</summary>
    public bool IsEmpty => Regions.Count == 0 && RegionValues.Count == 0 && ChunkValues.Count == 0;

    #endregion

    #region Mutation

    /// <summary>Drop all buffered regions, region values, and chunk values. Used to reset between loads or after PostLoad drains the buffer.</summary>
    public void Clear() {
      Regions.Clear();
      RegionValues.Clear();
      ChunkValues.Clear();
    }

    #endregion

  }

}
