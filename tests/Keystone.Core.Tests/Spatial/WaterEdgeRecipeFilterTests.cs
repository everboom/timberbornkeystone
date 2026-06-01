using Keystone.Core.Spatial;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Spatial {

  [TestClass]
  public class WaterEdgeRecipeFilterTests {

    [TestMethod]
    public void Name_IsWaterEdge() {
      var sut = new WaterEdgeRecipeFilter(null!);
      Assert.AreEqual("WaterEdge", sut.Name);
    }

  }

  [TestClass]
  public class NearWaterRecipeFilterTests {

    [TestMethod]
    public void Name_IsNearWater() {
      var sut = new NearWaterRecipeFilter(null!);
      Assert.AreEqual("NearWater", sut.Name);
    }

  }

  [TestClass]
  public class NearShoreRecipeFilterTests {

    [TestMethod]
    public void Name_IsNearShore() {
      var sut = new NearShoreRecipeFilter(null!);
      Assert.AreEqual("NearShore", sut.Name);
    }

  }

}
