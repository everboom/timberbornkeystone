using System;
using Keystone.Core.Biomes;
using Keystone.Mod.Diagnostics;
using Timberborn.BlueprintSystem;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// PostLoad populator for <see cref="BiomeLevelTable"/>. Walks
  /// every <see cref="KeystoneBiomeLevelsSpec"/> registered across
  /// all loaded mods and merges them into the per-biome ladder
  /// the rule-handler tier reads.
  ///
  /// <para><b>Two-pass merge.</b> Default-ladder specs (empty
  /// <c>Biome</c>) are applied first to every biome; per-biome
  /// specs are applied on top, overwriting matching level ids.
  /// Both passes route through <see cref="BiomeLevelTable.Define"/>,
  /// which already implements the overwrite-on-duplicate-id semantics.</para>
  ///
  /// <para><b>Bad data is logged loudly, not silently dropped.</b>
  /// Unknown biome strings, non-positive level widths, missing
  /// level ids, all surface as warnings. The calling level entry
  /// is skipped; the rest of the spec's entries proceed.</para>
  /// </summary>
  public sealed class BiomeLevelCatalog : IPostLoadableSingleton {

    private readonly ISpecService _specs;
    private readonly BiomeLevelTable _table;
    private bool _postLoadCompleted;

    /// <summary>True once <see cref="PostLoad"/> has run. Read by the
    /// startup self-check to defer until the catalog is populated.</summary>
    public bool IsLoaded => _postLoadCompleted;

    /// <summary>Biomes the default ladder applies to -- the per-chunk
    /// scored set (<see cref="BiomeValueKinds.AllBiomes"/>). Per-tile
    /// biomes (Riparian) are excluded: their levels come from explicit
    /// per-biome specs (Pass 2), not the default ladder.</summary>
    private static readonly BiomeKind[] AllBiomes = BiomeValueKinds.AllBiomes;

    public BiomeLevelCatalog(ISpecService specs, BiomeLevelTable table) {
      _specs = specs;
      _table = table;
    }

    /// <summary>Run <see cref="PostLoad"/> if it hasn't run this
    /// session. Idempotent: a second call no-ops. Used by
    /// <c>KeystoneStartupWarmup</c> to enforce ordering relative to
    /// Bindito's non-deterministic <c>PostLoad</c> sequence without
    /// depending on <c>OrderingAttribute</c>.</summary>
    public void EnsurePostLoaded() {
      if (_postLoadCompleted) return;
      PostLoad();
    }

    /// <inheritdoc />
    public void PostLoad() {
      if (_postLoadCompleted) return;
      // Outermost try/catch wraps the BiomeLevels build. A failure
      // leaves the table partially populated; _postLoadCompleted
      // stays false so downstream EnsurePostLoaded retries. Spawn
      // logic without a populated level table can't decide what to
      // place per biome and silently produces no flora.
      try {
      _table.Clear();

      var defaultSpecs = 0;
      var biomeSpecs = 0;
      var entriesApplied = 0;
      var entriesSkipped = 0;

      // Pass 1: default-ladder specs (empty Biome) applied to every biome.
      foreach (var spec in _specs.GetSpecs<KeystoneBiomeLevelsSpec>()) {
        if (!string.IsNullOrEmpty(spec.Biome)) continue;
        defaultSpecs++;
        for (var i = 0; i < AllBiomes.Length; i++) {
          var biome = AllBiomes[i];
          foreach (var entry in spec.Levels) {
            if (TryApply(biome, entry, "default ladder")) entriesApplied++;
            else entriesSkipped++;
          }
        }
      }

      // Pass 2: per-biome overrides. Apply on top so they win on conflict.
      foreach (var spec in _specs.GetSpecs<KeystoneBiomeLevelsSpec>()) {
        if (string.IsNullOrEmpty(spec.Biome)) continue;
        biomeSpecs++;
        if (!Enum.TryParse<BiomeKind>(spec.Biome, ignoreCase: true, out var biome)) {
          KeystoneLog.Warn(
              $"[Keystone] BiomeLevelCatalog: spec on blueprint " +
              $"'{spec.Blueprint?.Name ?? "<unbound>"}' has Biome='{spec.Biome}' " +
              $"which is not a known BiomeKind. Spec skipped.");
          entriesSkipped += spec.Levels.Length;
          continue;
        }
        foreach (var entry in spec.Levels) {
          if (TryApply(biome, entry, $"override for {biome}")) entriesApplied++;
          else entriesSkipped++;
        }
      }

      KeystoneLog.Verbose(
          $"[Keystone] BiomeLevelCatalog: {defaultSpecs} default ladder spec(s) + " +
          $"{biomeSpecs} biome override spec(s) -> " +
          $"{entriesApplied} entries applied across {AllBiomes.Length} biome(s), " +
          $"{entriesSkipped} skipped. Table now holds {_table.Count} entries total.");

      _postLoadCompleted = true;
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleError(
            "BiomeLevelCatalog.PostLoad", "Subsystem failed", ex);
      }
    }

    /// <summary>Project a spec-side <see cref="BiomeLevelEntry"/> into
    /// the Core-side <see cref="BiomeLevelInput"/> shape and hand it to
    /// <see cref="BiomeLevelEntryValidator.TryApply"/>. The validator
    /// owns the actual rules — empty-id rejection, range check, density
    /// sentinel / clamp, mode parse fallback — so they're testable
    /// without the Timberborn spec deserializer in the picture.</summary>
    private bool TryApply(BiomeKind biome, BiomeLevelEntry entry, string source) {
      var input = new BiomeLevelInput(
          LevelId: entry.LevelId,
          LowerMaturity: entry.LowerMaturity,
          UpperMaturity: entry.UpperMaturity,
          Density: entry.Density,
          RunAtStartup: entry.RunAtStartup,
          Mode: entry.Mode,
          FaunaCapacityAtSaturation: entry.FaunaCapacityAtSaturation,
          FaunaMinScore: entry.FaunaMinScore);
      return BiomeLevelEntryValidator.TryApply(_table, biome, input, source, KeystoneLog.Warn);
    }

  }

}
