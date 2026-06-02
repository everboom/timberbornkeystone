using System.Collections.Generic;
using Keystone.Core.Biomes;

namespace Keystone.Core.Planting {

  /// <summary>
  /// Per-tool selection policy for the Keystone mixed-planting brush:
  /// the set of plantable species the player has enabled, an optional
  /// "leave a gap" outcome, and a uniform random draw over the active
  /// set. One palette instance backs one planting tool (crops, or
  /// trees/bushes); the tool asks <see cref="Choose"/> once per tile to
  /// decide what (if anything) to mark there.
  ///
  /// <para><b>Why this lives in Core.</b> This is exactly the state the
  /// Forest Tool reference mod kept in a process-wide <c>static</c> class
  /// (<c>ForestToolParam</c>) with a baked-in <see cref="System.Random"/>
  /// — not testable, not instance-scoped, and a save/replay hazard if it
  /// were ever read inside a tick. Here it is a plain instance with no
  /// statics and an externally supplied pick value, so the policy is
  /// unit-testable in isolation and the Timberborn-facing tool only owns
  /// plumbing.</para>
  ///
  /// <para><b>Determinism.</b> <see cref="Choose"/> is a pure function of
  /// the palette's enabled set and the caller-supplied <c>pickHash</c>;
  /// the tool draws that hash from its own RNG at click time. Selection
  /// happens on a player action, never inside a simulation tick, so it
  /// does not participate in tick-replay determinism either way — but
  /// keeping the draw injectable means tests pin behavior with explicit
  /// hashes rather than a seeded global.</para>
  ///
  /// <para><b>Weights.</b> All enabled species draw with equal weight
  /// (Keystone's planting brush is a pure manual mixer — deliberately
  /// not biome-aware). The "gap" outcome, when enabled, is one further
  /// equal-weight candidate, matching the Forest Tool behavior where the
  /// "empty spot" entry sat in the pool at the same weight as every
  /// species. Equal weights are realized by reusing
  /// <see cref="WeightedPick"/> with a uniform weight list, so the draw
  /// shares one boundary/rounding implementation with the rest of Core.</para>
  /// </summary>
  public sealed class PlantingPalette {

    #region Fields

    /// <summary>Species in stable registration order. The order is the
    /// order species were <see cref="Add"/>ed (the menu-build order), so
    /// a given <c>pickHash</c> maps to the same species across draws as
    /// long as the enabled set is unchanged.</summary>
    private readonly List<string> _species = new();

    /// <summary>Enabled subset of <see cref="_species"/>. Membership,
    /// not order, is what <see cref="Choose"/> reads; order comes from
    /// <see cref="_species"/>.</summary>
    private readonly HashSet<string> _enabled = new();

    #endregion

    #region Properties

    /// <summary>When true, an additional "leave this tile empty"
    /// outcome competes in the draw at equal weight with each enabled
    /// species. The Forest Tool "Allow clearings" toggle. A gap result
    /// is surfaced as a <c>null</c> return from <see cref="Choose"/>.</summary>
    public bool AllowGaps { get; set; }

    /// <summary>All registered species, in registration order, regardless
    /// of enabled state. The panel iterates this to build its toggles.</summary>
    public IReadOnlyList<string> Species => _species;

    /// <summary>True when at least one species is enabled OR gaps are
    /// allowed — i.e. <see cref="Choose"/> can produce a meaningful
    /// outcome. When false, every <see cref="Choose"/> returns
    /// <c>null</c> and the brush has nothing to do.</summary>
    public bool HasActiveOutcome => _enabled.Count > 0 || AllowGaps;

    #endregion

    #region Mutation

    /// <summary>Register a species, enabled by default (mirrors the
    /// Forest Tool default where every discovered plantable starts on).
    /// Re-adding an existing species is a no-op that preserves its
    /// current enabled state and position.</summary>
    public void Add(string species) {
      if (string.IsNullOrEmpty(species)) return;
      if (_enabled.Contains(species) || _species.Contains(species)) {
        _enabled.Add(species);
        return;
      }
      _species.Add(species);
      _enabled.Add(species);
    }

    /// <summary>Enable or disable a single registered species. No-op for
    /// a species that was never <see cref="Add"/>ed.</summary>
    public void SetEnabled(string species, bool enabled) {
      if (!_species.Contains(species)) return;
      if (enabled) {
        _enabled.Add(species);
      } else {
        _enabled.Remove(species);
      }
    }

    /// <summary>True if <paramref name="species"/> is registered and
    /// currently enabled.</summary>
    public bool IsEnabled(string species) => _enabled.Contains(species);

    /// <summary>Bulk enable/disable every registered species. Backs the
    /// "All" master toggle in the options panel. Does not touch
    /// <see cref="AllowGaps"/>, which is an independent outcome.</summary>
    public void SetAllEnabled(bool enabled) {
      _enabled.Clear();
      if (!enabled) return;
      foreach (var species in _species) {
        _enabled.Add(species);
      }
    }

    #endregion

    #region Selection

    /// <summary>
    /// Pick an outcome for one tile from the enabled species (and the
    /// gap outcome, if <see cref="AllowGaps"/>), each at equal weight.
    /// </summary>
    /// <param name="pickHash">A uniform sample in <c>[0, 1)</c>, supplied
    /// by the caller's RNG. Same hash + same enabled set → same result.</param>
    /// <returns>The chosen species' name, or <c>null</c> to leave the
    /// tile empty. <c>null</c> covers both the gap outcome and the
    /// degenerate "nothing enabled and no gaps" case; the tool treats
    /// both identically (clear / skip the tile).</returns>
    public string? Choose(float pickHash) {
      var candidateCount = _enabled.Count + (AllowGaps ? 1 : 0);
      if (candidateCount == 0) return null;

      // Equal weights: one unit per enabled species, plus one for the
      // gap candidate if allowed. WeightedPick maps the hash onto an
      // index over this uniform list, sharing the boundary/rounding
      // contract with the rest of Core.
      var weights = new float[candidateCount];
      for (var i = 0; i < candidateCount; i++) weights[i] = 1f;

      var idx = WeightedPick.Pick(weights, pickHash);
      if (idx < 0) return null;

      // Map the chosen index back to a species. Enabled species occupy
      // indices [0, _enabled.Count) in _species order; the gap candidate
      // (if any) is the final index.
      var seen = 0;
      foreach (var species in _species) {
        if (!_enabled.Contains(species)) continue;
        if (seen == idx) return species;
        seen++;
      }
      // idx == _enabled.Count → the gap candidate (only reachable when
      // AllowGaps is true, since candidateCount accounted for it).
      return null;
    }

    #endregion

  }

}
