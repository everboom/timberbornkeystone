using Bindito.Core;

namespace Keystone.Mod.Settings {

  /// <summary>
  /// Bindito configurator for the Keystone mod-settings owners. Lives in
  /// both <c>MainMenu</c> and <c>Game</c> scopes so the settings panel
  /// finds the owners whether the player opens it from the main menu mod
  /// list or from the in-game options &gt; mods entry.
  ///
  /// <para>Deliberately separate from <see cref="KeystoneConfigurator"/>
  /// which is <c>[Context("Game")]</c> only. Widening that to also bind in
  /// the main-menu scope would drag every Keystone singleton into the main
  /// menu container, where most of them (Bindito-injected over game-only
  /// services like terrain / regions / clusters) would fail to construct.
  /// Keeping the settings owners in their own configurator scopes the
  /// MainMenu-side binding to just the owners.</para>
  ///
  /// <para>One owner per panel section. Section order in the UI is driven
  /// by each owner's <c>Order</c> property, not by binding order here.</para>
  /// </summary>
  [Context("MainMenu")]
  [Context("Game")]
  public class KeystoneSettingsConfigurator : Configurator {

    /// <inheritdoc />
    protected override void Configure() {
      Bind<KeystoneFloraSettings>().AsSingleton();
      Bind<KeystoneFaunaSettings>().AsSingleton();
      Bind<KeystoneEffectsSettings>().AsSingleton();
      Bind<KeystonePerformanceSettings>().AsSingleton();
      Bind<KeystoneUiSettings>().AsSingleton();
    }

  }

}
