using System.Collections.Generic;
using Keystone.Mod.Decoration;
using Keystone.Mod.Diagnostics;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.NaturalResourcesMoisture;
using Timberborn.Persistence;
using Timberborn.WorldPersistence;

namespace Keystone.Mod.Overgrowth {

  /// <summary>
  /// Per-instance augmentation component attached (via
  /// <c>AddDecorator&lt;TreeComponentSpec, KeystoneOvergrowth&gt;</c>) to
  /// every tree in the game — vanilla and any faction's, since they all
  /// carry <c>TreeComponentSpec</c>. Water-based trees (mangrove and any
  /// future aquatic species) self-filter out at runtime (see
  /// <see cref="CanOvergrow"/>). Holds an additive flourish decoration
  /// layered around the host entity; the host (the tree) is never
  /// modified, so all its native behaviour (growth, cutting,
  /// reproduction) is untouched.
  ///
  /// <para><b>Persistence.</b> The <i>logical</i> state (is-decorated +
  /// which composition) persists via <see cref="IPersistentEntity"/>; the
  /// decoration GameObject itself is a non-persisted
  /// <see cref="KeystoneDecorationRegistry"/> object, re-spawned from the
  /// saved <see cref="_decorationId"/> in <see cref="InitializeEntity"/>
  /// on load. So an overgrown tree comes back overgrown with the same
  /// composition after reload. (Alive/dying is moisture-derived by the
  /// controller and not persisted; the terminal dead state + age counters
  /// come in the kill/replace slice.)</para>
  ///
  /// <para><b>Trigger.</b> Dev-triggered for now via
  /// <see cref="OvergrowthTestTool"/> (<see cref="Apply()"/> /
  /// <see cref="Clear"/>); the biome-driven <see cref="OvergrowthHandler"/>
  /// calls <see cref="Apply(string)"/> with the recipe's composition.</para>
  ///
  /// <para><b>Rendering note.</b> The host tree renders from the custom
  /// flora matrix (not its Unity Transform), so the overlay can't read
  /// the tree's randomised scale/rotation off the Transform. The
  /// decoration is a plain GameObject that DOES honour its own transform,
  /// so it renders fine; matching it to the tree's random size is a later
  /// refinement (see issue #33).</para>
  /// </summary>
  public sealed class KeystoneOvergrowth
      : BaseComponent, IInitializableEntity, IDeletableEntity, IPersistentEntity {

    #region Constants

    /// <summary>Placeholder donor for the overgrowth overlay — an
    /// existing Keystone flourish composition. It already arranges its
    /// plants around the tile via <c>TransformSpec</c> (positioning
    /// baked, no hand offsets) and carries the standard
    /// <c>#Models/Mature/#Alive|#Dying|#Dead</c> hierarchy the lifecycle
    /// controller manages. Swapped for a purpose-built overgrowth
    /// composition (+ ivy) later.</summary>
    private const string PlaceholderDonor = "KeystoneGrasslandMini1";

    private static readonly ComponentKey ComponentKey = new("KeystoneOvergrowth");
    private static readonly PropertyKey<string> DecorationIdKey = new("DecorationId");

    #endregion

    #region Injected services

    private readonly KeystoneDecorationRegistry _decorations;

    #endregion

    #region Per-instance state

    /// <summary>Logical "is this tree overgrown" flag — the persisted,
    /// authoritative state. The live decoration GameObjects in
    /// <see cref="_spawned"/> are rebuilt from it on load.</summary>
    private bool _decorated;

    /// <summary>Which composition this tree is overgrown with. Persisted
    /// so the same one returns after reload.</summary>
    private string _decorationId = PlaceholderDonor;

    private readonly List<KeystoneDecoration> _spawned = new();

    #endregion

    #region Construction

    public KeystoneOvergrowth(KeystoneDecorationRegistry decorations) {
      _decorations = decorations;
    }

    #endregion

    #region Public API

    /// <summary>True when this tree is logically overgrown (persisted).
    /// Drives the dev-tool toggle and re-hydration on load.</summary>
    public bool IsOvergrown => _decorated;

    /// <summary>False for water-based trees (mangrove and any future
    /// aquatic species): overgrowth is a land-recovery signal, so trees
    /// standing in water are skipped. Detected via
    /// <c>FloodableNaturalResourceSpec.MinWaterHeight</c> — the same
    /// aquatic gate <c>KeystoneGrowthBonus</c> uses.</summary>
    public bool CanOvergrow {
      get {
        var floodable = GetComponent<FloodableNaturalResourceSpec>();
        return floodable == null || floodable.MinWaterHeight <= 0;
      }
    }

    /// <summary>Drape the host in a flourish decoration of the given
    /// <paramref name="composition"/> blueprint. No-op if already
    /// overgrown, the host has no <see cref="BlockObject"/>, or the host
    /// is a water-based tree (<see cref="CanOvergrow"/>). An empty
    /// composition falls back to the placeholder.</summary>
    public void Apply(string composition) {
      if (_decorated || !CanOvergrow) return;
      if (GetComponent<BlockObject>() == null) return;
      _decorated = true;
      _decorationId = string.IsNullOrEmpty(composition) ? PlaceholderDonor : composition;
      SpawnDecoration();
    }

    /// <summary>Overgrow with the default placeholder composition — used
    /// by <see cref="OvergrowthTestTool"/>. The biome-driven
    /// <see cref="OvergrowthHandler"/> calls <see cref="Apply(string)"/>
    /// with the recipe's composition.</summary>
    public void Apply() => Apply(PlaceholderDonor);

    /// <summary>Remove the overgrowth from this entity (logical + visual).</summary>
    public void Clear() {
      _decorated = false;
      DespawnAll();
    }

    #endregion

    #region Lifecycle

    /// <summary>Runs after <see cref="Load"/> on load (and on fresh
    /// placement). If the persisted state says decorated, re-spawn the
    /// (non-persisted) decoration from <see cref="_decorationId"/>. On a
    /// fresh, never-overgrown tree this is a no-op.</summary>
    public void InitializeEntity() {
      if (_decorated && CanOvergrow) {
        SpawnDecoration();
      }
    }

    /// <summary>Tear down the overlay when the host entity is deleted so
    /// decorations don't outlive the tree. (Cut-to-stump leaves the
    /// entity alive, so that path needs a CuttableCutEvent hook — a later
    /// slice; deletion cleanup is enough for now.)</summary>
    public void DeleteEntity() {
      DespawnAll();
    }

    #endregion

    #region Persistence

    /// <inheritdoc />
    public void Save(IEntitySaver entitySaver) {
      // Presence of the component block means "decorated"; absence means
      // not (keeps non-overgrown trees out of the save entirely).
      if (!_decorated) return;
      entitySaver.GetComponent(ComponentKey).Set(DecorationIdKey, _decorationId);
    }

    /// <inheritdoc />
    public void Load(IEntityLoader entityLoader) {
      if (!entityLoader.TryGetComponent(ComponentKey, out var loader)) return;
      _decorated = true;
      if (loader.Has(DecorationIdKey)) {
        _decorationId = loader.Get(DecorationIdKey) ?? PlaceholderDonor;
      }
    }

    #endregion

    #region Helpers

    /// <summary>Spawn the decoration for <see cref="_decorationId"/> at
    /// the host's tile with a lifecycle controller (single alive/dying
    /// variant, ecology-driven via moisture — not tied to the host
    /// tree's life state). Guards against double-spawn.</summary>
    private void SpawnDecoration() {
      if (_spawned.Count > 0) return;
      var blockObject = GetComponent<BlockObject>();
      if (blockObject == null) return;

      var decoration = _decorations.Spawn(
          _decorationId, blockObject.Coordinates, new FloraLifecycleMoistureController());
      if (decoration != null) {
        _spawned.Add(decoration);
      }
      KeystoneLog.Verbose(
          $"[Keystone] Overgrowth: overlay '{_decorationId}' at " +
          $"{blockObject.Coordinates} (live={_spawned.Count}).");
    }

    private void DespawnAll() {
      for (var i = 0; i < _spawned.Count; i++) {
        _decorations.Despawn(_spawned[i]);
      }
      _spawned.Clear();
    }

    #endregion

  }

}
