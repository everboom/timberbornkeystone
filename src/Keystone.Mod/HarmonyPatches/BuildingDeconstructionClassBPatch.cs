using System;
using System.Collections.Generic;
using HarmonyLib;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Keystone.Mod.Diagnostics;
using Timberborn.DeconstructionSystemUI;
using UnityEngine;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Makes the building bulk-demolish tool (the one that instantly
  /// deletes player buildings inside an area drag) also pick up
  /// Keystone Class B mini-flourishes. Two Prefix patches widen the
  /// tool's input enumerable to include Class B entities in the
  /// drag rectangle:
  ///
  /// <list type="bullet">
  ///   <item><see cref="PreviewCallbackPrefix"/> on
  ///         <c>BuildingDeconstructionTool.PreviewCallback</c> — the
  ///         per-frame highlight + recoverable-good tooltip path.
  ///         Class Bs in the rect get highlighted alongside buildings
  ///         while the player is still dragging.</item>
  ///   <item><see cref="ActionCallbackPrefix"/> on
  ///         <c>BlockObjectDeletionTool&lt;BuildingSpec&gt;.ActionCallback</c>
  ///         — the commit step (mouse-up). Class Bs in the rect end
  ///         up in <c>_temporaryBlockObjects</c> and get deleted via
  ///         <c>EntityService.Delete</c> alongside buildings.</item>
  /// </list>
  ///
  /// <para><b>Decompilation evidence.</b> Patch sites and safety
  /// claims here are based on decompiling
  /// <c>Timberborn.DeconstructionSystemUI.dll</c>,
  /// <c>Timberborn.BlockObjectTools.dll</c>, and
  /// <c>Timberborn.AreaSelectionSystem.dll</c> (via <c>ilspycmd</c>
  /// against the local Timberborn install). The relevant findings:</para>
  ///
  /// <para><b>1. Where the spec filter lives.</b>
  /// <c>AreaBlockObjectAndTerrainPicker.GetBlockObjects&lt;T&gt;</c>
  /// applies a runtime LINQ predicate on the picker output:
  /// <code>
  /// from blockObject in _blockObjectPicker.PickBlockObjects(...)
  /// where blockObject.GetComponent&lt;T&gt;() != null
  /// select blockObject
  /// </code>
  /// With <c>T = BuildingSpec</c>, this drops every entity that doesn't
  /// carry a runtime <c>BuildingSpec</c> component. Class B blueprints
  /// don't carry <see cref="BuildingSpec"/> (we ruled that out — would
  /// force a migration off <c>WateredNaturalResourceSpec</c> and the
  /// flourish wilting lifecycle dies with it), so they're filtered
  /// here before reaching either callback. Widening the enumerable in
  /// a Prefix on the callbacks is the cleanest bypass that doesn't
  /// touch the spec or the picker.</para>
  ///
  /// <para><b>2. Why deletion is safe on Class Bs.</b> The base's
  /// <c>DeleteBlockObjects</c> (in
  /// <c>BlockObjectDeletionTool&lt;T&gt;</c>) is fully generic over
  /// <c>BlockObject</c>:
  /// <code>
  /// foreach (BlockObject bo in _temporaryBlockObjects)
  ///   if ((bool)bo) _entityService.Delete(bo);
  /// </code>
  /// No <c>GetSpec&lt;T&gt;()</c> call inside the deletion path; the
  /// generic parameter is consumed only by the picker's filter
  /// predicate and by tool tooltip / dialog content. Auxiliary
  /// surfaces are also safe by inspection of the same decompile:
  /// <list type="bullet">
  ///   <item><c>BuildingDeconstructionTool.PreviewCallback</c>'s
  ///         <c>FillObjectsToDeconstruct</c> queries
  ///         <c>IRecoverableObjectAdder</c>; Class B has none, so
  ///         <c>_recoverableGoodTooltip</c> just shows nothing for
  ///         them. No-op.</item>
  ///   <item><c>BlockObjectModelBlockadeIgnorer.IgnoreModelBlockades</c>
  ///         iterates <c>BlockObjectSpec</c>, which Class B carries.</item>
  ///   <item><c>SetVisibleLayerToShowAllObjects</c> reads
  ///         <c>PositionedBlocks.GetAllCoordinates()</c>, generic
  ///         over <c>BlockObject</c>.</item>
  ///   <item><c>_undoRegistry.CommitStack()</c> runs after deletion,
  ///         but the only concrete <c>IUndoRegistry</c> in the entire
  ///         API surface is <c>Timberborn.GameScene.DummyUndoRegistry</c>
  ///         — Timberborn 1.0 ships no player-facing undo, so this
  ///         is effectively a no-op.</item>
  /// </list></para>
  ///
  /// <para><b>3. Closed-generic patch trick.</b>
  /// <c>BlockObjectDeletionTool&lt;T&gt;.ActionCallback</c> is a
  /// private method on the open generic. We patch the closed generic
  /// <c>&lt;BuildingSpec&gt;</c> specialisation specifically — the
  /// other concrete subclasses (<c>EntityBlockObjectDeletionTool</c>
  /// uses <c>EntityComponent</c>; the recovered-good tool uses
  /// <c>RecoveredGoodStack</c>) have unrelated generic args and stay
  /// untouched. Harmony's <c>[HarmonyPatch(typeof(...&lt;BuildingSpec&gt;), ...)]</c>
  /// attribute resolves closed generics directly. The
  /// <c>BuildingDeconstructionTool</c> patches the more derived
  /// <c>PreviewCallback</c> override (which calls into the base's
  /// callback after its own bookkeeping); both Prefixes inject
  /// before that bookkeeping runs.</para>
  ///
  /// <para><b>Companion patches that stay.</b>
  /// <see cref="EntitySelectionServicePatch"/> /
  /// <see cref="SelectableObjectRetrieverPatch"/> still suppress
  /// hover + click selection on Class B (the player can't select
  /// individual Class B entities, only bulk-demolish them).
  /// <see cref="DemolishableSelectionToolPatch"/> still keeps Class B
  /// out of the resource (mark-for-beaver-work) demolish tool — the
  /// instant building tool is the only path that should reach
  /// Class B.</para>
  ///
  /// <para><b>If a Timberborn update breaks this.</b> Re-decompile
  /// the same three DLLs and check whether: (a) the LINQ filter at
  /// <c>GetBlockObjects&lt;T&gt;</c> changed shape (different
  /// predicate, or moved inside the picker), (b) the callback
  /// signatures changed (current shape:
  /// <c>(IEnumerable&lt;BlockObject&gt;, IEnumerable&lt;Vector3Int&gt;,
  /// Vector3Int start, Vector3Int end, bool selectionStarted, bool selectingArea)</c>),
  /// or (c) <c>BuildingDeconstructionTool</c> no longer extends
  /// <c>BlockObjectDeletionTool&lt;BuildingSpec&gt;</c>. The
  /// <c>ExpectedPatchedMethodCount</c> assertion in
  /// <c>KeystoneModStarter</c> catches Harmony resolution failures
  /// at startup; for runtime correctness regressions, in-game test
  /// the bullet list in <c>docs/timberborn-api.md</c> § "Bulk-demolish
  /// injection."</para>
  /// </summary>
  public static class BuildingDeconstructionClassBPatch {

    /// <summary>Widen <paramref name="blockObjects"/> with the Class B
    /// entities found in the <paramref name="start"/> /
    /// <paramref name="end"/> rect. No-ops if
    /// <see cref="ClassBAreaQuery.Instance"/> hasn't loaded yet
    /// (early game-startup) or no Class Bs are in the rect.</summary>
    private static void Inject(
        ref IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end) {
      var query = ClassBAreaQuery.Instance;
      if (query == null) return;
      // Delegates to the DI singleton so the rect scan + list merge can
      // be perf-tracked (a static patch can't take an injected
      // PerfTracker). See ClassBAreaQuery.InjectInto.
      query.InjectInto(ref blockObjects, start, end);
    }

    /// <summary>Prefix on <c>BuildingDeconstructionTool.PreviewCallback</c>
    /// — runs every frame while the player drags. Mutates the
    /// per-call <c>blockObjects</c> enumerable to include Class Bs in
    /// the rect so the highlight covers them too.</summary>
    [HarmonyPatch(typeof(BuildingDeconstructionTool), "PreviewCallback")]
    public static class PreviewCallbackPrefix {

      public static void Prefix(
          ref IEnumerable<BlockObject> blockObjects,
          Vector3Int start,
          Vector3Int end) {
        PatchInvocationLog.Once(nameof(BuildingDeconstructionClassBPatch) + "." + nameof(PreviewCallbackPrefix));
        try {
          Inject(ref blockObjects, start, end);
        } catch (Exception ex) {
          Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorByType(
              "BuildingDeconstructionClassBPatch.PreviewCallback", "Compatibility patch failed",
              ex, _previewLoggedExceptions);
        }
      }

      private static readonly System.Collections.Generic.HashSet<string> _previewLoggedExceptions =
          new(System.StringComparer.Ordinal);

    }

    /// <summary>Prefix on
    /// <c>BlockObjectDeletionTool&lt;BuildingSpec&gt;.ActionCallback</c>
    /// — runs once on mouse-up commit. Same widening as
    /// <see cref="PreviewCallbackPrefix"/>; downstream the entities
    /// land in <c>_temporaryBlockObjects</c> and are deleted via
    /// <c>EntityService.Delete</c>.
    /// <para><b>Closed-generic patch site.</b> The method lives on
    /// the open generic base; we patch the closed generic
    /// <c>&lt;BuildingSpec&gt;</c> specialisation so we don't accidentally
    /// catch <c>EntityBlockObjectDeletionTool</c> or the recovered-
    /// good deletion tool, which use different generic args. Harmony
    /// resolves closed generics fine via the attribute form.</para></summary>
    [HarmonyPatch(typeof(BlockObjectDeletionTool<BuildingSpec>), "ActionCallback")]
    public static class ActionCallbackPrefix {

      public static void Prefix(
          ref IEnumerable<BlockObject> blockObjects,
          Vector3Int start,
          Vector3Int end) {
        PatchInvocationLog.Once(nameof(BuildingDeconstructionClassBPatch) + "." + nameof(ActionCallbackPrefix));
        try {
          Inject(ref blockObjects, start, end);
        } catch (Exception ex) {
          Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorByType(
              "BuildingDeconstructionClassBPatch.ActionCallback", "Compatibility patch failed",
              ex, _actionLoggedExceptions);
        }
      }

      private static readonly System.Collections.Generic.HashSet<string> _actionLoggedExceptions =
          new(System.StringComparer.Ordinal);

    }

  }

}
