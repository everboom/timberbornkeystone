using System.Collections.Generic;
using Keystone.Core.Regions;
using Keystone.Core.Spatial;

namespace Keystone.Core.Ecology.Fields {

  /// <summary>
  /// Read-side port for per-region ecology fields. Consumers (eco-health
  /// rules, fauna spawners, debug overlays) inject this and pair the
  /// query "which region contains tile X?" with "what does that region's
  /// field say at tile X?". The implementation owns the rolling polling
  /// cycle that fills the fields; this interface just exposes the
  /// finished fields.
  /// </summary>
  public interface IEcologyFieldQuery {

    /// <summary>
    /// The published field for <paramref name="region"/>, or <c>null</c>
    /// when no field has been built for it yet (typically because the
    /// region was created during the current cycle and hasn't been
    /// reached by the polling sweep).
    /// </summary>
    RegionEcologyField? FieldFor(RegionId region);

    /// <summary>
    /// Resolve a blueprint name to its entity-channel index. Returns
    /// <c>null</c> when the blueprint isn't registered (typically because
    /// it isn't in the flora catalog).
    /// </summary>
    int? EntityIndex(string blueprintName);

    /// <summary>
    /// Blueprint names in entity-channel-index order. Index <c>i</c>
    /// in this list corresponds to <c>RegionEcologyField.SampleEntity(i, ...)</c>.
    /// </summary>
    IReadOnlyList<string> KnownEntityBlueprints { get; }

    /// <summary>
    /// Entity-channel index of the synthetic "mature trees" aggregate —
    /// the per-chunk count of live, fully-grown tree-kind entities,
    /// maintained by the producer alongside the per-blueprint channels.
    /// <c>null</c> when the producer hasn't registered its channels yet
    /// (same too-early window as <see cref="EntityIndex"/> returning
    /// null). Consumers read it via
    /// <c>RegionEcologyField.ChunkValueEntity(index, ...)</c> to derive
    /// <see cref="Biomes.ChunkBiomeInputs.MatureTreeCount"/>.
    /// </summary>
    int? MatureTreeEntityIndex { get; }

    /// <summary>
    /// Monotonic counter bumped whenever the producer side reshapes
    /// the per-region <see cref="RegionEcologyField"/> graph -- a
    /// field allocated for a new region, a field reallocated with a
    /// new bbox after a topology change, or a field dropped because
    /// its region disappeared.
    ///
    /// <para><b>Why this exists alongside <c>RegionService.TopologyVersion</c>.</b>
    /// Both signals advance on terrain edits and region splits /
    /// merges, but they don't advance in lockstep: <c>TopologyVersion</c>
    /// bumps the moment <c>RegionService.ProcessChanges</c> finishes,
    /// while <c>FieldShapeVersion</c> only bumps after the field
    /// updater's next polling cycle reaches the changed regions and
    /// reallocates. Downstream consumers that read both
    /// <c>RegionService</c> state <i>and</i> per-region field state
    /// (notably <c>ChunkBiomeTicker</c>) must rebuild any cached
    /// scratch on either signal — otherwise they can sample one source
    /// at version N and another at version N-1 and end up with a
    /// schedule that misses chunks the field has since added.</para>
    /// </summary>
    int FieldShapeVersion { get; }

    /// <summary>
    /// The per-tile data for <paramref name="region"/>, or <c>null</c>
    /// when no tile data has been built yet. Same lifecycle as
    /// <see cref="FieldFor"/> — allocated and dropped alongside the
    /// region's ecology field.
    /// </summary>
    RegionTileData? TileDataFor(RegionId region);

  }

}
