namespace Keystone.Core.Biomes {

  /// <summary>
  /// Per-tile per-level deterministic hash pair for flourish
  /// placement. Two pure functions, both producing values in
  /// <c>[0, 1)</c>:
  ///
  /// <list type="bullet">
  ///   <item><see cref="ComputeActivation"/> — gates whether the
  ///         <c>(biome, level)</c> bucket activates at this tile.
  ///         A tile activates iff
  ///         <c>activationHash &lt; level.Density · progress</c>,
  ///         where <c>progress</c> is the level's maturity-ramp
  ///         fraction in <c>[0, 1]</c>.</item>
  ///   <item><see cref="ComputePick"/> — given the bucket has activated
  ///         (or is being considered), this drives weighted-random
  ///         recipe selection from the bucket's recipes. Independent
  ///         of the activation hash so adding density doesn't shift
  ///         which recipe gets picked.</item>
  /// </list>
  ///
  /// <para><b>Per-level keying → additive across levels.</b> Both
  /// hashes mix <c>levelId</c> in, so L1's "10% of tiles" and L2's
  /// "10% of tiles" are different draws of tiles. When multiple
  /// levels are active for the same biome, their coverage adds (with
  /// the usual overlap). See <see cref="BiomeLevel.Density"/> for the
  /// designer-facing implication.</para>
  ///
  /// <para><b>Why per-level, not per-recipe.</b> Density is a level
  /// property (<see cref="BiomeLevel.Density"/>); recipes contribute
  /// the *what*, not the *how often*. Hashing on the level rather
  /// than the recipe means adding more recipes to a bucket broadens
  /// the variety pool but doesn't change the fraction of tiles that
  /// activate. This is cleaner than the older "per-recipe MaxDensity
  /// with cumulative coverage" model where adding a recipe inflated
  /// total spawn coverage.</para>
  ///
  /// <para><b>Determinism.</b> Same inputs produce the same output
  /// across game sessions and .NET versions. Uses FNV-1a (not
  /// <c>string.GetHashCode</c>, which became randomised in modern
  /// .NET and would jump flora around on reload).</para>
  /// </summary>
  public static class FlourishThreshold {

    /// <summary>Activation hash for the
    /// <c>(<paramref name="tileX"/>, <paramref name="tileY"/>,
    /// <paramref name="biome"/>, <paramref name="levelId"/>)</c>
    /// bucket. Handler activates iff this is below
    /// <c>level.Density</c>.</summary>
    public static float ComputeActivation(int tileX, int tileY, BiomeKind biome, string levelId) {
      return Hash(tileX, tileY, biome, levelId, salt: 0x4163);
    }

    /// <summary>Pick hash, independent of <see cref="ComputeActivation"/>.
    /// Used to choose one recipe from the bucket via weighted-random
    /// sampling; deterministic per <c>(tile, biome, level)</c> so the
    /// same tile always picks the same recipe.</summary>
    public static float ComputePick(int tileX, int tileY, BiomeKind biome, string levelId) {
      return Hash(tileX, tileY, biome, levelId, salt: 0x9C71);
    }

    private static float Hash(int tileX, int tileY, BiomeKind biome, string levelId, uint salt) {
      unchecked {
        uint h = 2166136261u;                  // FNV-1a offset basis
        h = Mix(h, (uint)tileX);
        h = Mix(h, (uint)tileY);
        h = Mix(h, (uint)biome);
        h = Mix(h, Fnv1a(levelId));
        // Salt mixes in *last* so the activation/pick distinction
        // survives even if a future refactor swaps the inner mix for
        // a hash function that's order-insensitive at any layer.
        // (FNV-1a is order-sensitive, so this is robustness margin
        // rather than a current correctness requirement.)
        h = Mix(h, salt);
        h = Murmur3Final(h);
        // 24-bit float in [0, 1) -- standard "uniform random" trick,
        // matches what Random.NextSingle does.
        return (h >> 8) * (1f / (1u << 24));
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

    private static uint Mix(uint h, uint v) {
      unchecked {
        h ^= v;
        h *= 16777619u;                        // FNV-1a prime
        return h;
      }
    }

    private static uint Fnv1a(string s) {
      unchecked {
        uint h = 2166136261u;
        for (var i = 0; i < s.Length; i++) {
          h ^= s[i];
          h *= 16777619u;
        }
        return h;
      }
    }

  }

}
