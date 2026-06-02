using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Keystone.Mod.Biomes;
using Keystone.Mod.Diagnostics.SelfTests;
using Keystone.Mod.Ecology;
using Keystone.Mod.Settings;
using Timberborn.RootProviders;
using Timberborn.SingletonSystem;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Floating perf-stats overlay. Non-modal (doesn't push onto the
  /// dialog stack, doesn't block game input), draggable by its header
  /// bar. Renders the full per-scope rolling-sweep table (samples,
  /// avg, P99, max, frequency) plus the load-time one-shot section
  /// from <see cref="PerfTracker.OneShots"/>. Toggled via Alt+Shift+K.
  ///
  /// <para><b>How it's mounted.</b> Built on a fresh
  /// <see cref="UIDocument"/> via
  /// <see cref="RootVisualElementProvider.CreateEmpty"/> rather than
  /// TimberUi's <c>DialogBoxElement.Show</c>. The dialog route works
  /// but goes through <c>PanelStack.PushDialog</c>, which gates game
  /// input -- not what we want for a passive diagnostic overlay that
  /// you glance at while the game runs.</para>
  ///
  /// <para><b>Refresh cadence.</b> Repaints at ~2 Hz while the window
  /// is visible; hidden windows skip the render entirely.</para>
  ///
  /// <para><b>Draggability.</b> Pointer events on the title bar
  /// capture the pointer and translate the window via inline
  /// <c>style.left</c> / <c>style.top</c>. Events propagation is
  /// stopped so the drag doesn't bleed into the world camera.</para>
  /// </summary>
  public sealed class KeystonePerfWindow : ILoadableSingleton, IUpdatableSingleton {

    #region Constants

    private const float WindowWidth = 900f;
    private const float WindowHeight = 900f;
    private const float HeaderHeight = 24f;
    private const float InitialLeft = 60f;
    private const float InitialTop = 60f;

    /// <summary>Refresh cadence in Unity frames. 30 frames @ 60fps =
    /// ~2 Hz, fast enough to see changes but not thrashy.</summary>
    private const int RefreshFrames = 30;

    /// <summary>Header copy-button labels. <see cref="CopiedLabel"/> is
    /// shown for <see cref="CopyFlashFrames"/> frames after a click as
    /// visual confirmation, then reverts to <see cref="CopyLabel"/>.</summary>
    private const string CopyLabel = "Copy";
    private const string CopiedLabel = "Copied!";

    /// <summary>Header clear-button labels. Mirrors the copy button's
    /// flash: <see cref="ClearedLabel"/> shows for
    /// <see cref="CopyFlashFrames"/> frames after a click, then reverts
    /// to <see cref="ClearLabel"/>.</summary>
    private const string ClearLabel = "Clear";
    private const string ClearedLabel = "Cleared!";

    /// <summary>How long the "Copied!" / "Cleared!" confirmations stay
    /// up, in Unity frames. ~60 @ 60fps = ~1s. Counted down in
    /// <see cref="UpdateSingleton"/>, which runs every frame.</summary>
    private const int CopyFlashFrames = 60;

    /// <summary>Real-time window the headline ms/sec is averaged over.
    /// Long enough that bursty work (e.g. cycle-boundary spikes from
    /// the rolling-sweep tickers) averages out into a representative
    /// CPU-cost number rather than flickering between 0 and the spike
    /// height.</summary>
    private const double WindowSeconds = 10.0;

    /// <summary>Scope-name suffix that routes a row to the background-
    /// threads table. Unlike the counter/timer split (now carried by
    /// <see cref="Keystone.Core.Diagnostics.PerfStatsKind"/> on the
    /// stats themselves, set at the record call site), "parallel" is
    /// still a name convention: a <c>.Parallel</c> scope is an ordinary
    /// ms timer, just one measured on a background thread, so it can't
    /// be distinguished by Kind.</summary>
    private const string ParallelRowSuffix = ".Parallel";

    private const int MaxScopeWidth = 40;
    private const int NColWidth = 5;
    private const int MsColWidth = 9;
    private const int HzColWidth = 6;

    /// <summary>Largest Hz value rendered numerically; anything above
    /// is shown as "&gt;999" so the column can't widen without bound and
    /// shove the layout sideways. A genuine runaway is possible: when a
    /// scope fires several times within a single stopwatch resolution
    /// tick the median inter-sample gap collapses toward zero and
    /// <see cref="Keystone.Core.Diagnostics.PerfStats.FrequencyHz"/>
    /// blows up into the thousands. Past ~1 kHz the exact figure isn't
    /// actionable anyway — "this fires faster than you can budget for"
    /// is all the column needs to say.</summary>
    private const double HzDisplayCap = 999.0;

    /// <summary>Fallback Hz cutoff between "per-tick" and "sporadic"
    /// main-thread scopes, used only when the live tick rate is
    /// unavailable (game paused — no sim ticks in the window). The
    /// primary classification is ratio-based against the measured tick
    /// rate (see <see cref="PerTickCadenceFraction"/> and the partition
    /// in <see cref="RenderPerfTab"/>). An absolute cutoff is
    /// game-speed-dependent and wrong: Timberborn's sim ticks at only
    /// ~5Hz at 1x speed, so a fixed 10Hz threshold mis-filed every
    /// per-tick scope as 'sporadic' below ~2x speed.</summary>
    private const double PerTickHzThreshold = 10.0;

    /// <summary>Fraction of the live sim-tick rate a scope must fire at
    /// to count as "per-tick" rather than "sporadic". 0.5 = "fires at
    /// least every other tick". Ratio-based (not absolute Hz) so the
    /// split stays stable across game speeds: a scope's measured Hz and
    /// the reference tick Hz both scale with speed, so their ratio does
    /// not. The rolling-sweep scopes are <c>ITickableSingleton</c> (one
    /// tick per sim tick), so a per-tick scope's Hz tracks the sim-tick
    /// rate and lands near ratio 1.0.</summary>
    private const double PerTickCadenceFraction = 0.5;

    // Colour-threshold constants are currently unused — coloring was
    // pulled back to plain output so a baseline can be established
    // against the engine tick juxtaposition before deciding what
    // "high" means. Helpers (`Colorize`, `YellowHex`, `RedHex`)
    // remain at the bottom of the file so a future round can
    // re-enable selectively without rebuilding the helper.
    private const string YellowHex = "#E6C200";
    private const string RedHex = "#E04040";

    #endregion

    #region Fields

    private readonly PerfTracker _tracker;
    private readonly GameTickCounter _ticks;
    private readonly RootVisualElementProvider _rootProvider;
    private readonly Timberborn.TimeSystem.IDayNightCycle _dayNightCycle;
    private readonly StringBuilder _buffer = new();

    private VisualElement? _root;
    private VisualElement? _header;
    private Label? _content;
    private Label? _copyButton;
    private Label? _clearButton;
    private int _framesSinceRefresh;

    /// <summary>Frames remaining on the "Copied!" header flash, counted
    /// down in <see cref="UpdateSingleton"/>. Zero when idle.</summary>
    private int _copyFlashFramesLeft;

    /// <summary>Frames remaining on the "Cleared!" header flash, counted
    /// down in <see cref="UpdateSingleton"/>. Zero when idle.</summary>
    private int _clearFlashFramesLeft;

    // Drag state.
    private bool _dragging;
    private Vector2 _grabOffset;

    // Activity reference snapshot. Captured the first time the window
    // becomes visible (or on Load if it starts visible) and held until
    // the window is hidden + reshown. Lets the activity section show
    // per-day rates relative to "when you opened this window" — same
    // pull-model contract: no work when hidden, no subscriptions.
    private readonly KeystoneActivityRecorder _activityRecorder;
    private ActivitySnapshot? _activityReference;

    /// <summary>Which of the window's content sections is currently
    /// rendered. Tabs share the single window so the player only has
    /// to track one keybind; the tab strip in the header lets them
    /// flip between them without taking up screen real estate.</summary>
    private enum Tab { Perf, Activity, Test }

    private Tab _currentTab = Tab.Perf;
    private Label? _perfTabButton;
    private Label? _activityTabButton;
    private Label? _testTabButton;

    // Test tab — manual-run developer self-test battery. The runner is
    // injected; the Render side of Test is event-driven (clicking Run
    // populates the result label), not refresh-driven, so the per-tick
    // Render() short-circuits when the Test tab is active.
    private readonly SelfTestRunner _selfTestRunner;

    // Map-update-cadence diagnostics. The slider (perfSettings) gives
    // the *requested* cycle length; each ticker's CycleDurationDays
    // gives the value it actually latched at construction. The two
    // should agree — if they don't, the persisted-setting hydration
    // race documented on KeystonePerformanceSettings.MapUpdateCycleDays
    // is hitting and the slider isn't taking effect.
    private readonly KeystonePerformanceSettings _perfSettings;
    private readonly EcologyFieldUpdater _ecologyFieldUpdater;
    private readonly ChunkBiomeTicker _chunkBiomeTicker;
    private readonly ChunkClusterTicker _chunkClusterTicker;

    private VisualElement? _testRoot;
    private Label? _testResultLabel;
    private Label? _testSummaryLabel;
    private ScrollView? _perfActivityScroll;

    #endregion

    #region Construction

    public KeystonePerfWindow(
        PerfTracker tracker,
        GameTickCounter ticks,
        RootVisualElementProvider rootProvider,
        KeystoneActivityRecorder activityRecorder,
        SelfTestRunner selfTestRunner,
        Timberborn.TimeSystem.IDayNightCycle dayNightCycle,
        KeystonePerformanceSettings perfSettings,
        EcologyFieldUpdater ecologyFieldUpdater,
        ChunkBiomeTicker chunkBiomeTicker,
        ChunkClusterTicker chunkClusterTicker) {
      _tracker = tracker;
      _ticks = ticks;
      _rootProvider = rootProvider;
      _activityRecorder = activityRecorder;
      _selfTestRunner = selfTestRunner;
      _dayNightCycle = dayNightCycle;
      _perfSettings = perfSettings;
      _ecologyFieldUpdater = ecologyFieldUpdater;
      _chunkBiomeTicker = chunkBiomeTicker;
      _chunkClusterTicker = chunkClusterTicker;
    }

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      // CreateEmpty: fresh UIDocument with a bare rootVisualElement at
      // the given sort order. We pick a high sort order so the window
      // overlays the existing UI rather than appearing under it.
      // CreateEmpty(name, sortOrder). High sort order so the window
      // overlays the existing UI rather than sitting under it.
      var document = _rootProvider.CreateEmpty("KeystonePerfWindow", 100);
      var canvas = document.rootVisualElement;

      _root = new VisualElement {
          name = "KeystonePerfRoot",
      };
      _root.style.position = Position.Absolute;
      _root.style.left = InitialLeft;
      _root.style.top = InitialTop;
      _root.style.width = WindowWidth;
      // Fixed height (rather than min/maxHeight) so the ScrollView's
      // flexGrow gets a deterministic viewport to clip into. With
      // a content-sized root the ScrollView grows with its label
      // and nothing ever overflows -- the scrollbar is then a
      // no-op even when there are 40+ scope rows.
      _root.style.height = WindowHeight;
      _root.style.backgroundColor = new Color(0f, 0f, 0f, 0.82f);
      _root.style.borderTopLeftRadius = 4;
      _root.style.borderTopRightRadius = 4;
      _root.style.borderBottomLeftRadius = 4;
      _root.style.borderBottomRightRadius = 4;
      _root.style.borderLeftWidth = 1;
      _root.style.borderRightWidth = 1;
      _root.style.borderTopWidth = 1;
      _root.style.borderBottomWidth = 1;
      _root.style.borderLeftColor = new Color(0.4f, 0.4f, 0.4f, 1f);
      _root.style.borderRightColor = new Color(0.4f, 0.4f, 0.4f, 1f);
      _root.style.borderTopColor = new Color(0.4f, 0.4f, 0.4f, 1f);
      _root.style.borderBottomColor = new Color(0.4f, 0.4f, 0.4f, 1f);
      _root.style.display = DisplayStyle.None;  // start hidden

      BuildHeader();
      BuildTabStrip();
      BuildContent();

      canvas.Add(_root);
    }

    private void BuildHeader() {
      _header = new VisualElement { name = "Header" };
      _header.style.height = HeaderHeight;
      _header.style.flexDirection = FlexDirection.Row;
      _header.style.alignItems = Align.Center;
      _header.style.justifyContent = Justify.SpaceBetween;
      _header.style.paddingLeft = 8;
      _header.style.paddingRight = 4;
      _header.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
      _header.style.borderTopLeftRadius = 3;
      _header.style.borderTopRightRadius = 3;
      _header.RegisterCallback<PointerDownEvent>(OnHeaderPointerDown);
      _header.RegisterCallback<PointerMoveEvent>(OnHeaderPointerMove);
      _header.RegisterCallback<PointerUpEvent>(OnHeaderPointerUp);

      var title = new Label("Keystone  (Alt+Shift+K)") { name = "Title" };
      title.style.color = Color.white;
      title.style.unityFontStyleAndWeight = FontStyle.Bold;
      _header.Add(title);

      // Right-side button group: Clear, Copy, then close. Grouped in a
      // row so the header's SpaceBetween keeps the title hard-left and
      // the buttons hard-right rather than spreading them all evenly.
      var buttons = new VisualElement { name = "HeaderButtons" };
      buttons.style.flexDirection = FlexDirection.Row;
      buttons.style.alignItems = Align.Center;

      _clearButton = new Label(ClearLabel) { name = "Clear" };
      _clearButton.style.color = Color.white;
      _clearButton.style.fontSize = 12;
      _clearButton.style.paddingLeft = 6;
      _clearButton.style.paddingRight = 10;
      _clearButton.tooltip =
          "Reset the rolling stats / counters to re-baseline after load "
          + "(keeps the Startup costs section)";
      _clearButton.RegisterCallback<PointerDownEvent>(ev => {
        ClearStats();
        ev.StopPropagation();
      });
      buttons.Add(_clearButton);

      _copyButton = new Label(CopyLabel) { name = "Copy" };
      _copyButton.style.color = Color.white;
      _copyButton.style.fontSize = 12;
      _copyButton.style.paddingLeft = 6;
      _copyButton.style.paddingRight = 10;
      _copyButton.tooltip = "Copy this tab's text to the clipboard";
      _copyButton.RegisterCallback<PointerDownEvent>(ev => {
        CopyCurrentTabToClipboard();
        ev.StopPropagation();
      });
      buttons.Add(_copyButton);

      var close = new Label("×") { name = "Close" };  // multiplication sign as a tidy ×
      close.style.color = Color.white;
      close.style.fontSize = 18;
      close.style.paddingLeft = 6;
      close.style.paddingRight = 6;
      close.RegisterCallback<PointerDownEvent>(ev => {
        SetVisible(false);
        ev.StopPropagation();
      });
      buttons.Add(close);

      _header.Add(buttons);

      _root!.Add(_header);
    }

    /// <summary>Build the tab strip — a single horizontal row of
    /// clickable labels under the header. Each label represents one
    /// <see cref="Tab"/>; the active tab has a brighter background.
    /// Click handlers flip <see cref="_currentTab"/> and force a
    /// repaint by resetting <see cref="_framesSinceRefresh"/>.</summary>
    private void BuildTabStrip() {
      var strip = new VisualElement { name = "TabStrip" };
      strip.style.flexDirection = FlexDirection.Row;
      strip.style.backgroundColor = new Color(0.10f, 0.10f, 0.10f, 1f);
      strip.style.borderBottomWidth = 1;
      strip.style.borderBottomColor = new Color(0.30f, 0.30f, 0.30f, 1f);

      _perfTabButton = BuildTab("Perf", Tab.Perf);
      _activityTabButton = BuildTab("Activity", Tab.Activity);
      _testTabButton = BuildTab("Test", Tab.Test);
      strip.Add(_perfTabButton);
      strip.Add(_activityTabButton);
      strip.Add(_testTabButton);

      _root!.Add(strip);
      UpdateTabStyles();
    }

    private Label BuildTab(string text, Tab tab) {
      var label = new Label(text) { name = $"Tab_{tab}" };
      label.style.color = Color.white;
      label.style.paddingLeft = 14;
      label.style.paddingRight = 14;
      label.style.paddingTop = 4;
      label.style.paddingBottom = 4;
      label.style.borderRightWidth = 1;
      label.style.borderRightColor = new Color(0.20f, 0.20f, 0.20f, 1f);
      label.style.unityFontStyleAndWeight = FontStyle.Bold;
      label.RegisterCallback<PointerDownEvent>(ev => {
        SelectTab(tab);
        ev.StopPropagation();
      });
      return label;
    }

    private void SelectTab(Tab tab) {
      if (_currentTab == tab) return;
      _currentTab = tab;
      UpdateTabStyles();
      // Force immediate repaint so the user sees the tab swap on the
      // next frame, not after the next 2 Hz tick.
      _framesSinceRefresh = RefreshFrames;
    }

    private void UpdateTabStyles() {
      var active = new Color(0.30f, 0.30f, 0.30f, 1f);
      var inactive = new Color(0.15f, 0.15f, 0.15f, 1f);
      if (_perfTabButton != null) {
        _perfTabButton.style.backgroundColor =
            _currentTab == Tab.Perf ? active : inactive;
      }
      if (_activityTabButton != null) {
        _activityTabButton.style.backgroundColor =
            _currentTab == Tab.Activity ? active : inactive;
      }
      if (_testTabButton != null) {
        _testTabButton.style.backgroundColor =
            _currentTab == Tab.Test ? active : inactive;
      }
      // Show / hide the two parallel content roots based on the active
      // tab. Perf and Activity share the text-rendering ScrollView (both
      // are pull-model text); Test has its own root with a button and a
      // result label that's only updated on button click.
      var showTest = _currentTab == Tab.Test;
      if (_perfActivityScroll != null) {
        _perfActivityScroll.style.display =
            showTest ? DisplayStyle.None : DisplayStyle.Flex;
      }
      if (_testRoot != null) {
        _testRoot.style.display =
            showTest ? DisplayStyle.Flex : DisplayStyle.None;
      }
    }

    /// <summary>Preferred OS monospace font names, in priority order.
    /// Windows ships Consolas + Courier New; the rest cover common
    /// Linux / macOS installs. <see cref="ResolveMonoFont"/> picks the
    /// first that is actually installed.</summary>
    private static readonly string[] MonoFontCandidates =
        { "Consolas", "Courier New", "Liberation Mono", "DejaVu Sans Mono", "Menlo" };

    /// <summary>Resolve an installed OS monospace font, or
    /// <c>null</c> if none of <see cref="MonoFontCandidates"/> is
    /// present.
    /// <para><b>Why the installed-name filter.</b> Passing a missing
    /// font name to <see cref="Font.CreateDynamicFontFromOSFont(string,int)"/>
    /// does not return <c>null</c> — it returns a non-null Font that
    /// resolves to nothing under UI Toolkit's SDF text path, so the
    /// label renders <i>blank</i> (no glyphs at all), not just in a
    /// variable-width fallback. That's exactly what happened on a
    /// machine without Consolas: an empty perf window. Querying
    /// <see cref="Font.GetOSInstalledFontNames"/> first guarantees we
    /// only ever hand <c>CreateDynamicFontFromOSFont</c> a name we know
    /// exists; when none match we return <c>null</c> and the caller
    /// leaves the label's default (rendering, variable-width) font in
    /// place — degraded alignment beats invisible text.</para></summary>
    private static Font? ResolveMonoFont() {
      var installed = new HashSet<string>(
          Font.GetOSInstalledFontNames(), System.StringComparer.OrdinalIgnoreCase);
      foreach (var name in MonoFontCandidates) {
        if (installed.Contains(name)) {
          return Font.CreateDynamicFontFromOSFont(name, 12);
        }
      }
      return null;
    }

    private void BuildContent() {
      // Pull a true monospace font from the OS so PadLeft / PadRight
      // produces visually aligned columns. Shared across the
      // perf/activity scroll and the test result label. Only names
      // confirmed installed are used (see ResolveMonoFont) — handing
      // CreateDynamicFontFromOSFont a missing name yields a font that
      // renders blank under SDF, which is worse than the variable-
      // width fallback we get by leaving the default font alone.
      var monoFont = ResolveMonoFont();
      if (monoFont == null) {
        KeystoneLog.Error(
            "[Keystone] PerfWindow: no monospace OS font installed; "
            + "columns in the perf panel will render in a variable-"
            + "width fallback. Tried: " + string.Join(", ", MonoFontCandidates) + ".");
      }

      _perfActivityScroll =
          new ScrollView(ScrollViewMode.VerticalAndHorizontal) { name = "PerfActivityScroll" };
      _perfActivityScroll.style.flexGrow = 1;
      _perfActivityScroll.style.paddingLeft = 8;
      _perfActivityScroll.style.paddingRight = 8;
      _perfActivityScroll.style.paddingTop = 4;
      _perfActivityScroll.style.paddingBottom = 8;

      _content = new Label("(no samples yet)") { name = "Content" };
      _content.style.color = Color.white;
      _content.style.whiteSpace = WhiteSpace.Pre;
      _content.style.fontSize = 12;
      // Rich text enabled so the perf tab can colour-tag values that
      // are over the player-impact thresholds (headline ms/sec,
      // per-scope ms/sec, P99). The activity tab uses no markup so
      // flipping this on doesn't affect its rendering. Test tab has
      // its own Label (_testResultLabel) which keeps rich text off
      // because its content is verbatim test output that may include
      // angle brackets in exception messages.
      _content.enableRichText = true;
      ApplyMonoFont(_content, monoFont);
      _perfActivityScroll.Add(_content);
      _root!.Add(_perfActivityScroll);

      BuildTestTab(monoFont);
    }

    /// <summary>Build the Test tab content: a Run button at the top,
    /// a summary line under it, and a scrollable result label below.
    /// Hidden by default; <see cref="UpdateTabStyles"/> shows it when
    /// <see cref="Tab.Test"/> is the active tab.</summary>
    private void BuildTestTab(Font? monoFont) {
      _testRoot = new VisualElement { name = "TestRoot" };
      _testRoot.style.flexGrow = 1;
      _testRoot.style.flexDirection = FlexDirection.Column;
      _testRoot.style.paddingLeft = 8;
      _testRoot.style.paddingRight = 8;
      _testRoot.style.paddingTop = 4;
      _testRoot.style.paddingBottom = 8;
      _testRoot.style.display = DisplayStyle.None;

      var runButton = new Button(RunSelfTestsAndRender) {
        name = "TestRunButton",
        text = "Run integration tests",
      };
      runButton.style.alignSelf = Align.FlexStart;
      _testRoot.Add(runButton);

      _testSummaryLabel = new Label("(not run yet — click Run integration tests)") { name = "TestSummary" };
      _testSummaryLabel.style.color = new Color(0.75f, 0.75f, 0.75f, 1f);
      _testSummaryLabel.style.marginTop = 6;
      _testSummaryLabel.style.marginBottom = 6;
      _testSummaryLabel.style.fontSize = 12;
      _testRoot.Add(_testSummaryLabel);

      var resultScroll =
          new ScrollView(ScrollViewMode.VerticalAndHorizontal) { name = "TestResultScroll" };
      resultScroll.style.flexGrow = 1;

      _testResultLabel = new Label("") { name = "TestResult" };
      _testResultLabel.style.color = Color.white;
      _testResultLabel.style.whiteSpace = WhiteSpace.Pre;
      _testResultLabel.style.fontSize = 12;
      _testResultLabel.enableRichText = false;
      ApplyMonoFont(_testResultLabel, monoFont);
      resultScroll.Add(_testResultLabel);
      _testRoot.Add(resultScroll);

      _root!.Add(_testRoot);
    }

    /// <summary>Run every bound self-test, render the report into
    /// <see cref="_testResultLabel"/>, and update the summary line.
    /// Invoked synchronously on button click — the developer
    /// expects the report immediately. A long-running test would block
    /// the UI thread for its duration; today's tests are all
    /// millisecond-scale so this is fine.</summary>
    private void RunSelfTestsAndRender() {
      if (_testResultLabel == null || _testSummaryLabel == null) return;
      var report = _selfTestRunner.RunAll();

      var sb = new StringBuilder();
      string? currentCategory = null;
      foreach (var (test, result) in report.Rows) {
        if (test.Category != currentCategory) {
          if (currentCategory != null) sb.AppendLine();
          sb.Append("[").Append(test.Category).AppendLine("]");
          currentCategory = test.Category;
        }
        sb.Append("  ").Append(TagFor(result.Status))
          .Append(" ").Append(test.Name);
        if (!string.IsNullOrEmpty(result.Message)) {
          sb.Append(" — ").Append(result.Message);
        }
        sb.AppendLine();
        if (!string.IsNullOrEmpty(result.Detail)) {
          // Indent every detail line two spaces past the row.
          foreach (var line in result.Detail.Split('\n')) {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            sb.Append("        ").AppendLine(trimmed);
          }
        }
      }
      _testResultLabel.text = sb.Length > 0 ? sb.ToString().TrimEnd('\n', '\r') : "(no tests bound)";

      _testSummaryLabel.text =
          $"{report.PassCount} pass, {report.FailCount} fail, "
          + $"{report.WarningCount} warn, {report.SkippedCount} skipped"
          + $"   ({report.Rows.Count} test(s) total)";
      // Colour priority: fail > warning > pass. Warnings get a
      // distinct yellow so they don't blend with either side.
      _testSummaryLabel.style.color =
          report.FailCount > 0    ? new Color(1f,    0.55f, 0.55f, 1f) :
          report.WarningCount > 0 ? new Color(0.95f, 0.80f, 0.35f, 1f) :
                                    new Color(0.65f, 0.90f, 0.65f, 1f);
    }

    private static string TagFor(SelfTestStatus status) => status switch {
      SelfTestStatus.Pass    => "[PASS]",
      SelfTestStatus.Fail    => "[FAIL]",
      SelfTestStatus.Warning => "[WARN]",
      SelfTestStatus.Skipped => "[SKIP]",
      _                      => "[????]",
    };

    #endregion

    #region IUpdatableSingleton

    /// <inheritdoc />
    public void UpdateSingleton() {
      var keyboard = Keyboard.current;
      if (keyboard != null
          && keyboard.kKey.wasPressedThisFrame
          && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed)
          && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed)) {
        SetVisible(!IsVisible);
      }

      if (!IsVisible) return;

      // Revert the "Copied!" / "Cleared!" flashes once their frame
      // budget expires. Runs every frame (ahead of the 2 Hz render
      // gate) so the confirmation clears promptly regardless of the
      // active tab's refresh cadence.
      if (_copyFlashFramesLeft > 0 && --_copyFlashFramesLeft == 0 && _copyButton != null) {
        _copyButton.text = CopyLabel;
      }
      if (_clearFlashFramesLeft > 0 && --_clearFlashFramesLeft == 0 && _clearButton != null) {
        _clearButton.text = ClearLabel;
      }

      if (++_framesSinceRefresh < RefreshFrames) return;
      _framesSinceRefresh = 0;
      Render();
    }

    #endregion

    #region Visibility

    private bool IsVisible =>
        _root != null && _root.resolvedStyle.display == DisplayStyle.Flex;

    private void SetVisible(bool visible) {
      if (_root == null) return;
      _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
      if (visible) {
        _framesSinceRefresh = RefreshFrames;  // force immediate paint
        // Anchor the activity-section per-day-rate baseline at the
        // moment the window becomes visible. Hidden windows don't
        // hold a reference, so re-opening shows fresh "since open"
        // deltas rather than accumulating across the session.
        _activityReference = _activityRecorder.TakeSnapshot();
      } else {
        _activityReference = null;
      }
    }

    #endregion

    #region Clipboard

    /// <summary>Copy the active tab's rendered text to the system
    /// clipboard and flash the header button. Uses
    /// <see cref="GUIUtility.systemCopyBuffer"/> — Unity's
    /// cross-platform clipboard, the same one the engine's own copy
    /// fields use — so the text lands in the OS paste buffer directly,
    /// no <c>TextField</c> select-all dance required.</summary>
    private void CopyCurrentTabToClipboard() {
      GUIUtility.systemCopyBuffer = CurrentTabText();
      if (_copyButton != null) {
        _copyButton.text = CopiedLabel;
        _copyFlashFramesLeft = CopyFlashFrames;
      }
    }

    /// <summary>The text currently shown on the active tab. Perf and
    /// Activity share <see cref="_content"/>; Test concatenates its
    /// summary line and result body so a pasted report carries the
    /// pass/fail headline along with the detail.</summary>
    private string CurrentTabText() {
      if (_currentTab == Tab.Test) {
        var summary = _testSummaryLabel?.text ?? string.Empty;
        var result = _testResultLabel?.text ?? string.Empty;
        return summary.Length > 0 ? summary + "\n\n" + result : result;
      }
      return _content?.text ?? string.Empty;
    }

    #endregion

    #region Clear

    /// <summary>Re-baseline the rolling tables: drop all per-scope
    /// stats, counters, and latest-value rows via
    /// <see cref="PerfTracker.Clear"/>, reset the Activity tab's
    /// "since open" reference so its per-day rates restart from now, and
    /// flash the header button. Lets the developer wipe the cold
    /// load/prewarm samples once the game has settled so steady-state
    /// avg/P99/max stop being skewed by the first-tick spikes. The
    /// Startup costs (one-shot) section is intentionally preserved (see
    /// <see cref="PerfTracker.Clear"/>). Forces an immediate repaint so
    /// the cleared tables show on the next frame, not after the next
    /// 2 Hz tick.</summary>
    private void ClearStats() {
      _tracker.Clear();
      _activityReference = _activityRecorder.TakeSnapshot();
      if (_clearButton != null) {
        _clearButton.text = ClearedLabel;
        _clearFlashFramesLeft = CopyFlashFrames;
      }
      _framesSinceRefresh = RefreshFrames;
    }

    #endregion

    #region Drag

    private void OnHeaderPointerDown(PointerDownEvent ev) {
      if (ev.button != 0 || _root == null || _header == null) return;
      _dragging = true;
      // Capture in window/panel space. We translate by adjusting the
      // root's style.left/top relative to the canvas.
      _grabOffset = new Vector2(
          ev.position.x - _root.resolvedStyle.left,
          ev.position.y - _root.resolvedStyle.top);
      _header.CapturePointer(ev.pointerId);
      ev.StopPropagation();
    }

    private void OnHeaderPointerMove(PointerMoveEvent ev) {
      if (!_dragging || _root == null) return;
      _root.style.left = ev.position.x - _grabOffset.x;
      _root.style.top = ev.position.y - _grabOffset.y;
      ev.StopPropagation();
    }

    private void OnHeaderPointerUp(PointerUpEvent ev) {
      if (!_dragging || _header == null) return;
      _dragging = false;
      _header.ReleasePointer(ev.pointerId);
      ev.StopPropagation();
    }

    #endregion

    #region Render

    private void Render() {
      // Test tab is event-driven (button click populates the label),
      // not refresh-driven. Skip the per-tick render so we don't
      // overwrite the report while the developer is reading it.
      if (_currentTab == Tab.Test) return;
      if (_content == null) return;
      _buffer.Clear();
      switch (_currentTab) {
        case Tab.Perf:
          RenderPerfTab();
          break;
        case Tab.Activity:
          AppendActivitySection(_buffer);
          break;
      }
      if (_buffer.Length > 0 && _buffer[_buffer.Length - 1] == '\n') {
        _buffer.Length--;
      }
      _content.text = _buffer.ToString();
    }

    private void RenderPerfTab() {
      var snapshot = _tracker.Snapshot;
      var oneShots = _tracker.OneShots;
      var latestOrder = _tracker.LatestValueOrder;
      var latestValues = _tracker.LatestValues;
      if (snapshot.Count == 0 && oneShots.Count == 0 && latestOrder.Count == 0) {
        _buffer.AppendLine("(no perf samples yet)");
        return;
      }

      // Startup section first: one-shot costs measured during load /
      // post-load. Rendered as a plain "label: N ms" list -- avg /
      // P99 / Hz columns don't apply to single samples.
      if (oneShots.Count > 0) {
        var labelWidth = "Startup".Length;
        foreach (var entry in oneShots) {
          if (entry.Key.Length > labelWidth) labelWidth = entry.Key.Length;
        }
        _buffer.AppendLine("Startup costs (one-shot, post-load):");
        foreach (var entry in oneShots) {
          _buffer.Append("  ")
                 .Append(entry.Key.PadRight(labelWidth))
                 .Append("  ")
                 .Append(entry.Value.ToString("F0", CultureInfo.InvariantCulture).PadLeft(6))
                 .AppendLine(" ms");
        }
        _buffer.AppendLine();
      }

      // Latest-value section: periodic per-cycle totals whose most
      // recent observation is what matters (overwrite-on-call, unlike
      // OneShots which appends). Stable first-seen order keeps rows
      // from jumping as values update.
      if (latestOrder.Count > 0) {
        var labelWidth = "Latest".Length;
        for (var i = 0; i < latestOrder.Count; i++) {
          if (latestOrder[i].Length > labelWidth) labelWidth = latestOrder[i].Length;
        }
        _buffer.AppendLine("Per-cycle totals (latest observed):");
        for (var i = 0; i < latestOrder.Count; i++) {
          var label = latestOrder[i];
          if (!latestValues.TryGetValue(label, out var v)) continue;
          _buffer.Append("  ")
                 .Append(label.PadRight(labelWidth))
                 .Append("  ")
                 .Append(v.ToString("F1", CultureInfo.InvariantCulture).PadLeft(7))
                 .AppendLine(" ms");
        }
        _buffer.AppendLine();
      }

      if (snapshot.Count == 0) {
        return;
      }

      var names = new List<string>(snapshot.Keys);
      names.Sort(System.StringComparer.Ordinal);

      // Headline: per-tick cost juxtaposition. Engine.TickWork
      // (recorded by EngineTickProbe from vanilla
      // Ticker.LengthOfLastTickInSeconds) gives total sim work the
      // engine did per sim tick, which includes Keystone's
      // tickables — so subtracting Keystone's ms gives a rough
      // vanilla-only baseline to compare our share against.
      //
      // Keystone ms = main-thread-only timer scopes. Excludes
      // Engine.TickWork (double-count), counter scopes (unit counts,
      // not ms — summing them here used to silently inflate this
      // total), and .Parallel scopes (background threads, not
      // main-thread cost).
      var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
      var keystoneMsInWindow = 0.0;
      foreach (var name in names) {
        var s = snapshot[name];
        if (s.Kind == Keystone.Core.Diagnostics.PerfStatsKind.Counter) continue;
        if (IsParallelRow(name)) continue;
        if (name == EngineTickProbe.ScopeName) continue;
        keystoneMsInWindow += s.SumInWindow(nowTicks, WindowSeconds);
      }
      var engineMsInWindow = snapshot.TryGetValue(EngineTickProbe.ScopeName, out var engineStats)
          ? engineStats.SumInWindow(nowTicks, WindowSeconds)
          : 0.0;
      var gameTicksInWindow = _ticks.CountInWindow(nowTicks, WindowSeconds);
      var gameHoursInWindow = (double)_dayNightCycle.TicksToHours(gameTicksInWindow);

      _buffer.Append("Last ")
             .Append(WindowSeconds.ToString("F0", CultureInfo.InvariantCulture))
             .AppendLine("s:");
      if (gameTicksInWindow == 0) {
        _buffer.AppendLine("  (paused — no game ticks in window)");
      } else {
        var keystoneMsPerTick = keystoneMsInWindow / gameTicksInWindow;
        var engineMsPerTick = engineMsInWindow / gameTicksInWindow;
        _buffer.Append("  Per game-tick:   Keystone ")
               .Append(keystoneMsPerTick.ToString("F3", CultureInfo.InvariantCulture).PadLeft(7))
               .Append(" ms     Engine ")
               .Append(engineMsPerTick.ToString("F3", CultureInfo.InvariantCulture).PadLeft(7))
               .Append(" ms");
        if (engineMsInWindow > 0.0) {
          var share = 100.0 * keystoneMsInWindow / engineMsInWindow;
          _buffer.Append("   (Keystone is ")
                 .Append(share.ToString("F1", CultureInfo.InvariantCulture))
                 .Append("% of Engine)");
        }
        _buffer.AppendLine();
      }
      if (gameHoursInWindow > 0.0) {
        var keystoneMsPerHour = keystoneMsInWindow / gameHoursInWindow;
        var engineMsPerHour = engineMsInWindow / gameHoursInWindow;
        _buffer.Append("  Per game-hour:   Keystone ")
               .Append(keystoneMsPerHour.ToString("F1", CultureInfo.InvariantCulture).PadLeft(7))
               .Append(" ms     Engine ")
               .Append(engineMsPerHour.ToString("F1", CultureInfo.InvariantCulture).PadLeft(7))
               .Append(" ms   (over ")
               .Append(gameHoursInWindow.ToString("F2", CultureInfo.InvariantCulture))
               .AppendLine(" game-hours)");
      }
      _buffer.Append("  Wall-clock:      Keystone ")
             .Append((keystoneMsInWindow / WindowSeconds).ToString("F2", CultureInfo.InvariantCulture).PadLeft(7))
             .Append(" ms/sec  Engine ")
             .Append((engineMsInWindow / WindowSeconds).ToString("F2", CultureInfo.InvariantCulture).PadLeft(7))
             .AppendLine(" ms/sec");

      // Partition scopes into timer rows (durations in ms, the normal
      // case) and counter rows (unit counts recorded via
      // PerfTracker.RecordCount rather than .Track/.Record). Rendering
      // both in one table with ms-labeled columns misled readers into
      // thinking a count of 250 meant "250 ms per tick" when it
      // actually meant "250 chunks drained per tick"; the counter rows
      // get their own table with count-labeled columns. Classification
      // is by the stats' Kind flag (set at the record call site), not
      // by a name suffix — so any RecordCount scope lands here without
      // needing a magic name.
      var timerNames = new List<string>(names.Count);
      var parallelNames = new List<string>();
      var counterNames = new List<string>();
      foreach (var name in names) {
        if (snapshot[name].Kind == Keystone.Core.Diagnostics.PerfStatsKind.Counter) {
          counterNames.Add(name);
        } else if (IsParallelRow(name)) {
          parallelNames.Add(name);
        } else {
          timerNames.Add(name);
        }
      }

      // Column width is based on display names (last segment +
      // dot-depth indent), not the full dotted scope strings.
      var nameColWidth = "Scope".Length;
      foreach (var name in names) {
        var disp = DisplayName(name);
        var displayLen = disp.Length > MaxScopeWidth ? MaxScopeWidth : disp.Length;
        if (displayLen > nameColWidth) nameColWidth = displayLen;
      }

      // Main-thread rows split into two tables by firing cadence:
      //   - Per-tick: fires (nearly) every sim tick. These are the
      //     sustained-frame-cost suspects -- if avg*Hz is high they're
      //     eating budget every tick.
      //   - Sporadic: fires at cycle boundaries / one-shot bookkeeping.
      //     Spike sources -- max matters more than avg.
      // The cutoff is a FRACTION of the live sim-tick rate, not an
      // absolute Hz: Timberborn ticks at ~5Hz at 1x, so the old fixed
      // 10Hz threshold dumped every per-tick scope into 'sporadic' below
      // ~2x speed. gameTicksInWindow / WindowSeconds is the measured tick
      // rate (GameTickCounter counts sim ticks); a scope clears the bar
      // when it fires at >= PerTickCadenceFraction of that. Falls back to
      // the absolute threshold only when paused (no ticks in window).
      if (timerNames.Count > 0) {
        var tickRateHz = gameTicksInWindow > 0
            ? gameTicksInWindow / WindowSeconds
            : 0.0;
        var perTickCutoffHz = tickRateHz > 0.0
            ? tickRateHz * PerTickCadenceFraction
            : PerTickHzThreshold;

        // timerNames is already sorted alphabetically (from the
        // snapshot.Keys sort above). Partition preserves that order
        // within each group so rows stay in stable positions as
        // values churn -- easier to scan when comparing two windows.
        var perTick = new List<string>();
        var sporadic = new List<string>();
        foreach (var name in timerNames) {
          if (snapshot[name].FrequencyHz >= perTickCutoffHz) perTick.Add(name);
          else sporadic.Add(name);
        }
        // Pin Engine.TickWork to the top of whichever group it lands
        // in. It's the engine-baseline reference everything else
        // gets compared against (Keystone scope ms / Engine ms in the
        // headline), so it deserves the position above alphabetical
        // order. Hoist by name; no-op if absent.
        PinToFront(perTick, EngineTickProbe.ScopeName);
        PinToFront(sporadic, EngineTickProbe.ScopeName);

        if (perTick.Count > 0) {
          _buffer.AppendLine("Main thread (per-tick):");
          AppendTimerHeader(nameColWidth);
          foreach (var name in perTick) {
            AppendTimerRow(name, snapshot[name], nowTicks, gameHoursInWindow, nameColWidth);
          }
        }

        if (sporadic.Count > 0) {
          if (perTick.Count > 0) _buffer.AppendLine();
          _buffer.AppendLine("Main thread (sporadic):");
          AppendTimerHeader(nameColWidth);
          foreach (var name in sporadic) {
            AppendTimerRow(name, snapshot[name], nowTicks, gameHoursInWindow, nameColWidth);
          }
        }
      }

      if (parallelNames.Count > 0) {
        _buffer.AppendLine();
        _buffer.AppendLine(
            "Background threads (wall-clock dispatch-to-join; not included in main-thread cost):");
        _buffer.Append("Scope".PadRight(nameColWidth))
               .Append("  ").Append("n".PadLeft(NColWidth))
               .Append("  ").Append("avg".PadLeft(MsColWidth))
               .Append("  ").Append("P99".PadLeft(MsColWidth))
               .Append("  ").Append("max".PadLeft(MsColWidth))
               .Append("  ").Append("Hz".PadLeft(HzColWidth))
               .AppendLine();

        foreach (var name in parallelNames) {
          var s = snapshot[name];
          _buffer.Append(Truncate(DisplayName(name), MaxScopeWidth).PadRight(nameColWidth))
                 .Append("  ")
                 .Append(s.SampleCount.ToString().PadLeft(NColWidth))
                 .Append("  ")
                 .Append(s.Average.ToString("F2", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
                 .Append("  ")
                 .Append(s.P99.ToString("F2", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
                 .Append("  ")
                 .Append(s.Max.ToString("F2", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
                 .Append("  ")
                 .Append(FormatHz(s.FrequencyHz).PadLeft(HzColWidth))
                 .AppendLine();
        }
      }

      if (counterNames.Count > 0) {
        _buffer.AppendLine();
        _buffer.AppendLine(
            "Per-tick counters (avg/P99/max are unit counts per recorded tick, not ms):");
        _buffer.Append("Scope".PadRight(nameColWidth))
               .Append("  ").Append("n".PadLeft(NColWidth))
               .Append("  ").Append("avg".PadLeft(MsColWidth))
               .Append("  ").Append("P99".PadLeft(MsColWidth))
               .Append("  ").Append("max".PadLeft(MsColWidth))
               .Append("  ").Append("Hz".PadLeft(HzColWidth))
               .AppendLine();

        foreach (var name in counterNames) {
          var s = snapshot[name];
          _buffer.Append(Truncate(DisplayName(name), MaxScopeWidth).PadRight(nameColWidth))
                 .Append("  ")
                 .Append(s.SampleCount.ToString().PadLeft(NColWidth))
                 .Append("  ")
                 .Append(s.Average.ToString("F0", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
                 .Append("  ")
                 .Append(s.P99.ToString("F0", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
                 .Append("  ")
                 .Append(s.Max.ToString("F0", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
                 .Append("  ")
                 .Append(FormatHz(s.FrequencyHz).PadLeft(HzColWidth))
                 .AppendLine();
        }
      }

      AppendPerfFooterTip();
    }

    /// <summary>Build the short, indented display string for a
    /// dotted scope name.
    /// <list type="bullet">
    /// <item><b>Depth 0 / 1 (top-level rows)</b> — show the full
    ///       scope name verbatim. The namespace prefix
    ///       (<c>Engine.</c>, <c>ChunkRulesApplier.</c>) is the row's
    ///       identity at the top of its tree, so dropping it would
    ///       leave nameless siblings like a bare <c>"Tick"</c> with no
    ///       indication which subsystem it belongs to.</item>
    /// <item><b>Depth 2+ (children)</b> — show only the last segment,
    ///       indented one level per dot beyond the top. The parent
    ///       row supplies the namespace context, so the prefix is
    ///       redundant on each child.</item>
    /// <item><b><c>.Units</c> counter rows</b> — strip the suffix
    ///       before indenting / labeling so each counter visually
    ///       sits at the same depth as the timer it counts. (Counter
    ///       classification itself is now by <c>PerfStatsKind</c>, not
    ///       this suffix; the <c>.Units</c> tag survives only as a
    ///       display hint for the rows that still carry it, so they
    ///       indent under their sibling timer.)</item>
    /// </list>
    /// Examples:
    /// <list type="bullet">
    /// <item><c>"Engine.TickWork"</c> → <c>"Engine.TickWork"</c></item>
    /// <item><c>"ChunkRulesApplier.Tick"</c> → <c>"ChunkRulesApplier.Tick"</c></item>
    /// <item><c>"ChunkRulesApplier.Tick.HandlerDispatch"</c> → <c>"  HandlerDispatch"</c></item>
    /// <item><c>"ChunkRulesApplier.Tick.HandlerDispatch.Units"</c> → <c>"  HandlerDispatch"</c></item>
    /// <item><c>"...HandlerDispatch.ClassBSpawnHandler.Gate"</c> → <c>"      Gate"</c></item>
    /// </list></summary>
    private static string DisplayName(string scopeName) {
      var effective = scopeName;
      if (effective.EndsWith(".Units")) {
        effective = effective.Substring(0, effective.Length - ".Units".Length);
        if (effective.Length == 0) return scopeName;
      }
      var dotCount = 0;
      for (var i = 0; i < effective.Length; i++) {
        if (effective[i] == '.') dotCount++;
      }
      // Depth 0 (no namespace) or 1 (top-level under a single
      // namespace prefix): show the full name. The prefix IS the row
      // identity at the top of its tree.
      if (dotCount <= 1) return effective;
      var lastDot = effective.LastIndexOf('.');
      var last = effective.Substring(lastDot + 1);
      return new string(' ', (dotCount - 1) * 2) + last;
    }

    /// <summary>Move <paramref name="name"/> to index 0 of
    /// <paramref name="list"/> if present. No-op otherwise.</summary>
    private static void PinToFront(List<string> list, string name) {
      var idx = list.IndexOf(name);
      if (idx <= 0) return;
      list.RemoveAt(idx);
      list.Insert(0, name);
    }

    /// <summary>Apply a monospace font to <paramref name="label"/>'s
    /// text rendering. Sets both the legacy IMGUI font property
    /// (<see cref="IStyle.unityFont"/>) and the modern UI Toolkit
    /// SDF font definition (<see cref="IStyle.unityFontDefinition"/>).
    /// <para>The SDF path is the one UI Toolkit actually uses for
    /// <see cref="Label"/>; setting only <c>unityFont</c> is a no-op
    /// on SDF-rendered text and leaves the variable-width default in
    /// place — that's why columns looked jumbled before this fix.
    /// Both writes are no-ops when <paramref name="font"/> is null
    /// (couldn't load any OS monospace).</para></summary>
    private static void ApplyMonoFont(Label label, Font? font) {
      if (font == null) return;
      label.style.unityFont = font;
      label.style.unityFontDefinition = new StyleFontDefinition(font);
    }

    private void AppendTimerHeader(int nameColWidth) {
      _buffer.Append("Scope".PadRight(nameColWidth))
             .Append("  ").Append("n".PadLeft(NColWidth))
             .Append("  ").Append("avg".PadLeft(MsColWidth))
             .Append("  ").Append("P99".PadLeft(MsColWidth))
             .Append("  ").Append("max".PadLeft(MsColWidth))
             .Append("  ").Append("Hz".PadLeft(HzColWidth))
             .Append("  ").Append("ms/h".PadLeft(MsColWidth))
             .AppendLine();
    }

    private void AppendTimerRow(
        string name, Keystone.Core.Diagnostics.PerfStats s, long nowTicks,
        double gameHoursInWindow, int nameColWidth) {
      // Per-scope cost in ms / in-game-hour: scope's ms in the window
      // divided by game-hours in the same window. Same game-speed-
      // stable property as the headline. When paused (no game hours
      // elapsed) render an em-dash.
      var scopeMsInWindow = s.SumInWindow(nowTicks, WindowSeconds);
      var msPerHourCell = gameHoursInWindow <= 0.0
          ? "—".PadLeft(MsColWidth)
          : (scopeMsInWindow / gameHoursInWindow).ToString("F1", CultureInfo.InvariantCulture).PadLeft(MsColWidth);

      _buffer.Append(Truncate(DisplayName(name), MaxScopeWidth).PadRight(nameColWidth))
             .Append("  ")
             .Append(s.SampleCount.ToString().PadLeft(NColWidth))
             .Append("  ")
             .Append(s.Average.ToString("F2", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
             .Append("  ")
             .Append(s.P99.ToString("F2", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
             .Append("  ")
             .Append(s.Max.ToString("F2", CultureInfo.InvariantCulture).PadLeft(MsColWidth))
             .Append("  ")
             .Append(FormatHz(s.FrequencyHz).PadLeft(HzColWidth))
             .Append("  ")
             .Append(msPerHourCell)
             .AppendLine();
    }

    /// <summary>Plain-English tip at the bottom of the perf tab
    /// pointing the player at the Mod Settings throttles. Kept short
    /// (two lines max) so it doesn't crowd the data above; the
    /// settings names are written so a player who doesn't know the
    /// codebase can find them.
    ///
    /// <para>Names line up with the actual setting headers in
    /// <see cref="Keystone.Mod.Settings.KeystonePerformanceSettings"/>,
    /// <see cref="Keystone.Mod.Settings.KeystoneFaunaSettings"/>, and
    /// <see cref="Keystone.Mod.Settings.KeystoneEffectsSettings"/>. If
    /// any of those headers get renamed, update this string to
    /// match.</para></summary>
    private void AppendPerfFooterTip() {
      _buffer.AppendLine();
      _buffer.AppendLine(
          "Too slow? Settings → Mods → Keystone has throttles:");
      _buffer.AppendLine(
          "  • Performance → \"Map update frequency\": raise from 1 to 2–4 hours per cycle (main menu only).");
      _buffer.AppendLine(
          "  • Fauna → \"Enable fauna\" off, or lower the per-category abundance sliders.");
      _buffer.AppendLine(
          "  • Effects → lower atmospheric effects frequency.");
    }

    /// <summary>Append the activity section to <paramref name="buffer"/>:
    /// cumulative counters from <see cref="KeystoneActivityRecorder"/>,
    /// plus per-day rates relative to <see cref="_activityReference"/>
    /// (which is captured on window-open). Pull-model preserved — the
    /// recorder is only consulted while this method runs, and this
    /// method only runs when the window is visible (RefreshFrames gate
    /// in <see cref="UpdateSingleton"/>).</summary>
    private void AppendActivitySection(StringBuilder buffer) {
      var now = _activityRecorder.TakeSnapshot();
      buffer.AppendLine();
      buffer.AppendLine($"Activity (game-day {now.TotalDaysElapsed.ToString("F2", CultureInfo.InvariantCulture)}):");

      AppendMapUpdateCadenceLine(buffer);

      // Ticker cycle counts. The per-day rate is the load-bearing
      // number for verifying MapUpdateHours scaling — at MapUpdateHours=1
      // the ecology / biome / cluster tickers should show ~24/day; at
      // MapUpdateHours=4 they should show ~6/day.
      AppendActivityRow(buffer, "EcologyField cycles",
          now.EcologyFieldCycles, _activityReference?.EcologyFieldCycles, now, _activityReference);
      AppendActivityRow(buffer, "BiomeTicker cycles",
          now.BiomeTickerCycles, _activityReference?.BiomeTickerCycles, now, _activityReference);
      AppendActivityRow(buffer, "ClusterTicker cycles",
          now.ClusterTickerCycles, _activityReference?.ClusterTickerCycles, now, _activityReference);
      AppendActivityRow(buffer, "FaunaCycle cycles",
          now.FaunaCycleTickerCycles, _activityReference?.FaunaCycleTickerCycles, now, _activityReference);
      // Fixed 1-day cadence (not slider-driven), so this should read
      // ~1/day regardless of the map-update-frequency setting — a quick
      // confirmation the dead-flourish decay sweep is actually cycling.
      AppendActivityRow(buffer, "FlourishDecay cycles",
          now.FlourishDecayCycles, _activityReference?.FlourishDecayCycles, now, _activityReference);
      buffer.AppendLine();

      // Region churn collapsed to a single line — events are usually
      // sparse (a long quiet game has zero churn for hours), so four
      // dedicated rows is more noise than signal. Optional "since
      // opened" delta on the end when a reference snapshot exists.
      AppendRegionChurnLine(buffer, now, _activityReference);
      AppendClusterChurnLine(buffer, now, _activityReference);
      buffer.AppendLine();

      // Live state — instantaneous, no deltas. (Per-species fauna
      // counts below; this is just the grand totals for at-a-glance.)
      buffer.Append("  Live: ")
            .Append(now.LiveRegions).Append(" regions, ")
            .Append(now.LiveClusters).Append(" clusters, ")
            .Append(now.LiveFauna).AppendLine(" fauna");

      // Per-species fauna breakdown. The grand totals (live, spawned,
      // Take the despawn-reason rows once and reuse them across the
      // species table's Total row AND the histogram below — so the
      // "Total Despawned" cell matches the histogram sum by
      // construction. Reading them separately (and FaunaRemoved
      // from the snapshot) caused a frame-to-frame mismatch on
      // active maps as despawns landed between reads.
      var despawnRows = _activityRecorder.TakeDespawnReasonBreakdown();
      long despawnTotal = 0;
      for (var i = 0; i < despawnRows.Count; i++) despawnTotal += despawnRows[i].Count;

      buffer.AppendLine();
      AppendFaunaSpeciesTable(buffer, now, despawnTotal);

      // Temporary diagnostic: cumulative despawn-reason histogram.
      // Helps narrow down "why are fauna disappearing" without
      // grepping Player.log. Categories that haven't fired are
      // omitted to keep the section short on quiet maps.
      buffer.AppendLine();
      AppendDespawnReasonTable(buffer, despawnRows);

      // Per-biome maturity breakdown. Counts chunks with Maturity
      // strictly above each threshold — the threshold values are
      // chosen to align with biome ladder boundaries
      // (1 = Forest L1 floor, 2.5 = Grassland L2 floor, 5 = Wetland
      // L3, 10 = mid-level marker, 20 = late-level marker).
      buffer.AppendLine();
      AppendBiomeMaturityTable(buffer);
    }

    /// <summary>Render the "what cadence are we actually running at"
    /// line. Shows the slider value (what the player picked) alongside
    /// the value each of the three map-update tickers latched at
    /// construction. They should agree; if they don't, the persisted-
    /// settings hydration race documented on
    /// <see cref="KeystonePerformanceSettings.MapUpdateCycleDays"/> is
    /// hitting and the slider isn't actually driving the sweep.
    /// <para>Slider is reported in hours/cycle (its native unit); the
    /// ticker values are converted from <c>CycleDurationDays</c> back
    /// to hours so both sides of the comparison share units.</para></summary>
    private void AppendMapUpdateCadenceLine(StringBuilder buffer) {
      var sliderHours = _perfSettings.MapUpdateHours.Value;
      var ecologyHours = _ecologyFieldUpdater.CycleDurationDays * 24f;
      var biomeHours = _chunkBiomeTicker.CycleDurationDays * 24f;
      var clusterHours = _chunkClusterTicker.CycleDurationDays * 24f;
      // Tolerance: a 1-hour slider and a 1/24-day latched value
      // round-trip to exactly the same float, so 1e-3 h is plenty of
      // headroom against any future fractional setting.
      var matches =
          Mathf.Abs(ecologyHours - sliderHours) < 1e-3f
          && Mathf.Abs(biomeHours - sliderHours) < 1e-3f
          && Mathf.Abs(clusterHours - sliderHours) < 1e-3f;
      buffer.Append("  Map update cadence: slider=")
            .Append(sliderHours.ToString(CultureInfo.InvariantCulture))
            .Append(" h/cycle, active: ecology=")
            .Append(ecologyHours.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" h, biome=")
            .Append(biomeHours.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" h, cluster=")
            .Append(clusterHours.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" h");
      if (!matches) {
        buffer.Append("   [MISMATCH — slider value did not take effect; reload save]");
      }
      buffer.AppendLine();
      buffer.AppendLine();
    }

    private void AppendFaunaSpeciesTable(
        StringBuilder buffer, ActivitySnapshot now, long despawnTotal) {
      var rows = _activityRecorder.TakeFaunaSpeciesBreakdown();
      const int speciesWidth = 22;
      const int liveColWidth = 6;
      const int countColWidth = 10;

      buffer.AppendLine("  Fauna by species:");
      buffer.Append("  ").Append("Species".PadRight(speciesWidth))
            .Append("Live".PadLeft(liveColWidth))
            .Append("Spawned".PadLeft(countColWidth))
            .Append("Despawned".PadLeft(countColWidth))
            .AppendLine();

      // Grand-totals row. Despawned uses the despawn-reason sum
      // (single source of truth shared with the histogram below);
      // Live and Spawned read from the snapshot.
      buffer.Append("  ").Append("Total".PadRight(speciesWidth))
            .Append(now.LiveFauna.ToString(CultureInfo.InvariantCulture).PadLeft(liveColWidth))
            .Append(now.FaunaAdded.ToString(CultureInfo.InvariantCulture).PadLeft(countColWidth))
            .Append(despawnTotal.ToString(CultureInfo.InvariantCulture).PadLeft(countColWidth))
            .AppendLine();

      if (rows.Count == 0) {
        buffer.AppendLine("  (no per-species data yet)");
        return;
      }

      var sorted = new List<FaunaSpeciesRow>(rows);
      sorted.Sort((a, b) =>
          string.Compare(a.BlueprintName, b.BlueprintName, System.StringComparison.Ordinal));
      foreach (var row in sorted) {
        buffer.Append("  ").Append(row.BlueprintName.PadRight(speciesWidth))
              .Append(row.Live.ToString(CultureInfo.InvariantCulture).PadLeft(liveColWidth))
              .Append(row.CumulativeSpawned.ToString(CultureInfo.InvariantCulture).PadLeft(countColWidth))
              .Append(row.CumulativeDespawned.ToString(CultureInfo.InvariantCulture).PadLeft(countColWidth))
              .AppendLine();
      }
    }

    /// <summary>Single-line cluster-churn rollup. Rebuilds count is
    /// cumulative (the cluster ticker should fire once per game-hour
    /// at <c>MapUpdateHours=1</c>); created/destroyed cluster
    /// compositions use exact-chunkset identity, so a cluster that
    /// gains/loses one chunk contributes +1 to each. High values
    /// relative to the live cluster count mean clusters are thrashing
    /// — typically a sign that biome dominance or maturity floors are
    /// flipping per rebuild.</summary>
    private static void AppendClusterChurnLine(
        StringBuilder buffer, ActivitySnapshot now, ActivitySnapshot? reference) {
      buffer.Append("  Cluster churn:            ")
            .Append(now.ClusterRebuilds).Append(" rebuilds, ")
            .Append(now.ClustersCreated).Append(" created, ")
            .Append(now.ClustersDestroyed).Append(" destroyed");
      if (reference.HasValue) {
        var r = reference.Value;
        var deltaCreated = now.ClustersCreated - r.ClustersCreated;
        var deltaDestroyed = now.ClustersDestroyed - r.ClustersDestroyed;
        var deltaRebuilds = now.ClusterRebuilds - r.ClusterRebuilds;
        if (deltaCreated + deltaDestroyed + deltaRebuilds > 0) {
          buffer.Append("   (+").Append(deltaRebuilds).Append("/")
                .Append(deltaCreated).Append("/").Append(deltaDestroyed)
                .Append(" since open)");
        }
      }
      buffer.AppendLine();

      // Last-rebuild outcome breakdown + field shape version. Reads
      // the cluster ticker's just-completed rebuild: how many regions
      // were processed, how many silently skipped because their
      // field was null, how many skipped because the field had < 2
      // valid chunks. If a wholesale "all clusters destroyed" event
      // recurs, the panel will catch the rebuild where the included
      // count crashes and one of the skip counts jumps. FieldShapeVer
      // bumping at the same moment is the field-reallocation trigger
      // (entity-channel-count growth or per-region bbox change).
      buffer.Append("  Last rebuild:             ")
            .Append(now.LastRebuildRegionsIncluded).Append(" included, ")
            .Append(now.LastRebuildRegionsSkippedNoField).Append(" no-field, ")
            .Append(now.LastRebuildRegionsSkippedFewValidChunks).Append(" too-few-valid")
            .AppendLine();
      buffer.Append("  Field shape ver:          ")
            .Append(now.FieldShapeVersion);
      if (reference.HasValue) {
        var deltaShape = now.FieldShapeVersion - reference.Value.FieldShapeVersion;
        if (deltaShape > 0) {
          buffer.Append("   (+").Append(deltaShape).Append(" since open)");
        }
      }
      buffer.AppendLine();
    }

    /// <summary>Single-line region-churn rollup as a since-open
    /// delta. Reports only what's changed since the panel was opened,
    /// not cumulative totals — the cumulative <c>RegionsCreated</c>
    /// is bumped by N at map load (the initial <c>Index()</c> scan
    /// creates every region in one pass), so a cumulative display
    /// would read "42 created, 0 split, 0 merged, 0 removed" on a
    /// 42-region map even on a brand-new save, which is more
    /// confusing than informative. Delta from the open-time
    /// reference snapshot is honest: it shows only what's happened
    /// while the panel has been visible.</summary>
    private static void AppendRegionChurnLine(
        StringBuilder buffer, ActivitySnapshot now, ActivitySnapshot? reference) {
      if (!reference.HasValue) {
        // No reference yet — Render shouldn't run when the window is
        // hidden, but the panel can render once at PostLoad before
        // SetVisible captures the reference. Show a placeholder so
        // the line doesn't disappear and reflow the layout.
        buffer.AppendLine("  Region churn since open: (no reference yet)");
        return;
      }
      var r = reference.Value;
      var elapsed = now.TotalDaysElapsed - r.TotalDaysElapsed;
      buffer.Append("  Region churn since open: +")
            .Append(now.RegionsCreated - r.RegionsCreated).Append(" created, ")
            .Append(now.RegionSplits - r.RegionSplits).Append(" split, ")
            .Append(now.RegionMerges - r.RegionMerges).Append(" merged, ")
            .Append(now.RegionsRemoved - r.RegionsRemoved).Append(" removed");
      if (elapsed >= 0.01f) {
        buffer.Append("   (over ")
              .Append(elapsed.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" d)");
      }
      buffer.AppendLine();
    }

    /// <summary>Render the despawn-reason histogram — one row per
    /// reason category that has any cumulative count. Sorted by
    /// count descending so the most-fired category surfaces first;
    /// that's the diagnostic you want when you spot a despawn wave
    /// in the species table.</summary>
    private void AppendDespawnReasonTable(
        StringBuilder buffer, IReadOnlyList<FaunaDespawnReasonRow> rows) {
      if (rows.Count == 0) {
        buffer.AppendLine("  Despawn reasons: (none yet)");
        return;
      }

      var sorted = new List<FaunaDespawnReasonRow>(rows);
      sorted.Sort((a, b) => b.Count.CompareTo(a.Count));

      const int reasonWidth = 24;
      const int countWidth = 8;

      buffer.AppendLine("  Despawn reasons (cumulative):");
      buffer.Append("  ").Append("Count".PadLeft(countWidth))
            .Append("  ").Append("Reason").AppendLine();
      foreach (var row in sorted) {
        buffer.Append("  ")
              .Append(row.Count.ToString(CultureInfo.InvariantCulture).PadLeft(countWidth))
              .Append("  ").Append(row.Reason.ToString().PadRight(reasonWidth))
              .AppendLine();
      }
    }

    private void AppendBiomeMaturityTable(StringBuilder buffer) {
      var rows = _activityRecorder.TakeBiomeMaturityBreakdown();
      if (rows.Count == 0) {
        buffer.AppendLine("  (no biome Maturity entries yet)");
        return;
      }

      // Sort by biome name for stable rendering across refreshes —
      // the recorder returns rows in first-seen order which can flip
      // when chunks are added/removed.
      var sorted = new List<BiomeMaturityRow>(rows);
      sorted.Sort((a, b) =>
          string.Compare(a.Biome.ToString(), b.Biome.ToString(), System.StringComparison.Ordinal));

      // Layout: numeric columns first (variable-width font keeps
      // digit columns roughly aligned via PadLeft), biome name LAST
      // so its variable-width letters don't ratchet the count
      // columns out of alignment.
      const int countColWidth = 7;
      var thresholds = KeystoneActivityRecorder.MaturityThresholds;
      var binLabels = BuildBinLabels(thresholds);

      buffer.AppendLine("  Biome maturity (chunks with Maturity in range):");
      buffer.Append("  ");
      for (var i = 0; i < binLabels.Length; i++) {
        buffer.Append(binLabels[i].PadLeft(countColWidth));
      }
      buffer.Append("  Biome").AppendLine();

      foreach (var row in sorted) {
        buffer.Append("  ");
        for (var i = 0; i < thresholds.Count; i++) {
          var n = i < row.CountsInBin.Count ? row.CountsInBin[i] : 0;
          buffer.Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(countColWidth));
        }
        buffer.Append("  ").Append(row.Biome.ToString()).AppendLine();
      }
    }

    /// <summary>Build the column-header labels for the maturity
    /// table. Bin i covers <c>(thresholds[i], thresholds[i+1]]</c>;
    /// the last bin is open right (<c>&gt; thresholds[N-1]</c>).
    /// Returned strings use ASCII hyphen range notation
    /// (e.g. <c>"1-2.5"</c>) for the bounded bins and a trailing
    /// <c>"+"</c> for the open-right tail. ASCII rather than en-dash
    /// so a non-true-monospace fallback font still treats every
    /// header cell as the same width per character.</summary>
    private static string[] BuildBinLabels(IReadOnlyList<float> thresholds) {
      var labels = new string[thresholds.Count];
      for (var i = 0; i < thresholds.Count - 1; i++) {
        labels[i] = thresholds[i].ToString(CultureInfo.InvariantCulture)
            + "-"
            + thresholds[i + 1].ToString(CultureInfo.InvariantCulture);
      }
      labels[thresholds.Count - 1] =
          thresholds[thresholds.Count - 1].ToString(CultureInfo.InvariantCulture) + "+";
      return labels;
    }

    /// <summary>One row of the activity section: label, cumulative
    /// count, and (when a reference snapshot exists)
    /// "+Δ over T game-days = R/day" derived from
    /// <paramref name="reference"/>.</summary>
    private static void AppendActivityRow(
        StringBuilder buffer, string label, long current, long? referenceValue,
        ActivitySnapshot now, ActivitySnapshot? reference) {
      buffer.Append("  ")
            .Append(label.PadRight(22))
            .Append("  ")
            .Append(current.ToString(CultureInfo.InvariantCulture).PadLeft(8));
      if (referenceValue.HasValue && reference.HasValue) {
        var elapsed = now.TotalDaysElapsed - reference.Value.TotalDaysElapsed;
        var delta = current - referenceValue.Value;
        if (elapsed > 0.001f) {
          var rate = delta / elapsed;
          buffer.Append("   +").Append(delta)
                .Append(" over ").Append(elapsed.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" d  =  ").Append(rate.ToString("F1", CultureInfo.InvariantCulture))
                .Append("/day");
        }
      }
      buffer.AppendLine();
    }

    private static bool IsParallelRow(string name) =>
        name.EndsWith(ParallelRowSuffix, System.StringComparison.Ordinal);

    private static string Truncate(string value, int max) {
      if (value.Length <= max) return value;
      return value.Substring(0, max - 1) + "…";
    }

    /// <summary>Format a frequency for the Hz column. Values above
    /// <see cref="HzDisplayCap"/> render as "&gt;999" so a runaway Hz
    /// (see that constant) can't blow out the column width. Below the
    /// cap: one decimal place, matching the other rate cells.</summary>
    private static string FormatHz(double hz) =>
        hz > HzDisplayCap
            ? ">" + HzDisplayCap.ToString("F0", CultureInfo.InvariantCulture)
            : hz.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>Wrap <paramref name="padded"/> in a UIElements rich-text
    /// <c>&lt;color&gt;</c> tag when <paramref name="measure"/> hits a
    /// warning threshold. Padding must already be applied before
    /// calling, since the rich-text tag chars don't render but do
    /// count toward string length — wrap-then-pad would shorten the
    /// visible cell.
    /// <para>Below <paramref name="yellow"/>: returned unchanged (no
    /// tag = default text colour). At or above <paramref name="yellow"/>
    /// but below <paramref name="red"/>: yellow. At or above
    /// <paramref name="red"/>: red.</para></summary>
    private static string Colorize(string padded, double measure, double yellow, double red) {
      if (measure >= red) return "<color=" + RedHex + ">" + padded + "</color>";
      if (measure >= yellow) return "<color=" + YellowHex + ">" + padded + "</color>";
      return padded;
    }

    #endregion

  }

}
