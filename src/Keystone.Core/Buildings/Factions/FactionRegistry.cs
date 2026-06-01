using System;
using System.Collections.Generic;

namespace Keystone.Core.Buildings.Factions {

  /// <summary>
  /// Aggregates per-source building classifications into the global
  /// sets that <see cref="BlueprintNamePolicy"/> exposes to the
  /// adapter. A "source" is either a faction (e.g.
  /// <see cref="FactionFolktails"/>, classifying that faction's own
  /// vanilla content) or a third-party mod (e.g.
  /// <see cref="ModNewBuildingStyles"/>, classifying blueprints a
  /// workshop mod adds across one or more factions). Each source
  /// gets its own file under <c>Buildings/Factions/</c>; the
  /// registry is the single explicit list of sources Keystone knows
  /// about — adding a new one = creating the file plus appending one
  /// row to each contribution array below.
  ///
  /// <para>Aggregation happens at type-initialization. Cross-source
  /// duplicates throw <see cref="InvalidOperationException"/> so a
  /// configuration error (typo or accidental overlap between a
  /// faction file and a mod file) surfaces at startup rather than
  /// silently deduplicating.</para>
  /// </summary>
  public static class FactionRegistry {

    /// <summary>The complete list of sources Keystone classifies.
    /// Adding a new source: create the per-source file, list its
    /// transparent / no-aura contributions there, then append a row
    /// here.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<string>>
        AllTransparentContributions = new[] {
          FactionFolktails.TransparentBuildings,
          FactionIronTeeth.TransparentBuildings,
          FactionLeafCoats.TransparentBuildings,
          FactionEmberpelts.TransparentBuildings,
          ModNewBuildingStyles.TransparentBuildings,
          ModTreeOfLife.TransparentBuildings,
        };

    private static readonly IReadOnlyList<IReadOnlyList<string>>
        AllNoAuraContributions = new[] {
          FactionFolktails.NoAuraBuildings,
          FactionIronTeeth.NoAuraBuildings,
          FactionLeafCoats.NoAuraBuildings,
          FactionEmberpelts.NoAuraBuildings,
          ModNewBuildingStyles.NoAuraBuildings,
          ModTreeOfLife.NoAuraBuildings,
        };

    /// <summary>Per-source Nature-source declarations. Each entry
    /// is one source file's contributions — every per-faction file
    /// contributes (typically) one row targeting its own
    /// <c>Id</c>, every per-mod file contributes one row per faction
    /// whose buildings the mod augments.
    /// <see cref="AllNatureFactions"/> aggregates these by
    /// <see cref="NatureContribution.FactionId"/> into the
    /// consumer-facing per-faction shape.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<NatureContribution>>
        AllNatureContributions = new[] {
          FactionFolktails.NatureContributions,
          FactionIronTeeth.NatureContributions,
          FactionLeafCoats.NatureContributions,
          FactionEmberpelts.NatureContributions,
          ModNewBuildingStyles.NatureContributions,
          ModTreeOfLife.NatureContributions,
        };

    /// <summary>Per-faction Nature-source declarations. Aggregated
    /// from <see cref="AllNatureContributions"/> by
    /// <see cref="NatureContribution.FactionId"/> so the Mod-side
    /// modifier provider can iterate them in one pass and emit
    /// exactly one NeedCollection-append per faction (a duplicate
    /// would be a <c>Dictionary.Add</c> throw downstream). Factions
    /// with no Nature buildings (IronTeeth opts out by design)
    /// yield empty entries; the provider skips those when computing
    /// the per-faction biome union.
    ///
    /// <para>Order preserves first appearance in
    /// <see cref="AllNatureContributions"/> so the canonical
    /// faction listing order (Folktails, IronTeeth, LeafCoats,
    /// Emberpelts) survives the aggregation step.</para></summary>
    public static readonly IReadOnlyList<NatureFactionEntry> AllNatureFactions =
        BuildNatureFactions(AllNatureContributions);

    // Aggregated exact-first / case-insensitive-fallback matchers.
    // Construction throws on exact duplicates AND case-fold collisions
    // across the source lists (a name's faction/mod suffix normally
    // makes cross-source overlap impossible; the throw guards typos and
    // a mod accidentally re-listing a vanilla blueprint). Case-tolerant
    // because Timberborn's own naming is inconsistent and a faction
    // blueprint's runtime Name can disagree with its catalog path casing
    // (e.g. Emberpelts ships "Farmhouse" though its path says
    // "FarmHouse") — see <see cref="CaseTolerantNameSet"/>.
    private static readonly CaseTolerantNameSet _transparent =
        new CaseTolerantNameSet("transparent", Flatten(AllTransparentContributions));
    private static readonly CaseTolerantNameSet _noAura =
        new CaseTolerantNameSet("no-aura", Flatten(AllNoAuraContributions));

    /// <summary>Union of every source's transparent-building names,
    /// deduplicated. Read-only view; callers can't mutate.</summary>
    public static IReadOnlyCollection<string> AllTransparent => _transparent.Names;

    /// <summary>Union of every source's no-aura-building names,
    /// deduplicated with the same throw-on-duplicate guarantee.</summary>
    public static IReadOnlyCollection<string> AllNoAura => _noAura.Names;

    /// <summary>O(1) exact-first, case-insensitive-fallback membership
    /// in the transparent set. Used by
    /// <see cref="BlueprintNamePolicy.IsTransparentByName"/>.</summary>
    public static bool IsTransparent(string blueprintName) =>
        _transparent.Contains(blueprintName);

    /// <summary>O(1) exact-first, case-insensitive-fallback membership
    /// in the no-aura set. Used by
    /// <see cref="BlueprintNamePolicy.IsNoAuraByName"/>.</summary>
    public static bool IsNoAura(string blueprintName) =>
        _noAura.Contains(blueprintName);

    /// <summary>Resolve <paramref name="blueprintName"/> against the
    /// transparent set, distinguishing an exact hit from a
    /// case-insensitive fallback (so callers can surface casing drift).
    /// </summary>
    public static NameMatch MatchTransparent(string blueprintName, out string? canonical) =>
        _transparent.Match(blueprintName, out canonical);

    /// <summary>Resolve <paramref name="blueprintName"/> against the
    /// no-aura set, distinguishing an exact hit from a case-insensitive
    /// fallback.</summary>
    public static NameMatch MatchNoAura(string blueprintName, out string? canonical) =>
        _noAura.Match(blueprintName, out canonical);

    /// <summary>
    /// Scan <paramref name="actualBlueprintNames"/> (the real runtime
    /// names from the loaded catalog) and yield every case where a name
    /// matches a transparent or no-aura entry ONLY via the
    /// case-insensitive fallback — i.e. the list's casing has drifted
    /// from reality. Pure; the Mod layer feeds it the catalog and logs
    /// the result. Each tuple is
    /// <c>(actual runtime name, listed canonical name, which list)</c>.
    /// </summary>
    public static IEnumerable<(string Actual, string Listed, string List)> FindCasingDrift(
        IEnumerable<string> actualBlueprintNames) {
      if (actualBlueprintNames == null) yield break;
      foreach (var name in actualBlueprintNames) {
        if (_noAura.Match(name, out var noAuraCanonical) == NameMatch.CaseInsensitiveFallback) {
          yield return (name, noAuraCanonical!, "no-aura");
        }
        if (_transparent.Match(name, out var transparentCanonical) == NameMatch.CaseInsensitiveFallback) {
          yield return (name, transparentCanonical!, "transparent");
        }
      }
    }

    /// <summary>Aggregate per-source <see cref="NatureContribution"/>
    /// rows into one <see cref="NatureFactionEntry"/> per
    /// distinct <see cref="NatureContribution.FactionId"/>. Order of
    /// the output mirrors the order in which each FactionId is first
    /// encountered while walking the input lists, so per-faction
    /// source files (which appear before per-mod source files in
    /// <see cref="AllNatureContributions"/>) determine the canonical
    /// ordering.</summary>
    private static IReadOnlyList<NatureFactionEntry> BuildNatureFactions(
        IReadOnlyList<IReadOnlyList<NatureContribution>> contributions) {
      var orderedFactionIds = new List<string>();
      var perFactionBuildings = new Dictionary<string, List<NatureBuilding>>(
          StringComparer.Ordinal);
      for (var i = 0; i < contributions.Count; i++) {
        var list = contributions[i];
        for (var j = 0; j < list.Count; j++) {
          var contribution = list[j];
          if (!perFactionBuildings.TryGetValue(contribution.FactionId,
                                               out var bucket)) {
            bucket = new List<NatureBuilding>();
            perFactionBuildings[contribution.FactionId] = bucket;
            orderedFactionIds.Add(contribution.FactionId);
          }
          for (var k = 0; k < contribution.Buildings.Count; k++) {
            bucket.Add(contribution.Buildings[k]);
          }
        }
      }
      var result = new List<NatureFactionEntry>(orderedFactionIds.Count);
      for (var i = 0; i < orderedFactionIds.Count; i++) {
        var id = orderedFactionIds[i];
        result.Add(new NatureFactionEntry(id, perFactionBuildings[id]));
      }
      return result;
    }

    /// <summary>Flatten the per-source contribution lists into one
    /// sequence of canonical names. Deduplication and throw-on-collision
    /// (exact and case-fold) now live in
    /// <see cref="CaseTolerantNameSet"/>'s constructor, which consumes
    /// this sequence.</summary>
    private static IEnumerable<string> Flatten(
        IReadOnlyList<IReadOnlyList<string>> contributions) {
      for (var i = 0; i < contributions.Count; i++) {
        var list = contributions[i];
        for (var j = 0; j < list.Count; j++) {
          yield return list[j];
        }
      }
    }

  }

}
