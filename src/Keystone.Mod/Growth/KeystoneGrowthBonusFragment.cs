using Keystone.Core.Biomes;
using Keystone.Mod.Diagnostics;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.Growing;
using Timberborn.Localization;
using Timberborn.NaturalResourcesLifecycle;
using UnityEngine.UIElements;

namespace Keystone.Mod.Growth {

  /// <summary>
  /// Entity-panel fragment that shows the biome growth bonus on
  /// natural-resource plants. Uses <see cref="NineSliceVisualElement"/>
  /// with Timberborn's standard panel classes for consistent styling.
  /// Text goes through <see cref="ILoc"/> so highlight tags render
  /// with proper colors.
  /// </summary>
  public class KeystoneGrowthBonusFragment : IEntityPanelFragment {

    #region Loc keys

    private const string GrowingSuitableKey = "Keystone.GrowthBonus.Growing.SuitableConditions";
    private const string GrowingNearbyKey = "Keystone.GrowthBonus.Growing.NearbyEstablished";
    private const string GrowingMaturingKey = "Keystone.GrowthBonus.Growing.Maturing";
    private const string GrowingThrivingKey = "Keystone.GrowthBonus.Growing.Thriving";
    private const string WouldGrowKey = "Keystone.GrowthBonus.WouldGrow";
    private const string HealthyBiomeKey = "Keystone.GrowthBonus.HealthyBiome";
    private const string MonocultureWarningKey = "Keystone.GrowthBonus.MonocultureWarning";
    private const string MonocultureBriefKey = "Keystone.GrowthBonus.MonocultureBrief";
    private const string RiverWarningKey = "Keystone.GrowthBonus.RiverWarning";
    private const string RiverBriefKey = "Keystone.GrowthBonus.RiverBrief";

    #endregion

    #region Injected services

    private readonly ILoc _loc;

    #endregion

    #region UI elements

    private NineSliceVisualElement _root;
    private Label _bonusLabel;
    private Label _warningLabel;
    private Label _debugLabel;

    #endregion

    #region Cached entity state

    private KeystoneGrowthBonus? _bonus;
    private Growable? _growable;
    private LivingNaturalResource? _living;

    #endregion

    #region Construction

    public KeystoneGrowthBonusFragment(ILoc loc) {
      _loc = loc;
    }

    #endregion

    #region IEntityPanelFragment

    public VisualElement InitializeFragment() {
      _root = new NineSliceVisualElement();
      _root.AddToClassList("bg-sub-box--green");
      _root.AddToClassList("entity-sub-panel");
      _root.ToggleDisplayStyle(false);

      _bonusLabel = new Label();
      _bonusLabel.AddToClassList("entity-panel__text");
      _root.Add(_bonusLabel);

      _warningLabel = new Label();
      _warningLabel.AddToClassList("entity-panel__text");
      _root.Add(_warningLabel);

      _debugLabel = new Label();
      _debugLabel.AddToClassList("entity-panel__text");
      _root.Add(_debugLabel);

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
      _root.ToggleDisplayStyle(false);
    }

    public void UpdateFragment() {
      if (_bonus == null || !_bonus.IsActive || _growable == null) {
        _root.ToggleDisplayStyle(false);
        return;
      }

      if (_living != null && _living.IsDead) {
        _root.ToggleDisplayStyle(false);
        return;
      }

      _bonus.RefreshBiomeState();
      var biome = _bonus.TargetBiome!.Value;

      _root.ToggleDisplayStyle(true);

      if (KeystoneDevMode.IsEnabled) {
        var added = _bonus.TotalProgressAdded;
        if (added > 0f) {
          var pct = (int)(added * 100f + 0.5f);
          _debugLabel.text = $"[debug] Bonus progress applied: {pct}% of growth time";
          _debugLabel.ToggleDisplayStyle(true);
        } else {
          _debugLabel.ToggleDisplayStyle(false);
        }
      } else {
        _debugLabel.ToggleDisplayStyle(false);
      }

      if (_growable.IsGrown) {
        UpdateGrownPlant(biome);
      } else {
        UpdateGrowingPlant(biome);
      }
    }

    #endregion

    #region Display logic

    private void UpdateGrowingPlant(BiomeKind biome) {
      var currentBonus = _bonus!.CurrentBonus;
      if (currentBonus > 0f) {
        var tier = BonusTier(currentBonus, _bonus.ConfiguredMaxBonus);
        var key = FactorKey(_bonus.CurrentSuitability, _bonus.CurrentMaturityFraction);
        _bonusLabel.text = _loc.T(key, tier, biome.ToString());
      } else {
        _bonusLabel.text = _loc.T(WouldGrowKey, biome.ToString());
      }
      _bonusLabel.ToggleDisplayStyle(true);

      if (_bonus.IsSuppressedByCompetingBiome) {
        _warningLabel.text = _loc.T(CompetingWarningKey(_bonus.CompetingBiome));
        _warningLabel.ToggleDisplayStyle(true);
      } else {
        _warningLabel.ToggleDisplayStyle(false);
      }
    }

    private void UpdateGrownPlant(BiomeKind biome) {
      if (_bonus!.IsSuppressedByCompetingBiome) {
        _bonusLabel.text = _loc.T(CompetingBriefKey(_bonus.CompetingBiome));
        _bonusLabel.ToggleDisplayStyle(true);
      } else if (_bonus.CurrentBonus > 0f) {
        _bonusLabel.text = _loc.T(HealthyBiomeKey, biome.ToString());
        _bonusLabel.ToggleDisplayStyle(true);
      } else {
        _bonusLabel.ToggleDisplayStyle(false);
      }

      _warningLabel.ToggleDisplayStyle(false);
    }

    private static string BonusTier(float bonus, float maxBonus) {
      if (maxBonus <= 0f) return "Slight";
      var ratio = bonus / maxBonus;
      if (ratio < 0.25f) return "Slight";
      if (ratio < 0.50f) return "Moderate";
      if (ratio < 0.75f) return "Strong";
      return "Exceptional";
    }

    private static string FactorKey(float suitability, float maturityFraction) {
      var suitHigh = suitability >= 0.5f;
      var matHigh = maturityFraction >= 0.3f;
      if (suitHigh && matHigh) return GrowingThrivingKey;
      if (suitHigh) return GrowingSuitableKey;
      if (matHigh) return GrowingNearbyKey;
      return GrowingMaturingKey;
    }

    private static string CompetingWarningKey(BiomeKind? competitor) => competitor switch {
      BiomeKind.Monoculture => MonocultureWarningKey,
      BiomeKind.River => RiverWarningKey,
      _ => MonocultureWarningKey,
    };

    private static string CompetingBriefKey(BiomeKind? competitor) => competitor switch {
      BiomeKind.Monoculture => MonocultureBriefKey,
      BiomeKind.River => RiverBriefKey,
      _ => MonocultureBriefKey,
    };

    #endregion

  }

}
