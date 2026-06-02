using System.Collections.Generic;
using Keystone.Core.Biomes;

namespace Keystone.Core.Planting {

  /// <summary>
  /// Per-tool selection policy for the Keystone mixed-planting brush:
  /// an integer <em>weight</em> per plantable species, an optional
  /// "leave a gap" outcome, and a weighted random draw over the active
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
  /// the palette's weights and the caller-supplied <c>pickHash</c>; the
  /// tool draws that hash from its own RNG at click time. Selection
  /// happens on a player action, never inside a simulation tick, so it
  /// does not participate in tick-replay determinism either way — but
  /// keeping the draw injectable means tests pin behavior with explicit
  /// hashes rather than a seeded global.</para>
  ///
  /// <para><b>Weights.</b> Each species carries an integer weight in
  /// <c>[<see cref="MinWeight"/>, <see cref="MaxWeight"/>]</c>; a species
  /// at weight 0 is excluded from the draw entirely (the panel's "clear
  /// all" / the &#8722; button at its floor). New species register at
  /// <see cref="DefaultWeight"/>. The draw is proportional to weight, so
  /// a species at weight 3 is picked three times as often as one at
  /// weight 1. The "clearings" (leave-the-tile-empty) outcome is itself a
  /// weighted candidate — <see cref="GapWeight"/>, dialled by the player like
  /// any species — so it competes in the same proportional draw and the player
  /// controls how much open ground the brush scatters. Weights are realized
  /// through <see cref="WeightedPick"/>, so the draw shares one
  /// boundary/rounding implementation with the rest of Core.</para>
  /// </summary>
  public sealed class PlantingPalette {

    #region Constants

    /// <summary>Lowest weight; a species here is excluded from the draw.</summary>
    public const int MinWeight = 0;

    /// <summary>Highest weight a species can be nudged to (single-digit so
    /// the panel's number stays one column wide).</summary>
    public const int MaxWeight = 9;

    /// <summary>Weight a freshly <see cref="Add"/>ed species starts at —
    /// in the draw, at parity with every other default species (mirrors
    /// the Forest Tool default where every plantable started on).</summary>
    public const int DefaultWeight = 1;

    #endregion

    #region Fields

    /// <summary>Species in stable registration order. The order is the
    /// order species were <see cref="Add"/>ed (the menu-build order), so
    /// a given <c>pickHash</c> maps to the same species across draws as
    /// long as the weights are unchanged.</summary>
    private readonly List<string> _species = new();

    /// <summary>Per-species draw weight, keyed by the same names held in
    /// <see cref="_species"/>. A species is registered iff it has an entry
    /// here; the value is its weight in <c>[MinWeight, MaxWeight]</c>.</summary>
    private readonly Dictionary<string, int> _weights = new();

    /// <summary>Draw weight of the clearings (leave-empty) outcome, in
    /// <c>[MinWeight, MaxWeight]</c>. 0 = never leave gaps.</summary>
    private int _gapWeight;

    #endregion

    #region Properties

    /// <summary>Draw weight of the "clearings" (leave-this-tile-empty)
    /// outcome, in <c>[MinWeight, MaxWeight]</c>. 0 means the brush never
    /// leaves gaps; higher values scatter more open ground. It competes in
    /// <see cref="Choose"/> exactly like a species at the same weight, and a
    /// clearings result surfaces as a <c>null</c> return. (Replaced the old
    /// boolean "Allow gaps" toggle so the player dials how much white space
    /// they want.)</summary>
    public int GapWeight => _gapWeight;

    /// <summary>All registered species, in registration order, regardless
    /// of weight. The panel iterates this to build its rows.</summary>
    public IReadOnlyList<string> Species => _species;

    /// <summary>True when at least one species has a positive weight OR the
    /// clearings weight is positive — i.e. <see cref="Choose"/> can produce a
    /// meaningful outcome. When false, every <see cref="Choose"/> returns
    /// <c>null</c> and the brush has nothing to do.</summary>
    public bool HasActiveOutcome {
      get {
        if (_gapWeight > 0) return true;
        foreach (var weight in _weights.Values) {
          if (weight > 0) return true;
        }
        return false;
      }
    }

    #endregion

    #region Mutation

    /// <summary>Register a species at <see cref="DefaultWeight"/> (mirrors
    /// the Forest Tool default where every discovered plantable starts on).
    /// Re-adding an existing species is a no-op that preserves its current
    /// weight and position.</summary>
    public void Add(string species) {
      if (string.IsNullOrEmpty(species)) return;
      if (_weights.ContainsKey(species)) return;
      _species.Add(species);
      _weights[species] = DefaultWeight;
    }

    /// <summary>This species' current draw weight, or 0 if it was never
    /// <see cref="Add"/>ed.</summary>
    public int GetWeight(string species) =>
        _weights.TryGetValue(species, out var weight) ? weight : 0;

    /// <summary>Set a registered species' weight, clamped to
    /// <c>[MinWeight, MaxWeight]</c>. No-op for a species that was never
    /// <see cref="Add"/>ed.</summary>
    public void SetWeight(string species, int weight) {
      if (!_weights.ContainsKey(species)) return;
      _weights[species] = Clamp(weight);
    }

    /// <summary>Nudge a species' weight up by one (capped at
    /// <see cref="MaxWeight"/>) and return the new value. 0 for an
    /// unregistered species.</summary>
    public int IncrementWeight(string species) => Nudge(species, +1);

    /// <summary>Nudge a species' weight down by one (floored at
    /// <see cref="MinWeight"/>) and return the new value. 0 for an
    /// unregistered species.</summary>
    public int DecrementWeight(string species) => Nudge(species, -1);

    /// <summary>True if <paramref name="species"/> is registered and has a
    /// positive weight (so it participates in the draw).</summary>
    public bool IsEnabled(string species) => GetWeight(species) > 0;

    /// <summary>Set every registered species to <paramref name="weight"/>
    /// (clamped). Backs the panel's "select all" (weight 1) and "clear
    /// all" (weight 0) buttons. Does not touch <see cref="GapWeight"/>, which
    /// the player controls independently via the clearings row.</summary>
    public void SetAllWeights(int weight) {
      var clamped = Clamp(weight);
      foreach (var species in _species) {
        _weights[species] = clamped;
      }
    }

    /// <summary>Set the clearings weight, clamped to
    /// <c>[MinWeight, MaxWeight]</c>.</summary>
    public void SetGapWeight(int weight) => _gapWeight = Clamp(weight);

    /// <summary>Nudge the clearings weight up by one (capped at
    /// <see cref="MaxWeight"/>) and return the new value.</summary>
    public int IncrementGapWeight() => _gapWeight = Clamp(_gapWeight + 1);

    /// <summary>Nudge the clearings weight down by one (floored at
    /// <see cref="MinWeight"/>) and return the new value.</summary>
    public int DecrementGapWeight() => _gapWeight = Clamp(_gapWeight - 1);

    /// <summary>Shared up/down step with clamp + lookup guard.</summary>
    private int Nudge(string species, int delta) {
      if (!_weights.TryGetValue(species, out var weight)) return 0;
      var next = Clamp(weight + delta);
      _weights[species] = next;
      return next;
    }

    private static int Clamp(int weight) =>
        weight < MinWeight ? MinWeight : (weight > MaxWeight ? MaxWeight : weight);

    #endregion

    #region Selection

    /// <summary>
    /// Pick an outcome for one tile from the positively-weighted species and
    /// the clearings outcome (when <see cref="GapWeight"/> &gt; 0), proportional
    /// to each candidate's weight.
    /// </summary>
    /// <param name="pickHash">A uniform sample in <c>[0, 1)</c>, supplied
    /// by the caller's RNG. Same hash + same weights → same result.</param>
    /// <returns>The chosen species' name, or <c>null</c> to leave the
    /// tile empty. <c>null</c> covers both the clearings outcome and the
    /// degenerate "nothing weighted" case; the tool treats both identically
    /// (clear / skip the tile).</returns>
    public string? Choose(float pickHash) {
      // Count the participating candidates: every species with a positive
      // weight, plus the clearings candidate when its weight is positive.
      var candidateCount = _gapWeight > 0 ? 1 : 0;
      foreach (var species in _species) {
        if (_weights[species] > 0) candidateCount++;
      }
      if (candidateCount == 0) return null;

      // Build the weight list in _species order (positive-weight species,
      // then the clearings candidate). WeightedPick maps the hash onto an
      // index over this list, sharing the boundary/rounding contract with the
      // rest of Core.
      var weights = new float[candidateCount];
      var written = 0;
      foreach (var species in _species) {
        var weight = _weights[species];
        if (weight <= 0) continue;
        weights[written++] = weight;
      }
      if (_gapWeight > 0) weights[written] = _gapWeight;

      var idx = WeightedPick.Pick(weights, pickHash);
      if (idx < 0) return null;

      // Map the chosen index back to a species. Positive-weight species
      // occupy indices [0, count) in _species order; the clearings candidate
      // (if any) is the final index.
      var seen = 0;
      foreach (var species in _species) {
        if (_weights[species] <= 0) continue;
        if (seen == idx) return species;
        seen++;
      }
      // idx == positive-species count → the clearings candidate (only
      // reachable when _gapWeight > 0, since candidateCount accounted for it).
      return null;
    }

    #endregion

  }

}
