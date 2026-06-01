using System;
using System.Collections.Generic;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Shared catch-body handlers for the "outermost try/catch on every
  /// host-called method" pattern. Every Keystone-owned method the host
  /// invokes (PostLoad, Tick, InitializeEntity, Harmony prefix, etc.)
  /// wraps its body in a try/catch; the catch delegates to one of these
  /// overloads so the log + integration-health-record boilerplate lives
  /// in one place.
  ///
  /// <para><b>Three overloads for three rate-limit shapes:</b>
  /// <list type="bullet">
  ///   <item><see cref="HandleError"/> — no rate limit. For one-shot
  ///         methods (PostLoad, Load, InitializeEntity) that fire at
  ///         most once per entity or singleton.</item>
  ///   <item><see cref="HandleErrorOnce"/> — per-instance bool flag.
  ///         For per-tick methods where one error per entity per session
  ///         is enough (KeystoneFlourish.Tick, KeystoneNatureSource.Tick,
  ///         RegionUpdater.Tick, etc.).</item>
  ///   <item><see cref="HandleErrorByType"/> — per-exception-type
  ///         HashSet. For hot-path methods where different exception
  ///         types carry distinct diagnostic value (adapter ClassifyAt,
  ///         Harmony prefix catches, RollingSweep OnUnitError). Always
  ///         records to the aggregator for accurate TotalOccurrences;
  ///         only logs the first occurrence of each type.</item>
  /// </list></para>
  ///
  /// <para><b>Why not wrap the whole body?</b> An <c>Action body</c>
  /// parameter would allocate a delegate on every call — fine for
  /// one-shot methods, bad for per-tick paths on 1000+ entities.
  /// By handling only the catch body, the caller keeps the try/catch
  /// structure (which varies: some sites have finally blocks, nested
  /// inner catches, early returns, or IsLoaded ordering) and this
  /// helper collapses the 3-line repetition into 1 line with zero
  /// allocation.</para>
  /// </summary>
  public static class LifecycleGuard {

    /// <summary>Log + record. No rate limit. Use in one-shot catch
    /// blocks (PostLoad, Load, InitializeEntity).</summary>
    public static void HandleError(string label, string category, Exception ex) {
      KeystoneLog.Error($"[Keystone] {label} threw: {ex}");
      KeystoneIntegrationHealth.TryRecord(category, $"{label}: {ex.GetType().Name}");
    }

    /// <summary>Log + record once per instance. Use in per-tick catch
    /// blocks where one log line per entity per session is enough.
    /// <paramref name="logged"/> is a <c>ref bool</c> field on the
    /// enclosing instance; once true, subsequent calls are no-ops.</summary>
    public static void HandleErrorOnce(
        string label, string category, Exception ex, ref bool logged) {
      if (logged) return;
      logged = true;
      HandleError(label, category, ex);
    }

    /// <summary>Log once per distinct exception type; always record.
    /// Use in hot-path catch blocks (adapters, Harmony prefixes,
    /// RollingSweep callbacks) where different exception types carry
    /// distinct diagnostic value and the aggregator's
    /// <c>TotalOccurrences</c> should reflect every miss. First
    /// occurrence of each type logs <c>Error</c>; subsequent
    /// occurrences of the same type are log-silent but still bump
    /// the aggregator count.</summary>
    public static void HandleErrorByType(
        string label, string category, Exception ex, HashSet<string> loggedTypes) {
      var typeName = ex.GetType().Name;
      if (loggedTypes.Add(typeName)) {
        KeystoneLog.Error($"[Keystone] {label} threw: {ex}");
      }
      KeystoneIntegrationHealth.TryRecord(category, $"{label}: {typeName}");
    }

  }

}
