using System.Collections.Generic;
using Timberborn.SingletonSystem;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Mod-wide aggregator for "Keystone caught a thing during load or
  /// runtime that the user should know about." Subsystems call
  /// <see cref="Record"/> whenever they log-and-continue past a
  /// failure they can isolate (a malformed third-party spec, a
  /// per-entity init that threw, a per-tile classification that
  /// skipped a BO, etc.); the
  /// <see cref="StartupChecks.IntegrationHealthCheck"/> reads the
  /// aggregated counts and surfaces them in the startup-report dialog
  /// the next time the player loads.
  ///
  /// <para><b>Why this exists.</b> Before this aggregator, every
  /// "skipped because malformed" or "isolated per-entity exception"
  /// path logged a <c>KeystoneLog.Warn</c> and then continued
  /// silently. The player saw a working-looking game with subtly
  /// missing behaviour; the only diagnostic surface was grep'ing
  /// <c>Player.log</c>. The startup-report dialog existed and was
  /// well-architected, but it only watched a hardcoded set of
  /// pre-identified failure modes (Harmony attach, catalog
  /// emptiness, save-decode counts) -- not the broad "Keystone had
  /// problems integrating with this mod stack" signal the original
  /// design intent called for.</para>
  ///
  /// <para><b>The bridge.</b> This singleton + the
  /// <see cref="StartupChecks.IntegrationHealthCheck"/> close that
  /// gap. Any new log-and-continue path can record into the
  /// aggregator with one extra line; the startup check picks it up
  /// for free, no per-subsystem-check authoring required.</para>
  ///
  /// <para><b>Storage shape.</b> Two-level: category -> per-subject
  /// occurrence count. The category groups related issues so the
  /// dialog can present them under a heading; the subject identifies
  /// the specific offender (blueprint name, collection id, entity
  /// name, voxel coord) so a bug report can name names. Same subject
  /// recorded N times stays as one entry with count=N -- a hot-path
  /// per-tick failure on one entity won't bloat the bucket.</para>
  ///
  /// <para><b>Thread-safety.</b> Recording sites all live on the
  /// main game thread (postfixes, lifecycle hooks, tickable
  /// components, adapter queries from the surveyor). The internal
  /// dictionaries are not synchronised; recording from a background
  /// thread would race. If a future subsystem records off-thread,
  /// switch to <c>ConcurrentDictionary</c> or wrap recording in a
  /// lock; today the simpler shape suffices.</para>
  ///
  /// <para><b>Static accessor.</b> Harmony patches and other static
  /// contexts that can't take a constructor dependency reach the
  /// instance via <see cref="Instance"/>. Bindito constructs this
  /// singleton at load time and the ctor publishes the static; until
  /// then <see cref="Instance"/> is <c>null</c> and recording is a
  /// no-op via <see cref="TryRecord"/>.</para>
  /// </summary>
  public sealed class KeystoneIntegrationHealth : ILoadableSingleton {

    #region Constants

    /// <summary>Cap on the number of distinct subjects we retain per
    /// category. Once the cap is hit, further unique subjects are
    /// collapsed into an overflow counter so the bucket doesn't grow
    /// without bound on a pathological mod stack. The first N
    /// subjects are kept so the bug report can name names; the
    /// overflow count tells the player there's more beyond what's
    /// shown.</summary>
    private const int MaxSubjectsPerCategory = 25;

    #endregion

    #region Static accessor

    private static KeystoneIntegrationHealth? _instance;

    /// <summary>The Bindito-constructed instance, or <c>null</c> if
    /// the container hasn't built it yet. Static contexts (Harmony
    /// patches, accessor classes) read this directly; for callers
    /// that want a no-op on a null instance, prefer
    /// <see cref="TryRecord"/>.</summary>
    public static KeystoneIntegrationHealth? Instance => _instance;

    /// <summary>Pre-instance buffer for static callers that record
    /// BEFORE Bindito constructs this singleton. The motivating case
    /// is <see cref="HarmonyPatches.TemplateCollectionServicePatch"/>:
    /// Harmony patches run from <c>KeystoneModStarter.Configure</c>
    /// at mod-load time, and their first invocations happen as part
    /// of Timberborn's bootstrap -- potentially before our Bindito
    /// scope has constructed any singletons. Without this buffer the
    /// v0.4.4 Badfurs-class crash (patch throws on first load) would
    /// log to <c>Player.log</c> but the dialog would not see the
    /// record. With it, the patch records into the static queue, the
    /// ctor below drains the queue when it eventually fires, and the
    /// dialog surfaces the record on the next reporter pass.
    ///
    /// <para><b>Thread-safety.</b> Harmony postfixes and Bindito
    /// construction both run on Unity's main thread, but the lock
    /// guards against a future cross-thread caller (e.g. a worker-
    /// thread surveyor pass) racing the ctor's drain.</para></summary>
    private static readonly List<(string Category, string Subject)> _pendingRecords = new();
    private static readonly object _pendingLock = new();

    /// <summary>Record <paramref name="subject"/> under
    /// <paramref name="category"/>. If the aggregator exists, the
    /// record lands directly; if not, the (category, subject) tuple
    /// is appended to a pre-instance buffer that the ctor drains
    /// when DI eventually constructs the singleton. Safe to call
    /// from any context without prior null-checks; never throws.</summary>
    public static void TryRecord(string category, string subject) {
      var inst = _instance;
      if (inst != null) {
        inst.Record(category, subject);
        return;
      }
      lock (_pendingLock) {
        // Re-check after acquiring the lock -- the instance might have
        // been published while we were waiting.
        if (_instance != null) {
          _instance.Record(category, subject);
          return;
        }
        _pendingRecords.Add((category, subject));
      }
    }

    #endregion

    #region Storage

    private readonly Dictionary<string, CategoryBucket> _byCategory =
        new(System.StringComparer.Ordinal);

    #endregion

    #region Construction

    public KeystoneIntegrationHealth() {
      _instance = this;
      // Drain any records buffered before Bindito got around to
      // constructing us. See _pendingRecords docstring above for the
      // race scenario; the lock pairs with TryRecord's append.
      lock (_pendingLock) {
        for (var i = 0; i < _pendingRecords.Count; i++) {
          var (cat, sub) = _pendingRecords[i];
          Record(cat, sub);
        }
        _pendingRecords.Clear();
      }
    }

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      // No-op. The constructor publishes the static; ILoadableSingleton
      // is implemented only so Bindito eagerly resolves and constructs
      // this singleton during scope startup, before any subsystem that
      // wants to record into it has a chance to run.
    }

    #endregion

    #region Recording

    /// <summary>Append <paramref name="subject"/> to the
    /// <paramref name="category"/> bucket. Subjects are deduped per
    /// category; repeat records for the same (category, subject) pair
    /// increment that pair's occurrence count without growing the
    /// stored subject list. New subjects beyond
    /// <see cref="MaxSubjectsPerCategory"/> fold into the bucket's
    /// overflow counter.</summary>
    public void Record(string category, string subject) {
      if (!_byCategory.TryGetValue(category, out var bucket)) {
        bucket = new CategoryBucket(category);
        _byCategory[category] = bucket;
      }
      bucket.Record(subject, MaxSubjectsPerCategory);
    }

    #endregion

    #region Query

    /// <summary>True if any subsystem has recorded at least one
    /// issue. The startup check uses this to short-circuit dialog
    /// generation on a clean load.</summary>
    public bool HasIssues => _byCategory.Count > 0;

    /// <summary>All buckets in insertion order of category. The
    /// startup check enumerates these and renders one section per
    /// category.</summary>
    public IEnumerable<CategoryBucket> Categories => _byCategory.Values;

    #endregion

    #region CategoryBucket

    /// <summary>Per-category storage: unique subjects up to the cap,
    /// plus an overflow counter for any beyond. Total occurrence
    /// count tracks how many <see cref="Record"/> calls landed in
    /// this category overall (including ones that overflowed).</summary>
    public sealed class CategoryBucket {

      private readonly Dictionary<string, int> _subjectCounts =
          new(System.StringComparer.Ordinal);
      private int _overflowSubjects;
      private int _overflowOccurrences;

      public CategoryBucket(string category) {
        Category = category;
      }

      /// <summary>Category name, used as the dialog section heading.</summary>
      public string Category { get; }

      /// <summary>Distinct subjects retained in
      /// <see cref="SubjectCounts"/>; capped at
      /// <see cref="MaxSubjectsPerCategory"/>.</summary>
      public int RetainedSubjectCount => _subjectCounts.Count;

      /// <summary>Distinct subjects that hit the cap and were not
      /// retained individually. Zero in the common case; non-zero
      /// only when a pathological mod stack produces &gt;25 distinct
      /// failures in one category.</summary>
      public int OverflowSubjectCount => _overflowSubjects;

      /// <summary>Sum across <see cref="SubjectCounts"/> plus
      /// <see cref="_overflowOccurrences"/> -- "how many times we
      /// recorded into this category at all." A hot-path failure on
      /// one entity ticking 600 times still shows as 1 retained
      /// subject with TotalOccurrences=600.</summary>
      public int TotalOccurrences { get; private set; }

      /// <summary>Per-subject occurrence count. Iteration order is
      /// insertion order (Dictionary's documented behaviour); the
      /// startup check renders the first few in the dialog body and
      /// stuffs the rest into the detailed message.</summary>
      public IReadOnlyDictionary<string, int> SubjectCounts => _subjectCounts;

      internal void Record(string subject, int maxSubjects) {
        TotalOccurrences++;
        if (_subjectCounts.TryGetValue(subject, out var existing)) {
          _subjectCounts[subject] = existing + 1;
          return;
        }
        if (_subjectCounts.Count >= maxSubjects) {
          _overflowSubjects++;
          _overflowOccurrences++;
          return;
        }
        _subjectCounts[subject] = 1;
      }

    }

    #endregion

  }

}
