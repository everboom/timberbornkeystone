# Keystone.Mod.FieldTint

Overrides the terrain's **wet-field (tilled-soil) albedo** with a Keystone-edited
texture, so tiles marked for planting read with a lighter tilled look instead of
the dark vanilla brown.

## How it works

`KeystoneFieldTextureOverride` loads `Textures/KeystoneWetField` and binds it to
the global `_WetFieldTex` that the **stock** terrain shader samples (the same
global `TerrainMaterialMap` sets from `TerrainMaterialMapSpec.WetFieldTexture`).
Pure global-texture swap — no custom shader, no material swap — so it can't
affect terrain shader variants or rendering beyond the field albedo.

Gated behind a **default-off** UI toggle (`KeystoneUiSettings.CustomTilledSoilTexture`,
live-switchable). It writes the global **only on a toggle transition** — once
after load if on, and whenever it flips — never per-frame. While off it touches
nothing, so other mods are free to own `_WetFieldTex`; on turn-off it restores
the previous texture only if Keystone still owns the global (won't stomp a mod
that rebound it).

The edited texture lives at
`unity-assets/Keystone/AssetBundles/Resources/Textures/KeystoneWetField.png`
(import: sRGB on, Wrap = Repeat). Only the *wet* field is overridden; dry-soil
fields still use vanilla. Add a `DryField` override the same way if an edited
version is made.

## History

This is the keeper from a larger shader experiment (custom `TerrainURP` swap +
a live prominence slider) that's archived under
`tmp/research-backups/2026-06-05-fieldtint-shader/`. That route worked but was
fragile (shader-variant stripping turned cliffs / older-save terrain magenta);
the texture override delivers the wanted look without owning the shader. See
`docs/timberborn-api.md` → "Terrain ground recoloring" for the underlying system
(and the contamination / desert / field channels) if revisiting this.
