using System;
using System.Collections.Generic;
using System.Text;
using Keystone.Mod.Diagnostics;
using Timberborn.SingletonSystem;
using Timberborn.TemplateCollectionSystem;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// One-shot diagnostic: dumps every <see cref="Timberborn.BlueprintSystem.Blueprint.Name"/>
  /// in <see cref="TemplateCollectionService.AllTemplates"/> whose name
  /// starts with any stem in <see cref="CandidateStems"/>. Used to lock
  /// down the literal blueprint-name strings for the upcoming
  /// blocked-region whitelist (folder names under
  /// <c>Resources/mapeditor/objects/</c> suggest the prefixes, but only
  /// the runtime catalog can confirm the exact PascalCase casing for
  /// numbered variants).
  ///
  /// <para>Stems with zero matches are logged separately so missing
  /// categories are obvious.</para>
  ///
  /// <para>Unbound by default. Uncomment its <c>Bind</c> in
  /// <c>KeystoneConfigurator</c> to run the dump once, then re-comment
  /// once the names are captured.</para>
  /// </summary>
  public sealed class BlockingCandidateProbe : IPostLoadableSingleton {

    #region Constants

    /// <summary>
    /// Folder-name stems found under <c>Resources/mapeditor/objects/</c>
    /// that are expected to map to obstructive natural block objects.
    /// Relics, slopes, and thorns are intentionally absent per the
    /// design discussion -- they are NOT meant to block regions.
    /// </summary>
    private static readonly string[] CandidateStems = {
        "Blockage",
        "NaturalDam",
        "NaturalOverhang",
        "UnstableCore",
        "GeothermalField",
        "ReservePile",
        "ReserveTank",
        "ReserveWarehouse",
    };

    #endregion

    #region Fields

    private readonly TemplateCollectionService _templateCollections;

    #endregion

    #region Construction

    public BlockingCandidateProbe(TemplateCollectionService templateCollections) {
      _templateCollections = templateCollections;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      try {
        var matchesByStem = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var stem in CandidateStems) {
          matchesByStem[stem] = new List<string>();
        }

        foreach (var bp in _templateCollections.AllTemplates) {
          foreach (var stem in CandidateStems) {
            if (bp.Name.StartsWith(stem, StringComparison.Ordinal)) {
              matchesByStem[stem].Add(bp.Name);
              break;
            }
          }
        }

        var buffer = new StringBuilder();
        buffer.AppendLine("[Keystone] BlockingCandidateProbe: blueprint names matching blocked-region stems");
        foreach (var stem in CandidateStems) {
          var names = matchesByStem[stem];
          if (names.Count == 0) {
            buffer.Append("  ").Append(stem).AppendLine(": <no matches>");
            continue;
          }
          names.Sort(StringComparer.Ordinal);
          buffer.Append("  ").Append(stem).Append(":");
          foreach (var name in names) {
            buffer.Append(' ').Append(name);
          }
          buffer.AppendLine();
        }
        KeystoneLog.Verbose(buffer.ToString());
      } catch (Exception ex) {
        KeystoneLog.Verbose($"[Keystone] BlockingCandidateProbe: dump failed: {ex}");
      }
    }

    #endregion

  }

}
