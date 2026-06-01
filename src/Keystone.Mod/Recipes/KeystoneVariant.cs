using Timberborn.BaseComponentSystem;
using Timberborn.Persistence;
using Timberborn.WorldPersistence;

namespace Keystone.Mod.Recipes {

  /// <summary>
  /// Per-entity persistent component carrying the content-class
  /// designation set by the handler that spawned the entity.
  /// Attached to every Keystone-spawn-eligible blueprint via
  /// <see cref="KeystoneVariantSpec"/>. Saved across reload via
  /// <see cref="IPersistentEntity"/> so a Class B entity stays
  /// Class B (and a Class C entity stays Class C) after a save and
  /// reload.
  ///
  /// <para><b>Default state.</b> A freshly-instantiated variant has
  /// <see cref="Class"/> = <c>""</c>. Handlers call
  /// <see cref="SetClass"/> immediately after
  /// <c>BlockObjectFactory.CreateFinished</c> to stamp it. Entities
  /// with an empty class are treated by the Harmony selection
  /// patches as "not Keystone-managed" -- vanilla selection /
  /// demolish behavior applies. This is the right fallback for
  /// blueprints carrying the spec but instantiated through some
  /// non-Keystone path (e.g. a third-party tool).</para>
  ///
  /// <para><b>Class values.</b> Currently <c>"B"</c> drives Harmony
  /// suppression (inert flourish). <c>"A"</c> and <c>"C"</c> mean
  /// "Keystone-managed but selectable" -- present for diagnostics
  /// and future class-conditional behavior, not currently load-
  /// bearing for the patches. The string values match the recipe
  /// <c>Class</c> field for symmetry.</para>
  /// </summary>
  public sealed class KeystoneVariant : BaseComponent, IPersistentEntity {

    private static readonly ComponentKey ComponentKey = new("KeystoneVariant");
    private static readonly PropertyKey<string> ClassKey = new("Class");

    /// <summary>The content class this entity was spawned as.
    /// <c>""</c> when unset (no Keystone handler stamped it).</summary>
    public string Class { get; private set; } = "";

    /// <summary>Stamp the class designation. Called by handlers
    /// (and the dev placement tool) immediately after the entity is
    /// instantiated. Idempotent: setting the same value is a no-op.</summary>
    public void SetClass(string classId) {
      var normalised = classId ?? "";
      if (Class == normalised) return;
      Class = normalised;
    }

    /// <inheritdoc />
    public void Save(IEntitySaver entitySaver) {
      // Skip writing the empty default to keep saves lean and to let
      // the Load fallback (no-component-present) handle older saves.
      if (string.IsNullOrEmpty(Class)) return;
      entitySaver.GetComponent(ComponentKey).Set(ClassKey, Class);
    }

    /// <inheritdoc />
    public void Load(IEntityLoader entityLoader) {
      if (!entityLoader.TryGetComponent(ComponentKey, out var loader)) return;
      if (loader.Has(ClassKey)) {
        Class = loader.Get(ClassKey) ?? "";
      }
    }

  }

}
