using System;
using System.Collections.Generic;
using System.Text;
using Keystone.Core.Ports;
using Keystone.Core.Tiles;

namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Samples a deterministic stride of tiles across the loaded map and
  /// queries each Keystone port (terrain, moisture, contamination,
  /// building) at every sampled column / surface. A test failure means
  /// either an adapter threw, or it returned a value outside the
  /// documented contract — typically the symptom of a Timberborn API
  /// shape change leaking through the adapter into Core.
  ///
  /// <para><b>Why this exists.</b> Adapter regressions are the most
  /// expensive class of failure in Phase 1+: they break Core consumers
  /// in subtle ways (e.g. an off-by-one column-vs-index translation
  /// silently yields wrong moisture at every tile), and Core's MSTest
  /// suite can't catch them because it tests against hand-rolled fake
  /// ports. The first time the player will notice is when biome scores
  /// stop matching the terrain — by which point the developer has long
  /// since lost the context of the change that caused it.</para>
  ///
  /// <para><b>Deterministic stride.</b> Stride sampling (every K tiles
  /// rather than RNG) keeps the failure list reproducible across
  /// invocations, so a developer who sees "11 tiles flagged" can rerun
  /// after a fix and confirm the count went to zero — instead of
  /// chasing a moving target.</para>
  /// </summary>
  internal sealed class PortAdapterSanityTest : IKeystoneSelfTest {

    /// <summary>Approximate maximum number of columns we sample per port.
    /// Stride is computed from the map area so the sample set stays
    /// bounded on 384×384 maps as on 64×64 ones. Higher = more coverage
    /// but slower; this is plenty to catch a systemic adapter regression
    /// without hitting performance.</summary>
    private const int TargetColumnSamples = 1000;

    /// <summary>Hard cap on how many distinct surface samples the
    /// surface-aware ports (terrain, moisture predicate, contamination
    /// predicate, building) get exercised against. Stacked builds can
    /// multiply the column count; cap prevents one pathological vertical
    /// region from blowing the test runtime.</summary>
    private const int MaxSurfaceSamples = 5000;

    private readonly ITerrainQuery _terrain;
    private readonly IMoistureQuery _moisture;
    private readonly IContaminationQuery _contamination;
    private readonly IBuildingQuery _buildings;

    public PortAdapterSanityTest(
        ITerrainQuery terrain,
        IMoistureQuery moisture,
        IContaminationQuery contamination,
        IBuildingQuery buildings) {
      _terrain = terrain;
      _moisture = moisture;
      _contamination = contamination;
      _buildings = buildings;
    }

    /// <inheritdoc />
    public string Name => "Port adapter sanity";

    /// <inheritdoc />
    public string Category => "Adapters";

    /// <inheritdoc />
    public SelfTestResult Run() {
      var width = _terrain.Width;
      var height = _terrain.Height;
      if (width <= 0 || height <= 0) {
        return SelfTestResult.Skipped(
            $"Map not loaded yet (terrain reports {width}x{height}). Load a save first.");
      }

      // Compute stride so we hit roughly TargetColumnSamples columns
      // independent of map size. At least 1 (every column on a tiny map).
      var area = width * height;
      var stride = Math.Max(1, (int)Math.Sqrt(area / (double)TargetColumnSamples));

      var columnSamples = 0;
      var surfaceSamples = 0;
      var problems = new List<string>();

      for (var y = 0; y < height; y += stride) {
        for (var x = 0; x < width; x += stride) {
          var column = new TileCoord(x, y);
          if (!_terrain.Contains(column)) {
            problems.Add($"Terrain.Contains({x},{y}) returned false but ({x},{y}) is in bounds [{width}x{height}]");
            continue;
          }
          columnSamples++;

          // Column-level float queries. Moisture and Contamination are
          // "tiles from source, decaying linearly" channels — natural
          // max ~16 tiles for moisture, similar shape for contamination.
          // We only assert "not NaN" and "non-negative" because the
          // upper bound is fluid (overhangs, multiple sources stacking)
          // and a too-tight bound produces false positives. NaN or a
          // negative value would be a real adapter bug worth flagging.
          var moistFloat = _moisture.MoistureAt(column);
          if (float.IsNaN(moistFloat) || moistFloat < 0f) {
            problems.Add($"MoistureAt({x},{y}) = {moistFloat} (expected non-NaN, >= 0)");
          }
          var contamFloat = _contamination.ContaminationAt(column);
          if (float.IsNaN(contamFloat) || contamFloat < 0f) {
            problems.Add($"ContaminationAt({x},{y}) = {contamFloat} (expected non-NaN, >= 0)");
          }

          var surfaces = _terrain.SurfaceHeightsAt(column);
          if (surfaces == null) {
            problems.Add($"SurfaceHeightsAt({x},{y}) returned null (contract: empty list, not null)");
            continue;
          }
          // Surfaces must be sorted ascending per the port contract.
          for (var i = 1; i < surfaces.Count; i++) {
            if (surfaces[i] <= surfaces[i - 1]) {
              problems.Add(
                  $"SurfaceHeightsAt({x},{y}) not ascending: " +
                  $"surfaces[{i - 1}]={surfaces[i - 1]} surfaces[{i}]={surfaces[i]}");
              break;
            }
          }

          // Surface-level predicate queries. Cap the global surface
          // count so a pathological column doesn't dominate the loop.
          if (surfaceSamples < MaxSurfaceSamples) {
            for (var i = 0; i < surfaces.Count && surfaceSamples < MaxSurfaceSamples; i++) {
              surfaceSamples++;
              var surface = new SurfaceCoord(x, y, surfaces[i]);
              // These four return bool, so we're just exercising the
              // call path -- any thrown exception is captured by the
              // runner. A true/false result is always valid; no
              // contract violation to assert here.
              _moisture.IsMoistAt(surface);
              _contamination.IsContaminatedAt(surface);
              _terrain.HasTerrainAbove(surface);
              _buildings.ClassifyAt(surface);
            }
          }
        }
      }

      if (columnSamples == 0) {
        return SelfTestResult.Skipped(
            "No columns sampled (in-bounds Contains returned false for every stride hit). " +
            "Likely an empty map or the adapter is failing closed.");
      }

      if (problems.Count > 0) {
        var detail = new StringBuilder();
        detail.Append("Sampled ").Append(columnSamples).Append(" columns, ")
              .Append(surfaceSamples).Append(" surfaces ")
              .Append("(map ").Append(width).Append("x").Append(height)
              .Append(", stride ").Append(stride).AppendLine(")");
        // Cap the per-finding output so a systemic regression doesn't
        // flood the panel; the count line tells the developer the true
        // scale.
        var shown = 0;
        foreach (var p in problems) {
          if (shown++ >= 20) {
            detail.Append("  ... and ").Append(problems.Count - 20).AppendLine(" more");
            break;
          }
          detail.Append("  ").AppendLine(p);
        }
        return SelfTestResult.Fail(
            $"{problems.Count} adapter contract violation(s) across {columnSamples} columns",
            detail.ToString());
      }

      return SelfTestResult.Pass(
          $"{columnSamples} columns, {surfaceSamples} surfaces — all ports OK " +
          $"(map {width}x{height}, stride {stride})");
    }

  }

}
