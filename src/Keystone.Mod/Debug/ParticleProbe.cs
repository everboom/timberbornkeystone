using System;
using Keystone.Mod.Diagnostics;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.GameDistricts;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Phase 1, prototype #3: code-built placeholder particle effects.
  /// Spawns four small <see cref="ParticleSystem"/> rigs west of the
  /// district center, one for each of the atmospheric/critter effect
  /// types we want to support: mist, flying critters, ground critters,
  /// and fish.
  ///
  /// <para>The visuals are deliberately ugly — Unity's default particle
  /// material with primitive colour and shape modules. The point is
  /// to verify the construction + positioning + lifecycle pattern, so
  /// that when we author proper assets in the Unity SDK we know the
  /// integration plumbing works. Substituting a custom mesh/material/
  /// VFX-graph asset later is a one-line change per effect.</para>
  ///
  /// <para>Layout (X grows east, Y grows north, Z grows up):</para>
  /// <list type="bullet">
  ///   <item>Mist: tile <c>(dc.x - 6, dc.y, dc.z)</c></item>
  ///   <item>Flying: tile <c>(dc.x - 6, dc.y + 4, dc.z)</c>, particles spawn ~5 tiles up</item>
  ///   <item>Ground: tile <c>(dc.x - 6, dc.y + 8, dc.z)</c></item>
  ///   <item>Fish: tile <c>(dc.x - 6, dc.y + 12, dc.z)</c> — placement
  ///         doesn't yet honour water boundaries; just visual validation here.</item>
  /// </list>
  /// </summary>
  public sealed class ParticleProbe : IPostLoadableSingleton {

    #region Constants

    private const int RowOffsetX = -6;
    private const int MistOffsetY = 0;
    private const int FlyingOffsetY = 4;
    private const int GroundOffsetY = 8;
    private const int FishOffsetY = 12;

    /// <summary>How high above the tile floor the flying particles emit.</summary>
    private const float FlyingHeight = 5f;

    #endregion

    #region Fields

    private readonly DistrictCenterRegistry _districts;

    #endregion

    #region Construction

    public ParticleProbe(DistrictCenterRegistry districts) {
      _districts = districts;
    }

    #endregion

    #region IPostLoadableSingleton

    /// <inheritdoc />
    public void PostLoad() {
      try {
        var center = PickDistrictCenter();
        if (center == null) {
          KeystoneLog.Verbose("[Keystone] ParticleProbe: no district center; skipping.");
          return;
        }
        if (!center.TryGetComponent<BlockObject>(out var dcBlock)) {
          KeystoneLog.Verbose("[Keystone] ParticleProbe: district center has no BlockObject; skipping.");
          return;
        }
        var dc = dcBlock.Coordinates;

        SpawnMist(new Vector3Int(dc.x + RowOffsetX, dc.y + MistOffsetY, dc.z));
        SpawnFlying(new Vector3Int(dc.x + RowOffsetX, dc.y + FlyingOffsetY, dc.z));
        SpawnGround(new Vector3Int(dc.x + RowOffsetX, dc.y + GroundOffsetY, dc.z));
        SpawnFish(new Vector3Int(dc.x + RowOffsetX, dc.y + FishOffsetY, dc.z));

        KeystoneLog.Verbose(
            $"[Keystone] ParticleProbe: spawned mist/flying/ground/fish placeholders " +
            $"west of DC ({dc}). Look {Math.Abs(RowOffsetX)} tiles west, " +
            $"y in [{dc.y + MistOffsetY}..{dc.y + FishOffsetY}].");
      } catch (Exception ex) {
        KeystoneLog.Warn($"[Keystone] ParticleProbe threw: {ex}");
      }
    }

    #endregion

    #region Effect builders

    /// <summary>
    /// Slow upward drift of soft white puffs. Cylindrical emission
    /// shape so the cloud has a visible footprint on the tile.
    /// </summary>
    private static void SpawnMist(Vector3Int tileCoord) {
      var go = NewParticleObject("Mist", CoordinateSystem.GridToWorldCentered(tileCoord));
      var ps = go.GetComponent<ParticleSystem>();

      var main = ps.main;
      main.duration = 5f;
      main.loop = true;
      main.startLifetime = 6f;
      main.startSpeed = 0.3f;
      main.startSize = 1.5f;
      main.startColor = new Color(1f, 1f, 1f, 0.4f);
      main.maxParticles = 200;

      var emission = ps.emission;
      emission.rateOverTime = 25f;

      var shape = ps.shape;
      shape.shapeType = ParticleSystemShapeType.Circle;
      shape.radius = 0.5f;

      var vel = ps.velocityOverLifetime;
      vel.enabled = true;
      vel.space = ParticleSystemSimulationSpace.Local;
      vel.y = new ParticleSystem.MinMaxCurve(0.4f);
    }

    /// <summary>
    /// Particles emitted at altitude moving in random horizontal
    /// directions — placeholder for circling birds or bats.
    /// </summary>
    private static void SpawnFlying(Vector3Int tileCoord) {
      var pos = CoordinateSystem.GridToWorldCentered(tileCoord) + Vector3.up * FlyingHeight;
      var go = NewParticleObject("Flying", pos);
      var ps = go.GetComponent<ParticleSystem>();

      var main = ps.main;
      main.duration = 5f;
      main.loop = true;
      main.startLifetime = 4f;
      main.startSpeed = 1.5f;
      main.startSize = 0.4f;
      main.startColor = Color.black;
      main.maxParticles = 30;

      var emission = ps.emission;
      emission.rateOverTime = 5f;

      var shape = ps.shape;
      shape.shapeType = ParticleSystemShapeType.Sphere;
      shape.radius = 0.5f;
      shape.randomDirectionAmount = 1f;
    }

    /// <summary>
    /// Particles low to the ground moving outward — placeholder for
    /// scurrying small critters.
    /// </summary>
    private static void SpawnGround(Vector3Int tileCoord) {
      var go = NewParticleObject("Ground", CoordinateSystem.GridToWorldCentered(tileCoord));
      var ps = go.GetComponent<ParticleSystem>();

      var main = ps.main;
      main.duration = 5f;
      main.loop = true;
      main.startLifetime = 2f;
      main.startSpeed = 0.6f;
      main.startSize = 0.15f;
      main.startColor = new Color(0.4f, 0.25f, 0.1f);
      main.maxParticles = 40;

      var emission = ps.emission;
      emission.rateOverTime = 8f;

      var shape = ps.shape;
      shape.shapeType = ParticleSystemShapeType.Donut;
      shape.radius = 0.5f;
      shape.donutRadius = 0.05f;
      shape.randomDirectionAmount = 1f;
    }

    /// <summary>
    /// Placeholder fish — coloured particles moving horizontally.
    /// Real version needs water-tile containment via IWaterQuery; this
    /// just validates the spawn pattern.
    /// </summary>
    private static void SpawnFish(Vector3Int tileCoord) {
      var go = NewParticleObject("Fish", CoordinateSystem.GridToWorldCentered(tileCoord));
      var ps = go.GetComponent<ParticleSystem>();

      var main = ps.main;
      main.duration = 5f;
      main.loop = true;
      main.startLifetime = 3f;
      main.startSpeed = 0.8f;
      main.startSize = 0.2f;
      main.startColor = new Color(0.3f, 0.5f, 0.8f);
      main.maxParticles = 25;

      var emission = ps.emission;
      emission.rateOverTime = 6f;

      var shape = ps.shape;
      shape.shapeType = ParticleSystemShapeType.Box;
      shape.scale = new Vector3(1f, 0.1f, 1f);
      shape.randomDirectionAmount = 1f;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Build a fresh root <see cref="GameObject"/> with a single
    /// <see cref="ParticleSystem"/> component, positioned at
    /// <paramref name="worldPos"/>. Caller configures the modules.
    /// </summary>
    private static GameObject NewParticleObject(string label, Vector3 worldPos) {
      var go = new GameObject($"Keystone.Particles.{label}");
      go.transform.position = worldPos;
      go.AddComponent<ParticleSystem>();
      return go;
    }

    private DistrictCenter? PickDistrictCenter() {
      var finished = _districts.FinishedDistrictCenters;
      if (finished.Count > 0) return finished[0];
      var all = _districts.AllDistrictCenters;
      return all.Count > 0 ? all[0] : null;
    }

    #endregion

  }

}
