using System.Collections.Generic;
using Keystone.Mod.Diagnostics;
using UnityEngine;

namespace Keystone.Mod.Assets {

  /// <summary>
  /// Loads and caches Keystone-mod custom assets from the deployed
  /// AssetBundle produced by the Modding SDK Mod Builder.
  ///
  /// <para>The Mod Builder produces one bundle per platform named
  /// <c>{modname}_{platform}</c> (e.g. <c>keystone_win</c>) packing
  /// every non-script/scene/meta file under <c>AssetBundles/</c>.
  /// Timberborn's <c>ModAssetBundleLoader</c> loads those bundles at
  /// startup; this service finds ours via Unity's app-domain-wide
  /// <see cref="AssetBundle.GetAllLoadedAssetBundles"/> and resolves
  /// named assets on demand.</para>
  ///
  /// <para><b>Why not inject <c>ModAssetBundleLoader</c>:</b> it lives
  /// in a Bindito scope that isn't reachable from the <c>"Game"</c>
  /// configurator context, so constructor-injecting it produces a
  /// "missing dependency" exception at container creation. Unity's
  /// static enumeration sidesteps the DI question entirely; the
  /// bundle is already loaded by the time any in-game tool fires.</para>
  ///
  /// <para><b>Lazy:</b> first property access drives the lookup, so
  /// load ordering relative to the modding-assets subsystem doesn't
  /// matter. Failed lookups log diagnostics (the names of every
  /// bundle currently loaded, and the names of assets in our bundle
  /// when we find it) and return <c>null</c>; callers must null-check
  /// and skip cleanly.</para>
  /// </summary>
  public sealed class KeystoneAssetService {

    #region Bundle / asset names

    /// <summary>The platform-suffixed bundle name produced by the Mod
    /// Builder for the Windows target. Unity lowercases bundle names
    /// at build time regardless of the mod's display name.</summary>
    private const string KeystoneBundleName = "keystone_win";

    /// <summary>The Ground Fog prefab's asset name as Unity stores it
    /// in the bundle (filename without extension).</summary>
    private const string GroundFogAssetName = "Ground Fog";

    #endregion

    #region Fields + ctor

    private GameObject? _groundFogPrefab;
    private bool _groundFogResolved;

    public KeystoneAssetService() {
    }

    #endregion

    #region Public API

    /// <summary>The Ground Fog particle-system prefab from
    /// <c>AssetBundles/Resources/GPU Fog Particles/Prefabs/</c>.
    /// <c>null</c> if the bundle or asset can't be found -- a single
    /// diagnostic is logged on the first failed lookup, then cached.
    /// Callers must null-check and skip cleanly.</summary>
    public GameObject? GroundFogPrefab {
      get {
        if (!_groundFogResolved) {
          _groundFogPrefab = ResolveAsset<GameObject>(GroundFogAssetName);
          _groundFogResolved = true;
        }
        return _groundFogPrefab;
      }
    }

    #endregion

    #region Resolution

    private static T? ResolveAsset<T>(string assetName) where T : Object {
      AssetBundle? bundle = null;
      var loadedNames = new List<string>();
      foreach (var b in AssetBundle.GetAllLoadedAssetBundles()) {
        loadedNames.Add(b.name);
        if (b.name == KeystoneBundleName) {
          bundle = b;
        }
      }
      if (bundle == null) {
        KeystoneLog.Error(
            $"[Keystone] KeystoneAssetService: bundle '{KeystoneBundleName}' "
            + "not in AssetBundle.GetAllLoadedAssetBundles(). Loaded bundles: "
            + $"[{string.Join(", ", loadedNames)}]. Check that Mod Builder "
            + $"produced AssetBundles/{KeystoneBundleName} in the deployed "
            + "mod folder and that the game's modding loader picked it up.");
        return null;
      }
      var asset = bundle.LoadAsset<T>(assetName);
      if (asset == null) {
        KeystoneLog.Error(
            $"[Keystone] KeystoneAssetService: asset '{assetName}' (type "
            + $"{typeof(T).Name}) not found in bundle '{KeystoneBundleName}'. "
            + "Bundle contains: ["
            + $"{string.Join(", ", bundle.GetAllAssetNames())}].");
      }
      return asset;
    }

    #endregion

  }

}
