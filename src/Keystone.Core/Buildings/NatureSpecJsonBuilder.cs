using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Keystone.Core.Buildings {

  /// <summary>
  /// Pure JSON builders for the modifier payloads emitted by
  /// Keystone's Nature integration into faction-side blueprints at
  /// <c>SpecService</c> load time. Lives in Core so the JSON shapes
  /// can be unit-tested without a Timberborn host — the Mod-side
  /// provider is now a thin iteration over
  /// <see cref="Factions.FactionRegistry.AllNatureFactions"/>
  /// that calls these builders and collects the results.
  ///
  /// <para><b>Shape stability is load-bearing.</b> The JSON keys must
  /// match the spec type names Timberborn looks for during
  /// <c>SpecService.Deserialize</c>. A missing comma, a wrong field
  /// name, or a shifted brace silently breaks Nature integration —
  /// the building's spec doesn't deserialize and the player sees no
  /// Nature need fill. The tests in <c>NatureSpecJsonBuilderTests</c>
  /// pin the exact shapes against regression.</para>
  /// </summary>
  public static class NatureSpecJsonBuilder {

    /// <summary>
    /// Builds the modifier JSON appended to a faction's
    /// <c>NeedCollection</c> blueprint: adds one Nature need id per
    /// biome in <paramref name="biomes"/>.
    ///
    /// <para>Emits, e.g. for collectionId="Folktails", needIdPrefix=
    /// "KeystoneNature.", biomes=["Forest","Grassland"]:</para>
    /// <code>{"NeedCollectionSpec":{"CollectionId":"Folktails","Needs#append":["KeystoneNature.Forest","KeystoneNature.Grassland"]}}</code>
    /// </summary>
    public static string BuildNeedCollectionAppend(
        string collectionId,
        string needIdPrefix,
        IReadOnlyList<string> biomes) {
      var sb = new StringBuilder();
      sb.Append("{\"NeedCollectionSpec\":{\"CollectionId\":\"")
        .Append(collectionId)
        .Append("\",\"Needs#append\":[");
      for (var i = 0; i < biomes.Count; i++) {
        if (i > 0) sb.Append(',');
        sb.Append('"').Append(needIdPrefix).Append(biomes[i]).Append('"');
      }
      sb.Append("]}}");
      return sb.ToString();
    }

    /// <summary>
    /// Builds the modifier JSON attached to one Nature-source
    /// building: the Nature source spec listing per-biome
    /// <c>(Biome, NeedId, PointsPerHour)</c> entries, plus the
    /// matching footprint marker per the
    /// <paramref name="transparent"/> / <paramref name="noAura"/>
    /// flags.
    ///
    /// <para>Footprint cases:
    /// <list type="bullet">
    ///   <item>transparent=true → appends
    ///         <c>,"KeystoneEcologyTransparentSpec":{}</c></item>
    ///   <item>noAura=true → appends
    ///         <c>,"KeystoneEcologyNoAuraSpec":{}</c></item>
    ///   <item>neither → no footprint marker (building settles with
    ///         its 1-tile aura, default for the rare "Nature source
    ///         that genuinely settles" case)</item>
    /// </list>
    /// Both flags true is a configuration error — asserted at the
    /// caller (the Mod-side provider) since it sees the
    /// <c>NatureBuilding</c> identity for a clear error message.</para>
    ///
    /// <para>Example emission for biomes=["Forest","Grassland"],
    /// needIdPrefix="KeystoneNature.", pointsPerHour=4.0,
    /// noAura=true:</para>
    /// <code>{"KeystoneNatureSourceSpec":{"Sources":[{"Biome":"Forest","NeedId":"KeystoneNature.Forest","PointsPerHour":4.0},{"Biome":"Grassland","NeedId":"KeystoneNature.Grassland","PointsPerHour":4.0}]},"KeystoneEcologyNoAuraSpec":{}}</code>
    /// </summary>
    public static string BuildBuildingSpec(
        IReadOnlyList<string> biomes,
        string needIdPrefix,
        float pointsPerHour,
        bool transparent,
        bool noAura) {
      var sb = new StringBuilder();
      sb.Append("{\"KeystoneNatureSourceSpec\":{\"Sources\":[");
      for (var i = 0; i < biomes.Count; i++) {
        if (i > 0) sb.Append(',');
        sb.Append("{\"Biome\":\"").Append(biomes[i]).Append('"')
          .Append(",\"NeedId\":\"").Append(needIdPrefix).Append(biomes[i]).Append('"')
          .Append(",\"PointsPerHour\":")
          .Append(pointsPerHour.ToString("0.0", CultureInfo.InvariantCulture))
          .Append('}');
      }
      sb.Append("]}");
      if (transparent) {
        sb.Append(",\"KeystoneEcologyTransparentSpec\":{}");
      } else if (noAura) {
        sb.Append(",\"KeystoneEcologyNoAuraSpec\":{}");
      }
      sb.Append('}');
      return sb.ToString();
    }

  }

}
