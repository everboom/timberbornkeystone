namespace Keystone.Core.Ecology.Fields {

  /// <summary>
  /// Threshold + predicate for "water at this surface counts as
  /// badwater." Pulled into Core so the boundary semantics — the
  /// 0.05 cutoff and the inclusive comparison — are pinnable by a
  /// unit test without standing up the Mod-side ecology pipeline.
  ///
  /// <para><b>Why a non-zero threshold.</b> The game's water-column
  /// contamination value diffuses through connected pools at trace
  /// levels (a tiny badtide event in one corner of a lake produces
  /// fractional contamination everywhere). A strict <c>&gt; 0</c>
  /// predicate paints every tile in a touched pool as badwater
  /// regardless of severity, which is wrong for the Keystone visual
  /// and scoring layer. The 0.05 floor filters that trace.</para>
  ///
  /// <para><b>Why inclusive comparison.</b> The boundary value
  /// (<c>0.05</c> exactly) should count as badwater — at the floor,
  /// not above it. A surface saturated to exactly the threshold is
  /// not "almost badwater," it's "the lowest badwater intensity we
  /// recognise." Mod-side EcologyFieldUpdater consumes
  /// <see cref="IsBadwater"/> and gets the right boundary handling
  /// for free.</para>
  /// </summary>
  public static class WaterContamination {

    /// <summary>Saturation value (in <c>[0, 1]</c>, from
    /// <c>IWaterQuery.WaterContaminationAt</c>) at or above which a
    /// surface counts as badwater. Strict <c>&gt; 0</c> would paint
    /// every tile in a contaminated pool as badwater from trace
    /// diffusion; this floor filters that down to surfaces with
    /// meaningful contamination.</summary>
    public const float Threshold = 0.05f;

    /// <summary>True iff <paramref name="contamination"/> meets the
    /// badwater threshold. Inclusive: a value of exactly
    /// <see cref="Threshold"/> returns <c>true</c>. Soil-side
    /// contamination uses no equivalent threshold; only the
    /// water-column path needs the trace filter.</summary>
    public static bool IsBadwater(float contamination)
        => contamination >= Threshold;

  }

}
