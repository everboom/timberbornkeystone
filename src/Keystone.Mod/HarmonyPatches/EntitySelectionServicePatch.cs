using System;
using HarmonyLib;
using Timberborn.BaseComponentSystem;
using Keystone.Mod.Diagnostics;
using Timberborn.SelectionSystem;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Companion to <see cref="SelectableObjectRetrieverPatch"/>.
  ///
  /// <para>The retriever patch alone would be sufficient for cursor
  /// hover (no SelectableObject -> no highlight), but cursor *click*
  /// goes through <c>EntitySelectionService.Select</c> -> <c>SelectableObjectRetriever.GetSelectableObject(BaseComponent)</c>,
  /// which internally calls our patched
  /// <c>TryGetSelectableObject(GameObject)</c>, sees <c>false</c>, and
  /// throws "SelectableObject component not found" because that API is
  /// assertion-style (no graceful "not found" path).</para>
  ///
  /// <para>This patch closes that gap: when the click target is a
  /// Keystone-ambient entity (see <see cref="AmbientNaming"/>), we skip
  /// <c>Select</c> entirely. Combined
  /// with the retriever patch, the entity is invisible to both hover
  /// highlighting and click selection. The other public selection
  /// entry points (<c>SelectAndFollow</c>, <c>SelectAndFocusOn</c>,
  /// <c>Replace</c>) all eventually funnel into the same internal
  /// <c>SelectSelectable</c>; if we hit a flow that isn't covered by
  /// just patching <c>Select</c>, we'd patch <c>SelectSelectable</c>
  /// instead.</para>
  /// </summary>
  [HarmonyPatch(typeof(EntitySelectionService), nameof(EntitySelectionService.Select),
                typeof(BaseComponent))]
  public static class EntitySelectionServicePatch {

    public static bool Prefix(BaseComponent target) {
      PatchInvocationLog.Once(nameof(EntitySelectionServicePatch));
      try {
        if (AmbientNaming.IsAmbient(target)) {
          return false; // skip Select entirely; nothing downstream runs
        }
      } catch (Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorByType(
            "EntitySelectionServicePatch", "Compatibility patch failed",
            ex, _loggedExceptions);
      }
      return true;
    }

    private static readonly System.Collections.Generic.HashSet<string> _loggedExceptions =
        new(System.StringComparer.Ordinal);

  }

}
