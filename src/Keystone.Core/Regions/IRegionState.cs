namespace Keystone.Core.Regions {

  /// <summary>
  /// Contract for any datum attached to a <see cref="Region"/> that needs
  /// to follow region lifecycle correctly through splits and merges.
  ///
  /// <para>Two flavors fit naturally on top of this interface:</para>
  ///
  /// <para><b>Extensive state</b> (population, biomass, accumulated
  /// resources, anything where "how much" is a count): scales by size
  /// ratio on split, sums on merge. Implementation:
  /// <c>ForChildOnSplit(r) = WithValue(round(Value * r))</c>;
  /// <c>Absorbing(other) = WithValue(Value + other.Value)</c>.</para>
  ///
  /// <para><b>Intensive state</b> (eco-health value, average moisture,
  /// classification probability, anything where "how strong" is the
  /// quality): inherits unchanged on split, weighted-averages on merge.
  /// Implementation: <c>ForChildOnSplit(_) = this</c>;
  /// <c>Absorbing(other, ms, os) = WithValue((Value*ms + other.Value*os) / (ms+os))</c>.</para>
  ///
  /// <para>The interface doesn't classify the kind -- consumers pick
  /// whichever transformation makes physical sense for their datum.
  /// State implementations should be immutable value types where
  /// possible (records or readonly structs); split/merge produce new
  /// values rather than mutating in place.</para>
  /// </summary>
  public interface IRegionState {

    /// <summary>
    /// Called when a region splits. Returns the state value a child of
    /// the given proportion should receive.
    /// </summary>
    /// <param name="sizeRatio">
    /// Child size divided by the pre-split parent size, in the open
    /// interval (0, 1). The piece keeping the parent's id calls this
    /// too -- it gets its own proportional share, not the full parent
    /// value.
    /// </param>
    IRegionState ForChildOnSplit(double sizeRatio);

    /// <summary>
    /// Called on the survivor of a merge. <paramref name="other"/> is
    /// the same state type from the absorbed region, or <c>null</c> if
    /// the absorbed region didn't have this state. <paramref name="mySize"/>
    /// and <paramref name="otherSize"/> are the pre-merge sizes -- useful
    /// for weighted averages on intensive state.
    /// </summary>
    IRegionState Absorbing(IRegionState? other, int mySize, int otherSize);

  }

}
