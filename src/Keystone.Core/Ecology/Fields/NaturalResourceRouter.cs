using System;
using Keystone.Core.Ports;

namespace Keystone.Core.Ecology.Fields {

  /// <summary>
  /// Per-entity routing rule for the chunk-aggregator's entity walk:
  /// given a <see cref="NaturalResourceProbe"/> from the enumerator
  /// port, decide which entity channel index to increment (or skip
  /// the probe entirely).
  ///
  /// <para><b>Three reasons to skip a probe.</b>
  /// <list type="number">
  ///   <item><b>Keystone-owned content (Class A/B/C).</b> Excluded from
  ///         the chunk's natural-resource fingerprint by design — if
  ///         our own decor fed the score that decides what to spawn,
  ///         a decor-dense chunk would self-amplify into looking
  ///         healthier than it is. This is the silent failure the
  ///         audit specifically flagged.</item>
  ///   <item><b>Unresolvable blueprint name.</b> Empty/null name or a
  ///         name not in the entity-index map means the channel
  ///         routing has no slot; skip rather than guess.</item>
  ///   <item><b>Unknown blueprint with a name.</b> Same as above —
  ///         <c>entityIndexFor</c> returns null for blueprints not in
  ///         the flora catalog.</item>
  /// </list></para>
  ///
  /// <para><b>Dead entities are routed to a catch-all channel</b>
  /// (<paramref name="deadEntityIndex"/>) rather than the
  /// blueprint-specific live channel, so a dead Birch doesn't pad
  /// live-Birch density.</para>
  /// </summary>
  public static class NaturalResourceRouter {

    /// <summary>Channel index this probe should increment, or
    /// <c>null</c> if the probe should be skipped. See class doc for
    /// the three skip cases.</summary>
    public static int? RouteToChannel(
        NaturalResourceProbe probe,
        Func<string, int?> entityIndexFor,
        int deadEntityIndex) {
      if (probe.IsKeystoneOwned) return null;
      if (string.IsNullOrEmpty(probe.BlueprintName)) return null;
      var idx = entityIndexFor(probe.BlueprintName);
      if (!idx.HasValue) return null;
      return probe.IsDead ? deadEntityIndex : idx.Value;
    }

  }

}
