using System.Collections.Generic;
using Keystone.Core.Ecology.Clusters;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Tiles;
using Keystone.Mod.Debug;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Survey;
using Keystone.Mod.Wellbeing;
using Timberborn.BlockSystem;
using Timberborn.CursorToolSystem;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace Keystone.Mod.Visualization {

  /// <summary>
  /// Per-frame visualizer that highlights the <see cref="Region"/> the
  /// cursor is hovering over plus the single ecology-field chunk the
  /// cursor sits in. Resolves the cursor surface, looks up its region
  /// via <see cref="RegionService.Containing"/>, and draws every member
  /// surface via Timberborn's <see cref="AreaHighlightingService"/>.
  /// Region surfaces draw cyan; the cursor's chunk overrides to red so
  /// the spatial granularity of the score system is visible (mirrors
  /// the "Scores (region R, chunk Cx,Cy)" header in the debug panel).
  /// Smoke-test feature for the region indexer.
  ///
  /// <para>Region lookup is O(1); the previous implementation re-ran a
  /// flood-fill per cursor move, this one just reads the precomputed
  /// surface→region map. Drawing still runs every frame because
  /// <see cref="AreaHighlightingService"/> is a per-frame commit
  /// pattern -- <c>UnhighlightAll</c>, then accumulate via
  /// <c>DrawTile</c>, then <c>Highlight</c>.</para>
  ///
  /// <para><b>Gating mechanism (HACK -- read carefully).</b> We piggyback
  /// on the Keystone debug panels' <c>GetText()</c> being called as our
  /// "user is looking at Keystone right now" signal. Empirically, the
  /// Timberborn debug overlay only calls <c>GetText()</c> for sections
  /// the user has expanded; collapsed sections are skipped. So
  /// <see cref="PanelActivity.WasQueriedRecently"/> becomes a free
  /// "panel is expanded" probe. The activities are split per panel —
  /// <see cref="KeystoneChunkPanelActivity"/> gates the chunk-side
  /// overlays (region cyan / cluster magenta / cursor-chunk red), and
  /// <see cref="KeystoneTilePanelActivity"/> gates the tile-side
  /// overlay (Nature-source sample yellow) — so a user opening the
  /// tile panel doesn't get chunk-panel overlays they didn't ask for.
  /// See <see cref="PanelActivity"/> for the full discussion of why
  /// the signal is smelly and what breaks if the assumption fails
  /// (short version: overlays silently revert to always-on while the
  /// overlay is open; fallback would be a hotkey or a
  /// <c>DebugModeController</c>-only gate).</para>
  /// </summary>
  public sealed class PlateauHighlighter : IUpdatableSingleton {

    #region Constants

    /// <summary>Cyan, semi-transparent. Used for every member surface of the cursor's region.</summary>
    private static readonly Color RegionColor = new(0f, 1f, 1f, 0.5f);

    /// <summary>Red, semi-transparent. Used to mark the single ecology-field chunk the cursor sits in -- mirrors the "Scores (region R, chunk Cx,Cy)" header in the debug panel so the spatial granularity is visible.</summary>
    private static readonly Color ChunkColor = new(1f, 0f, 0f, 0.5f);

    /// <summary>Magenta, semi-transparent. Tiles in any chunk that
    /// belongs to the cursor's cluster but isn't the cursor's chunk
    /// itself. Stacks visually: region cyan → cluster magenta →
    /// cursor-chunk red, with later wins in the per-tile decide
    /// below.</summary>
    private static readonly Color ClusterColor = new(1f, 0f, 1f, 0.5f);

    /// <summary>Yellow, semi-transparent. Tiles whose surface the
    /// hovered Nature source's scan sampled — i.e. surfaces at or
    /// below the BO's <c>buildingBaseZ</c> in the BO's chunk
    /// neighbourhood that resolved to a non-null region. Drawn on
    /// top of the region/cluster/chunk layers so the "what is the
    /// tree gazing at" set stays visible against any background. Only
    /// fires when the cursor is over a BO with a
    /// <see cref="KeystoneNatureSource"/>.</summary>
    private static readonly Color NatureSampleColor = new(1f, 1f, 0f, 0.6f);

    #endregion

    #region Fields

    private readonly CursorDebugger _cursor;
    private readonly KeystoneSurveyor _surveyor;
    private readonly KeystoneTilePanelActivity _tilePanelActivity;
    private readonly KeystoneChunkPanelActivity _chunkPanelActivity;
    private readonly IEcologyFieldQuery _fieldQuery;
    private readonly ChunkClusterIndex _clusterIndex;
    private readonly IBlockService _blockService;
    private readonly AreaHighlightingService _areaHighlight;
    private readonly BiomeOverlayRenderer _biomeOverlay;
    private readonly HashSet<(int Cx, int Cy)> _clusterChunkSet = new();

    private RegionId? _cachedRegionId;
    private bool _hadHighlightLastFrame;

    /// <summary>Surfaces materialised for <see cref="_cachedRegionId"/> at
    /// <see cref="_cachedSurfacesVersion"/>. Reused across frames while the
    /// cursor stays in the same region and the topology hasn't changed.</summary>
    private readonly List<SurfaceCoord> _cachedSurfaces = new();
    private int _cachedSurfacesVersion = -1;

    #endregion

    #region Construction

    public PlateauHighlighter(
        CursorDebugger cursor,
        KeystoneSurveyor surveyor,
        KeystoneTilePanelActivity tilePanelActivity,
        KeystoneChunkPanelActivity chunkPanelActivity,
        IEcologyFieldQuery fieldQuery,
        ChunkClusterIndex clusterIndex,
        IBlockService blockService,
        AreaHighlightingService areaHighlight,
        BiomeOverlayRenderer biomeOverlay) {
      _cursor = cursor;
      _surveyor = surveyor;
      _tilePanelActivity = tilePanelActivity;
      _chunkPanelActivity = chunkPanelActivity;
      _fieldQuery = fieldQuery;
      _clusterIndex = clusterIndex;
      _blockService = blockService;
      _areaHighlight = areaHighlight;
      _biomeOverlay = biomeOverlay;
    }

    #endregion

    #region IUpdatableSingleton

    /// <inheritdoc />
    public void UpdateSingleton() {
      // Outermost try/catch: this is a debug-only overlay (only fires
      // when a debug panel is open), but a throw would still let
      // Bindito drop us. Rate-limited so a persistent failure doesn't
      // spam every frame.
      try {
        if (_biomeOverlay.IsActive) return;
        var chunkActive = _chunkPanelActivity.WasQueriedRecently();
        var tileActive = _tilePanelActivity.WasQueriedRecently();
        if (!chunkActive && !tileActive) {
          if (_hadHighlightLastFrame) {
            _areaHighlight.UnhighlightAll();
            _hadHighlightLastFrame = false;
          }
          _cachedRegionId = null;
          return;
        }

        _areaHighlight.UnhighlightAll();
        _hadHighlightLastFrame = false;

        if (!_cursor.Active) {
          _cachedRegionId = null;
          return;
        }

        var c = _cursor.Coordinates;
        if (chunkActive) {
          DrawChunkPanelOverlays(c);
        }
        if (tileActive) {
          DrawNatureSampleOverlay(c);
        }
        _areaHighlight.Highlight();
        _hadHighlightLastFrame = true;
      } catch (System.Exception ex) {
        Keystone.Mod.Diagnostics.LifecycleGuard.HandleErrorOnce(
            "PlateauHighlighter.UpdateSingleton", "Subsystem failed", ex, ref _updateFailureLogged);
      }
    }

    private bool _updateFailureLogged;

    /// <summary>Chunk-panel-owned overlays: region cyan, cluster
    /// magenta, cursor-chunk red. Fired when the chunk debug panel
    /// is expanded; gives spatial context for the chunk panel's
    /// numeric readouts (region id, cluster membership, chunk
    /// boundary).</summary>
    private void DrawChunkPanelOverlays(Vector3Int c) {
      // Direct lookup only -- the cursor's reported SurfaceCoord is
      // authoritative. If the survey doesn't know that surface, the data
      // is stale (or the cursor is over something we don't track). Either
      // way, the right behavior is "highlight nothing", not "snap to a
      // nearby surface that might not exist any more". The debug panel
      // is the place that complains about staleness; this active path
      // stays quiet.
      var seed = new SurfaceCoord(c.x, c.y, c.z);
      var region = _surveyor.Regions.Containing(seed);
      if (region is null) {
        _cachedRegionId = null;
        return;
      }

      // Compute the cursor's chunk within the region's ecology field, if
      // any. Settled regions don't get fields, so the chunk overlay
      // simply doesn't draw for them; the region overlay still does.
      var field = _fieldQuery.FieldFor(region.Id);
      var chunkBounds = ChunkBoundsAt(field, c.x, c.y);

      // Resolve the cursor's cluster (if any). Cache the cluster's
      // chunk coords in a HashSet so the per-surface loop below can
      // colour them in O(1) per check. Cleared each frame so a cursor
      // exit returns to the no-cluster path.
      _clusterChunkSet.Clear();
      var cursorGlobalCx = c.x / RegionEcologyField.ChunkSize;
      var cursorGlobalCy = c.y / RegionEcologyField.ChunkSize;
      var clusterId = _clusterIndex.ClusterFor(region.Id, cursorGlobalCx, cursorGlobalCy);
      if (clusterId is { } cid) {
        foreach (var coord in _clusterIndex.ChunksIn(cid)) {
          _clusterChunkSet.Add((coord.GlobalChunkX, coord.GlobalChunkY));
        }
      }

      // Members aren't kept on Region (single source of truth lives in
      // RegionService._surfaceToRegion); SurfacesInRegion is an
      // O(all-surfaces) reverse-scan. Cache the materialised list and
      // reuse it across frames as long as the cursor stays in the same
      // region and topology hasn't changed -- the per-frame cost then
      // collapses to a TopologyVersion compare plus the DrawTile loop
      // that AreaHighlightingService requires anyway.
      var topology = _surveyor.Regions.TopologyVersion;
      if (_cachedRegionId != region.Id || _cachedSurfacesVersion != topology) {
        _cachedSurfaces.Clear();
        foreach (var coord in _surveyor.Regions.SurfacesInRegion(region.Id)) {
          _cachedSurfaces.Add(coord);
        }
        _cachedSurfacesVersion = topology;
      }
      _cachedRegionId = region.Id;

      for (var i = 0; i < _cachedSurfaces.Count; i++) {
        var coord = _cachedSurfaces[i];
        // Priority: cursor's chunk (red) > cluster member (magenta) >
        // region member (cyan). Cluster check is per-surface chunk
        // resolution (tile / ChunkSize), so a surface near the cursor
        // chunk that shares the cluster lights up magenta around the
        // red chunk for a clear "this is the connected biome blob."
        Color color;
        if (chunkBounds is { } cb && InBounds(coord.X, coord.Y, cb)) {
          color = ChunkColor;
        } else if (_clusterChunkSet.Count > 0
                   && _clusterChunkSet.Contains((
                       coord.X / RegionEcologyField.ChunkSize,
                       coord.Y / RegionEcologyField.ChunkSize))) {
          color = ClusterColor;
        } else {
          color = RegionColor;
        }
        _areaHighlight.DrawTile(new Vector3Int(coord.X, coord.Y, coord.Z), color);
      }
    }

    /// <summary>If the cursor is over a BO that carries a
    /// <see cref="KeystoneNatureSource"/>, run a fresh inspection
    /// scan and draw every sample surface in
    /// <see cref="NatureSampleColor"/>. Layers on top of the
    /// region/cluster/chunk colours so "what is the tree gazing at"
    /// stays visible regardless of background. Silent when no Nature
    /// source is hovered (the common case).
    ///
    /// <para>The scan is freshly executed each frame the cursor stays
    /// on the BO — debug-only path, allocation cost is acceptable.
    /// If this ever becomes a hover hot-spot we can cache by (entity,
    /// topology version).</para></summary>
    private void DrawNatureSampleOverlay(Vector3Int cursor) {
      foreach (var bo in _blockService.GetObjectsAt(cursor)) {
        var nature = bo.GetComponent<KeystoneNatureSource>();
        if (nature == null) continue;
        var r = nature.RunInspectionScan();
        if (!r.HasResult) continue;
        var samples = r.SampledSurfaces;
        for (var i = 0; i < samples.Count; i++) {
          var s = samples[i];
          _areaHighlight.DrawTile(new Vector3Int(s.X, s.Y, s.Z), NatureSampleColor);
        }
      }
    }

    /// <summary>
    /// Tile-space inclusive bounds of the chunk that the cursor's
    /// <c>(x, y)</c> falls into within <paramref name="field"/>.
    /// Null when there's no field for the cursor's region.
    /// </summary>
    private static (int minX, int minY, int maxX, int maxY)? ChunkBoundsAt(
        RegionEcologyField? field, int tileX, int tileY) {
      if (field is null) return null;
      var cx = (tileX - field.OriginX) / RegionEcologyField.ChunkSize;
      var cy = (tileY - field.OriginY) / RegionEcologyField.ChunkSize;
      var minX = field.OriginX + cx * RegionEcologyField.ChunkSize;
      var minY = field.OriginY + cy * RegionEcologyField.ChunkSize;
      var maxX = minX + RegionEcologyField.ChunkSize - 1;
      var maxY = minY + RegionEcologyField.ChunkSize - 1;
      return (minX, minY, maxX, maxY);
    }

    private static bool InBounds(int x, int y, (int minX, int minY, int maxX, int maxY) b) =>
        x >= b.minX && x <= b.maxX && y >= b.minY && y <= b.maxY;

    #endregion

  }

}
