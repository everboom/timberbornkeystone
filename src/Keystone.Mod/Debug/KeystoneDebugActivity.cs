using UnityEngine;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// Per-panel "was this debug section queried in the last few frames"
  /// signal. Each Keystone <c>IDebuggingPanel</c> owns one — touched in
  /// its <c>GetText()</c> and consulted by visualizers
  /// (<see cref="Keystone.Mod.Visualization.PlateauHighlighter"/>) so
  /// each panel's overlays fire only when that panel is expanded.
  ///
  /// <para><b>HACK preserved.</b> The trick is still "the host calls
  /// <c>GetText()</c> only on expanded sections" — see the original
  /// commentary on <see cref="KeystoneTileDebugPanel"/>. Same risks,
  /// same fallback (overlays revert to always-on if the host changes
  /// to call <c>GetText()</c> on collapsed sections too).</para>
  ///
  /// <para><b>Per-panel split.</b> An earlier iteration had a single
  /// shared <c>KeystoneDebugActivity</c> instance touched by both
  /// panels — either one expanded would fire every Keystone overlay.
  /// That meant a user opening the tile panel got the chunk panel's
  /// region/cluster/chunk highlights too. The split below pins each
  /// overlay to its owning panel: <see cref="KeystoneTilePanelActivity"/>
  /// gates tile-panel overlays (currently just the Nature-source
  /// sample highlight), <see cref="KeystoneChunkPanelActivity"/> gates
  /// chunk-panel overlays (the region/cluster/chunk
  /// <see cref="Keystone.Mod.Visualization.PlateauHighlighter"/> set).
  /// Both can be active simultaneously; the highlighter draws the
  /// union.</para>
  /// </summary>
  public abstract class PanelActivity {

    /// <summary>Frame index of the most recent <see cref="MarkActive"/>
    /// call. <c>-1</c> if never touched.</summary>
    private int _lastTouchedFrame = -1;

    /// <summary>Record that the panel's <c>GetText()</c> was just
    /// called. Cheap; idempotent within a frame.</summary>
    public void MarkActive() {
      _lastTouchedFrame = Time.frameCount;
    }

    /// <summary>True iff <see cref="MarkActive"/> has fired within the
    /// last <paramref name="frameTolerance"/> frames. The tolerance
    /// absorbs <c>UpdateSingleton</c> ordering between the debug
    /// overlay's own pass and downstream consumers.</summary>
    public bool WasQueriedRecently(int frameTolerance = 2) {
      if (_lastTouchedFrame < 0) return false;
      return Time.frameCount - _lastTouchedFrame <= frameTolerance;
    }
  }

  /// <summary>Activity signal owned by
  /// <see cref="KeystoneTileDebugPanel"/>. Gates the tile-panel-side
  /// highlights (Nature-source sample overlay).</summary>
  public sealed class KeystoneTilePanelActivity : PanelActivity { }

  /// <summary>Activity signal owned by
  /// <see cref="KeystoneChunkDebugPanel"/>. Gates the chunk-panel-side
  /// highlights (region / cluster / chunk overlays in
  /// <see cref="Keystone.Mod.Visualization.PlateauHighlighter"/>).</summary>
  public sealed class KeystoneChunkPanelActivity : PanelActivity { }

}
