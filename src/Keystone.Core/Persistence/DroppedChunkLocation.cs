using System.Collections.Generic;
using System.Text;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Where a dropped chunk's ecology data lived on the map, captured so
  /// the load-time and per-flush "chunk(s) could not be matched to a
  /// region" diagnostics can point the player/dev at the affected area
  /// rather than only reporting a count.
  ///
  /// <para>Global chunk coordinates (same lattice as
  /// <see cref="Keystone.Core.Tiles.ChunkCoord"/>); convert to inclusive
  /// tile bounds via <see cref="TileBounds"/> using the field's
  /// <c>RegionEcologyField.ChunkSize</c>. <see cref="Z"/> is the layer the
  /// saved chunk was attached to, or <c>null</c> when the source path had
  /// no Z anchor (a v1 save record without a representative surface).</para>
  /// </summary>
  /// <param name="ChunkX">Chunk X on the global lattice (tile X / ChunkSize).</param>
  /// <param name="ChunkY">Chunk Y on the global lattice (tile Y / ChunkSize).</param>
  /// <param name="Z">Layer the chunk was attached to, or null when unknown.</param>
  public readonly record struct DroppedChunkLocation(int ChunkX, int ChunkY, int? Z) {

    /// <summary>How many sample locations the diagnostics carry before
    /// collapsing the rest into a "+N more area(s)" tail — enough to point
    /// at the affected region(s) without flooding the log on a big drop.</summary>
    public const int SampleCap = 5;

    /// <summary>Inclusive tile-coordinate bounds this chunk covers, given
    /// the field's chunk size.</summary>
    public (int X0, int Y0, int X1, int Y1) TileBounds(int chunkSize) {
      var x0 = ChunkX * chunkSize;
      var y0 = ChunkY * chunkSize;
      return (x0, y0, x0 + chunkSize - 1, y0 + chunkSize - 1);
    }

    /// <summary>Human-readable tile span, e.g. <c>tiles (32..47, 16..31) Z=5</c>.</summary>
    public string Describe(int chunkSize) {
      var (x0, y0, x1, y1) = TileBounds(chunkSize);
      var z = Z.HasValue ? Z.Value.ToString() : "?";
      return $"tiles ({x0}..{x1}, {y0}..{y1}) Z={z}";
    }

    /// <summary>
    /// Render a capped sample of dropped-chunk locations into a single
    /// clause, e.g. <c>near tiles (32..47, 16..31) Z=5; tiles (0..15, 0..15)
    /// Z=3 (+4 more area(s))</c>. Returns an empty string when the sample is
    /// empty so callers can append unconditionally.
    /// </summary>
    /// <param name="sample">Up to <see cref="SampleCap"/> distinct locations.</param>
    /// <param name="totalAreas">Total distinct dropped-chunk areas, so the
    ///   tail can report how many were elided beyond the sample.</param>
    /// <param name="chunkSize">Field chunk size for the tile-bounds math.</param>
    public static string Summarize(
        IReadOnlyList<DroppedChunkLocation>? sample, int totalAreas, int chunkSize) {
      if (sample == null || sample.Count == 0) return "";
      var sb = new StringBuilder("near ");
      for (var i = 0; i < sample.Count; i++) {
        if (i > 0) sb.Append("; ");
        sb.Append(sample[i].Describe(chunkSize));
      }
      var more = totalAreas - sample.Count;
      if (more > 0) sb.Append($" (+{more} more area(s))");
      return sb.ToString();
    }

  }

}
