using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.DemolishingUI;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Filters Keystone-ambient entities (see <see cref="AmbientNaming"/>)
  /// out of the demolish tool's player-facing actions, so the
  /// demolish/remove tool can't mark them for demolition.
  ///
  /// <para><b>Why a Harmony patch.</b> <c>Demolishable</c> is wired to
  /// a foundational spec we keep (similar to <c>SelectableObject</c>);
  /// stripping <c>DemolishableSpec</c> causes <c>Demolishable.Awake</c>
  /// to null-deref reading the missing config; calling
  /// <c>DisableComponent</c> on the runtime component does not stop the
  /// tool from finding it. The cleanest cut is at the tool's input
  /// path: filter the <c>BlockObject</c> list before the tool acts.</para>
  ///
  /// <para><b>Two callbacks.</b>
  /// <c>DemolishableSelectionTool.ActionCallback</c> is the commit
  /// step (mouse-up on the area selection). <c>PreviewCallback</c> is
  /// the visual preview while the player is dragging. We patch both so
  /// the player gets no false-positive ghost on our entities.</para>
  ///
  /// <para><b>What still works.</b> Code-driven removal -- e.g.
  /// <c>EntityService.Delete</c>, or the
  /// <c>WateredNaturalResource</c> auto-delete after
  /// <c>DaysToDieDry</c> -- is unaffected. Player-driven demolish is
  /// what we're blocking, not all removal.</para>
  /// </summary>
  public static class DemolishableSelectionToolPatch {

    private static IEnumerable<BlockObject> FilterAmbient(IEnumerable<BlockObject> input) {
      // Materialise to a list -- the tool may iterate the enumerable
      // multiple times, and we don't want our Where to re-evaluate.
      return input.Where(bo => !AmbientNaming.IsAmbient(bo)).ToList();
    }

    [HarmonyPatch(typeof(DemolishableSelectionTool), "ActionCallback")]
    public static class ActionCallback {
      public static void Prefix(ref IEnumerable<BlockObject> blockObjects) {
        PatchInvocationLog.Once(nameof(DemolishableSelectionToolPatch) + "." + nameof(ActionCallback));
        try {
          if (blockObjects == null) return;
          blockObjects = FilterAmbient(blockObjects);
        } catch (Exception ex) {
          Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorByType(
              "DemolishableSelectionToolPatch.ActionCallback", "Compatibility patch failed",
              ex, _actionLoggedExceptions);
        }
      }

      private static readonly System.Collections.Generic.HashSet<string> _actionLoggedExceptions =
          new(System.StringComparer.Ordinal);
    }

    [HarmonyPatch(typeof(DemolishableSelectionTool), "PreviewCallback")]
    public static class PreviewCallback {
      public static void Prefix(ref IEnumerable<BlockObject> blockObjects) {
        PatchInvocationLog.Once(nameof(DemolishableSelectionToolPatch) + "." + nameof(PreviewCallback));
        try {
          if (blockObjects == null) return;
          blockObjects = FilterAmbient(blockObjects);
        } catch (Exception ex) {
          Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorByType(
              "DemolishableSelectionToolPatch.PreviewCallback", "Compatibility patch failed",
              ex, _previewLoggedExceptions);
        }
      }

      private static readonly System.Collections.Generic.HashSet<string> _previewLoggedExceptions =
          new(System.StringComparer.Ordinal);
    }

  }

}
