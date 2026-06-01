using Keystone.Core.Tiles;
using Timberborn.Coordinates;
using UnityEngine;

namespace Keystone.Mod.Fauna {

  /// <summary>
  /// Off-screen test used by the fauna spawn/cull pipeline to hide
  /// instantiation pops and despawn pops from the player. A tile is
  /// "in frustum" when its world-space centre projects into the active
  /// camera's viewport with a small margin away from the edge — the
  /// margin is what prevents pops right at the screen edge from being
  /// caught in the corner of the eye.
  ///
  /// <para><b>Camera-not-available defaults to in-frustum.</b> If
  /// <see cref="Camera.main"/> is null during the brief windows where
  /// it can be (scene transitions, very early load), <see cref="IsInFrustum"/>
  /// returns true so the caller defers the spawn / cull rather than
  /// performing it blind. Worst case the work happens a frame later
  /// once the camera is back; never an unhidden pop.</para>
  /// </summary>
  public static class FaunaFrustumFilter {

    /// <summary>Viewport-space margin away from each edge of the
    /// screen. A tile within this fraction of any edge is treated as
    /// in-frustum even though it would technically pass a raw
    /// <c>[0,1]</c> viewport test. Tuned for "the eye doesn't catch
    /// pops near the screen border at typical play zoom"; raise if
    /// pops at the edge are still noticeable, lower to free more
    /// spawnable area.</summary>
    public const float ViewportMargin = 0.05f;

    /// <summary>Cached camera reference. <see cref="Camera.main"/>
    /// internally runs <c>FindObjectsWithTag("MainCamera")</c> on every
    /// access — cheap-ish but noticeable when called per qualifying
    /// chunk per drain visit. We resolve once and refresh whenever the
    /// reference goes null (scene unload, scene transition); the
    /// reference itself, once obtained, stays valid for the lifetime of
    /// the camera GameObject.</summary>
    private static Camera? _cachedCamera;

    /// <summary>True when the given tile centre (at the given world Z
    /// level) is inside the active camera's viewport with margin.
    /// Returns true defensively when no main camera is available — the
    /// caller should treat that as "defer," not "go ahead."</summary>
    public static bool IsInFrustum(TileCoord tile, int z) {
      var camera = _cachedCamera;
      // Unity overloads `==` on UnityEngine.Object to consider a
      // destroyed reference equal to null even when the C# reference
      // isn't actually null. Re-resolve on either kind of null.
      if (camera == null) {
        camera = Camera.main;
        _cachedCamera = camera;
        if (camera == null) return true;
      }
      var world = CoordinateSystem.GridToWorldCentered(
          new Vector3Int(tile.X, tile.Y, z));
      var vp = camera.WorldToViewportPoint(world);
      if (vp.z <= 0f) return false;
      return vp.x >= ViewportMargin && vp.x <= 1f - ViewportMargin
          && vp.y >= ViewportMargin && vp.y <= 1f - ViewportMargin;
    }

  }

}
