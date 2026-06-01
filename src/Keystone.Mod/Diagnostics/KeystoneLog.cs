using UDebug = UnityEngine.Debug;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Keystone-wide logging facade. Routes through
  /// <see cref="UnityEngine.Debug"/> but adds a verbose-mode gate so
  /// players who haven't asked for diagnostics aren't subjected to a
  /// firehose of per-cycle / per-action chatter in <c>Player.log</c>.
  ///
  /// <para><b>Levels.</b>
  /// <list type="bullet">
  ///   <item><see cref="Info"/> — always logs. Reserve for one-shot
  ///         milestones the player <i>should</i> see (currently just
  ///         "Loaded successfully"). Add new <see cref="Info"/> sites
  ///         sparingly.</item>
  ///   <item><see cref="Verbose"/> — logs only when
  ///         <see cref="IsVerbose"/> is true. Use for everything that
  ///         used to be a plain <c>UDebug.Log</c>: catalog summaries,
  ///         per-cycle progress, placement events, etc.</item>
  ///   <item><see cref="Warn"/> / <see cref="Error"/> — always log,
  ///         via <c>LogWarning</c> / <c>LogError</c>. These should
  ///         fire only on genuine deviations.</item>
  /// </list></para>
  ///
  /// <para><b>Toggle.</b> <see cref="IsVerbose"/> is a plain static
  /// bool. <see cref="KeystoneModStarter.StartMod"/> initialises it
  /// from <see cref="KeystoneDevMode.IsEnabled"/>, so it's true on a
  /// dev deploy and false on a release deploy. A future mod-settings
  /// UI hook can flip it at runtime independently; no DI plumbing is
  /// required because the callers are pure log sites.</para>
  ///
  /// <para><b>Prefix.</b> The <c>"[Keystone] "</c> prefix is baked
  /// into each call site's string, not into this helper, so log lines
  /// remain greppable by static search even if the routing layer
  /// changes.</para>
  /// </summary>
  public static class KeystoneLog {

    /// <summary>Master switch for <see cref="Verbose"/> output. When
    /// false, every <c>Verbose</c> call is a near-zero branch +
    /// return. Initialised by <see cref="KeystoneModStarter.StartMod"/>
    /// from <see cref="KeystoneDevMode.IsEnabled"/>; can be toggled at
    /// runtime by a future mod-settings UI without breaking anything.</summary>
    public static bool IsVerbose = false;

    /// <summary>Always-on informational log. Use sparingly — only for
    /// milestones the player should see without enabling verbose mode.</summary>
    public static void Info(string message) {
      UDebug.Log(message);
    }

    /// <summary>Diagnostic log gated on <see cref="IsVerbose"/>. The
    /// goal is that a default-config Keystone session produces
    /// near-empty <c>Player.log</c> output until something goes wrong.</summary>
    public static void Verbose(string message) {
      if (!IsVerbose) return;
      UDebug.Log(message);
    }

    /// <summary>Always-on warning. Use for conditions the player
    /// should know about but that don't break the mod
    /// (malformed JSON entries skipped, deprecated config fields,
    /// recipe references that didn't resolve, etc.).</summary>
    public static void Warn(string message) {
      UDebug.LogWarning(message);
    }

    /// <summary>Always-on error. Use for outright failures that
    /// likely indicate a bug or a hard incompatibility.</summary>
    public static void Error(string message) {
      UDebug.LogError(message);
    }

  }

}
