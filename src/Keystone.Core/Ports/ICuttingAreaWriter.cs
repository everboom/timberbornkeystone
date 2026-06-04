using System.Collections.Generic;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Write-side port over the host's tree-cutting <em>area</em> registry:
  /// the set of coordinates designated for cutting, which the host reconciles
  /// into per-tree cut marks and the forester then fells. Engine-agnostic
  /// counterpart of Timberborn's <c>Timberborn.Forestry.TreeCuttingArea</c>;
  /// the Mod layer supplies an adapter wrapping that singleton.
  ///
  /// <para><b>First write-side port.</b> The read-side ports
  /// (<see cref="ICuttingMarkQuery" />, <see cref="IPlantingMarkQuery" />, …)
  /// flow Mod → Core: an adapter answers questions about game state. This one
  /// flows Core → Mod: the logging tool decides which tiles to mark or
  /// unmark and pushes those decisions out through here. It is the concrete
  /// instance of the symmetric write-side seam the architecture doc
  /// anticipated ("place a flora here", "tag this tile contaminated").</para>
  ///
  /// <para><b>Distinct from <see cref="ICuttingMarkQuery" />.</b> That reads a
  /// single tree's <c>Cuttable.IsMarked</c> flag (per-entity). This writes the
  /// <em>area registry of coordinates</em> — the canonical designation path
  /// the vanilla forester area tool and Cordial's Cutter Tool both use — and
  /// the host derives the per-tree marks from it. Prefer this over poking
  /// <c>Cuttable.Mark()</c> per tree; the registry is save-aware and is the
  /// surface the cut pipeline actually consumes.</para>
  ///
  /// <para><b>Batched.</b> Both calls take a coordinate batch so the adapter
  /// issues one host-level area update (and one refresh event) per drag
  /// commit, rather than one per tile.</para>
  ///
  /// <para><b>Coordinates.</b> <c>(X, Y, Z)</c> integer tiles, matching the
  /// raw-int addressing the read-side ports use (e.g.
  /// <see cref="IPlantingMarkQuery.MarksInTileRect" />). The adapter is the
  /// only place these become host <c>Vector3Int</c>s.</para>
  /// </summary>
  public interface ICuttingAreaWriter {

    /// <summary>
    /// Designate every <c>(X, Y, Z)</c> in <paramref name="coordinates" /> for
    /// cutting. Idempotent: coordinates already designated are left as-is.
    /// </summary>
    void MarkForCutting(IEnumerable<(int X, int Y, int Z)> coordinates);

    /// <summary>
    /// Remove every <c>(X, Y, Z)</c> in <paramref name="coordinates" /> from
    /// the cutting designation. Idempotent: coordinates not currently
    /// designated are left as-is. Backs the brush's "clear existing marks"
    /// pass — the tool unmarks the active-species tiles in the dragged area
    /// before re-marking the freshly selected subset, so each drag <em>sets</em>
    /// the area to ~X% rather than accumulating across drags.
    /// </summary>
    void UnmarkForCutting(IEnumerable<(int X, int Y, int Z)> coordinates);

  }

}
