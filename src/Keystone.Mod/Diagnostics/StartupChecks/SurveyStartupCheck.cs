using System.Collections.Generic;
using Keystone.Mod.Survey;

namespace Keystone.Mod.Diagnostics.StartupChecks {

  /// <summary>
  /// Verifies the surveyor produced a non-empty world picture. An
  /// empty surveyor at PostLoad-time means the terrain query came
  /// back with no surfaces or the region indexer produced zero
  /// regions -- almost certainly broken since real Timberborn maps
  /// always have terrain.
  /// </summary>
  public sealed class SurveyStartupCheck : IStartupCheck {

    private readonly KeystoneSurveyor _surveyor;

    public SurveyStartupCheck(KeystoneSurveyor surveyor) {
      _surveyor = surveyor;
    }

    /// <inheritdoc />
    public string Category => "Survey";

    /// <inheritdoc />
    public bool IsReady => _surveyor.PostLoadCompleted;

    /// <inheritdoc />
    public IEnumerable<StartupFinding> Run() {
      var surfaceCount = _surveyor.Core.Surfaces.Count;
      var regionCount = _surveyor.Regions.Count;

      if (surfaceCount == 0) {
        yield return new StartupFinding(
            StartupFindingSeverity.Error,
            "Keystone couldn't read the terrain. Ecology won't function " +
            "on this map.",
            DetailedMessage:
                "Surveyor reported zero surfaces; ITerrainQuery returned " +
                "nothing or the surveyor never ran.");
        yield break;  // region check below would be redundant
      }

      if (regionCount == 0) {
        yield return new StartupFinding(
            StartupFindingSeverity.Error,
            "Keystone couldn't divide the map into ecology regions. " +
            "Ecology won't function on this map.",
            DetailedMessage:
                $"{surfaceCount} surface(s) surveyed but region indexer " +
                "produced zero regions.");
      }
    }

  }

}
