using System;
using System.Reflection;
using HarmonyLib;
using Keystone.Mod.Diagnostics;
using Timberborn.NaturalResourcesModelSystem;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Skips the body of <see cref="NaturalResourceModel.ShowCurrentModel"/>
  /// when the entity has no <c>Growable</c> component.
  ///
  /// <para><b>Why this exists.</b> Vanilla
  /// <c>NaturalResourceModel.ShowCurrentModel</c> contains an unconditional
  /// <c>_growable.ShowMatureModel()</c> call in its else-branch, with no
  /// null check. The two earlier branches gate on
  /// <c>(bool)_growable</c>, so the else-branch is reachable with
  /// <c>_growable == null</c> when an entity has
  /// <c>NaturalResourceSpec</c> but no <c>GrowableSpec</c>.
  /// </para>
  ///
  /// <para>Keystone flourishes opt out of vanilla's timer-driven growth
  /// pipeline (no <c>GrowableSpec</c>) but still want
  /// <c>NaturalResource</c> integration — catalog membership,
  /// drought/flood reactivity via <c>WateredNaturalResource</c> /
  /// <c>FloodableNaturalResource</c>, custom lifecycle code keyed off
  /// <c>LivingNaturalResource.Died</c> and
  /// <c>DyingNaturalResource.StartedDying</c>. Without this patch,
  /// <c>ShowCurrentModel</c> NREs at <c>PostInitializeEntity</c>.
  /// </para>
  ///
  /// <para><b>Skip is safe.</b> Without <c>Growable</c> there is also no
  /// <c>NaturalResourceLifecycleModel</c> created (those are constructed
  /// by <c>Growable.InitializeEntity</c> per stage), so
  /// <c>ShowCurrentModel</c> has no models to show or hide regardless.
  /// Skipping the body is functionally equivalent to a no-op for these
  /// entities. The <c>ModelChanged</c> event also doesn't fire — that's
  /// fine; consumers (e.g.
  /// <c>NaturalResourceMarkerPositionUpdater</c>) handle missing events
  /// gracefully or aren't relevant for ambient flourishes.
  /// </para>
  ///
  /// <para><b>Currently global, by necessity.</b> The patch fires on every
  /// <c>NaturalResourceModel</c> in the game — Keystone-owned or not —
  /// and skips the body whenever <c>_growable</c> is null. We attempted
  /// to narrow this to a Keystone-owned predicate but several of our own
  /// blueprints (e.g. <c>KeystoneDeer</c>, <c>Rock_medium_1</c>) don't
  /// carry the marker spec the predicate looked for, so the gate
  /// rebroke our own load path. The semantics this restores ("don't NRE
  /// when an entity has <c>NaturalResourceSpec</c> but no
  /// <c>GrowableSpec</c>") are also genuinely correct for any other mod
  /// in the same shape, so leaving the patch global is the conservative
  /// choice until every Keystone-placed blueprint carries an explicit
  /// owner marker (tracked separately).</para>
  /// </summary>
  [HarmonyPatch(typeof(NaturalResourceModel), "ShowCurrentModel")]
  public static class NaturalResourceModelShowCurrentModelPatch {

    /// <summary>Cached private-field accessor for
    /// <c>NaturalResourceModel._growable</c>. Resolved once on first
    /// patch invocation; throws if the field has been renamed in a
    /// game update so the failure is loud rather than silent.</summary>
    private static readonly FieldInfo GrowableField =
        AccessTools.Field(typeof(NaturalResourceModel), "_growable")
        ?? throw new InvalidOperationException(
            "Could not locate NaturalResourceModel._growable field -- " +
            "Timberborn API changed?");

    public static bool Prefix(NaturalResourceModel __instance) {
      PatchInvocationLog.Once(nameof(NaturalResourceModelShowCurrentModelPatch));
      try {
        var growable = GrowableField.GetValue(__instance);
        if (growable == null) {
          // No Growable -> vanilla else-branch would NRE.
          // No models to show/hide either, so skipping is safe.
          return false; // skip original method body
        }
      } catch (Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorByType(
            "NaturalResourceModelShowCurrentModelPatch", "Compatibility patch failed",
            ex, _loggedExceptions);
      }
      return true; // run original
    }

    private static readonly System.Collections.Generic.HashSet<string> _loggedExceptions =
        new(System.StringComparer.Ordinal);

  }

}
