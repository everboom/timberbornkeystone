using System;
using Keystone.Core.Regions;

namespace Keystone.Core.Persistence {

  /// <summary>
  /// Composite key for an entry in <see cref="RegionValueStore"/>: a
  /// region id and a string discriminator (the "kind"). Mod 1's own
  /// kinds are namespaced under <c>"keystone."</c>; external mods are
  /// strongly recommended to prefix with their own mod id.
  /// </summary>
  public readonly record struct RegionValueKey {

    #region Properties

    /// <summary>The region this value is attached to.</summary>
    public RegionId RegionId { get; }

    /// <summary>The value's discriminator. Non-empty.</summary>
    public string Kind { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Construct a region-value key. <paramref name="kind"/> must be
    /// non-null and non-empty -- empty kinds would make the value
    /// store impossible to debug ("which value is the empty-kind
    /// one?") so we fail loudly at the boundary rather than silently
    /// accept them.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="kind"/> is null or empty.</exception>
    public RegionValueKey(RegionId regionId, string kind) {
      if (string.IsNullOrEmpty(kind)) {
        throw new ArgumentException("RegionValueKey.Kind must be non-empty.", nameof(kind));
      }
      RegionId = regionId;
      Kind = kind;
    }

    #endregion

  }

}
