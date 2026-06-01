using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.GameDistricts;
using Timberborn.PrefabOptimization;
using Timberborn.SingletonSystem;
using Timberborn.TemplateCollectionSystem;
using UnityEngine;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Phase 1, prototype #2: prove out the "passive object" pattern --
  /// a Unity <see cref="GameObject"/> with just a renderer at a
  /// Timberborn tile coord, carrying no behaviour, no entity-system
  /// participation, no save/load, no UI tooltips.
  ///
  /// <para><b>Why.</b> Cross-faction crops can't be made to render as
  /// real natural-resource entities without dragging in a cascade of
  /// good/need/recipe dependencies. The cleaner shape for ecology
  /// visuals is a bare-mesh decorative object that the player can't
  /// interact with -- which is also the original Capability 1 we
  /// scoped for Phase 1 (decorative flourishes).</para>
  ///
  /// <para><b>What this prototype proves.</b>
  /// <list type="number">
  ///   <item>Coord translation -- Timberborn's <c>Vector3Int(x, y, z)</c>
  ///         (z = height) → Unity's <c>Vector3</c> world position via
  ///         <see cref="CoordinateSystem.GridToWorldCentered(Vector3Int)"/>.</item>
  ///   <item>GameObject construction -- bare primitive geometry suffices
  ///         to verify the position+rendering loop end-to-end without
  ///         tangling with mesh sourcing.</item>
  ///   <item>Lifecycle -- the GameObject persists across the session.
  ///         Save/load and despawn handling are deliberately out of
  ///         scope for this first probe; we own those once we know
  ///         the rendering works.</item>
  /// </list></para>
  ///
  /// <para><b>Sourcing real meshes</b> (Mangrove, Maple, etc. for
  /// genuine flora visuals) is a follow-up. Once primitives render
  /// correctly we'll graduate to extracting meshes from the
  /// cross-faction prefabs we already load via
  /// <see cref="CrossFactionMaterialProvider"/>.</para>
  /// </summary>
  public sealed class PassiveObjectProbe : IPostLoadableSingleton {

    #region Constants

    /// <summary>X-distance from the district center for the demo single spawn.</summary>
    private const int SpawnDistance = 9;

    /// <summary>
    /// Blueprint to source a mesh+material from. Currently an
    /// Ironteeth-only crop -- if this renders on a Folktails game, the
    /// cross-faction crop visibility problem is fully solved through
    /// the passive-object path, no further planter/good plumbing needed.
    /// </summary>
    private const string MeshDonorTemplate = "Canola";

    #endregion

    #region Fields

    private readonly DistrictCenterRegistry _districts;
    private readonly IPrefabOptimizationChain _prefabChain;
    private readonly TemplateCollectionService _templates;

    #endregion

    #region Construction

    public PassiveObjectProbe(
        DistrictCenterRegistry districts,
        IPrefabOptimizationChain prefabChain,
        TemplateCollectionService templates) {
      _districts = districts;
      _prefabChain = prefabChain;
      _templates = templates;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      try {
        SpawnPassiveMesh();
      } catch (Exception ex) {
        KeystoneLog.Warn($"[Keystone] PassiveObjectProbe threw: {ex}");
      }
    }

    #endregion

    #region Probe

    private void SpawnPassiveMesh() {
      var center = PickDistrictCenter();
      if (center == null) {
        KeystoneLog.Verbose("[Keystone] PassiveObjectProbe: no district center; skipping.");
        return;
      }

      if (!center.TryGetComponent<BlockObject>(out var dcBlock)) {
        KeystoneLog.Verbose("[Keystone] PassiveObjectProbe: district center has no BlockObject; skipping.");
        return;
      }

      // ISpecService.GetBlueprint expects a path-style id (e.g.
      // "FarmHouse.Folktails"); bare template names like "Maple" don't
      // resolve there. TemplateCollectionService.AllTemplates is the
      // post-load union we can scan by Blueprint.Name directly.
      var donor = _templates.AllTemplates.FirstOrDefault(b => b.Name == MeshDonorTemplate);
      if (donor == null) {
        KeystoneLog.Verbose(
            $"[Keystone] PassiveObjectProbe: no blueprint named '{MeshDonorTemplate}' in AllTemplates.");
        return;
      }

      // IPrefabOptimizationChain.Process returns the optimized cached
      // prefab GameObject for a blueprint. We don't own it; clone via
      // GameObject.Instantiate so we don't mutate the cache.
      var optimizedPrefab = _prefabChain.Process(donor);
      if (optimizedPrefab == null) {
        KeystoneLog.Verbose(
            $"[Keystone] PassiveObjectProbe: prefab chain returned null for '{MeshDonorTemplate}'.");
        return;
      }

      DumpHierarchy(optimizedPrefab, MeshDonorTemplate);

      var dc = dcBlock.Coordinates;
      var tileCoord = new Vector3Int(dc.x + SpawnDistance, dc.y, dc.z);
      // Natural-resource prefabs use BlockObject anchor convention --
      // origin at the tile's south-west corner, not the center. Use
      // GridToWorld (un-centered) so the visual lands on the named tile.
      // Unity primitives need GridToWorldCentered because their pivot
      // is at the geometry center; the conventions differ.
      var worldPos = CoordinateSystem.GridToWorld(tileCoord);

      var instance = UnityEngine.Object.Instantiate(optimizedPrefab, worldPos, Quaternion.identity);
      instance.name = $"Keystone.PassiveObjectProbe.{MeshDonorTemplate}";

      KeystoneLog.Verbose(
          $"[Keystone] PassiveObjectProbe: instantiated '{MeshDonorTemplate}' prefab at tile {tileCoord} " +
          $"-> world {worldPos}. Look for it {SpawnDistance} tiles east of the DC ({dc}).");

      DumpParticleBearers();
    }

    /// <summary>
    /// One-shot inventory: iterate every loaded blueprint, process its
    /// prefab via the optimization chain, and log any whose hierarchy
    /// contains visual-effect components. The earlier
    /// <see cref="ParticleSystem"/>-only scan came back empty, so we
    /// also look at <c>VisualEffect</c> (VFX Graph),
    /// <see cref="TrailRenderer"/>, <see cref="LineRenderer"/>, plus
    /// any component-type-name containing "Particle", "Effect",
    /// "Smoke", "Mist", "Fog", or "Emitter" — Timberborn likely uses
    /// custom names for stylized atmospherics.
    /// </summary>
    private void DumpParticleBearers() {
      var sw = Stopwatch.StartNew();
      var buffer = new StringBuilder();
      buffer.AppendLine("[Keystone] PassiveObjectProbe: visual-effect inventory:");
      var hits = 0;

      foreach (var bp in _templates.AllTemplates) {
        GameObject prefab;
        try {
          prefab = _prefabChain.Process(bp);
        } catch {
          continue;
        }
        if (prefab == null) continue;

        var matches = new List<(string Type, string Path)>();
        foreach (var c in prefab.GetComponentsInChildren<Component>(includeInactive: true)) {
          if (c == null) continue;
          var typeName = c.GetType().Name;
          if (IsEffectish(typeName)) {
            matches.Add((typeName, GetPath(c.transform, prefab.transform)));
          }
        }
        if (matches.Count == 0) continue;
        hits++;

        buffer.Append("  ").Append(bp.Name).AppendLine();
        foreach (var (type, path) in matches) {
          buffer.Append("    [").Append(type).Append("] ").AppendLine(path);
        }
      }

      sw.Stop();
      buffer.Append("  -- ").Append(hits).Append(" effect-bearing blueprint(s) found in ")
            .Append(sw.Elapsed.TotalMilliseconds.ToString("F0")).AppendLine(" ms.");
      KeystoneLog.Verbose(buffer.ToString());
    }

    /// <summary>
    /// Heuristic: does this component look like it produces a visual
    /// effect we'd want to clone? Catches both Unity built-ins and
    /// any Timberborn-named effect components.
    /// </summary>
    private static bool IsEffectish(string typeName) {
      return typeName switch {
          nameof(ParticleSystem) => true,
          "VisualEffect" => true,
          nameof(TrailRenderer) => true,
          nameof(LineRenderer) => true,
          _ => typeName.Contains("Particle") || typeName.Contains("Effect")
                  || typeName.Contains("Smoke") || typeName.Contains("Mist")
                  || typeName.Contains("Fog") || typeName.Contains("Emitter")
                  || typeName.Contains("Steam"),
      };
    }

    /// <summary>Path from <paramref name="root"/> down to <paramref name="leaf"/>, as "/"-separated names.</summary>
    private static string GetPath(Transform leaf, Transform root) {
      if (leaf == root) return leaf.name;
      var sb = new StringBuilder(leaf.name);
      var t = leaf.parent;
      while (t != null && t != root) {
        sb.Insert(0, t.name + "/");
        t = t.parent;
      }
      return sb.ToString();
    }

    /// <summary>
    /// One-shot dump of an optimized prefab's structure — names of every
    /// GameObject in the hierarchy and the components on each. Useful to
    /// understand where the visible <see cref="MeshFilter"/> and
    /// <see cref="MeshRenderer"/> live before we strip the rest.
    /// </summary>
    private static void DumpHierarchy(GameObject root, string label) {
      var buffer = new StringBuilder();
      buffer.Append("[Keystone] PassiveObjectProbe: hierarchy of '")
            .Append(label).AppendLine("' optimized prefab:");
      AppendNode(buffer, root.transform, 0);
      KeystoneLog.Verbose(buffer.ToString());
    }

    private static void AppendNode(StringBuilder buffer, Transform node, int depth) {
      for (var i = 0; i < depth; i++) buffer.Append("  ");
      buffer.Append(node.name).Append("  [");
      var components = node.GetComponents<Component>()
          .Where(c => c != null)
          .Select(c => c.GetType().Name);
      buffer.Append(string.Join(", ", components));
      buffer.AppendLine("]");
      for (var i = 0; i < node.childCount; i++) {
        AppendNode(buffer, node.GetChild(i), depth + 1);
      }
    }

    private DistrictCenter? PickDistrictCenter() {
      var finished = _districts.FinishedDistrictCenters;
      if (finished.Count > 0) return finished[0];
      var all = _districts.AllDistrictCenters;
      return all.Count > 0 ? all[0] : null;
    }

    #endregion

  }

}
