# Keystone.Mod.Materials

Material-collection registration for Keystone-original materials --
the runtime side of "use Keystone-authored textures on Keystone-
authored meshes."

## How it fits

`.timbermesh` files (under `Data/NaturalResources/...`) encode mesh
geometry plus a material **name string** per surface. At runtime
`IMaterialRepository.GetMaterial(name)` walks every registered
`MaterialCollection` looking for a `.mat` asset whose name matches.

For a Keystone mesh whose material name is `KeystoneRock` to resolve,
two things must be true:
1. A `.mat` asset named `KeystoneRock` exists in the asset bundle.
2. A `MaterialCollection` whose `Materials` list includes that
   `.mat` is registered via an `IMaterialCollectionIdsProvider`.

This folder owns the second half.

## Types

- **`KeystoneMaterialProvider`** -- `IMaterialCollectionIdsProvider`
  multi-bind. Returns `"Keystone"`, which matches the `CollectionId`
  in `Data/Materials/MaterialCollection.Keystone.blueprint.json`.

## Adding a new Keystone-original material

1. Create the `.mat` (URP/Lit, assign texture) under
   `unity-assets/Keystone/AssetBundles/Resources/Materials/`.
2. Add its bundle path (without `.mat` extension) to the `Materials`
   array in `Data/Materials/MaterialCollection.Keystone.blueprint.json`.
3. In Blender, name the mesh's material exactly the `.mat` filename.
4. Rebuild the asset bundle via the Modding SDK Mod Builder.
