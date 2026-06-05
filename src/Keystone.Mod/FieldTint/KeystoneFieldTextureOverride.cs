using System;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Settings;
using Timberborn.AssetSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace Keystone.Mod.FieldTint {

  /// <summary>
  /// Replaces the terrain's wet-field (tilled-soil) albedo with Keystone's
  /// edited texture, by overriding the global <c>_WetFieldTex</c> that the
  /// <em>vanilla</em> terrain shader already samples (bound by
  /// <c>TerrainMaterialMap</c> from <c>TerrainMaterialMapSpec.WetFieldTexture</c>).
  ///
  /// <para><b>Opt-in and non-aggressive.</b> Gated behind a default-off UI
  /// toggle (<c>KeystoneUiSettings.CustomTilledSoilTexture</c>). The global is
  /// written only on a toggle transition — once after load if on, and on each
  /// flip — never per-frame. While off it touches nothing, so other mods are
  /// free to own <c>_WetFieldTex</c>; on turn-off it restores what it replaced
  /// only if it still owns the global, so it won't stomp a mod that rebound it.</para>
  ///
  /// <para>This is the one keeper from the FieldTint shader experiment (the
  /// rest — custom <c>TerrainURP</c> swap, prominence slider, backdrops — is
  /// archived under <c>tmp/research-backups/2026-06-05-fieldtint-shader/</c>).
  /// Because it's a pure global-texture swap on the stock shader, it cannot hit
  /// the shader-variant / magenta issues the swap prototype had, and it leaves
  /// terrain rendering otherwise untouched.</para>
  ///
  /// <para>Only the <em>wet</em> field texture is overridden; dry-soil fields
  /// still use vanilla. A <c>DryField</c> equivalent can be added the same way
  /// if/when an edited version exists.</para>
  /// </summary>
  public sealed class KeystoneFieldTextureOverride : IPostLoadableSingleton, IUpdatableSingleton {

    private const string WetFieldTexturePath = "Textures/KeystoneWetField";
    private static readonly int WetFieldTexProperty = Shader.PropertyToID("_WetFieldTex");

    private readonly IAssetLoader _assetLoader;
    private readonly KeystoneUiSettings _uiSettings;
    private Texture _wetFieldTexture;
    private Texture _capturedTexture;
    private bool _overriding;

    public KeystoneFieldTextureOverride(IAssetLoader assetLoader, KeystoneUiSettings uiSettings) {
      _assetLoader = assetLoader;
      _uiSettings = uiSettings;
    }

    /// <inheritdoc />
    public void PostLoad() {
      try {
        _wetFieldTexture = _assetLoader.Load<Texture2D>(WetFieldTexturePath);
        KeystoneLog.Verbose(
            $"[Keystone] FieldTexture: loaded custom wet-field texture '{WetFieldTexturePath}'.");
      } catch (Exception e) {
        // Not fatal — terrain keeps the vanilla wet-field look.
        KeystoneLog.Verbose(
            $"[Keystone] FieldTexture: '{WetFieldTexturePath}' unavailable, keeping vanilla ({e.Message}).");
      }
    }

    /// <inheritdoc />
    public void UpdateSingleton() {
      // Touch the global ONLY on a toggle transition — once after load if the
      // setting is on, and whenever it flips. Between transitions (and while
      // off) we do nothing, so we never fight other mods over _WetFieldTex.
      // The per-frame body here is just a bool read + compare; no global access
      // unless the state actually changed.
      if (_wetFieldTexture == null) {
        return;
      }
      var wantOn = _uiSettings.CustomTilledSoilTexture.Value;
      if (wantOn == _overriding) {
        return; // no change since last frame
      }
      if (wantOn) {
        // Turning on: remember whatever is currently bound (vanilla, or even
        // another mod's override), then apply ours once. Running here rather
        // than in PostLoad guarantees we're after TerrainMaterialMap's bind.
        _capturedTexture = Shader.GetGlobalTexture(WetFieldTexProperty);
        Shader.SetGlobalTexture(WetFieldTexProperty, _wetFieldTexture);
        _overriding = true;
      } else {
        // Turning off: put back what we replaced — but only if we still own the
        // global. If another mod has rebound it since, leave theirs untouched.
        if (ReferenceEquals(Shader.GetGlobalTexture(WetFieldTexProperty), _wetFieldTexture)) {
          Shader.SetGlobalTexture(WetFieldTexProperty, _capturedTexture);
        }
        _overriding = false;
      }
    }
  }
}
