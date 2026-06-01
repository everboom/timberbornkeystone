using System.Collections.Generic;
using Keystone.Core.Buildings;
using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Wellbeing {

  /// <summary>
  /// Injects Keystone's Nature integration into faction-side blueprints
  /// at <c>SpecService</c> load time, replacing the per-faction
  /// <c>.optional.blueprint.json</c> overlays that previously sat under
  /// <c>unity-assets/Keystone/Data/</c>. Reads
  /// <see cref="Keystone.Core.Buildings.Factions.FactionRegistry.AllNatureFactions"/>
  /// and emits:
  /// <list type="bullet">
  /// <item>For each opted-in faction: a
  ///       <c>NeedCollectionSpec.Needs#append</c> modifier on the
  ///       faction's <c>NeedCollection</c> blueprint (adds the
  ///       Nature need ids that the faction's beavers
  ///       instantiate).</item>
  /// <item>For each listed building: a modifier attaching
  ///       <see cref="KeystoneNatureSourceSpec"/> with the building's
  ///       biome subset, plus the matching footprint marker
  ///       (<see cref="KeystoneEcologyTransparentSpec"/> or
  ///       <see cref="KeystoneEcologyNoAuraSpec"/>) per the entry's
  ///       <see cref="NatureBuilding.Transparent"/> /
  ///       <see cref="NatureBuilding.NoAura"/> flags. Both flags
  ///       false leaves the building with default settle+aura
  ///       semantics.</item>
  /// </list>
  ///
  /// <para><b>Note on standalone Transparent / No-aura entries.</b>
  /// Non-Nature buildings tagged transparent or no-aura
  /// (lanterns, beehives, sensors, designation flags, decorations,
  /// dynamite, etc.) are NOT modified here. They're detected
  /// adapter-side by name via
  /// <see cref="BlueprintNamePolicy.TransparentBuildingNames"/> and
  /// <see cref="BlueprintNamePolicy.NoAuraBuildingNames"/>, which
  /// keeps Keystone's footprint on vanilla blueprints minimal.
  /// External mods that want to opt their own content in can still
  /// attach the spec types directly — the adapter ORs both detection
  /// paths.</para>
  ///
  /// <para><b>Path matching.</b> Each entry produces a lowercased
  /// <c>"/&lt;filename&gt;.blueprint"</c> suffix. Blueprints are matched
  /// by <c>EndsWith</c> on this suffix against the (already-lowercased)
  /// blueprint Path. This is the trick that makes Emberpelts and vanilla
  /// integrate without per-mod directory overlays: the suffix is
  /// faction-mod-agnostic, the merge key (the Path) is whatever
  /// directory the target mod chose.</para>
  ///
  /// <para><b>Lifecycle.</b> Singleton in the Game scope, bound by
  /// <c>KeystoneConfigurator</c> via concrete-then-<c>ToExisting</c>
  /// (same pattern as Timberborn's own
  /// <c>FactionBlueprintModifierProvider</c>). The modifier dictionary
  /// is built once at construction; <see cref="GetModifiers"/> is then
  /// called per loaded blueprint by <c>SpecService.Deserialize</c>.</para>
  /// </summary>
  internal sealed class KeystoneNatureModifierProvider : IBlueprintModifierProvider {

    #region Fields

    // Lowercased "/<filename>.blueprint" suffix → JSON modifier string.
    // Built once; iterated per loaded blueprint. Linear scan is fine at
    // this scale (one entry per faction + one per building, total ~5).
    private readonly Dictionary<string, string> _modifiersBySuffix;

    #endregion

    #region Construction

    public KeystoneNatureModifierProvider() {
      _modifiersBySuffix = BuildLookup();
    }

    #endregion

    #region IBlueprintModifierProvider

    /// <inheritdoc />
    public string ModifierName => "Keystone Nature integration";

    /// <inheritdoc />
    public IEnumerable<string> GetModifiers(string blueprintPath) {
      foreach (var (suffix, json) in _modifiersBySuffix) {
        if (blueprintPath.EndsWith(suffix)) {
          yield return json;
        }
      }
    }

    #endregion

    #region Modifier table

    private static Dictionary<string, string> BuildLookup() {
      var dict = new Dictionary<string, string>();
      foreach (var faction in Keystone.Core.Buildings.Factions.FactionRegistry.AllNatureFactions) {
        // Per-faction biome union: only the biomes that appear on at
        // least one of the faction's buildings get appended to its
        // NeedCollection. Lets a faction with no water-themed
        // buildings (e.g. Emberpelts) opt out of the Wetland need
        // entirely — beavers in that faction never instantiate it on
        // their NeedManager.
        var factionBiomes = ComputeFactionBiomeUnion(faction);
        if (factionBiomes.Count > 0) {
          var needSuffix = $"/needcollection.{faction.FactionId.ToLowerInvariant()}.blueprint";
          dict.Add(needSuffix, NatureSpecJsonBuilder.BuildNeedCollectionAppend(
              faction.FactionId,
              KeystoneNatureFactions.NeedIdPrefix,
              factionBiomes));
        }

        // Each listed building — attach the source spec, plus a
        // footprint marker depending on the entry's flags:
        //   Transparent=true  → KeystoneEcologyTransparentSpec
        //   NoAura=true       → KeystoneEcologyNoAuraSpec
        //   neither           → no footprint marker (settles + aura)
        // Both flags true is a configuration error.
        foreach (var building in faction.Buildings) {
          if (building.Transparent && building.NoAura) {
            throw new System.InvalidOperationException(
                $"NatureBuilding '{building.BlueprintName}' has both Transparent and "
                + "NoAura set; these are mutually exclusive (transparent = the surveyor "
                + "doesn't see the building; no-aura = the surveyor counts it but it "
                + "doesn't propagate). Pick one.");
          }
          var bldgSuffix = $"/{building.BlueprintName.ToLowerInvariant()}.blueprint";
          dict.Add(bldgSuffix, NatureSpecJsonBuilder.BuildBuildingSpec(
              building.Biomes,
              KeystoneNatureFactions.NeedIdPrefix,
              KeystoneNatureFactions.DefaultPointsPerHour,
              building.Transparent,
              building.NoAura));
        }
      }

      // Note: standalone transparent / no-aura entries do NOT get
      // spec modifiers any more — they're detected adapter-side via
      // BlueprintNamePolicy.{Transparent,NoAura}BuildingNames. That
      // keeps Keystone's footprint on vanilla blueprints minimal: the
      // only buildings we still inject specs into are Nature sources
      // (the 12 KeystoneNatureFactions entries above, which need the
      // per-building Sources biome data attached).
      return dict;
    }

    /// <summary>Walk a faction's building list and return the union
    /// of biomes used across them, preserving the canonical ordering
    /// from <see cref="Keystone.Core.Buildings.Factions.NatureBiomes.All"/>.
    /// Used to scope the faction's <c>NeedCollection</c> append to
    /// only the biomes the faction actually engages with.</summary>
    private static IReadOnlyList<string> ComputeFactionBiomeUnion(
        Keystone.Core.Buildings.Factions.NatureFactionEntry faction) {
      var seen = new HashSet<string>();
      foreach (var building in faction.Buildings) {
        foreach (var biome in building.Biomes) {
          seen.Add(biome);
        }
      }
      var result = new List<string>(seen.Count);
      foreach (var biome in Keystone.Core.Buildings.Factions.NatureBiomes.All) {
        if (seen.Contains(biome)) result.Add(biome);
      }
      return result;
    }

    #endregion

  }

}
