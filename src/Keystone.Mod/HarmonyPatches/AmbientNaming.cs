using System;
using Keystone.Mod.Recipes;
using Timberborn.BaseComponentSystem;
using UnityEngine;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Single source of truth for "is this entity Keystone-inert" --
  /// the predicate used by every Harmony patch in this folder to skip
  /// player-driven interaction (hover highlight, click select,
  /// demolish-tool preview/commit) on Keystone entities that should
  /// be non-interactive.
  ///
  /// <para>An entity is inert if EITHER condition holds:
  /// <list type="bullet">
  ///   <item><b>Variant-driven (preferred path).</b> The entity carries
  ///         a <see cref="KeystoneVariant"/> component whose
  ///         <c>Class</c> equals <see cref="InertClass"/> (currently
  ///         <c>"B"</c>). Class B handler stamps this at spawn
  ///         time; the variant is persisted across save/load via
  ///         <c>IPersistentEntity</c>. Class C entities carry the
  ///         component too but with <c>Class = "C"</c>, which the
  ///         predicate ignores -- they fall through to vanilla
  ///         selection / demolish behavior even when the underlying
  ///         blueprint asset is the same one a Class B recipe
  ///         references.</item>
  ///   <item><b>Name-prefix (legacy/probe path).</b> The blueprint name
  ///         starts with <see cref="Prefix"/>. This is the path the
  ///         debug <see cref="Keystone.Mod.Debug.StrippedEntityProbe"/>
  ///         takes when it spawns stripped donor blueprints (e.g. a
  ///         Maple renamed to <c>Keystone.Stripped.Maple</c>) -- those
  ///         entities have no <see cref="KeystoneVariant"/> because
  ///         they bypass the handler's spawn path.</item>
  /// </list>
  /// Both paths exist intentionally: the prefix path lets us repurpose
  /// vanilla content for prototyping without going through a recipe;
  /// the variant path is the contract for shipped Keystone content.</para>
  ///
  /// <para>Consumers:
  /// <list type="bullet">
  ///   <item><see cref="SelectableObjectRetrieverPatch"/> -- blocks cursor
  ///         hover highlight.</item>
  ///   <item><see cref="EntitySelectionServicePatch"/> -- blocks click
  ///         selection.</item>
  ///   <item><see cref="DemolishableSelectionToolPatch"/> -- blocks the
  ///         demolish-tool preview and commit.</item>
  /// </list></para>
  /// </summary>
  internal static class AmbientNaming {

    /// <summary>Blueprint-name prefix marking an inert entity that
    /// has no <see cref="KeystoneVariant"/> component (typically a
    /// stripped donor blueprint). Must be unique enough to avoid
    /// third-party collisions.</summary>
    public const string Prefix = "Keystone.Stripped.";

    /// <summary>The variant.Class value that triggers selection /
    /// demolish suppression. Class B is the inert tier; Class C and
    /// future tiers are deliberately selectable.</summary>
    private const string InertClass = "B";

    /// <summary>True if <paramref name="gameObject"/> represents a
    /// Keystone-inert entity by either contract (variant tag or
    /// name prefix). Null-safe; returns false on null.</summary>
    public static bool IsAmbient(GameObject gameObject) {
      if (gameObject == null) return false;
      if (gameObject.name.StartsWith(Prefix, StringComparison.Ordinal)) return true;
      // Variant check. GetComponent on a destroyed/odd object should
      // not throw under normal Unity rules, but the patches wrap us in
      // try/catch so a failure here can't leak into the cursor path.
      var variant = gameObject.GetComponent<KeystoneVariant>();
      return variant != null && variant.Class == InertClass;
    }

    /// <summary>True if <paramref name="component"/>'s entity is
    /// Keystone-inert (see <see cref="IsAmbient(GameObject)"/>).
    /// Uses Timberborn's <c>GetComponentFast</c> for the variant
    /// check because we're already on the BaseComponent surface.
    /// Null-safe.</summary>
    public static bool IsAmbient(BaseComponent component) {
      if (component == null) return false;
      var go = component.GameObject;
      if (go != null && go.name.StartsWith(Prefix, StringComparison.Ordinal)) return true;
      return component.HasComponent<KeystoneVariant>()
          && component.GetComponent<KeystoneVariant>().Class == InertClass;
    }

  }

}
