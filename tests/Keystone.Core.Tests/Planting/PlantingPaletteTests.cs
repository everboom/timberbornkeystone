using Keystone.Core.Planting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Planting {

  /// <summary>
  /// Tests for <see cref="PlantingPalette"/> — the per-tool mixed-planting
  /// selection policy. The palette is pure: registration + enabled-set
  /// mutation, plus a uniform <c>Choose(pickHash)</c> draw over the active
  /// candidates (enabled species, and the gap outcome when allowed). These
  /// pin the behaviors the Forest Tool reference kept in untestable statics.
  /// </summary>
  [TestClass]
  public class PlantingPaletteTests {

    #region Registration

    /// <summary>Newly added species are enabled by default — mirrors the
    /// Forest Tool default where every discovered plantable starts on.</summary>
    [TestMethod]
    public void Add_NewSpecies_EnabledByDefault() {
      // Arrange
      var palette = new PlantingPalette();

      // Act
      palette.Add("Birch");

      // Assert
      Assert.IsTrue(palette.IsEnabled("Birch"));
      CollectionAssert.AreEqual(new[] { "Birch" }, (System.Collections.ICollection)palette.Species);
    }

    /// <summary>Re-adding a species does not duplicate it and preserves
    /// registration order.</summary>
    [TestMethod]
    public void Add_Duplicate_NoDuplicateEntry() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.Add("Pine");

      // Act
      palette.Add("Birch");

      // Assert
      CollectionAssert.AreEqual(new[] { "Birch", "Pine" }, (System.Collections.ICollection)palette.Species);
    }

    /// <summary>Null/empty species names are ignored rather than
    /// registered (an empty toggle row would be meaningless).</summary>
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

    #region Enabled-set mutation

    /// <summary>Disabling a species removes it from the active draw set
    /// but keeps it registered (the toggle row stays visible).</summary>
    [TestMethod]
    public void SetEnabled_False_DisablesButKeepsRegistered() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");

      // Act
      palette.SetEnabled("Birch", false);

      // Assert
      Assert.IsFalse(palette.IsEnabled("Birch"));
      CollectionAssert.AreEqual(new[] { "Birch" }, (System.Collections.ICollection)palette.Species);
    }

    /// <summary>Toggling an unregistered species is a no-op (no implicit
    /// registration through the enabled set).</summary>
    [TestMethod]
    public void SetEnabled_UnregisteredSpecies_NoOp() {
      // Arrange
      var palette = new PlantingPalette();

      // Act
      palette.SetEnabled("Ghost", true);

      // Assert
      Assert.IsFalse(palette.IsEnabled("Ghost"));
      Assert.AreEqual(0, palette.Species.Count);
    }

    /// <summary>The "All" master toggle flips every registered species at
    /// once and leaves <see cref="PlantingPalette.AllowGaps"/> untouched.</summary>
    [TestMethod]
    public void SetAllEnabled_TogglesEverySpecies_NotGaps() {
      // Arrange
      var palette = new PlantingPalette { AllowGaps = true };
      palette.Add("Birch");
      palette.Add("Pine");

      // Act
      palette.SetAllEnabled(false);

      // Assert
      Assert.IsFalse(palette.IsEnabled("Birch"));
      Assert.IsFalse(palette.IsEnabled("Pine"));
      Assert.IsTrue(palette.AllowGaps, "AllowGaps is an independent outcome, not swept by the species master toggle");
    }

    #endregion

    #region HasActiveOutcome

    /// <summary>Empty palette has nothing to draw.</summary>
    [TestMethod]
    public void HasActiveOutcome_Empty_False() {
      Assert.IsFalse(new PlantingPalette().HasActiveOutcome);
    }

    /// <summary>Gaps alone are a valid outcome even with no species
    /// enabled (the brush would clear tiles).</summary>
    [TestMethod]
    public void HasActiveOutcome_GapsOnly_True() {
      // Arrange
      var palette = new PlantingPalette { AllowGaps = true };
      palette.Add("Birch");
      palette.SetEnabled("Birch", false);

      // Act / Assert
      Assert.IsTrue(palette.HasActiveOutcome);
    }

    #endregion

    #region Choose

    /// <summary>Nothing enabled and no gaps → every draw is a no-op
    /// (<c>null</c>), regardless of hash.</summary>
    [TestMethod]
    public void Choose_NoCandidates_ReturnsNull() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.SetEnabled("Birch", false);

      // Act / Assert
      Assert.IsNull(palette.Choose(0.0f));
      Assert.IsNull(palette.Choose(0.999f));
    }

    /// <summary>A single enabled species is returned for any hash.</summary>
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
    public void Choose_TwoSpecies_SplitsAtHalfInRegistrationOrder() {
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

    /// <summary>A disabled species is skipped without shifting the draw
    /// off the remaining enabled ones. Birch disabled, Pine + Oak on →
    /// the half-split lands on Pine then Oak, never Birch.</summary>
    [TestMethod]
    public void Choose_DisabledSpecies_ExcludedFromDraw() {
      // Arrange
      var palette = new PlantingPalette();
      palette.Add("Birch");
      palette.Add("Pine");
      palette.Add("Oak");
      palette.SetEnabled("Birch", false);

      // Act / Assert
      Assert.AreEqual("Pine", palette.Choose(0.0f));
      Assert.AreEqual("Pine", palette.Choose(0.499f));
      Assert.AreEqual("Oak", palette.Choose(0.5f));
    }

    /// <summary>With gaps enabled, the gap outcome (<c>null</c>) occupies
    /// the final equal-weight candidate slot. One species + gap → hash
    /// &lt; 0.5 plants, hash &gt;= 0.5 leaves a gap.</summary>
    [TestMethod]
    public void Choose_SpeciesPlusGap_GapIsFinalCandidate() {
      // Arrange
      var palette = new PlantingPalette { AllowGaps = true };
      palette.Add("Birch");

      // Act / Assert
      Assert.AreEqual("Birch", palette.Choose(0.0f));
      Assert.AreEqual("Birch", palette.Choose(0.499f));
      Assert.IsNull(palette.Choose(0.5f), "second of two equal candidates is the gap");
      Assert.IsNull(palette.Choose(0.9999f));
    }

    /// <summary>Coarse Monte-Carlo over evenly-spaced hashes recovers an
    /// even split across three enabled species, guarding against
    /// off-by-one regressions in the index→species mapping.</summary>
    [TestMethod]
    public void Choose_ManySamples_EvenAcrossThreeSpecies() {
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
