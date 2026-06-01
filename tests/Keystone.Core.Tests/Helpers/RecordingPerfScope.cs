using System;
using System.Collections.Generic;
using Keystone.Core.Diagnostics;

namespace Keystone.Core.Tests.Helpers {

  /// <summary>
  /// Test fake for <see cref="IPerfScope"/>. Records every Track,
  /// Record, and RecordCount call so tests can assert that timing
  /// scopes opened and closed in the expected order — useful for
  /// verifying the rolling-sweep ticker emits the right Build / Tick /
  /// Drain / Complete / Units stats per cycle. No real timing; all
  /// scopes report 0 ms.
  /// </summary>
  internal sealed class RecordingPerfScope : IPerfScope {

    /// <summary>Names passed to <see cref="Track"/>, in open order.</summary>
    public List<string> Opened { get; } = new();

    /// <summary>Names passed to <see cref="Track"/>, in close (Dispose) order.</summary>
    public List<string> Closed { get; } = new();

    /// <summary>(name, value) pairs passed to <see cref="Record"/>
    /// (duration samples), in call order.</summary>
    public List<(string Name, double Value)> Records { get; } = new();

    /// <summary>(name, count) pairs passed to <see cref="RecordCount"/>
    /// (counter samples), in call order. Kept separate from
    /// <see cref="Records"/> so tests can assert on the count rows
    /// without the duration rows getting in the way.</summary>
    public List<(string Name, long Value)> Counts { get; } = new();

    public IDisposable Track(string name) {
      Opened.Add(name);
      return new TrackScope(this, name);
    }

    public void Record(string name, double valueMs) {
      Records.Add((name, valueMs));
    }

    public void RecordCount(string name, long count) {
      Counts.Add((name, count));
    }

    private sealed class TrackScope : IDisposable {

      private readonly RecordingPerfScope _owner;
      private readonly string _name;

      public TrackScope(RecordingPerfScope owner, string name) {
        _owner = owner;
        _name = name;
      }

      public void Dispose() {
        _owner.Closed.Add(_name);
      }

    }

  }

}
