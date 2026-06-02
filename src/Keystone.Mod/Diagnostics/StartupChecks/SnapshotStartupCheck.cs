using System.Collections.Generic;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Persistence;
using Keystone.Mod.Persistence;

namespace Keystone.Mod.Diagnostics.StartupChecks {

  /// <summary>
  /// Checks the state Keystone read from the saved blob. New games
  /// are silent; an existing save with no Keystone state, a
  /// future-version save, or a save whose region/value counts come
  /// back surprisingly empty all produce findings.
  ///
  /// <para>The "save written before Keystone was installed" case
  /// (existing settlement, no Keystone blob) is intentionally
  /// surfaced once -- a player who installs the mod mid-playthrough
  /// should understand why their first reload starts with empty
  /// ecology state.</para>
  /// </summary>
  public sealed class SnapshotStartupCheck : IStartupCheck {

    private readonly KeystonePersistence _persistence;

    public SnapshotStartupCheck(KeystonePersistence persistence) {
      _persistence = persistence;
    }

    /// <inheritdoc />
    public string Category => "Save state";

    /// <inheritdoc />
    public bool IsReady => _persistence.IsLoaded && _persistence.IsPostLoaded;

    /// <inheritdoc />
    public IEnumerable<StartupFinding> Run() {
      // New games have no save to validate. Silent.
      if (_persistence.IsNewGame) yield break;

      var report = _persistence.LoadReport;

      if (!report.HasSnapshot) {
        yield return new StartupFinding(
            StartupFindingSeverity.Warning,
            "Keystone was installed on an existing settlement. Ecology " +
            "will build up from scratch as you play.",
            DetailedMessage:
                "No Keystone singleton blob present in the save -- save " +
                "predates the mod install. SnapshotLoadReport.HasSnapshot=false.");
        yield break;
      }

      if (report.SchemaVersion > SnapshotCodec.CurrentSchemaVersion) {
        yield return new StartupFinding(
            StartupFindingSeverity.Warning,
            "This save was written by a newer version of Keystone. " +
            "Some data may be ignored.",
            DetailedMessage:
                $"Save schema version {report.SchemaVersion} > supported " +
                $"{SnapshotCodec.CurrentSchemaVersion}; loading best-effort.");
      }

      // Two distinct signals, deliberately separated. The old check
      // lumped region stamps, region values, and chunk values into one
      // map-wide retention percentage -- a hamfisted tool that (a) let the
      // usually-large region-record counts drown out chunk drops, and (b)
      // was really built to answer a migration question ("can we carry
      // region-level state across a field-config change?"), not an
      // ecology-integrity one. A whole cluster of chunks losing its
      // maturity is a handful of dropped chunk values out of thousands --
      // far under 5% map-wide, so the old percentage stayed silent on
      // exactly the symptom we care about.
      //
      // Player.log retains the full report via the Verbose line in
      // KeystonePersistence.PostLoadInner regardless of whether either
      // warning fires, so devs investigating a player report always have
      // the counts. The mid-game counterpart to this is the per-flush
      // warning in RegionUpdater.Flush (ChunkReconciler drops).

      // Signal 1 -- lost ecology maturity. Chunk values carry the per-chunk
      // biome Maturity/Suitability the player watches accrue; a drop is a
      // patch of map whose ecology resets to bare. Its own, tighter check.
      if (report.ChunkValueCount > 0 && report.DroppedChunkValues > 0) {
        var droppedPct = report.DroppedChunkValues * 100.0 / report.ChunkValueCount;
        if (report.DroppedChunkValues >= ChunkDropFloor || droppedPct >= ChunkDropWarnPct) {
          // Player line gets the plain-English count of affected patches;
          // the precise tile-span + Z sample (dev jargon) goes only into
          // DetailedMessage and the Player.log line below, never the dialog
          // body a release player reads.
          var where = DroppedChunkLocation.Summarize(
              report.DroppedChunkSample, report.DroppedChunkAreas, RegionEcologyField.ChunkSize);
          yield return new StartupFinding(
              StartupFindingSeverity.Warning,
              $"About {report.DroppedChunkValues} saved ecology value(s) could not " +
              "be matched to the loaded map and were reset" +
              (report.DroppedChunkAreas > 0 ? $" in {report.DroppedChunkAreas} area(s) of the map" : "") +
              ". Affected areas will rebuild their biome over the next few game-days.",
              DetailedMessage:
                  $"DroppedChunkValues={report.DroppedChunkValues} of " +
                  $"{report.ChunkValueCount} ({droppedPct:F1}%) across " +
                  $"{report.DroppedChunkAreas} chunk area(s); rescued " +
                  $"{report.RescuedChunkValues} via spatial-footprint lookup. A drop " +
                  "means no live region existed at a saved chunk's footprint+Z -- " +
                  "terrain edited between save and load, or a version/mod-set change " +
                  "altered region topology. A large count on an unchanged map points " +
                  "at a load-path regression." +
                  (where.Length > 0 ? $" Locations: {where}." : ""));
        }
      }

      // Signal 2 -- region-level migration. The original percentage's real
      // job: did region stamps/values survive a topology/field-config
      // change? Kept coarse (these rebuild cheaply and don't reset visible
      // ecology).
      var regionSaved = report.RegionCount + report.RegionValueCount;
      var regionDropped = report.DroppedRegionStamps + report.DroppedRegionValues;
      if (regionDropped > 0 && regionSaved > 0) {
        var preservedPct = (int)System.Math.Round(
            (regionSaved - regionDropped) * 100.0 / regionSaved);
        if (preservedPct < RegionSilenceAbovePct) {
          yield return new StartupFinding(
              StartupFindingSeverity.Warning,
              "A version update or terrain change may have invalidated some saved " +
              $"region data. About {preservedPct}% of region-level state was preserved.",
              DetailedMessage:
                  $"Dropped {report.DroppedRegionStamps} region stamp(s), " +
                  $"{report.DroppedRegionValues} region value(s) of {regionSaved} " +
                  $"region-level records (matched {report.MatchedRegionStamps}, " +
                  $"recovered {report.RecoveredRegionStamps} via representative surface).");
        }
      }
    }

    /// <summary>
    /// Absolute floor of dropped chunk values above which the ecology-loss
    /// warning fires regardless of percentage. Sized at roughly a small
    /// multi-chunk cluster's worth of value entries (a chunk carries
    /// several kinds, so a ~4-chunk cluster is on this order) -- low enough
    /// to catch a whole cluster vanishing, high enough not to nag on the
    /// normal case of the player terraforming a tile or two between save
    /// and load. Player.log always carries the exact counts regardless.
    /// </summary>
    private const int ChunkDropFloor = 8;

    /// <summary>Fraction of saved chunk values whose loss warns even below
    /// <see cref="ChunkDropFloor"/> -- catches proportionally large losses
    /// on small saves.</summary>
    private const double ChunkDropWarnPct = 2.0;

    /// <summary>
    /// Preserved-percentage threshold above which the region-level
    /// migration warning stays silent. 95% tolerates up to 5% region-stamp
    /// drops -- typical for a single version-update topology change.
    /// Region-level only now; chunk (ecology) drops have their own check
    /// above.
    /// </summary>
    private const int RegionSilenceAbovePct = 95;

  }

}
