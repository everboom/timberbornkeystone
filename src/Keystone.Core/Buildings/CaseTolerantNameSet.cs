using System;
using System.Collections.Generic;

namespace Keystone.Core.Buildings {

  /// <summary>How a query resolved against a
  /// <see cref="CaseTolerantNameSet"/>.</summary>
  public enum NameMatch {

    /// <summary>No entry matched, even case-insensitively.</summary>
    None,

    /// <summary>An entry matched with exact (ordinal) casing — the
    /// normal, preferred path.</summary>
    Exact,

    /// <summary>No exact entry matched, but one matched
    /// case-insensitively. The runtime name and the listed canonical
    /// name agree only after case folding — i.e. the list's casing has
    /// drifted from the real blueprint name. Treated as a match, but
    /// worth surfacing so the list can be corrected.</summary>
    CaseInsensitiveFallback,
  }

  /// <summary>
  /// A fixed set of canonical blueprint names with <b>exact-first,
  /// case-insensitive-fallback</b> membership. Built once from a
  /// hand-maintained list; an exact (ordinal) hit always wins, and a
  /// case-insensitive hit is the lenient fallback that tolerates casing
  /// drift between our lists and a mod's actual runtime
  /// <c>Blueprint.Name</c> (Timberborn's own naming is inconsistent —
  /// e.g. <c>Farmhouse</c> vs <c>FarmHouse</c> vs <c>AquaticFarmhouse</c>,
  /// and faction blueprints' internal <c>Name</c> can disagree with their
  /// catalog file path's casing).
  ///
  /// <para><b>The fallback is deterministic.</b> Two facts guarantee a
  /// query resolves to at most one canonical name:
  /// <list type="number">
  ///   <item>Timberborn allows only one blueprint per exact <c>Name</c>
  ///         (<c>TemplateNameMapper</c> throws on duplicates), so the
  ///         runtime never presents two different blueprints under the
  ///         same name to classify.</item>
  ///   <item>Construction throws on any <b>case-fold collision</b> among
  ///         the canonical entries (two list entries differing only by
  ///         case), so the case-insensitive index can never map one
  ///         folded key to two different canonical spellings.</item>
  /// </list>
  /// Exact duplicates also throw, preserving the old
  /// throw-on-duplicate guarantee that surfaced list typos at
  /// startup.</para>
  ///
  /// <para>Lookups are O(1). The exact-vs-fallback distinction is
  /// derived by an ordinal compare of the query against the stored
  /// canonical spelling, so only one dictionary is needed.</para>
  /// </summary>
  public sealed class CaseTolerantNameSet {

    // OrdinalIgnoreCase key (the folded name) -> canonical (ordinal)
    // spelling as listed. The dictionary's comparer does the folding;
    // the stored value preserves the exact casing so Match can tell an
    // exact hit from a fallback hit.
    private readonly Dictionary<string, string> _byFoldedName;

    /// <summary>Build the set from <paramref name="names"/>.</summary>
    /// <param name="label">Human-readable set name used in the
    /// exceptions thrown on a duplicate or case-fold collision (e.g.
    /// <c>"no-aura"</c>).</param>
    /// <param name="names">Canonical blueprint names. Must contain no
    /// null/empty entries, no exact duplicates, and no two entries
    /// differing only by case.</param>
    /// <exception cref="ArgumentNullException"><paramref name="names"/>
    /// is null.</exception>
    /// <exception cref="InvalidOperationException">A null/empty entry, an
    /// exact duplicate, or a case-fold collision is present.</exception>
    public CaseTolerantNameSet(string label, IEnumerable<string> names) {
      if (names == null) throw new ArgumentNullException(nameof(names));
      _byFoldedName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var name in names) {
        if (string.IsNullOrEmpty(name)) {
          throw new InvalidOperationException(
              $"{label}: null or empty blueprint name in the list.");
        }
        if (_byFoldedName.TryGetValue(name, out var existing)) {
          throw new InvalidOperationException(
              string.Equals(existing, name, StringComparison.Ordinal)
                  ? $"{label}: duplicate blueprint name '{name}'. "
                    + "Each per-source list (faction or mod) must hold its own "
                    + "blueprints only — likely a typo, or a mod file re-listing a "
                    + "name a faction file already covers."
                  : $"{label}: case-fold collision between '{name}' and '{existing}' — "
                    + "two entries differ only by case, which would make the "
                    + "case-insensitive fallback ambiguous. Use one canonical spelling.");
        }
        _byFoldedName[name] = name;
      }
    }

    /// <summary>The canonical names, in unspecified order. Read-only
    /// view over the backing store.</summary>
    public IReadOnlyCollection<string> Names => _byFoldedName.Values;

    /// <summary>Number of canonical names.</summary>
    public int Count => _byFoldedName.Count;

    /// <summary>
    /// Resolve <paramref name="name"/> exact-first, then
    /// case-insensitively. On a hit, <paramref name="canonical"/> is the
    /// listed spelling (equal to <paramref name="name"/> for an
    /// <see cref="NameMatch.Exact"/> hit; differing only in case for a
    /// <see cref="NameMatch.CaseInsensitiveFallback"/> hit). Null on
    /// <see cref="NameMatch.None"/>.
    /// </summary>
    public NameMatch Match(string name, out string? canonical) {
      canonical = null;
      if (string.IsNullOrEmpty(name)) return NameMatch.None;
      if (!_byFoldedName.TryGetValue(name, out var c)) return NameMatch.None;
      canonical = c;
      return string.Equals(name, c, StringComparison.Ordinal)
          ? NameMatch.Exact
          : NameMatch.CaseInsensitiveFallback;
    }

    /// <summary>True iff <paramref name="name"/> matches an entry exactly
    /// or via the case-insensitive fallback.</summary>
    public bool Contains(string name) => Match(name, out _) != NameMatch.None;
  }

}
