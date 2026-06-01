using System;
using System.Collections.Generic;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.TickSystem;
using Timberborn.Timbermesh;
using UnityEngine;

namespace Keystone.Mod.Flourish {

  /// <summary>
  /// Per-entity tickable that re-paints the entity's child rock
  /// renderers based on the live ecological state of the entity's
  /// tile. Attaches to any blueprint carrying
  /// <see cref="KeystoneRockTintSpec"/> via the decorator binding in
  /// <c>KeystoneTemplateModuleProvider</c>.
  ///
  /// <para><b>Per-entity tick, no central registry.</b> Extends
  /// <see cref="TickableComponent"/> so Timberborn's tick system
  /// drives each instance's <see cref="Tick"/> directly at game-tick
  /// cadence (~5/sec at 1x speed). No singleton service, no rolling
  /// sweep, no static accessor -- each rock cluster polls its own
  /// state on the engine's schedule.</para>
  ///
  /// <para><b>Tile-signal policy, not chunk-biome resolution.</b>
  /// Each tick the component queries the entity's tile directly:
  /// <list type="bullet">
  ///   <item><c>WaterDepth &gt; 0</c> (submerged):
  ///         <list type="bullet">
  ///           <item>column contamination &gt; 5% -> Default (base) --
  ///                 badwater killed the moss; rock reads as clean stone.</item>
  ///           <item>otherwise -> Mossy variant -- clean water,
  ///                 healthy moss growth.</item>
  ///         </list></item>
  ///   <item><c>IsContaminated</c> -> Dry variant (contamination wins
  ///         over moisture; a contaminated wet tile still reads as
  ///         dead).</item>
  ///   <item><c>IsMoist</c> (irrigated land) -> Default (untinted) --
  ///         clean stone.</item>
  ///   <item>otherwise -> Dry variant (arid).</item>
  /// </list>
  /// More semantically accurate than chunk-biome resolution: a dry
  /// tile in a notional "Wetland" chunk correctly reads as dry stone,
  /// not as moss. And the reactivity is bounded only by the
  /// sim tick rate -- no waiting for Score drift to flip chunk
  /// classification.</para>
  ///
  /// <para><b>Family auto-detection.</b> Each cached renderer
  /// remembers the material-family base name detected at init
  /// (stripping <c>_Dry</c>/<c>_Mossy</c> suffixes if present). The
  /// tick appends the policy-derived suffix to that base, so
  /// <c>KeystoneRock</c> and <c>KeystonePathRocks</c> families coexist
  /// in one cluster without the component knowing about either
  /// specifically.</para>
  ///
  /// <para><b>Family filter.</b> Only renderers whose base name
  /// starts with <c>KeystoneRock</c> or <c>KeystonePathRocks</c> are
  /// tracked, so a hybrid blueprint that accidentally bundles vanilla
  /// flora doesn't get its plants re-tinted.</para>
  /// </summary>
  public sealed class KeystoneRockTint
      : TickableComponent, IInitializableEntity, IDeletableEntity {

    #region Constants

    private const string DrySuffix = "_Dry";
    private const string MossySuffix = "_Mossy";
    private const string DefaultSuffix = "";

    /// <summary>Allowed family prefixes -- only renderers whose base
    /// name starts with one of these are tracked. Defensive against
    /// hybrid blueprints accidentally tinting vanilla flora.</summary>
    private static readonly string[] FamilyPrefixes = {
        "KeystoneRock",
        "KeystonePathRocks",
    };

    #endregion

    #region Injected dependencies

    private readonly IMaterialRepository _materials;
    private readonly IWaterQuery _water;
    private readonly IMoistureQuery _moisture;
    private readonly IContaminationQuery _contamination;

    #endregion

    #region Per-instance state

    private BlockObject? _blockObject;
    private readonly List<RendererEntry> _renderers = new();

    /// <summary>De-dup set so a missing-material lookup logs once per
    /// unique target name per instance, not per tick.</summary>
    private readonly HashSet<string> _warnedMissing = new();

    #endregion

    #region Construction

    public KeystoneRockTint(
        IMaterialRepository materials,
        IWaterQuery water,
        IMoistureQuery moisture,
        IContaminationQuery contamination) {
      _materials = materials;
      _water = water;
      _moisture = moisture;
      _contamination = contamination;
    }

    #endregion

    #region Entity lifecycle

    public void InitializeEntity() {
      // Outermost try/catch: a MeshRenderer walk failure (degenerate
      // prefab hierarchy, missing transform) shouldn't leave the
      // entity in a Bindito-managed-but-unwired state. Catch + log;
      // the entity exists with an empty _renderers list, ApplyTint
      // becomes a no-op.
      try {
        _blockObject = GetComponent<BlockObject>();
        var meshRenderers = Transform.GetComponentsInChildren<MeshRenderer>();
        for (var i = 0; i < meshRenderers.Length; i++) {
          var renderer = meshRenderers[i];
          if (renderer == null) continue;
          var material = renderer.sharedMaterial;
          if (material == null) continue;
          var baseName = StripVariantSuffix(material.name);
          if (!IsKnownFamily(baseName)) continue;
          _renderers.Add(new RendererEntry(renderer, baseName));
        }
        // Initial paint so the entity doesn't render one tick of "default
        // material from the .timbermesh" before the first Tick() fires.
        ApplyTint();
      } catch (System.Exception ex) {
        Diagnostics.LifecycleGuard.HandleError($"KeystoneRockTint.InitializeEntity on '{Name}'", "Per-entity init errors", ex);
      }
    }

    public void DeleteEntity() {
      // Tick system handles teardown; no manual unregister.
    }

    #endregion

    #region TickableComponent

    /// <inheritdoc />
    public override void Tick() {
      // Outermost try/catch around ApplyTint: a renderer / material
      // accessor failure on this entity shouldn't escape the per-tick
      // queue and drop subsequent rock-tint entities from update.
      // Rate-limited per entity so a persistently failing renderer
      // doesn't spam every tick.
      try {
        ApplyTint();
      } catch (System.Exception ex) {
        Diagnostics.LifecycleGuard.HandleErrorOnce("KeystoneRockTint.Tick", "Per-entity tick errors", ex, ref _tickFailureLogged);
      }
    }

    private bool _tickFailureLogged;

    #endregion

    #region Tint application

    private void ApplyTint() {
      var bo = _blockObject;
      if (bo == null || _renderers.Count == 0) return;
      var suffix = SuffixForTile(bo.Coordinates);
      for (var i = 0; i < _renderers.Count; i++) {
        var entry = _renderers[i];
        var renderer = entry.Renderer;
        if (renderer == null) continue;
        var targetName = entry.BaseName + suffix;
        var current = renderer.sharedMaterial;
        if (current != null && current.name == targetName) continue;
        var target = _materials.GetMaterial(targetName);
        if (target == null) {
          if (_warnedMissing.Add(targetName)) {
            KeystoneLog.Error(
                $"[Keystone] KeystoneRockTint '{Name}': IMaterialRepository." +
                $"GetMaterial('{targetName}') returned null. Check that " +
                "MaterialCollection.Keystone lists it.");
          }
          continue;
        }
        renderer.sharedMaterial = target;
      }
    }

    /// <summary>The variant this cluster is currently rendering as,
    /// per the tile-signal policy. Used by the debug overlay; the
    /// runtime tick reads the same policy via <see cref="SuffixForTile"/>.
    /// Returns <c>"base"</c> for the untinted default, <c>"mossy"</c>
    /// for the wet variant, <c>"dry"</c> for the arid/contaminated
    /// variant. Returns <c>"(uninitialised)"</c> before
    /// <see cref="InitializeEntity"/> has captured the block object
    /// — should never appear in a player-visible context but is the
    /// honest answer for the diagnostic surface.</summary>
    public string CurrentVariantLabel() {
      var bo = _blockObject;
      if (bo == null) return "(uninitialised)";
      var suffix = SuffixForTile(bo.Coordinates);
      if (suffix == MossySuffix) return "mossy";
      if (suffix == DrySuffix) return "dry";
      return "base";
    }

    /// <summary>Tile-signal -> suffix policy. Order matters:
    /// submerged beats contamination beats moisture beats absence.
    /// When submerged we read the water column's contamination
    /// directly (Timberborn's <c>IThreadSafeWaterMap.ColumnContamination</c>
    /// via <see cref="IWaterQuery.WaterContaminationAt"/>) — that's
    /// the actual badwater-vs-clean-water signal. The earlier
    /// soil-side check could miss fresh badwater pools because the
    /// contamination plume in the dirt lags the water by hours.</summary>
    private string SuffixForTile(Vector3Int tile) {
      var surface = new SurfaceCoord(tile.x, tile.y, tile.z);
      if (_water.WaterDepthAt(surface) > 0f) {
        return _water.WaterContaminationAt(surface) > 0f
            ? DefaultSuffix
            : MossySuffix;
      }
      if (_contamination.IsContaminatedAt(surface)) return DrySuffix;
      if (_moisture.IsMoistAt(surface)) return DefaultSuffix;
      return DrySuffix;
    }

    #endregion

    #region Helpers

    private static string StripVariantSuffix(string materialName) {
      // Unity sometimes appends " (Instance)" when the renderer's
      // shared material has been instanced; defensively strip that
      // first so the suffix check works against the raw name.
      var instanceTag = " (Instance)";
      var tagIdx = materialName.IndexOf(instanceTag, StringComparison.Ordinal);
      if (tagIdx >= 0) {
        materialName = materialName.Substring(0, tagIdx);
      }
      if (materialName.EndsWith(DrySuffix, StringComparison.Ordinal)) {
        return materialName.Substring(0, materialName.Length - DrySuffix.Length);
      }
      if (materialName.EndsWith(MossySuffix, StringComparison.Ordinal)) {
        return materialName.Substring(0, materialName.Length - MossySuffix.Length);
      }
      return materialName;
    }

    private static bool IsKnownFamily(string baseName) {
      for (var i = 0; i < FamilyPrefixes.Length; i++) {
        if (baseName.StartsWith(FamilyPrefixes[i], StringComparison.Ordinal)) {
          return true;
        }
      }
      return false;
    }

    #endregion

    #region Nested types

    /// <summary>One tracked renderer + the material-family base name
    /// the tick appends its policy-derived suffix to.</summary>
    public readonly struct RendererEntry {
      public MeshRenderer Renderer { get; }
      public string BaseName { get; }

      public RendererEntry(MeshRenderer renderer, string baseName) {
        Renderer = renderer;
        BaseName = baseName;
      }
    }

    #endregion

  }

}
