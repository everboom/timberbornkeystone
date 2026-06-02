using Keystone.Core.Planting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Planting {

  /// <summary>
  /// Tests for <see cref="PlantingPalette"/> — the per-tool mixed-planting
  /// selection policy. The palette is pure: registration + per-species
  /// integer-weight mutation, plus a weighted <c>Choose(pickHash)</c> draw
  /// over the active candidates (positively-weighted species, and the gap
  /// outcome when allowed). These pin the behaviors the Forest Tool
  /// reference kept in untestable statics.
  /// </summary>
  [TestClass]
  public class PlantingPaletteTests {

    #region Registration

    /// <summary>Newly added species register at
    /// <see cref="PlantingPalette.DefaultWeight"/> (1) — in the draw, mirrors
    /// the Forest Tool default where every discovered plantable started on.</summary>
    [TestMethod]
    public void Add_NewSpecies_DefaultWeight() {
      // Arrange
      var palette = new PlantingPalette();

      // Act
      palette.Add("Birch");

      // Assert
      Assert.AreEqual(PlantingPalette.DefaultWeight, palette.GetWeight("Birch"));
      Assert.IsTrue(palette.IsEnabled("Birch"));
      CollectionAssert.AreEqual(new[] { "Birch" }, (System.Collections.ICollection)palette.Species);
    }

    /// <summary>Re-adding a species does not duplicate it and preserves
    /// both its registration order and its current weight.</summary>
    [TestMethod]
    public void Add_Duplicate_PreservesWeightAndOrder() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.Add("Pine");
      palette.SetWeight("Birch", 5);

      // Act
      palette.Add("Birch");

      // Assert
      CollectionAssert.AreEqual(new[] { "Birch", "Pine" }, (System.Collections.ICollection)palette.Species);
      Assert.AreEqual(5, palette.GetWeight("Birch"), "re-Add must not reset an existing species' weight");
    }

    /// <summary>Null/empty species names are ignored rather than
    /// registered (an empty weight row would be meaningless).</summary>
    [TestMethod]
    public void Add_NullOrEmpty_Ignored() {
      // Arrange
      var palette = new PlantingPalette();

      // Act
      palette.Add(null!);
      palette.Add("");

      // Assert
      Assert.AreEqual(0, palette.Species.Count);
    }

    #endregion

    #region Weight mutation

    /// <summary>Setting a species to weight 0 removes it from the active
    /// draw set but keeps it registered (the row stays visible).</summary>
    [TestMethod]
    public void SetWeight_Zero_ExcludesButKeepsRegistered() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");

      // Act
      palette.SetWeight("Birch", 0);

      // Assert
      Assert.AreEqual(0, palette.GetWeight("Birch"));
      Assert.IsFalse(palette.IsEnabled("Birch"));
      CollectionAssert.AreEqual(new[] { "Birch" }, (System.Collections.ICollection)palette.Species);
    }

    /// <summary>Weighting an unregistered species is a no-op (no implicit
    /// registration through the weight map).</summary>
    [TestMethod]
    public void SetWeight_UnregisteredSpecies_NoOp() {
      // Arrange
      var palette = new PlantingPalette();

      // Act
      palette.SetWeight("Ghost", 3);

      // Assert
      Assert.AreEqual(0, palette.GetWeight("Ghost"));
      Assert.AreEqual(0, palette.Species.Count);
    }

    /// <summary>The &#8722; button floors at <see cref="PlantingPalette.MinWeight"/>
    /// (0) and the + button caps at <see cref="PlantingPalette.MaxWeight"/>
    /// (9); both return the resulting weight.</summary>
    [TestMethod]
    public void IncrementDecrement_ClampToBounds() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");          // starts at DefaultWeight (1)

      // Act / Assert: decrement floors at 0 and stays there.
      Assert.AreEqual(0, palette.DecrementWeight("Birch"));
      Assert.AreEqual(0, palette.DecrementWeight("Birch"));

      // Act / Assert: increment caps at MaxWeight and stays there.
      for (var i = 0; i < PlantingPalette.MaxWeight; i++) {
        palette.IncrementWeight("Birch");
      }
      Assert.AreEqual(PlantingPalette.MaxWeight, palette.GetWeight("Birch"));
      Assert.AreEqual(PlantingPalette.MaxWeight, palette.IncrementWeight("Birch"));
    }

    /// <summary><see cref="PlantingPalette.SetWeight"/> clamps an
    /// out-of-range request rather than storing it raw.</summary>
    [TestMethod]
    public void SetWeight_OutOfRange_Clamped() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");

      // Act / Assert
      palette.SetWeight("Birch", 99);
      Assert.AreEqual(PlantingPalette.MaxWeight, palette.GetWeight("Birch"));
      palette.SetWeight("Birch", -5);
      Assert.AreEqual(PlantingPalette.MinWeight, palette.GetWeight("Birch"));
    }

    /// <summary>"Select all" (weight 1) and "Clear all" (weight 0) drive
    /// every registered species and leave
    /// <see cref="PlantingPalette.GapWeight"/> untouched (the player controls
    /// clearings independently via their own row).</summary>
    [TestMethod]
    public void SetAllWeights_SetsEverySpecies_NotGaps() {
      // Arrange
      var palette = new PlantingPalette();
      palette.SetGapWeight(2);
      palette.Add("Birch");
      palette.Add("Pine");
      palette.SetWeight("Birch", 4);

      // Act: clear all.
      palette.SetAllWeights(0);

      // Assert
      Assert.AreEqual(0, palette.GetWeight("Birch"));
      Assert.AreEqual(0, palette.GetWeight("Pine"));
      Assert.AreEqual(2, palette.GapWeight, "clearings weight is independent, not swept by the bulk buttons");

      // Act: select all (uniform reset to 1).
      palette.SetAllWeights(1);

      // Assert
      Assert.AreEqual(1, palette.GetWeight("Birch"), "select-all is a uniform reset, not a preserve-and-fill");
      Assert.AreEqual(1, palette.GetWeight("Pine"));
      Assert.AreEqual(2, palette.GapWeight);
    }

    /// <summary>The clearings weight starts at 0 and its steppers clamp to
    /// <c>[MinWeight, MaxWeight]</c>, mirroring the species weight controls.</summary>
    [TestMethod]
    public void GapWeight_StartsZero_StepsClampToBounds() {
      // Arrange
      var palette = new PlantingPalette();

      // Act / Assert: default 0 (no clearings), floors at 0.
      Assert.AreEqual(0, palette.GapWeight);
      Assert.AreEqual(0, palette.DecrementGapWeight());

      // Increment caps at MaxWeight.
      for (var i = 0; i < PlantingPalette.MaxWeight + 2; i++) {
        palette.IncrementGapWeight();
      }
      Assert.AreEqual(PlantingPalette.MaxWeight, palette.GapWeight);

      // SetGapWeight clamps out-of-range requests.
      palette.SetGapWeight(99);
      Assert.AreEqual(PlantingPalette.MaxWeight, palette.GapWeight);
      palette.SetGapWeight(-5);
      Assert.AreEqual(PlantingPalette.MinWeight, palette.GapWeight);
    }

    #endregion

    #region HasActiveOutcome

    /// <summary>Empty palette has nothing to draw.</summary>
    [TestMethod]
    public void HasActiveOutcome_Empty_False() {
      Assert.IsFalse(new PlantingPalette().HasActiveOutcome);
    }

    /// <summary>Every species at weight 0 and no gaps → nothing to draw.</summary>
    [TestMethod]
    public void HasActiveOutcome_AllZeroNoGaps_False() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.SetWeight("Birch", 0);

      // Act / Assert
      Assert.IsFalse(palette.HasActiveOutcome);
    }

    /// <summary>A positive clearings weight alone is a valid outcome even with
    /// every species at weight 0 (the brush would clear tiles).</summary>
    [TestMethod]
    public void HasActiveOutcome_GapsOnly_True() {
      // Arrange
      var palette = new PlantingPalette();
      palette.SetGapWeight(1);
      palette.Add("Birch");
      palette.SetWeight("Birch", 0);

      // Act / Assert
      Assert.IsTrue(palette.HasActiveOutcome);
    }

    #endregion

    #region Choose

    /// <summary>Everything at weight 0 and no gaps → every draw is a no-op
    /// (<c>null</c>), regardless of hash.</summary>
    [TestMethod]
    public void Choose_NoCandidates_ReturnsNull() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.SetWeight("Birch", 0);

      // Act / Assert
      Assert.IsNull(palette.Choose(0.0f));
      Assert.IsNull(palette.Choose(0.999f));
    }

    /// <summary>A single positively-weighted species is returned for any
    /// hash.</summary>
    [TestMethod]
    public void Choose_SingleSpecies_AlwaysThatSpecies() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");

      // Act / Assert
      Assert.AreEqual("Birch", palette.Choose(0.0f));
      Assert.AreEqual("Birch", palette.Choose(0.5f));
      Assert.AreEqual("Birch", palette.Choose(0.9999f));
    }

    /// <summary>Two equally-weighted species split the hash range in
    /// half, in registration order (Birch first, Pine second).</summary>
    [TestMethod]
    public void Choose_TwoEqualSpecies_SplitsAtHalfInRegistrationOrder() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.Add("Pine");

      // Act / Assert
      Assert.AreEqual("Birch", palette.Choose(0.0f));
      Assert.AreEqual("Birch", palette.Choose(0.499f));
      Assert.AreEqual("Pine", palette.Choose(0.5f));
      Assert.AreEqual("Pine", palette.Choose(0.9999f));
    }

    /// <summary>Unequal weights split the hash range proportionally:
    /// Birch=3, Pine=1 → Birch owns [0, 0.75), Pine owns [0.75, 1).</summary>
    [TestMethod]
    public void Choose_UnequalWeights_SplitsProportionally() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.Add("Pine");
      palette.SetWeight("Birch", 3);

      // Act / Assert
      Assert.AreEqual("Birch", palette.Choose(0.0f));
      Assert.AreEqual("Birch", palette.Choose(0.749f));
      Assert.AreEqual("Pine", palette.Choose(0.75f));
      Assert.AreEqual("Pine", palette.Choose(0.9999f));
    }

    /// <summary>A weight-0 species is skipped without shifting the draw
    /// off the remaining positively-weighted ones. Birch at 0, Pine + Oak
    /// at 1 → the half-split lands on Pine then Oak, never Birch.</summary>
    [TestMethod]
    public void Choose_ZeroWeightSpecies_ExcludedFromDraw() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.Add("Pine");
      palette.Add("Oak");
      palette.SetWeight("Birch", 0);

      // Act / Assert
      Assert.AreEqual("Pine", palette.Choose(0.0f));
      Assert.AreEqual("Pine", palette.Choose(0.499f));
      Assert.AreEqual("Oak", palette.Choose(0.5f));
    }

    /// <summary>The clearings outcome (<c>null</c>) competes as the final
    /// candidate at its own weight. One species at weight 1 + clearings at
    /// weight 1 → hash &lt; 0.5 plants, hash &gt;= 0.5 leaves a gap.</summary>
    [TestMethod]
    public void Choose_SpeciesPlusGap_GapIsFinalCandidate() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.SetGapWeight(1);

      // Act / Assert
      Assert.AreEqual("Birch", palette.Choose(0.0f));
      Assert.AreEqual("Birch", palette.Choose(0.499f));
      Assert.IsNull(palette.Choose(0.5f), "second of two equal candidates is the clearings");
      Assert.IsNull(palette.Choose(0.9999f));
    }

    /// <summary>The clearings weight is honored proportionally, exactly like a
    /// species weight (the old design hardwired it to a single unit; it is now
    /// player-controlled). Birch=3 + clearings=2 → total 5, Birch owns
    /// [0, 0.6), clearings owns [0.6, 1).</summary>
    [TestMethod]
    public void Choose_GapWeight_HonoredProportionally() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.SetWeight("Birch", 3);
      palette.SetGapWeight(2);

      // Act / Assert
      Assert.AreEqual("Birch", palette.Choose(0.0f));
      Assert.AreEqual("Birch", palette.Choose(0.599f));
      Assert.IsNull(palette.Choose(0.6f));
      Assert.IsNull(palette.Choose(0.9999f));
    }

    /// <summary>Coarse Monte-Carlo over evenly-spaced hashes recovers an
    /// even split across three equally-weighted species, guarding against
    /// off-by-one regressions in the index→species mapping.</summary>
    [TestMethod]
    public void Choose_ManySamples_EvenAcrossThreeEqualSpecies() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.Add("Pine");
      palette.Add("Oak");
      var counts = new System.Collections.Generic.Dictionary<string, int> {
        ["Birch"] = 0, ["Pine"] = 0, ["Oak"] = 0,
      };
      const int Samples = 999;

      // Act
      for (var i = 0; i < Samples; i++) {
        var species = palette.Choose((float)i / Samples);
        counts[species!]++;
      }

      // Assert: 333 each, deterministic given evenly-spaced hashes.
      Assert.AreEqual(333, counts["Birch"]);
      Assert.AreEqual(333, counts["Pine"]);
      Assert.AreEqual(333, counts["Oak"]);
    }

    #endregion

  }

}
