using System.Collections.Generic;
using Keystone.Core.Ports;
using Timberborn.Forestry;
using UnityEngine;

namespace Keystone.Mod.Adapters {

  /// <summary>
  /// <see cref="ICuttingAreaWriter"/> implementation backed by Timberborn's
  /// <see cref="TreeCuttingArea"/> — the write-side counterpart of the
  /// read-only <see cref="CuttingMarkAdapter"/>. Translates Core's
  /// <c>(X, Y, Z)</c> integer tuples into <see cref="Vector3Int"/>s (the only
  /// place that construction happens) and forwards to
  /// <see cref="TreeCuttingArea.AddCoordinates"/> /
  /// <see cref="TreeCuttingArea.RemoveCoordinates"/>, which each post their own
  /// <c>TreeCuttingAreaChangedEvent</c> — so no extra refresh event is needed.
  ///
  /// <para>Empty batches are dropped rather than forwarded: the host posts a
  /// change event even for a no-op add/remove, and an empty drag (e.g. no
  /// active-species tiles in the rectangle) shouldn't trigger one. That is a
  /// genuine no-op, not a swallowed failure.</para>
  /// </summary>
  public sealed class CuttingAreaWriter : ICuttingAreaWriter {

    private readonly TreeCuttingArea _cuttingArea;

    public CuttingAreaWriter(TreeCuttingArea cuttingArea) {
      _cuttingArea = cuttingArea;
    }

    /// <inheritdoc />
    public void MarkForCutting(IEnumerable<(int X, int Y, int Z)> coordinates) {
      var batch = ToVectorList(coordinates);
      if (batch.Count == 0) return;
      _cuttingArea.AddCoordinates(batch);
    }

    /// <inheritdoc />
    public void UnmarkForCutting(IEnumerable<(int X, int Y, int Z)> coordinates) {
      var batch = ToVectorList(coordinates);
      if (batch.Count == 0) return;
      _cuttingArea.RemoveCoordinates(batch);
    }

    private static List<Vector3Int> ToVectorList(IEnumerable<(int X, int Y, int Z)> coordinates) {
      var list = new List<Vector3Int>();
      foreach (var (x, y, z) in coordinates) {
        list.Add(new Vector3Int(x, y, z));
      }
      return list;
    }

  }

}
