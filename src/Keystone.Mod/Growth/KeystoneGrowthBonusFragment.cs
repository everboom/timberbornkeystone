using System.Text;
using Keystone.Core.Biomes;
using Keystone.Core.Growth;
using Keystone.Mod.Diagnostics;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.Growing;
using Timberborn.Localization;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.TooltipSystem;
using UnityEngine.UIElements;

namespace Keystone.Mod.Growth {

  /// <summary>
  /// Entity-panel fragment for the biome growth bonus. Shows a short
  /// <i>flavor</i> verdict line (e.g. "Taking root — Forest is still
  /// establishing.") and attaches a dynamic hover <i>tooltip</i> carrying
  /// the full technical breakdown — established biome vs. needed, current
  /// suitability + limiting factor, canopy state, and the bonus %.
  ///
  /// <para>The verdict comes from the pure
  /// <see cref="GrowthDiagnostics.Classify"/>; the line and tooltip text
  /// go through <see cref="ILoc"/>. The tooltip is registered once and
  /// reads a live <see cref="GrowthSignals"/> cache refreshed each frame
  /// in <see cref="UpdateFragment"/>, so its closure never holds a stale
  /// per-selection value.</para>
  /// </summary>
  public class KeystoneGrowthBonusFragment : IEntityPanelFragment {

    #region Loc keys

    private const string FlavorThrivingKey = "Keystone.GrowthBonus.Flavor.Thriving";
    private const string FlavorBenefitingKey = "Keystone.GrowthBonus.Flavor.Benefiting";
    private const string FlavorHostileKey = "Keystone.GrowthBonus.Flavor.Hostile";
    private const string FlavorEstablishingKey = "Keystone.GrowthBonus.Flavor.Establishing";
    private const string FlavorPotentialKey = "Keystone.GrowthBonus.Flavor.Potential";
    private const string FlavorWrongBiomeKey = "Keystone.GrowthBonus.Flavor.WrongBiome";
    private const string FlavorMonocultureKey = "Keystone.GrowthBonus.Flavor.Monoculture";
    private const string FlavorDormantKey = "Keystone.GrowthBonus.Flavor.Dormant";

    private const string TipResultKey = "Keystone.GrowthBonus.Tip.Result";
    private const string TipEstablishedKey = "Keystone.GrowthBonus.Tip.Established";
    private const string TipEstablishedNoneKey = "Keystone.GrowthBonus.Tip.EstablishedNone";
    private const string TipNeedsKey = "Keystone.GrowthBonus.Tip.Needs";
    private const string TipClusterKey = "Keystone.GrowthBonus.Tip.Cluster";
    private const string TipCanopyKey = "Keystone.GrowthBonus.Tip.Canopy";
    private const string TipWouldBeKey = "Keystone.GrowthBonus.Tip.WouldBe";
    private const string TipConditionsKey = "Keystone.GrowthBonus.Tip.Conditions";
    private const string TipLimitingKey = "Keystone.GrowthBonus.Tip.Limiting";
    private const string TipFormulaKey = "Keystone.GrowthBonus.Tip.Formula";

    private const string WouldBeGoodKey = "Keystone.GrowthBonus.WouldBe.Good";
    private const string WouldBePoorKey = "Keystone.GrowthBonus.WouldBe.Poor";

    private const string SuitPoorKey = "Keystone.GrowthBonus.Suit.Poor";
    private const string SuitWeakKey = "Keystone.GrowthBonus.Suit.Weak";
    private const string SuitGoodKey = "Keystone.GrowthBonus.Suit.Good";
    private const string SuitIdealKey = "Keystone.GrowthBonus.Suit.Ideal";

    private const string ObstacleMonocultureKey = "Keystone.GrowthBonus.Obstacle.Monoculture";
    private const string ObstacleDryKey = "Keystone.GrowthBonus.Obstacle.Dry";
    private const string ObstacleRiverKey = "Keystone.GrowthBonus.Obstacle.River";
    private const string ObstacleLakeKey = "Keystone.GrowthBonus.Obstacle.Lake";
    private const string ObstacleWetlandKey = "Keystone.GrowthBonus.Obstacle.Wetland";
    private const string ObstacleContaminatedKey = "Keystone.GrowthBonus.Obstacle.Contaminated";
    private const string ObstacleBadwaterKey = "Keystone.GrowthBonus.Obstacle.Badwater";
    private const string ObstacleGrasslandKey = "Keystone.GrowthBonus.Obstacle.Grassland";
    private const string ObstacleForestKey = "Keystone.GrowthBonus.Obstacle.Forest";
    private const string ObstacleCaveKey = "Keystone.GrowthBonus.Obstacle.Cave";

    #endregion

    #region Injected services

    private readonly ILoc _loc;
    private readonly ITooltipRegistrar _tooltipRegistrar;

    #endregion

    #region UI elements

    private NineSliceVisualElement _root;
    private Label _flavorLabel;
    private Label _debugLabel;

    #endregion

    #region Cached entity state

    private KeystoneGrowthBonus? _bonus;
    private Growable? _growable;
    private LivingNaturalResource? _living;
    private GrowthSignals? _signals;

    #endregion

    #region Construction

    public KeystoneGrowthBonusFragment(ILoc loc, ITooltipRegistrar tooltipRegistrar) {
      _loc = loc;
      _tooltipRegistrar = tooltipRegistrar;
    }

    #endregion

    #region IEntityPanelFragment

    public VisualElement InitializeFragment() {
      _root = new NineSliceVisualElement();
      _root.AddToClassList("bg-sub-box--green");
      _root.AddToClassList("entity-sub-panel");
      _root.ToggleDisplayStyle(false);

      _flavorLabel = new Label();
      _flavorLabel.AddToClassList("entity-panel__text");
      _root.Add(_flavorLabel);

      _debugLabel = new Label();
      _debugLabel.AddToClassList("entity-panel__text");
      _root.Add(_debugLabel);

      // Dynamic tooltip: evaluated on hover, reads the live signals cache.
      // Registered once; the closure reads the current _signals/_bonus so
      // it survives per-selection swaps in ShowFragment/ClearFragment.
      _tooltipRegistrar.Register(_root, BuildTooltip);

      return _root;
    }

    public void ShowFragment(BaseComponent entity) {
      _bonus = entity.GetComponent<KeystoneGrowthBonus>();
      _growable = entity.GetComponent<Growable>();
      _living = entity.GetComponent<LivingNaturalResource>();
    }

    public void ClearFragment() {
      _bonus = null;
      _growable = null;
      _living = null;
      _signals = null;
      _root.ToggleDisplayStyle(false);
    }

    public void UpdateFragment() {
      if (_bonus == null || !_bonus.IsActive || _growable == null
          || (_living != null && _living.IsDead)) {
        _signals = null;
        _root.ToggleDisplayStyle(false);
        return;
      }

      _bonus.RefreshBiomeState();
      var signals = _bonus.ComputeSignals();
      _signals = signals;

      _root.ToggleDisplayStyle(true);

      var verdict = GrowthDiagnostics.Classify(signals);
      _flavorLabel.text = FlavorText(verdict, signals);
      _flavorLabel.ToggleDisplayStyle(true);

      UpdateDebugLabel();
    }

    #endregion

    #region Flavor line

    private string FlavorText(GrowthVerdict verdict, in GrowthSignals s) {
      var biome = BiomeName(s.TargetBiome);
      var text = verdict switch {
          GrowthVerdict.Thriving => _loc.T(FlavorThrivingKey, biome),
          GrowthVerdict.Benefiting => _loc.T(FlavorBenefitingKey, biome),
          GrowthVerdict.Hostile => _loc.T(FlavorHostileKey, ObstaclePhrase(s) ?? biome),
          GrowthVerdict.Establishing => _loc.T(FlavorEstablishingKey, biome),
          GrowthVerdict.Potential => _loc.T(FlavorPotentialKey, biome),
          GrowthVerdict.WrongBiome =>
              s.DominantByMaturity == BiomeKind.Monoculture
                  ? _loc.T(FlavorMonocultureKey, biome)
                  : _loc.T(FlavorWrongBiomeKey, biome,
                      BiomeName(s.DominantByMaturity ?? s.TargetBiome)),
          _ => _loc.T(FlavorDormantKey, biome),
      };

      // The bonus % rides only on the bonus-positive verdicts. The "No
      // growth bonus…" lines (Hostile / WrongBiome / Dormant / Potential)
      // must never carry a "(+N%)" — a few % of residual maturity bonus
      // would otherwise contradict the text.
      var carriesBonus = verdict == GrowthVerdict.Thriving
          || verdict == GrowthVerdict.Benefiting
          || verdict == GrowthVerdict.Establishing;
      var pct = Percent(_bonus!.CurrentBonus);
      return carriesBonus && pct >= 1 ? $"{text} (+{pct}%)" : text;
    }

    #endregion

    #region Tooltip

    /// <summary>Build the technical hover breakdown from the live signal
    /// cache. Returns empty when no qualifying plant is selected (panel
    /// hidden) so the tooltip simply doesn't show.</summary>
    private string BuildTooltip() {
      if (_signals == null || _bonus == null) return string.Empty;
      var s = _signals.Value;
      var biome = BiomeName(s.TargetBiome);
      var sb = new StringBuilder();

      // --- Result ---
      sb.AppendLine(_loc.T(TipResultKey, biome,
          Percent(_bonus.CurrentBonus).ToString(),
          Percent(_bonus.ConfiguredMaxBonus).ToString()));
      sb.AppendLine();

      // --- Establishment (maturity axis) ---
      var established = s.DominantByMaturity.HasValue
          && s.DominantMaturityFraction >= GrowthDiagnostics.EstablishedMinFraction;
      if (established) {
        sb.AppendLine(_loc.T(TipEstablishedKey,
            BiomeName(s.DominantByMaturity!.Value),
            Percent(s.DominantMaturityFraction).ToString()));
        if (s.DominantByMaturity.Value != s.TargetBiome) {
          sb.AppendLine(_loc.T(TipNeedsKey, biome));
        }
      } else {
        sb.AppendLine(_loc.T(TipEstablishedNoneKey));
        sb.AppendLine(_loc.T(TipNeedsKey, biome));
      }
      if (s.ClusterMaturityFraction > 0.01f) {
        sb.AppendLine(_loc.T(TipClusterKey, biome,
            Percent(s.ClusterMaturityFraction).ToString()));
      }
      if (s.TargetBiome == BiomeKind.Forest
          && s.MatureCanopyGate >= 0f && s.MatureCanopyGate < 1f) {
        sb.AppendLine(_loc.T(TipCanopyKey, Percent(s.MatureCanopyGate).ToString()));
        sb.AppendLine(_loc.T(TipWouldBeKey,
            _loc.T(s.WouldBeForestFavorable ? WouldBeGoodKey : WouldBePoorKey)));
      }
      sb.AppendLine();

      // --- Current conditions (suitability axis) ---
      sb.AppendLine(_loc.T(TipConditionsKey, biome,
          s.Suitability.ToString("0.00"),
          _loc.T(SuitTierKey(GrowthDiagnostics.SuitabilityTierOf(s.Suitability)))));
      if (s.Suitability < GrowthDiagnostics.SuitabilityFavorable) {
        var obstacle = ObstacleReasonKey(s);
        if (obstacle != null) sb.AppendLine(_loc.T(TipLimitingKey, _loc.T(obstacle)));
      }
      sb.AppendLine();

      // --- Formula footnote ---
      sb.Append(_loc.T(TipFormulaKey));
      return sb.ToString().TrimEnd();
    }

    #endregion

    #region Helpers

    private void UpdateDebugLabel() {
      if (KeystoneDevMode.IsEnabled && _bonus!.TotalProgressAdded > 0f) {
        var pct = Percent(_bonus.TotalProgressAdded);
        _debugLabel.text = $"[debug] Bonus progress applied: {pct}% of growth time";
        _debugLabel.ToggleDisplayStyle(true);
      } else {
        _debugLabel.ToggleDisplayStyle(false);
      }
    }

    /// <summary>Loc key for the limiting-factor phrase, or null when the
    /// dominant-by-suitability biome is the target itself (conditions are
    /// simply weak, not blocked by a rival).</summary>
    private static string? ObstacleReasonKey(in GrowthSignals s) {
      var o = s.DominantBySuitability;
      if (!o.HasValue || o.Value == s.TargetBiome) return null;
      return o.Value switch {
          BiomeKind.Monoculture => ObstacleMonocultureKey,
          BiomeKind.Dry => ObstacleDryKey,
          BiomeKind.River => ObstacleRiverKey,
          BiomeKind.Lake => ObstacleLakeKey,
          BiomeKind.Wetland => ObstacleWetlandKey,
          BiomeKind.Contaminated => ObstacleContaminatedKey,
          BiomeKind.Badwater => ObstacleBadwaterKey,
          BiomeKind.Grassland => ObstacleGrasslandKey,
          BiomeKind.Forest => ObstacleForestKey,
          BiomeKind.Cave => ObstacleCaveKey,
          _ => null,
      };
    }

    private string? ObstaclePhrase(in GrowthSignals s) {
      var key = ObstacleReasonKey(s);
      return key == null ? null : _loc.T(key);
    }

    private static string SuitTierKey(SuitabilityTier tier) => tier switch {
        SuitabilityTier.Poor => SuitPoorKey,
        SuitabilityTier.Weak => SuitWeakKey,
        SuitabilityTier.Good => SuitGoodKey,
        _ => SuitIdealKey,
    };

    /// <summary>English biome display name. Uses the enum name (Forest,
    /// Grassland, …); upgrade to a loc lookup if these need translation.</summary>
    private static string BiomeName(BiomeKind biome) => biome.ToString();

    private static int Percent(float fraction) => (int)(fraction * 100f + 0.5f);

    #endregion

  }

}
