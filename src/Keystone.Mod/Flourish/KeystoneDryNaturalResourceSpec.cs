using Timberborn.BlueprintSystem;

namespace Keystone.Mod.Flourish {

  /// <summary>
  /// Habitat marker for dry-loving flourishes — desert / arid plants
  /// that don't want soil moisture. Parallels vanilla's
  /// <c>WateredNaturalResourceSpec</c> and
  /// <c>FloodableNaturalResourceSpec</c> in naming, but is a marker
  /// only: it doesn't drive any runtime visual reactivity today.
  ///
  /// <para><b>What this marker means.</b> A blueprint carrying this
  /// spec belongs to the Dry biome's content lineage. The generator
  /// (<c>--dry</c> flag) emits dry flourishes <i>without</i>
  /// <c>WateredNaturalResourceSpec</c> (they don't fire <c>Dying</c>
  /// when soil is dry — that's their preferred state) but still
  /// with <c>FloodableNaturalResourceSpec</c> (so they react to
  /// actual standing water).</para>
  ///
  /// <para><b>Today.</b> Marker only. Recognition via
  /// <c>entity.GetComponent&lt;KeystoneDryNaturalResource&gt;() != null</c>
  /// or <c>blueprint.GetSpec&lt;KeystoneDryNaturalResourceSpec&gt;()</c>.
  /// No visual auto-transition; visual stress on moist tiles is the
  /// blueprint author's choice (typically: ship the mesh in a
  /// pre-stressed / dried look so the plant reads correctly without
  /// state changes).</para>
  ///
  /// <para><b>Future (interpretation B from the design discussion).</b>
  /// May grow into a real runtime component that hooks into
  /// <c>DyingNaturalResource</c> and fires <c>StartedDying</c> when
  /// per-tile moisture rises above a threshold (mirror of
  /// <c>WateredNaturalResource</c>'s predicate, inverted). The name
  /// is chosen to fit that evolution without renaming.</para>
  /// </summary>
  public record KeystoneDryNaturalResourceSpec : ComponentSpec;

}
