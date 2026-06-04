namespace Keystone.Core.Cutting {

  /// <summary>
  /// Pure per-tile selection policy for the Keystone thinning-cut brush:
  /// decides whether one already-eligible tile falls within the player's
  /// "mark X% of these for cutting" fraction, as a deterministic function of
  /// the tile's coordinate and a per-drag seed.
  ///
  /// <para><b>Per-tile, not per-area — this is the whole point.</b> Each
  /// tile's verdict depends only on its own <c>(x, y, z)</c> and the
  /// <c>seed</c>, never on the size of the drag selection or on any other
  /// tile. Enlarging the drag rectangle therefore only <em>adds</em>
  /// verdicts; it never disturbs a tile already shown, so the highlight does
  /// not flicker as the player sizes the drag, and what the preview shows is
  /// exactly what the commit marks. A per-area "pick round(N·fraction) of the
  /// candidate set" scheme would reshuffle as N changed mid-drag — rejected
  /// for that reason.</para>
  ///
  /// <para><b>Reroll via seed.</b> The seed lives on the tool and is bumped
  /// after each completed drag. Within one drag it is fixed (so preview and
  /// commit agree); the next drag over the same tiles uses a new seed and
  /// selects a different ~X% subset. That is what lets the player redraw the
  /// same area a few times to get a thinning pattern they like.</para>
  ///
  /// <para><b>Expected fraction, not exact.</b> Each tile is an independent
  /// threshold test at probability <c>fraction</c>, so over N
  /// tiles the marked count is ≈<c>fraction·N</c> in expectation, not exactly
  /// that. Over small selections the realized share can differ noticeably (a
  /// 4-tile patch at 0.5 may mark 1 or 3); it converges as the area grows.
  /// There is deliberately <b>no</b> "mark at least one" floor — that would
  /// need the area count this per-tile policy never sees. So a low fraction
  /// over a tiny patch can legitimately mark nothing. By design.</para>
  ///
  /// <para><b>Species filtering is not here.</b> Whether a tile's species is
  /// one the player chose to thin (vs. leave alone) is decided Mod-side while
  /// resolving the tile — it needs game state (the tree/mark at the tile), so
  /// it cannot live in Core. This policy only ever sees tiles that already
  /// passed that filter; see <c>Keystone.Core.Cutting</c>'s README for the
  /// candidate-building pipeline.</para>
  ///
  /// <para><b>Determinism.</b> Same <c>(x, y, z, seed)</c> → same result
  /// across game sessions and .NET versions. Reuses the FNV-1a + Murmur3
  /// finalizer idiom of <see cref="Keystone.Core.Biomes.FlourishThreshold" />
  /// (never <c>string</c>/<c>object.GetHashCode</c>, which is randomised in
  /// modern .NET). The bit-mixing finalizer is load-bearing: a naive linear
  /// combination of the coordinates dithers into visible diagonal stripes
  /// instead of looking random.</para>
  /// </summary>
  public static class ThinningSelector {

    #region Selection

    /// <summary>
    /// True if the already-eligible tile at <c>(<paramref name="x" />,
    /// <paramref name="y" />, <paramref name="z" />)</c> should be marked for
    /// cutting at the target <paramref name="fraction" /> under the current
    /// <paramref name="seed" />.
    /// </summary>
    /// <param name="x">East-west tile index.</param>
    /// <param name="y">North-south tile index.</param>
    /// <param name="z">Height index (distinguishes stacked columns).</param>
    /// <param name="fraction">Target share to mark, clamped to <c>[0, 1]</c>.
    /// <c>&lt;= 0</c> never marks; <c>&gt;= 1</c> always marks.</param>
    /// <param name="seed">Per-drag seed; change it between drags to reroll
    /// the selected subset.</param>
    public static bool ShouldMark(int x, int y, int z, double fraction, int seed) {
      if (fraction <= 0d) return false;
      if (fraction >= 1d) return true;
      return Sample(x, y, z, seed) < fraction;
    }

    /// <summary>
    /// Uniform sample in <c>[0, 1)</c> for the tile under the seed. Exposed
    /// (rather than kept private) so tests can pin threshold boundaries
    /// directly and assert the distribution, independent of
    /// <see cref="ShouldMark" />'s clamping.
    /// </summary>
    public static float Sample(int x, int y, int z, int seed) {
      unchecked {
        uint h = 2166136261u;                  // FNV-1a offset basis
        h = Mix(h, (uint)x);
        h = Mix(h, (uint)y);
        h = Mix(h, (uint)z);
        h = Mix(h, (uint)seed);
        h = Murmur3Final(h);
        // 24-bit float in [0, 1) — matches Random.NextSingle's construction.
        return (h >> 8) * (1f / (1u << 24));
      }
    }

    #endregion

    #region Hash internals

    private static uint Mix(uint h, uint v) {
      unchecked {
        h ^= v;
        h *= 16777619u;                        // FNV-1a prime
        return h;
      }
    }

    private static uint Murmur3Final(uint h) {
      unchecked {
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h;
      }
    }

    #endregion

  }

}
