using System;

namespace Keystone.Core.Biomes {

  /// <summary>
  /// Validates a <see cref="BiomeLevelInput"/> and, on success, applies
  /// it to a <see cref="BiomeLevelTable"/> via
  /// <see cref="BiomeLevelTable.Define"/>. Splits the Mod-side
  /// <c>BiomeLevelCatalog</c>'s parse/validate/clamp logic from its
  /// I/O (<c>ISpecService</c> walk + warning sink) so the rules
  /// — sentinel-density fallback, density clamp, mode parse, range
  /// ordering — are testable without standing up a Timberborn host.
  ///
  /// <para>Pure: all rejected inputs go through the
  /// <paramref name="warn"/> callback (the Mod-side caller routes that
  /// to <c>UDebug.LogWarning</c>; tests route it to a list). The
  /// validator does not throw.</para>
  /// </summary>
  public static class BiomeLevelEntryValidator {

    /// <summary>Default activation density when the input omits
    /// <c>Density</c> (sentinel <c>-1f</c>). 10% matches the historical
    /// per-recipe MaxDensity default. Explicit <c>0</c> is <i>not</i>
    /// rewritten to this default — it's a valid choice meaning "no
    /// deterministic activation," distinct from "unset → fall back."</summary>
    public const float DefaultDensity = 0.10f;

    /// <summary>Validate <paramref name="input"/> and define a level on
    /// <paramref name="table"/> for <paramref name="biome"/> if it
    /// passes. Returns <c>true</c> on a successful define, <c>false</c>
    /// on any validation failure (one <paramref name="warn"/> call per
    /// failure, prefixed with <paramref name="source"/> so the caller
    /// can identify which spec the entry came from).
    ///
    /// <para>Rules applied, in order:</para>
    /// <list type="number">
    ///   <item>Empty <c>LevelId</c> → reject.</item>
    ///   <item><c>UpperMaturity</c> not strictly greater than
    ///         <c>LowerMaturity</c> → reject.</item>
    ///   <item>Negative <c>LowerMaturity</c> → reject.</item>
    ///   <item><c>Density &lt; 0</c> → use <see cref="DefaultDensity"/>
    ///         (no warning — sentinel by design).</item>
    ///   <item><c>Density &gt; 1</c> → clamp to 1, warn.</item>
    ///   <item>Non-empty <c>Mode</c> unknown to
    ///         <see cref="LevelDispatchMode"/> → warn and fall back to
    ///         <see cref="LevelDispatchMode.Deterministic"/>; entry still
    ///         defined.</item>
    ///   <item>Empty <c>Mode</c> → silently
    ///         <see cref="LevelDispatchMode.Deterministic"/>.</item>
    /// </list></summary>
    public static bool TryApply(
        BiomeLevelTable table,
        BiomeKind biome,
        BiomeLevelInput input,
        string source,
        Action<string> warn) {
      if (string.IsNullOrEmpty(input.LevelId)) {
        warn($"[Keystone] BiomeLevelEntryValidator: empty LevelId in {source} entry. Skipped.");
        return false;
      }
      if (!(input.UpperMaturity > input.LowerMaturity)) {
        warn($"[Keystone] BiomeLevelEntryValidator: invalid range for level " +
             $"'{input.LevelId}' on {biome} ({source}): " +
             $"upper={input.UpperMaturity}, lower={input.LowerMaturity}. " +
             "Upper must exceed lower; entry skipped.");
        return false;
      }
      if (input.LowerMaturity < 0f) {
        warn($"[Keystone] BiomeLevelEntryValidator: negative LowerMaturity " +
             $"({input.LowerMaturity}) for level '{input.LevelId}' on {biome} " +
             $"({source}). Entry skipped.");
        return false;
      }

      float density;
      if (input.Density < 0f) {
        density = DefaultDensity;
      } else if (input.Density > 1f) {
        warn($"[Keystone] BiomeLevelEntryValidator: Density={input.Density} for level " +
             $"'{input.LevelId}' on {biome} ({source}) exceeds 1; clamped to 1.");
        density = 1f;
      } else {
        density = input.Density;
      }

      var mode = LevelDispatchMode.Deterministic;
      if (!string.IsNullOrEmpty(input.Mode)) {
        if (!Enum.TryParse<LevelDispatchMode>(input.Mode, ignoreCase: true, out mode)) {
          warn($"[Keystone] BiomeLevelEntryValidator: unknown Mode='{input.Mode}' for level " +
               $"'{input.LevelId}' on {biome} ({source}). Falling back to Deterministic.");
          mode = LevelDispatchMode.Deterministic;
        }
      }

      table.Define(
          biome, input.LevelId, input.LowerMaturity, input.UpperMaturity,
          density, mode, input.RunAtStartup,
          input.FaunaCapacityAtSaturation, input.FaunaMinScore);
      return true;
    }

  }

}
