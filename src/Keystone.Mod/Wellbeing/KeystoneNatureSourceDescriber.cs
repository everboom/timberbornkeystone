using System.Collections.Generic;
using System.Text;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.Common;
using Timberborn.CoreUI;
using Timberborn.Effects;
using Timberborn.EntityPanelSystem;
using Timberborn.GameFactionSystem;
using Timberborn.NeedSpecs;

namespace Keystone.Mod.Wellbeing {

  /// <summary>
  /// <see cref="IEntityDescriber"/> that surfaces the building's
  /// Nature-need affordance in the build-menu tooltip and the placed
  /// entity panel. Reuses vanilla <see cref="EffectDescriber"/> to
  /// format need lines so the appearance matches what
  /// <see cref="Timberborn.AttractionsUI.AttractionDescriber"/>
  /// produces for vanilla attractions.
  ///
  /// <para><b>Why this exists.</b> The Nature-need mechanism rolls its
  /// own <see cref="KeystoneNatureSourceSpec"/> instead of extending
  /// <c>AttractionSpec.Effects</c> (so vanilla entertainment isn't
  /// scaled / affected by biome conditions). The vanilla
  /// <c>AttractionDescriber</c> only sees <c>AttractionSpec.Effects</c>,
  /// so without this describer the player would have no UI surface
  /// telling them the building also satisfies a Nature need at all.</para>
  ///
  /// <para><b>Static + live.</b> Always lists the building's eligible
  /// Nature sources (the static affordance). When the entity is in the
  /// world and <see cref="KeystoneNatureSource"/> has a winning source
  /// for the current biome state, also appends a plain-English line
  /// reporting the currently-active need and its scaled rate. The
  /// build-menu preview entity doesn't tick so the live line is
  /// silently omitted there — exactly what we want, since "what does
  /// this building actually do right now" only makes sense once
  /// placed.</para>
  /// </summary>
  public sealed class KeystoneNatureSourceDescriber : BaseComponent, IAwakableComponent, IEntityDescriber {

    #region Constants

    /// <summary>Sort order for our description section. Sits just below
    /// vanilla <c>AttractionDescriber</c> (1010) so when both fire on
    /// the same building, the Keystone block reads after the vanilla
    /// entertainment block rather than wedged above it.</summary>
    private const int DescriptionOrder = 1015;

    #endregion

    #region Injected services

    private readonly EffectDescriber _effectDescriber;
    private readonly FactionNeedService _factionNeedService;

    #endregion

    #region Per-instance state

    private KeystoneNatureSourceSpec? _spec;
    private KeystoneNatureSource? _source;
    private List<ContinuousEffectSpec>? _synthesizedEffects;
    private Dictionary<string, ContinuousEffectSpec>? _synthesizedByNeedId;
    /// <summary>Eligible source entries after current-faction
    /// filtering. Same shape as the spec but only includes entries
    /// whose <c>NeedId</c> is registered for the active faction —
    /// drives the build-menu list and the inactive-state biome hint.</summary>
    private List<KeystoneNatureSourceEntry>? _eligibleEntries;
    private readonly List<ContinuousEffectSpec> _scratchSingle = new();
    private readonly StringBuilder _builder = new();

    #endregion

    #region Construction

    public KeystoneNatureSourceDescriber(
        EffectDescriber effectDescriber,
        FactionNeedService factionNeedService) {
      _effectDescriber = effectDescriber;
      _factionNeedService = factionNeedService;
    }

    #endregion

    #region IAwakableComponent

    public void Awake() {
      var blockSpec = GetComponent<BlockObjectSpec>();
      _spec = blockSpec != null ? blockSpec.GetSpec<KeystoneNatureSourceSpec>() : null;
      _source = GetComponent<KeystoneNatureSource>();
      if (_spec != null && !_spec.Sources.IsDefault && _spec.Sources.Length > 0) {
        // Synthesise ContinuousEffectSpec instances so we can reuse
        // vanilla EffectDescriber (which only reads .NeedId). The
        // synthesised list never enters the simulation; it's purely a
        // text-formatter input.
        //
        // Filter to needs registered for the current faction. If a
        // third-party faction inherits ContemplationSpot or Lido
        // without registering our needs, EffectDescriber would call
        // FactionNeedService.GetBeaverOrBotNeedById which THROWS on
        // unknown ids — so dropping un-registered needs here is the
        // load-bearing safety check, not just a polish step.
        _synthesizedEffects = new List<ContinuousEffectSpec>(_spec.Sources.Length);
        _synthesizedByNeedId = new Dictionary<string, ContinuousEffectSpec>(_spec.Sources.Length);
        _eligibleEntries = new List<KeystoneNatureSourceEntry>(_spec.Sources.Length);
        for (var i = 0; i < _spec.Sources.Length; i++) {
          var entry = _spec.Sources[i];
          if (!_factionNeedService.IsCurrentFactionNeed(entry.NeedId)) continue;
          var synth = new ContinuousEffectSpec {
              NeedId = entry.NeedId,
              PointsPerHour = entry.PointsPerHour,
              SatisfyToMaxValue = false,
          };
          _synthesizedEffects.Add(synth);
          _synthesizedByNeedId[entry.NeedId] = synth;
          _eligibleEntries.Add(entry);
        }
      }
    }

    #endregion

    #region IEntityDescriber

    public IEnumerable<EntityDescription> DescribeEntity() {
      if (_synthesizedEffects == null || _synthesizedEffects.Count == 0) {
        yield break;
      }

      _builder.Clear();
      var inWorld = _source != null && _source.IsInWorld;
      var activeNeedId = _source?.CurrentNeedId;

      if (!inWorld) {
        // Build-menu preview: list every eligible Nature source so the
        // player can compare buildings at-a-glance before placing.
        _effectDescriber.DescribeEffects(_synthesizedEffects, _builder);
      } else if (activeNeedId != null
          && _synthesizedByNeedId != null
          && _synthesizedByNeedId.TryGetValue(activeNeedId, out var activeSynth)) {
        // Placed + active: only the currently-winning source plus a
        // qualitative tier label and two-axis description (size and
        // maturity) so the player can see both what's working and
        // what to improve. The raw rate (e.g. "3.2 pts/hour") is
        // hidden — they need to know whether their placement is good
        // and which biome is driving it, not a precise number.
        _scratchSingle.Clear();
        _scratchSingle.Add(activeSynth);
        _effectDescriber.DescribeEffects(_scratchSingle, _builder);
        _builder.AppendLine();
        _builder.Append(SpecialStrings.RowStarter);
        AppendQualitativeStatus(
            _builder,
            _source!.CurrentScore,
            _source.CurrentChunkCount,
            _source.CurrentAverageMaturity,
            _source.CurrentBiome);
      } else {
        // Placed + inactive: surface the placement requirement so the
        // player knows the building is awake but waiting on biome
        // conditions.
        AppendInactiveHelp(_builder);
      }

      var text = _builder.ToStringWithoutNewLineEndAndClean();
      if (string.IsNullOrEmpty(text)) yield break;
      yield return EntityDescription.CreateTextSection(text, DescriptionOrder);
    }

    /// <summary>Emit a two-axis status string like "Medium bonus from
    /// a small mature Forest nearby." Score buckets give the headline
    /// tier (Minor / Medium / Major / Pristine) and the size + maturity
    /// adjectives are derived independently from the winning cluster's
    /// chunk count and average Maturity, so the player can see which
    /// axis is weak and what to improve. Size buckets at 2/5 chunks,
    /// Maturity buckets at 5/10 game-days. Tier cutoffs at 0.25 / 0.50
    /// / 0.75 against the hyperbolic <c>Score</c>, evenly spaced
    /// across the practical score range — each step roughly doubles
    /// the cluster's effective biome footprint, so the leap between
    /// tiers feels earned. Pristine reads as "you've built a
    /// world-class example of this biome."</summary>
    private static void AppendQualitativeStatus(
        StringBuilder b,
        float score,
        int chunkCount,
        float averageMaturity,
        Keystone.Core.Biomes.BiomeKind? biome) {
      string sizeAdj;
      if (chunkCount <= 2) sizeAdj = "small";
      else if (chunkCount <= 5) sizeAdj = "medium";
      else sizeAdj = "large";

      string maturityAdj;
      if (averageMaturity < 5f) maturityAdj = "immature";
      else if (averageMaturity < 10f) maturityAdj = "healthy";
      else maturityAdj = "mature";

      string tierWord;
      if (score < 0.25f) tierWord = "Minor";
      else if (score < 0.50f) tierWord = "Medium";
      else if (score < 0.75f) tierWord = "Major";
      else tierWord = "Pristine";

      b.Append(tierWord)
          .Append(" bonus from a ")
          .Append(sizeAdj)
          .Append(' ')
          .Append(maturityAdj)
          .Append(' ')
          .Append(biome.HasValue ? biome.Value.ToString() : "biome")
          .Append(" nearby.");
    }

    /// <summary>Emit a player-facing hint for the inactive case,
    /// dynamically listing the building's eligible biomes so the same
    /// describer works for Lido (one biome) and Contemplation Spot
    /// (three) without per-building strings.</summary>
    private void AppendInactiveHelp(StringBuilder b) {
      b.Append(SpecialStrings.RowStarter)
          .Append("Place near a well-developed ");
      AppendBiomeList(b);
      b.Append(" to provide a Nature bonus.");
    }

    /// <summary>Format the eligible biomes (current-faction-filtered)
    /// as "Forest", "Forest or Grassland", or "Forest, Grassland, or
    /// Wetland" (Oxford comma).</summary>
    private void AppendBiomeList(StringBuilder b) {
      if (_eligibleEntries == null) return;
      var count = _eligibleEntries.Count;
      if (count == 0) return;
      if (count == 1) {
        b.Append(_eligibleEntries[0].Biome);
        return;
      }
      if (count == 2) {
        b.Append(_eligibleEntries[0].Biome).Append(" or ").Append(_eligibleEntries[1].Biome);
        return;
      }
      for (var i = 0; i < count - 1; i++) {
        b.Append(_eligibleEntries[i].Biome).Append(", ");
      }
      b.Append("or ").Append(_eligibleEntries[count - 1].Biome);
    }

    #endregion

  }

}
