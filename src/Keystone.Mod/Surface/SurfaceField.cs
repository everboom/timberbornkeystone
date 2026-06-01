using System;

namespace Keystone.Mod.Surface {

  /// <summary>
  /// Enumerates the persisted per-surface float layers held by
  /// <see cref="SurfaceFieldStore"/>. Values MUST be contiguous and
  /// zero-based: the store indexes its backing arrays directly by the
  /// integer value of each member (and asserts this at construction).
  ///
  /// <para>Adding a layer is additive and migration-safe: append a new
  /// member, give it a stable save id in <see cref="SurfaceFieldMeta"/>,
  /// and old saves -- which lack that layer's key -- load it as
  /// all-zero. Never reorder or reuse a value, and never change a save
  /// id; either silently rebinds previously-saved data to the wrong
  /// layer.</para>
  /// </summary>
  public enum SurfaceField {

    /// <summary>How long a tile has been continuously near water,
    /// integrated as a day-scale maturity (accrue while near water,
    /// dissipate otherwise). Gates Grassland's riparian flourishes so a
    /// transient flood -- water present for only a moment -- can't fire
    /// the semi-permanent decoration the way the old instantaneous
    /// "water nearby right now" check did.</summary>
    RiparianMaturity = 0,
  }

  /// <summary>Stable save-format metadata for <see cref="SurfaceField"/>.</summary>
  internal static class SurfaceFieldMeta {

    /// <summary>The persistence id for a layer, used to build its save
    /// key. Deliberately decoupled from the enum member name so a code
    /// rename can never break existing saves.</summary>
    public static string SaveId(SurfaceField field) => field switch {
      SurfaceField.RiparianMaturity => "RiparianMaturity",
      _ => throw new ArgumentOutOfRangeException(
          nameof(field), field, "No save id registered for surface field."),
    };
  }

}
