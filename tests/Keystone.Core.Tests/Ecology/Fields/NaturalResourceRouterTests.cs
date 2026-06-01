using System.Collections.Generic;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Ecology.Fields {

  /// <summary>
  /// Pins the per-entity channel-routing rule used by the chunk
  /// aggregator. The audit specifically flagged the Keystone-owned
  /// exclusion as a silent failure mode: if our own decor fed the
  /// score that decides what to spawn, decor-dense chunks would
  /// self-amplify into looking like a healthier biome than they are.
  /// </summary>
  [TestClass]
  public class NaturalResourceRouterTests {

    #region Helpers

    private const int DeadIndex = 99;

    private static System.Func<string, int?> IndexMap(params (string name, int idx)[] entries) {
      var dict = new Dictionary<string, int>();
      foreach (var (name, idx) in entries) dict[name] = idx;
      return name => dict.TryGetValue(name, out var i) ? i : (int?)null;
    }

    #endregion

    #region Keystone-owned exclusion

    [TestMethod]
    public void Route_KeystoneOwned_AlwaysSkips() {
      // The bug guard: anything Keystone placed (Class A/B/C stamped)
      // must not feed back into the chunk's natural-resource
      // fingerprint. Even with a known blueprint name + live status,
      // routing returns null.
      var probe = new NaturalResourceProbe(
          BlueprintName: "Birch", IsKeystoneOwned: true, IsDead: false);
      var idx = NaturalResourceRouter.RouteToChannel(
          probe, IndexMap(("Birch", 5)), DeadIndex);

      Assert.IsNull(idx, "Keystone-owned entities never route to a channel.");
    }

    [TestMethod]
    public void Route_KeystoneOwnedAndDead_StillSkipsNeverHitsDeadChannel() {
      // The Keystone-owned check must short-circuit before the dead-
      // channel routing — a Keystone-owned dead entity is doubly
      // excluded.
      var probe = new NaturalResourceProbe(
          BlueprintName: "Birch", IsKeystoneOwned: true, IsDead: true);
      var idx = NaturalResourceRouter.RouteToChannel(
          probe, IndexMap(("Birch", 5)), DeadIndex);

      Assert.IsNull(idx);
    }

    #endregion

    #region Blueprint resolution

    [TestMethod]
    public void Route_EmptyBlueprintName_Skips() {
      var probe = new NaturalResourceProbe(
          BlueprintName: "", IsKeystoneOwned: false, IsDead: false);
      var idx = NaturalResourceRouter.RouteToChannel(
          probe, IndexMap(("Birch", 5)), DeadIndex);

      Assert.IsNull(idx);
    }

    [TestMethod]
    public void Route_UnknownBlueprint_Skips() {
      var probe = new NaturalResourceProbe(
          BlueprintName: "Unicorn", IsKeystoneOwned: false, IsDead: false);
      var idx = NaturalResourceRouter.RouteToChannel(
          probe, IndexMap(("Birch", 5)), DeadIndex);

      Assert.IsNull(idx, "A blueprint not in the index map has no channel; skip.");
    }

    #endregion

    #region Live routing

    [TestMethod]
    public void Route_KnownLiveBlueprint_RoutesToLiveIndex() {
      var probe = new NaturalResourceProbe(
          BlueprintName: "Birch", IsKeystoneOwned: false, IsDead: false);
      var idx = NaturalResourceRouter.RouteToChannel(
          probe, IndexMap(("Birch", 5), ("Maple", 7)), DeadIndex);

      Assert.AreEqual(5, idx);
    }

    [TestMethod]
    public void Route_DifferentBlueprints_HitDifferentChannels() {
      var lookup = IndexMap(("Birch", 5), ("Maple", 7));
      Assert.AreEqual(5, NaturalResourceRouter.RouteToChannel(
          new NaturalResourceProbe("Birch", false, false), lookup, DeadIndex));
      Assert.AreEqual(7, NaturalResourceRouter.RouteToChannel(
          new NaturalResourceProbe("Maple", false, false), lookup, DeadIndex));
    }

    #endregion

    #region Dead routing

    [TestMethod]
    public void Route_DeadEntity_RoutesToDeadChannelNotLiveChannel() {
      // The audit's dead-channel routing contract: a dead Birch
      // increments the dead-naturals catch-all, NOT the live-Birch
      // channel. Without this, a chunk full of dead trees would read
      // as a healthy forest.
      var probe = new NaturalResourceProbe(
          BlueprintName: "Birch", IsKeystoneOwned: false, IsDead: true);
      var idx = NaturalResourceRouter.RouteToChannel(
          probe, IndexMap(("Birch", 5)), DeadIndex);

      Assert.AreEqual(DeadIndex, idx);
    }

    [TestMethod]
    public void Route_DeadUnknownBlueprint_StillSkipsNoRouting() {
      // Unknown blueprints skip even when dead — the resolution gate
      // runs first. (A dead unknown can't pad either channel.)
      var probe = new NaturalResourceProbe(
          BlueprintName: "Unicorn", IsKeystoneOwned: false, IsDead: true);
      var idx = NaturalResourceRouter.RouteToChannel(
          probe, IndexMap(("Birch", 5)), DeadIndex);

      Assert.IsNull(idx);
    }

    #endregion

  }

}
