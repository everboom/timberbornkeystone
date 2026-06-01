using System;
using System.Collections.Generic;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Recipes;
using Timberborn.BlockSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Query helper for enumerating Keystone Class B entities inside a
  /// 3D tile rectangle. Used by <see cref="BuildingDeconstructionClassBPatch"/>
  /// to inject Class B minis into the building bulk-demolish tool's
  /// preview / commit lists.
  ///
  /// <para><b>Why a singleton + static accessor.</b> Harmony patches
  /// can't take constructor-injected dependencies, so this service
  /// resolves through Bindito normally and then publishes itself to
  /// a static field on <see cref="Load"/>. The patches read
  /// <see cref="Instance"/> at call time and gracefully no-op if
  /// it's null (the brief startup window before Bindito's Game
  /// scope has spun up).</para>
  ///
  /// <para><b>Why filter on KeystoneVariant.Class == "B" specifically.</b>
  /// The bulk-demolish injection is meant to remove only inert
  /// flourishes (Class B), not Class C (player-managed weeds, already
  /// reachable via the vanilla resource demolish tool) or Class D
  /// (vanilla flora, also resource-demolish path). Class A is non-
  /// <c>BlockObject</c> so it never appears in <c>IBlockService</c>.</para>
  /// </summary>
  public sealed class ClassBAreaQuery : ILoadableSingleton {

    /// <summary>Static handle for Harmony patches. Null until
    /// <see cref="Load"/> runs (i.e. while Bindito is still wiring
    /// the Game scope). Patches must null-check.</summary>
    public static ClassBAreaQuery? Instance { get; private set; }

    private readonly IBlockService _blockService;
    private readonly PerfTracker _perf;

    public ClassBAreaQuery(IBlockService blockService, PerfTracker perf) {
      _blockService = blockService;
      _perf = perf;
    }

    /// <inheritdoc />
    public void Load() {
      Instance = this;
    }

    /// <summary>Widen <paramref name="blockObjects"/> in place with the
    /// Class B entities found in the <paramref name="start"/> /
    /// <paramref name="end"/> rect. Called from
    /// <c>BuildingDeconstructionClassBPatch</c>'s Harmony prefixes; the
    /// logic lives here (rather than in the static patch) so the
    /// volumetric rect scan + list materialisation can be timed through
    /// the DI-injected <see cref="PerfTracker"/> — the patch itself
    /// can't take injected dependencies. Runs every frame during a
    /// demolish-drag (off the sim tick), so without this scope its
    /// per-frame allocation is invisible to the perf window. The scope
    /// covers the full rect scan, which runs (and costs O(rect volume)
    /// block lookups) on every call regardless of whether any Class B
    /// is found — that scan is the cost we want visible.</summary>
    public void InjectInto(
        ref IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end) {
      using var _ = _perf.Track("ClassBDemolish.Inject");
      List<BlockObject>? classBs = null;
      foreach (var bo in EnumerateInRect(start, end)) {
        (classBs ??= new List<BlockObject>()).Add(bo);
      }
      if (classBs == null) return;
      // Materialise to List so the underlying enumerable isn't iterated
      // twice (the original might be a LINQ chain over a shared buffer
      // that the picker resets after we return).
      var combined = new List<BlockObject>();
      combined.AddRange(blockObjects);
      combined.AddRange(classBs);
      blockObjects = combined;
    }

    /// <summary>Enumerate every Keystone Class B entity whose tile
    /// position lies inside the inclusive 3D rect formed by
    /// <paramref name="start"/> and <paramref name="end"/>. Corner
    /// order doesn't matter; the rect is normalised here.
    /// <para>Cost: one <see cref="IBlockService.GetObjectsAt"/> call
    /// per tile in the rect (typically dozens; cheap dict lookups).
    /// Class B entities are 1x1x1 so a single entity won't appear in
    /// the output more than once even though we visit every tile in
    /// the rect; a hash set inside still dedupes defensively.</para></summary>
    public IEnumerable<BlockObject> EnumerateInRect(Vector3Int start, Vector3Int end) {
      var minX = Math.Min(start.x, end.x);
      var maxX = Math.Max(start.x, end.x);
      var minY = Math.Min(start.y, end.y);
      var maxY = Math.Max(start.y, end.y);
      var minZ = Math.Min(start.z, end.z);
      var maxZ = Math.Max(start.z, end.z);

      var seen = new HashSet<BlockObject>();
      for (var x = minX; x <= maxX; x++) {
        for (var y = minY; y <= maxY; y++) {
          for (var z = minZ; z <= maxZ; z++) {
            foreach (var bo in _blockService.GetObjectsAt(new Vector3Int(x, y, z))) {
              if (bo == null) continue;
              if (!seen.Add(bo)) continue;
              if (!bo.HasComponent<KeystoneVariant>()) continue;
              if (bo.GetComponent<KeystoneVariant>().Class != "B") continue;
              yield return bo;
            }
          }
        }
      }
    }

  }

}
