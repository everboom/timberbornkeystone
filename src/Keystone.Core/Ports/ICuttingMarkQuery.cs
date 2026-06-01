namespace Keystone.Core.Ports {

  /// <summary>
  /// Read-side port over the host's tree-cutting designation area: the
  /// per-tile coordinates the player adds via the Tree Cutting Area
  /// selection tool before beavers actually cut anything.
  /// Engine-agnostic counterpart of Timberborn's
  /// <c>Timberborn.Forestry.TreeCuttingArea</c>; the Mod layer supplies
  /// an adapter wrapping that singleton.
  ///
  /// <para><b>Why Keystone reads this.</b> Cutting marks signal "the
  /// player wants this tree gone." The Class D (vanilla flora) handler
  /// uses this with a tree-aware exception: bushes / crops / ground-
  /// cover never spawn on marked tiles, and trees only spawn when an
  /// adult cuttable tree already exists in the 8-tile Moore
  /// neighbourhood — so selective harvesting inside a Keystone forest
  /// keeps regrowing, but a wide clear-cut self-stabilises as cutting
  /// proceeds and neighbours disappear. Class A (small decorations),
  /// Class B (decorative blockobjects), and Class C (rocks) currently
  /// ignore cutting marks — they're either too small to interfere
  /// (A) or build-overable (B) or not really shipped (C). If Class B
  /// turns out to actually annoy players on construction sites, gating
  /// it on this port is a one-liner.</para>
  ///
  /// <para><b>Per-tile only.</b> Unlike <see cref="IPlantingMarkQuery"/>
  /// (which feeds both handler eligibility AND chunk monoculture
  /// detection via its rect query), cutting marks have no aggregator
  /// consumer today — players slating trees for cutting doesn't shift
  /// biome scoring. The port stays minimal and adds a rect query only
  /// if a future consumer needs one.</para>
  /// </summary>
  public interface ICuttingMarkQuery {

    /// <summary>True if the tile at <c>(x, y, z)</c> is currently inside
    /// the player's tree-cutting designation area. Cheap (O(1) on the
    /// host — a hashset lookup).</summary>
    bool IsMarkedForCutting(int x, int y, int z);

  }

}
