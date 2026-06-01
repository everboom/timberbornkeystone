namespace Keystone.Core.Regions {

  /// <summary>
  /// Opaque identifier for a <see cref="Region"/>. Wraps an integer for
  /// type safety -- consumers can't accidentally pass a raw int into a
  /// region API or compute one via arithmetic. Stable within a single
  /// indexing pass; stability across event-driven re-indexing is the
  /// concern of <c>RegionService</c>.
  /// </summary>
  /// <param name="Value">Underlying integer. Implementation detail; do not depend on the value's interpretation.</param>
  public readonly record struct RegionId(int Value) {

    /// <summary>Sentinel for "no region" / "unassigned".</summary>
    public static readonly RegionId None = new(-1);

    /// <inheritdoc />
    public override string ToString() => $"R{Value}";

  }

}
