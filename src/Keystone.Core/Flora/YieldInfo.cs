namespace Keystone.Core.Flora {

  /// <summary>
  /// Distilled yield definition from a Timberborn <c>YielderSpec</c> --
  /// what comes out, how much, how long the harvest takes, and which
  /// resource-group bucket it belongs to. Held by reference on
  /// <see cref="FloraEntry"/> for each harvest mode (cut, gather)
  /// independently so trees that both fell-for-logs and tap-for-syrup
  /// surface both signals.
  /// </summary>
  public sealed class YieldInfo {

    #region Properties

    /// <summary>Good identifier produced on harvest (<c>YielderSpec.Yield.Id</c>).</summary>
    public string GoodId { get; }

    /// <summary>Amount of <see cref="GoodId"/> per harvest (<c>YielderSpec.Yield.Amount</c>).</summary>
    public int Amount { get; }

    /// <summary>Removal time in hours (<c>YielderSpec.RemovalTimeInHours</c>).</summary>
    public float RemovalTimeInHours { get; }

    /// <summary>
    /// Resource-group bucket this yield belongs to
    /// (<c>YielderSpec.ResourceGroup</c>). Vanilla observed values
    /// include <c>Cuttable</c>, <c>Gatherable</c>, <c>Tappable</c>,
    /// <c>Farmhouse</c>, <c>AquaticFarmhouse</c>. The
    /// <c>Tappable</c> vs <c>Gatherable</c> split on a
    /// <c>GatherableSpec</c> is what distinguishes a sap tap from
    /// fruit picking -- there is no dedicated <c>TappableSpec</c>.
    /// </summary>
    public string ResourceGroup { get; }

    #endregion

    #region Construction

    public YieldInfo(string goodId, int amount, float removalTimeInHours, string resourceGroup) {
      GoodId = goodId;
      Amount = amount;
      RemovalTimeInHours = removalTimeInHours;
      ResourceGroup = resourceGroup;
    }

    #endregion

  }

}
