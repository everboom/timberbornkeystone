using System;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Runtime gate for developer-only behaviour (always-on startup
  /// dialogs, verbose logging, debug placement tools, etc.). Enabled
  /// when the user environment variable <c>KEYSTONE_DEV_MODE</c> is
  /// set to a non-empty, non-"0", non-"false" value.
  ///
  /// <para><b>How to enable on a dev machine.</b> Run
  /// <c>setx KEYSTONE_DEV_MODE 1</c> from a terminal. The probe reads
  /// the User-scope registry env block directly, so a Steam restart is
  /// not required (Steam captures its env at launch, but we don't rely
  /// on inheritance). Disable by clearing the variable
  /// (<c>setx KEYSTONE_DEV_MODE ""</c>). Toggling while Timberborn is
  /// running still requires a game restart because the result is
  /// cached for the process lifetime.</para>
  ///
  /// <para><b>Why an env var and not a sentinel file.</b> The previous
  /// design dropped a <c>keystone-dev.flag</c> next to <c>Code.dll</c>
  /// during deploy and probed for it via
  /// <c>Assembly.Location</c> at runtime. Under Timberborn's mod
  /// loader <c>Assembly.Location</c> can be empty (in-memory loads) or
  /// point to a shadow-copied path, both of which made the probe miss
  /// the sentinel even when it was correctly written. The env var
  /// sidesteps the assembly-path question entirely.</para>
  ///
  /// <para><b>Release-safety.</b> The env var is per-user OS state, not
  /// something a release packaging pipeline can produce. Anyone
  /// downloading a Keystone release archive will not have
  /// <c>KEYSTONE_DEV_MODE</c> set unless they explicitly opt in.</para>
  ///
  /// <para><b>Cost.</b> One <c>Environment.GetEnvironmentVariable</c>
  /// call on the first read, cached for the lifetime of the process.
  /// Callers may read <see cref="IsEnabled"/> as often as they like.</para>
  /// </summary>
  public static class KeystoneDevMode {

    /// <summary>Name of the environment variable that, when set to a
    /// truthy value, enables dev-only behaviour at runtime.</summary>
    public const string EnvVarName = "KEYSTONE_DEV_MODE";

    private static bool? _cached;

    /// <summary>True when the <see cref="EnvVarName"/> env var is set
    /// to a non-empty, non-"0", non-"false" (case-insensitive) value.
    /// Evaluated lazily once per process and then cached.</summary>
    public static bool IsEnabled {
      get {
        // No locking: the worst case under a race is multiple threads
        // each computing the same env-var read and writing the same
        // bool into _cached. The result is stable.
        if (_cached.HasValue) return _cached.Value;
        _cached = Probe();
        return _cached.Value;
      }
    }

    private static bool Probe() {
      // Check the process env block first (cheapest), then fall back to
      // the User-scope env var (HKCU\Environment on Windows). The
      // fallback matters because Steam captures its environment block at
      // launch -- if the dev-mode variable was set via `setx` after
      // Steam started, child Timberborn processes won't see it in their
      // inherited env. Reading the User target hits the registry
      // directly and bypasses that inheritance lag.
      return IsTruthy(SafeGet(EnvironmentVariableTarget.Process))
          || IsTruthy(SafeGet(EnvironmentVariableTarget.User));
    }

    private static string? SafeGet(EnvironmentVariableTarget target) {
      try {
        return Environment.GetEnvironmentVariable(EnvVarName, target);
      } catch (Exception) {
        // Defensive: GetEnvironmentVariable can throw SecurityException
        // under sandboxed hosts, and the User/Machine targets aren't
        // meaningful on non-Windows runtimes (Mono returns null there
        // on a good day, throws on a bad one).
        return null;
      }
    }

    private static bool IsTruthy(string? raw) {
      if (string.IsNullOrEmpty(raw)) return false;
      if (raw == "0") return false;
      if (raw!.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
      return true;
    }

  }

}
