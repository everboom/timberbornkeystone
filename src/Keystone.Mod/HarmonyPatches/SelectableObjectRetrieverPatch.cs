using System;
using System.Reflection;
using HarmonyLib;
using Keystone.Mod.Diagnostics;
using Timberborn.SelectionSystem;
using UnityEngine;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Makes Keystone-stripped flora unselectable by intercepting
  /// <c>SelectableObjectRetriever.TryGetSelectableObject(GameObject, out SelectableObject)</c>
  /// and returning <c>false</c> for entities whose GameObject name
  /// starts with the Keystone-stripped prefix.
  ///
  /// <para><b>Why this exists.</b>
  /// <see cref="SelectableObject"/> is attached by a decorator wired to
  /// a foundational spec (probably <c>BlockObjectSpec</c> or
  /// <c>NaturalResourceSpec</c>) that we must keep for tile placement
  /// and moisture reactivity. We tried these less-invasive routes first;
  /// none worked:
  /// <list type="bullet">
  ///   <item>Stripping a candidate spec -- decorator wired to a
  ///         foundational spec, not its own.</item>
  ///   <item><c>BaseComponent.DisableComponent</c> -- the retriever
  ///         finds disabled components anyway.</item>
  ///   <item>Disabling Unity colliders -- selection is tile-based,
  ///         not <c>Physics.Raycast</c>.</item>
  ///   <item>Reflection into <c>ComponentCache.RemoveDisabledComponent</c>
  ///         -- only removes from a secondary tracking set; the
  ///         component stays in the master list and the retriever
  ///         still sees it.</item>
  /// </list></para>
  ///
  /// <para><b>Why this is the right intercept level.</b> The retriever
  /// is the canonical "is this thing selectable" question. Returning
  /// <c>false</c> here makes the entity invisible to the entire
  /// selection subsystem -- no cursor highlight, no entity panel, no
  /// <c>SelectableObjectSelectedEvent</c>. Vanilla entities are
  /// completely unaffected because we filter on the Keystone-ambient
  /// predicate (see <see cref="AmbientNaming.IsAmbient(GameObject)"/>).</para>
  ///
  /// <para><b>Targeting note.</b> <c>HarmonyPatchAttribute</c>
  /// can't express the by-ref out-parameter cleanly via attribute
  /// args, so we resolve the method via <see cref="TargetMethod"/>.
  /// There are two <c>TryGetSelectableObject</c> overloads on the
  /// retriever; we patch only the <c>GameObject</c> overload because
  /// that's the cursor's entry point (per the
  /// <c>EntitySelectionService.Select -> SelectableObjectRetriever</c>
  /// stack frames we observed).</para>
  /// </summary>
  [HarmonyPatch]
  public static class SelectableObjectRetrieverPatch {

    [HarmonyTargetMethod]
    public static MethodBase TargetMethod() {
      var method = typeof(SelectableObjectRetriever).GetMethod(
          "TryGetSelectableObject",
          BindingFlags.Public | BindingFlags.Instance,
          binder: null,
          types: new[] { typeof(GameObject), typeof(SelectableObject).MakeByRefType() },
          modifiers: null);
      if (method == null) {
        throw new InvalidOperationException(
            "Could not find SelectableObjectRetriever.TryGetSelectableObject(GameObject, out SelectableObject) -- " +
            "Timberborn API changed?");
      }
      return method;
    }

    /// <inheritdoc cref="SelectableObjectRetrieverPatch"/>
    public static bool Prefix(GameObject gameObject,
                              ref SelectableObject selectableObject,
                              ref bool __result) {
      PatchInvocationLog.Once(nameof(SelectableObjectRetrieverPatch));
      // Defensive: a bug here would otherwise leak through every cursor
      // raycast. On any unexpected failure, log + run the original method.
      try {
        if (AmbientNaming.IsAmbient(gameObject)) {
          selectableObject = null;
          __result = false;
          return false; // skip the original method
        }
      } catch (Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorByType(
            "SelectableObjectRetrieverPatch", "Compatibility patch failed",
            ex, _loggedExceptions);
      }
      return true;
    }

    private static readonly System.Collections.Generic.HashSet<string> _loggedExceptions =
        new(System.StringComparer.Ordinal);

  }

}
