using System.ComponentModel;

namespace System.Runtime.CompilerServices {

  /// <summary>
  /// Polyfill required by C# 9+ <c>init</c> accessors when targeting
  /// frameworks that predate .NET 5 (we target <c>netstandard2.1</c>).
  /// </summary>
  [EditorBrowsable(EditorBrowsableState.Never)]
  internal static class IsExternalInit {
  }

}
