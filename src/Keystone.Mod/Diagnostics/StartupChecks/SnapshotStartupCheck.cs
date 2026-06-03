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

      // Signal 1 -- lost ecology maturity. We alarm on the count of distinct
      // chunk *areas* that lost accumulated maturity, NOT the raw dropped
      // value-row count. Maturity is the long-term ecology history the player
      // watches accrue; the suitability channel re-derives within a few ticks,
      // so a suitability-only / empty drop is benign churn. Counting value rows
      // over-reported badly: one destroyed chunk is ~10-20 rows (suitability +
      // maturity across every biome), so a single legitimately-removed chunk
      // tripped the old floor. (The mid-game reconciler already split maturity
      // vs empty; this brings load into line with it.)
      //
      // Player.log retains the full report via the Verbose line in
      // KeystonePersistence.PostLoadInner regardless of whether this warning
      // fires, so devs investigating a player report always have the counts.
      // The mid-game counterpart is the per-flush warning in RegionUpdater.Flush
      // (ChunkReconciler drops). Note Save now sweeps footprint orphans before
      // writing, so on a save written by this version a maturity drop at load
      // means terrain genuinely changed *between* save and load (external edit
      // or version/mod-set topology change), not a stale orphan we failed to
      // clean up.
      if (report.DroppedChunkAreasWithMaturity >= MaturityDropFloorAreas) {
        // Player line gets the plain-English count of affected patches; the
        // precise tile-span + Z sample (dev jargon) goes only into
        // DetailedMessage and the Player.log line, never the dialog body a
        // release player reads.
        var where = DroppedChunkLocation.Summarize(
            report.DroppedChunkSample, report.DroppedChunkAreasWithMaturity, RegionEcologyField.ChunkSize);
        yield return new StartupFinding(
            StartupFindingSeverity.Warning,
            $"About {report.DroppedChunkAreasWithMaturity} area(s) of the map lost saved " +
            "ecology maturity that couldn't be matched to the loaded terrain. They'll " +
            "rebuild their biome over the next few game-days.",
            DetailedMessage:
                $"DroppedChunkAreasWithMaturity={report.DroppedChunkAreasWithMaturity} of " +
                $"{report.DroppedChunkAreas} dropped chunk area(s) " +
                $"({report.DroppedChunkValues} value row(s), incl. recomputable suitability) " +
                $"out of {report.ChunkValueCount} saved; rescued {report.RescuedChunkValues} " +
                "via spatial-footprint lookup. A maturity drop means no live region existed " +
                "at a saved chunk's footprint+Z -- terrain removed between save and load, or a " +
                "version/mod-set change altered region topology. Save sweeps footprint orphans " +
                "before writing, so a large count here on an unchanged map points at an external " +
                "terrain edit or a load-path regression." +
                (where.Length > 0 ? $" Locations: {where}." : ""));
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
    /// Floor of distinct chunk <i>areas</i> that lost accumulated maturity,
    /// at or above which the ecology-loss warning fires. A small multi-chunk
    /// cluster -- low enough to catch a patch of matured terrain vanishing,
    /// high enough not to nag when the player terraforms a tile or two of
    /// matured ground between save and load. Player.log always carries the
    /// exact counts regardless.
    ///
    /// <para><b>Unit change.</b> This used to be a floor of 8 dropped value
    /// <i>rows</i> (with a companion percentage). That over-reported: one
    /// destroyed chunk is ~10-20 rows (suitability + maturity per biome), so a
    /// single removed chunk tripped it, and the recomputable suitability
    /// channel counted as "lost ecology." The unit is now distinct chunk areas
    /// that lost real maturity -- the honest measure of what the player feels,
    /// matching the mid-game reconciler's maturity-vs-empty split. The
    /// percentage check was dropped: a 3-area floor already handles small saves
    /// (three patches of matured ground resetting is worth a one-line note
    /// regardless of map size).</para>
    /// </summary>
    private const int MaturityDropFloorAreas = 3;

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
