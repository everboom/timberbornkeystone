# Keystone.Mod.Assets

Services that load Keystone's custom Unity assets out of the deployed
mod AssetBundle.

## Layout

The Modding SDK Mod Builder packs everything under `unity-assets/Keystone/AssetBundles/`
into a single per-platform bundle (`keystone_win` on Windows,
`keystone_mac` on Mac), and Timberborn's `ModAssetBundleLoader` loads
those at game start. Asset-level bundle tags in Unity `.meta` files
are ignored by this SDK -- everything in `AssetBundles/` ships in
the single mod-named bundle.

## Types

- **`KeystoneAssetService`** -- singleton, lazy. Holds references to
  the platform bundle (looked up out of
  `ModAssetBundleLoader.LoadedAssetBundles` on first asset access)
  and exposes typed accessors for individual assets. Logs loudly and
  returns `null` if a bundle or asset can't be found.

## Adding an asset

1. Drop the file under `unity-assets/Keystone/AssetBundles/Resources/<category>/`
   in the symlinked source-of-truth tree.
2. Open the Modding SDK Unity project and verify the asset imports
   without shader/script errors. Materials must point at URP-compatible
   shaders (see Timberborn's URP 17 baseline).
3. Run **Timberborn → Show Mod Builder** with **Build Windows Asset
   Bundle** ticked. Output lands at
   `%USERPROFILE%\Documents\Timberborn\Mods\Keystone\AssetBundles\keystone_win`.
4. Add a typed accessor on `KeystoneAssetService` and an asset-name
   constant. Lookup is by asset name (filename without extension).
