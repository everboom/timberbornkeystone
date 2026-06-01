using System;
using System.Linq;
using Keystone.Core.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Buildings {

  [TestClass]
  public class CaseTolerantNameSetTests {

    private static CaseTolerantNameSet Make(params string[] names) =>
        new CaseTolerantNameSet("test", names);

    #region Match — exact-first, CI-fallback

    [TestMethod]
    public void Match_ExactName_ReturnsExact() {
      var set = Make("FarmHouse.IronTeeth", "Lantern.Folktails");
      var kind = set.Match("Lantern.Folktails", out var canonical);
      Assert.AreEqual(NameMatch.Exact, kind);
      Assert.AreEqual("Lantern.Folktails", canonical);
    }

    [TestMethod]
    public void Match_DifferentCase_ReturnsFallbackWithCanonicalSpelling() {
      var set = Make("Farmhouse.Emberpelts");
      var kind = set.Match("FARMHOUSE.EMBERPELTS", out var canonical);
      Assert.AreEqual(NameMatch.CaseInsensitiveFallback, kind);
      // canonical is the LISTED spelling, not the query — that's what lets
      // the drift reporter say "list says X, runtime is Y".
      Assert.AreEqual("Farmhouse.Emberpelts", canonical);
    }

    [TestMethod]
    public void Match_NoFold_ReturnsNone() {
      var set = Make("Lantern.Folktails");
      Assert.AreEqual(NameMatch.None, set.Match("Lodge.Folktails", out var canonical));
      Assert.IsNull(canonical);
    }

    [TestMethod]
    public void Match_NullOrEmpty_ReturnsNone() {
      var set = Make("Lantern.Folktails");
      Assert.AreEqual(NameMatch.None, set.Match("", out _));
      Assert.AreEqual(NameMatch.None, set.Match(null!, out _));
    }

    #endregion

    #region Contains

    [TestMethod]
    public void Contains_ExactAndFallback_True_UnrelatedFalse() {
      var set = Make("Lantern.Folktails");
      Assert.IsTrue(set.Contains("Lantern.Folktails"));   // exact
      Assert.IsTrue(set.Contains("lantern.folktails"));   // CI fallback
      Assert.IsFalse(set.Contains("Lodge.Folktails"));
      Assert.IsFalse(set.Contains(""));
      Assert.IsFalse(set.Contains(null!));
    }

    #endregion

    #region Construction guards

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void Ctor_ExactDuplicate_Throws() {
      Make("Lantern.Folktails", "Lantern.Folktails");
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void Ctor_CaseFoldCollision_Throws() {
      // Two entries differing only by case would make the fallback
      // ambiguous (one folded key, two canonical spellings) -- must throw
      // at construction so the authoring mistake surfaces at startup.
      Make("Lantern.Folktails", "lantern.folktails");
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void Ctor_EmptyEntry_Throws() {
      Make("Lantern.Folktails", "");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void Ctor_NullEnumerable_Throws() {
      _ = new CaseTolerantNameSet("test", null!);
    }

    #endregion

    #region Names / Count

    [TestMethod]
    public void Names_ReturnsCanonicalSpellings() {
      var set = Make("A.X", "B.X");
      CollectionAssert.AreEquivalent(new[] { "A.X", "B.X" }, set.Names.ToArray());
      Assert.AreEqual(2, set.Count);
    }

    #endregion
  }

}
