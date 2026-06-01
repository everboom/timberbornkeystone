using System.Diagnostics;
using System.Linq;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Survey;
using Keystone.Mod.Diagnostics;
using Timberborn.SingletonSystem;
using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Survey {

  /// <summary>
  /// Mod-side trigger for the Core <see cref="TerrainSurveyor"/> +
  /// <see cref="RegionService"/>. On settlement post-load, runs one
  /// full-map survey and indexes regions so subsequent debug/visual
  /// systems can read region data.
  ///
  /// Uses <see cref="IPostLoadableSingleton"/> rather than
  /// <see cref="ILoadableSingleton"/> because <c>ITerrainService.MaxTerrainHeight</c>
  /// is updated by lazy events and is not yet valid during the Load phase.
  /// </summary>
  public sealed class KeystoneSurveyor : IPostLoadableSingleton {

    #region Fields

    private readonly TerrainSurveyor _surveyor;
    private readonly RegionService _regions;
    private readonly IMoistureQuery _moisture;
    private readonly IContaminationQuery _contamination;
    private readonly IWaterQuery _water;
    private bool _postLoadCompleted;

    #endregion

    #region Properties

    /// <summary>The Core surveyor -- exposed so other Mod-side singletons can read its surface map.</summary>
    public TerrainSurveyor Core => _surveyor;

    /// <summary>The region index, populated after <see cref="PostLoad"/>.</summary>
    public RegionService Regions => _regions;

    #endregion

    #region Construction

    public KeystoneSurveyor(
        TerrainSurveyor surveyor,
        RegionService regions,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IWaterQuery water) {
      _surveyor = surveyor;
      _regions = regions;
      _moisture = moisture;
      _contamination = contamination;
      _water = water;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <summary>
    /// True once the surveyor has finished its <see cref="PostLoad"/>
    /// pass. Lets dependents (notably <c>KeystonePersistence</c>) check
    /// before forcing the survey through <see cref="EnsurePostLoaded"/>
    /// rather than calling unconditionally and risking double-survey.
    /// </summary>
    public bool PostLoadCompleted => _postLoadCompleted;

    /// <summary>
    /// Run <see cref="PostLoad"/> if it hasn't already run this session.
    /// Idempotent: a second call is a no-op. Used to enforce ordering
    /// between <c>KeystonePersistence.PostLoad</c> and this surveyor's
    /// own PostLoad without depending on Timberborn's
    /// <c>OrderingAttribute</c> (whose constructor shape isn't covered
    /// by the generated API dump and we couldn't confirm cleanly).
    /// </summary>
    public void EnsurePostLoaded() {
      if (_postLoadCompleted) return;
      PostLoad();
    }

    /// <inheritdoc />
    public void PostLoad() {
      // Re-entrancy / double-call guard. KeystonePersistence.PostLoad()
      // forces this surveyor to run first (so freshly-Indexed regions
      // exist before stamps are applied), and then Timberborn's lifecycle
      // calls our PostLoad afterwards. The guard prevents the second
      // call from re-running survey + Index, which would clobber the
      // restored stamps.
      if (_postLoadCompleted) return;

      // Outermost try/catch: a throw out of Survey/Index would leave
      // the surveyor un-completed and every downstream consumer (region
      // service, ecology field updater, cluster index, all per-tile
      // queries) running against an empty surface map. Catch + record
      // so the dialog can tell the user; _postLoadCompleted stays false
      // so the next EnsurePostLoaded call retries (rather than silently
      // skipping a future re-attempt).
      try {
        var sw = Stopwatch.StartNew();
        var result = _surveyor.Survey();
        var surveyMs = sw.ElapsedMilliseconds;

        sw.Restart();
        _regions.Index();
        var indexMs = sw.ElapsedMilliseconds;

        KeystoneLog.Verbose($"[Keystone] Surveyed {result.Surfaces} surfaces across {result.Columns} columns in {surveyMs} ms.");

        // Structural fields come from the cached SurfaceSurvey; volatile
        // ecological fields go through the live ports (the surveyor
        // doesn't cache those any more -- see SurfaceSurvey docstring).
        var caves = _surveyor.Surfaces.Entries.Count(e => e.Value.IsCave);
        var settled = _surveyor.Surfaces.Entries.Count(e => e.Value.IsSettled);
        var moist = _surveyor.Surfaces.Entries.Count(e => _moisture.IsMoistAt(e.Key));
        var contam = _surveyor.Surfaces.Entries.Count(e => _contamination.IsContaminatedAt(e.Key));
        var underwater = _surveyor.Surfaces.Entries.Count(e => _water.WaterDepthAt(e.Key) > 0f);
        var flowing = _surveyor.Surfaces.Entries.Count(e => !_water.FlowAt(e.Key).IsZero);
        KeystoneLog.Verbose($"[Keystone] Surface stats: caves={caves} settled={settled} moist={moist} contam={contam} underwater={underwater} flowing={flowing}");

        var biggest = _regions.All.OrderByDescending(r => r.Size).FirstOrDefault();
        if (biggest != null) {
          KeystoneLog.Verbose($"[Keystone] Indexed {_regions.Count} regions in {indexMs} ms; biggest={biggest.Id} ({biggest.Size} surfaces, z={biggest.Z}, cave={biggest.IsCave}); created at cycle {biggest.CreatedAt.Cycle} day {biggest.CreatedAt.CycleDay} ({biggest.WeatherAtCreation}).");
        } else {
          KeystoneLog.Verbose($"[Keystone] Indexed 0 regions in {indexMs} ms.");
        }

        _postLoadCompleted = true;
      } catch (System.Exception ex) {
        LifecycleGuard.HandleError("KeystoneSurveyor.PostLoad", "Subsystem failed", ex);
      }
    }

    #endregion

  }

}
