using System;

namespace Keystone.Core.Ports {

  /// <summary>
  /// Probe describing one natural-resource entity at a queried voxel.
  /// Carries only the fields the chunk aggregator's per-channel fold
  /// reads — blueprint identity, whether the entity is Keystone-owned
  /// (Class A/B/C — excluded from the chunk's natural-resource
  /// fingerprint by design), and whether the entity's lifecycle says
  /// it's dead.
  /// </summary>
  /// <param name="BlueprintName">Blueprint name of the entity at the
  /// voxel. Empty string when no blueprint is resolvable (the chunk
  /// aggregator treats that as "skip").</param>
  /// <param name="IsKeystoneOwned">True if the entity carries a
  /// non-empty Keystone-class stamp (A/B/C — additive content this
  /// mod placed). Excluded from the chunk's fingerprint so Keystone's
  /// own decor doesn't feed back into the scores that decide what to
  /// spawn.</param>
  /// <param name="IsDead">True if the entity's lifecycle component
  /// reports the entity is dead. Routed to the dead-natural catch-all
  /// channel by the aggregator so a dead Birch doesn't pad live-Birch
  /// density. Entities without a lifecycle component are treated as
  /// alive (<c>false</c>).</param>
  public readonly record struct NaturalResourceProbe(
      string BlueprintName,
      bool IsKeystoneOwned,
      bool IsDead);

  /// <summary>
  /// Enumerator-style port over the per-voxel natural-resource scan.
  /// Replaces the chunk aggregator's direct
  /// <c>IBlockService.GetObjectsAt</c> walk + component probing with
  /// a single callback that emits one
  /// <see cref="NaturalResourceProbe"/> per natural-resource-bearing
  /// entity at the voxel.
  ///
  /// <para><b>Dedup is the caller's job.</b> The same entity can be
  /// reported by multiple adjacent voxel queries (a tall tree shows
  /// up at every Z it intersects). The aggregator dedups by reference
  /// via a per-chunk seen-set; this port doesn't try.</para>
  ///
  /// <para><b>Why callback instead of <c>IEnumerable</c>.</b> The Mod-
  /// side adapter walks <c>IBlockService.GetObjectsAt</c> which
  /// returns a live enumerable; passing each entity through a
  /// callback avoids allocating an intermediate list per voxel.</para>
  /// </summary>
  public interface INaturalResourceEnumerator {

    /// <summary>Invoke <paramref name="onProbe"/> once per
    /// natural-resource entity at the voxel
    /// (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>).
    /// Callback is invoked synchronously during this call; the
    /// implementation must not retain or reorder the probes. Voxels
    /// with no natural resources invoke the callback zero times.
    ///
    /// <para>The <paramref name="entityKey"/> in the probe-emitter
    /// signature is an opaque identity for the entity (the Mod-side
    /// adapter passes the <c>BlockObject</c> reference); the
    /// aggregator uses it as a dedup key in its per-chunk
    /// seen-set.</para></summary>
    void EnumerateNaturalResourcesAt(
        int x, int y, int z,
        Action<object, NaturalResourceProbe> onProbe);

  }

}
