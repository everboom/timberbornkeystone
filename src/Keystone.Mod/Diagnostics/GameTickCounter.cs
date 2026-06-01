using System.Diagnostics;
using Timberborn.TickSystem;

namespace Keystone.Mod.Diagnostics {

  /// <summary>
  /// Ring buffer of <see cref="Stopwatch.GetTimestamp"/> values, one
  /// per Timberborn <see cref="ITickableSingleton.Tick"/> call. Used
  /// by the perf window to count how many game ticks occurred inside
  /// a real-time window -- the denominator for "ms per tick"
  /// headlines that don't drift with game speed.
  ///
  /// <para>Game speed (1x/3x/10x) and pauses make "ms per tick" a
  /// more honest "what does one simulation step cost the player" than
  /// "ms per real-time second", which collapses when paused and
  /// inflates at 10x.</para>
  ///
  /// <para><b>Capacity.</b> 1024 entries; at the (10x speed, ~5Hz
  /// nominal tick rate) ceiling that's ~20 real-time seconds of
  /// history -- more than the 10s perf window needs, with headroom
  /// for any future longer windows.</para>
  /// </summary>
  public sealed class GameTickCounter : ITickableSingleton {

    private const int Capacity = 1024;

    private readonly long[] _timestamps = new long[Capacity];
    private int _next;
    private int _total;

    /// <inheritdoc />
    public void Tick() {
      try {
        _timestamps[_next] = Stopwatch.GetTimestamp();
        _next = (_next + 1) % Capacity;
        _total++;
      } catch (System.Exception ex) {
        LifecycleGuard.HandleErrorOnce(
            "GameTickCounter.Tick", "Subsystem failed", ex, ref _failureLogged);
      }
    }

    private bool _failureLogged;

    /// <summary>
    /// Number of recorded ticks whose timestamps fall within the
    /// <paramref name="windowSeconds"/>-long window ending at
    /// <paramref name="endTicks"/> (a <see cref="Stopwatch.GetTimestamp"/>
    /// value). Returns 0 if no ticks fall in the window (game paused,
    /// just loaded, etc.).
    /// </summary>
    public int CountInWindow(long endTicks, double windowSeconds) {
      var count = _total < Capacity ? _total : Capacity;
      if (count == 0) return 0;
      var cutoff = endTicks - (long)(windowSeconds * Stopwatch.Frequency);
      var n = 0;
      for (var i = 0; i < count; i++) {
        if (_timestamps[i] >= cutoff) n++;
      }
      return n;
    }

  }

}
