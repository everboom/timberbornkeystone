using System.Collections.Generic;

namespace Keystone.Core.Flora {

  /// <summary>
  /// Read-mostly registry of flora blueprints discovered at mod load.
  /// Populated once by a Mod-side loader from
  /// <c>ISpecService.GetSpecs&lt;NaturalResourceSpec&gt;()</c>; consumers
  /// (eco-pressure rules, biome classification, biotic spawning) read it
  /// freely thereafter.
  ///
  /// <para><b>Why a runtime catalog instead of compile-time constants.</b>
  /// Other mods can add, rename, or rebalance flora -- and Keystone's job
  /// is to make ecology emerge from whatever's actually installed. Hard-
  /// coding "Pine grows in 8 days" would lock us to vanilla and break
  /// silently when a mod ships "PineFast" with growth=2. Building the
  /// catalog from the live spec service gives us the world as it is.</para>
  ///
  /// <para><b>Lifecycle.</b> A loader fills the catalog during
  /// <c>ILoadableSingleton.Load()</c> via <see cref="Populate"/>, then
  /// the catalog is effectively read-only. Re-population is allowed (in
  /// case a future mod-loader change adds late-arriving specs) but
  /// callers must accept that consumers may have already read the prior
  /// snapshot.</para>
  /// </summary>
  public sealed class FloraCatalog {

    #region Fields

    private readonly Dictionary<string, FloraEntry> _byBlueprintName = new();

    #endregion

    #region Properties

    /// <summary>Number of entries currently in the catalog.</summary>
    public int Count => _byBlueprintName.Count;

    /// <summary>Iterate every entry, in insertion order.</summary>
    public IEnumerable<FloraEntry> Entries => _byBlueprintName.Values;

    #endregion

    #region Read API

    /// <summary>
    /// Look up a flora entry by its blueprint name (e.g. "Pine.WhitePine").
    /// Returns null when no entry by that name has been catalogued.
    /// </summary>
    public FloraEntry? Get(string blueprintName) =>
        _byBlueprintName.TryGetValue(blueprintName, out var entry) ? entry : null;

    /// <summary>True iff a flora with this blueprint name was discovered at load.</summary>
    public bool Contains(string blueprintName) =>
        _byBlueprintName.ContainsKey(blueprintName);

    /// <summary>Count entries matching <paramref name="kind"/>.</summary>
    public int CountOfKind(FloraKind kind) {
      var n = 0;
      foreach (var e in _byBlueprintName.Values) {
        if (e.Kind == kind) n++;
      }
      return n;
    }

    #endregion

    #region Write API (loader use)

    /// <summary>
    /// Replace the catalog contents with <paramref name="entries"/>.
    /// Intended for one-shot population by a loader at mod-load. Calling
    /// this after consumers have already read the catalog will silently
    /// invalidate their snapshots -- avoid except for explicit rebuilds.
    /// </summary>
    public void Populate(IEnumerable<FloraEntry> entries) {
      _byBlueprintName.Clear();
      foreach (var e in entries) {
        _byBlueprintName[e.BlueprintName] = e;
      }
    }

    #endregion

  }

}
