using HarmonyLib;
using Timberborn.NaturalResourcesReproduction;

namespace Keystone.Mod.HarmonyPatches {

  /// <summary>
  /// Scales vanilla natural-resource reproduction by the player's
  /// <c>Keystone.Settings.BaseGame.WildReproduction</c> multiplier.
  /// Postfix on the <see cref="Reproducible.ReproductionChance"/> getter:
  /// multiplies the returned chance by
  /// <see cref="NaturalReproductionRateAccessor.Multiplier"/>.
  ///
  /// <para><b>Why this getter, specifically.</b> The getter is the sole
  /// reader-facing source of a resource's reproduction chance, and the
  /// only place vanilla reads it is
  /// <c>NaturalResourceReproducer.MarkSpots</c> →
  /// <c>ReproducibleKey.Create</c>, which captures the value into a
  /// per-resource-id dictionary key at mark-time (entity load). The
  /// per-tick spread loop then reads that frozen key, never the getter
  /// again. So this postfix runs only at mark-time — a handful of calls
  /// over a resource's life (spawn, reproduction-block, revive) — and
  /// never on the simulation hot path. The frozen key carries the
  /// multiplied value for the rest of the session; because the
  /// governing setting is MainMenu-only it can't change mid-session
  /// anyway, and it re-applies cleanly on every load (the dictionary is
  /// rebuilt as resources re-mark).</para>
  ///
  /// <para><b>Throttle-only.</b> The multiplier is in <c>[0, 1]</c>
  /// (the 0–100% slider), so this can only reduce the stock rate:
  /// 5% → 1/20th (default), 0% → no spread, 100% → vanilla. A disabled
  /// resource's getter already returns <c>0f</c>; multiplying leaves it
  /// at <c>0f</c>, so the patch is a no-op in that case.</para>
  ///
  /// <para><b>Global by intent.</b> Fires on every <c>Reproducible</c>
  /// in the game — there is deliberately no per-species or
  /// Keystone-owned gate. The throttle is meant to suppress <em>all</em>
  /// vanilla neighbour-spread uniformly, because Keystone's own content
  /// pipeline is the intended driver of plant population (see
  /// <see cref="Keystone.Mod.Settings.KeystoneBaseGameSettings"/>).</para>
  /// </summary>
  [HarmonyPatch(typeof(Reproducible), nameof(Reproducible.ReproductionChance), MethodType.Getter)]
  public static class ReproducibleReproductionChancePatch {

    /// <summary>True once the postfix has executed at least once this
    /// session. Lets <c>WildReproductionThrottleTest</c> distinguish
    /// "patch attached but never fired" (the silent-no-op gap the
    /// startup <c>ExpectedPatchedMethodCount</c> assertion can't see)
    /// from a clean run. Stays true across scene loads (static
    /// lifetime); reset only on process restart.</summary>
    public static bool HasRun { get; private set; }

    public static void Postfix(ref float __result) {
      PatchInvocationLog.Once(nameof(ReproducibleReproductionChancePatch));
      HasRun = true;
      __result *= NaturalReproductionRateAccessor.Multiplier;
    }

  }

}
