namespace Keystone.Core.Tiles {

  /// <summary>
  /// 2D horizontal flow vector for water at a surface. <see cref="X"/> is
  /// east-west, <see cref="Y"/> is north-south. The magnitude is flow
  /// speed; the direction is heading. Engine-agnostic counterpart of
  /// Timberborn's <c>UnityEngine.Vector2</c> flow direction.
  /// </summary>
  /// <param name="X">East-west component of the flow.</param>
  /// <param name="Y">North-south component of the flow.</param>
  public readonly record struct FlowVector(float X, float Y) {

    /// <summary>The all-zero vector (no flow).</summary>
    public static readonly FlowVector Zero = default;

    /// <summary>True iff both components are exactly zero.</summary>
    public bool IsZero => X == 0f && Y == 0f;

    /// <summary>Flow speed (Euclidean magnitude). Allocates nothing.</summary>
    public float Magnitude => System.MathF.Sqrt(X * X + Y * Y);

  }

}
