using System;
using Timberborn.AssetSystem;
using Timberborn.CoreUI;
using Timberborn.SingletonSystem;
using Timberborn.TooltipSystem;
using Timberborn.UILayoutSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace Keystone.Mod.Visualization {

  /// <summary>
  /// Top-right toggle button that enables/disables the biome overlay.
  /// Follows the vanilla <c>WaterOpacityTogglePanel</c> pattern:
  /// load a <c>Common/SquareToggle</c>, register with
  /// <see cref="UILayout.AddTopRightButton"/>, flip a bool on click.
  ///
  /// <para>Icon is our own sprite, applied to the toggle's
  /// <c>unity-toggle__checkmark</c> child via inline
  /// <c>style.backgroundImage</c> (same approach Beaver Chronicles
  /// uses for its scroll button) — we can't register a vanilla-style
  /// <c>square-toggle--*</c> CSS class without authoring our own
  /// stylesheet.</para>
  /// </summary>
  public sealed class BiomeOverlayToggle : ILoadableSingleton {

    private const string IconPath = "Sprites/TopRight/BiomeOverlayToggle";
    private const string CheckmarkClass = "unity-toggle__checkmark";

    private static readonly string ShowLocKey = "Keystone.BiomeOverlay.Show";
    private static readonly string HideLocKey = "Keystone.BiomeOverlay.Hide";

    private readonly VisualElementLoader _visualElementLoader;
    private readonly UILayout _uiLayout;
    private readonly ITooltipRegistrar _tooltipRegistrar;
    private readonly EventBus _eventBus;
    private readonly IAssetLoader _assetLoader;

    private VisualElement _root;
    private bool _enabled;

    /// <summary>Whether the biome overlay is currently active. Read
    /// by <see cref="BiomeOverlayRenderer"/> each frame.</summary>
    public bool Enabled => _enabled;

    public BiomeOverlayToggle(
        VisualElementLoader visualElementLoader,
        UILayout uiLayout,
        ITooltipRegistrar tooltipRegistrar,
        EventBus eventBus,
        IAssetLoader assetLoader) {
      _visualElementLoader = visualElementLoader;
      _uiLayout = uiLayout;
      _tooltipRegistrar = tooltipRegistrar;
      _eventBus = eventBus;
      _assetLoader = assetLoader;
    }

    /// <inheritdoc />
    public void Load() {
      _root = _visualElementLoader.LoadVisualElement("Common/SquareToggle");
      _tooltipRegistrar.RegisterLocalizable(_root, () => _enabled ? HideLocKey : ShowLocKey);
      var toggle = _root.Q<Toggle>("Toggle");
      var checkmark = toggle.Q(null, CheckmarkClass);
      var texture = _assetLoader.Load<Texture2D>(IconPath);
      checkmark.style.backgroundImage = new StyleBackground(texture);
      toggle.RegisterValueChangedCallback(evt => _enabled = evt.newValue);
      _eventBus.Register(this);
    }

    [OnEvent]
    public void OnShowPrimaryUI(ShowPrimaryUIEvent showPrimaryUIEvent) {
      _uiLayout.AddTopRightButton(_root, 10);
    }

  }

}
