using Keystone.Core.Atmosphere;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Keystone.Core.Tests.Atmosphere {

  /// <summary>
  /// Pins the deterministic-RNG layer of the WetlandMistDirector's
  /// scheduling. Same (day, x, y) + same window/density parameters
  /// must produce the same outcome across runs and reloads. Day-wrap
  /// (despawn earlier in day than spawn) and mid-window-load skip
  /// (spawn moment already past) are the boundary behaviours.
  /// </summary>
  [TestClass]
  public class MistScheduleRollTests {

    #region Window constants

    private const float SpawnStart = 18.5f / 24f;
    private const float SpawnEnd = 19f / 24f;
    private const float DespawnStart = 0f / 24f;
    private const float DespawnEnd = 1f / 24f;

    #endregion

    #region ShouldRollToday

    [TestMethod]
    public void ShouldRollToday_Deterministic_SameDayProducesSameAnswer() {
      var a = MistScheduleRoll.ShouldRollToday(day: 42, foggyMorningProbability: 0.5f);
      var b = MistScheduleRoll.ShouldRollToday(day: 42, foggyMorningProbability: 0.5f);
      Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void ShouldRollToday_ProbabilityZero_NeverPasses() {
      // Sample a range of days; none should pass.
      for (var day = 0; day < 200; day++) {
        Assert.IsFalse(
            MistScheduleRoll.ShouldRollToday(day, foggyMorningProbability: 0f),
            $"day {day} unexpectedly passed at probability 0.");
      }
    }

    [TestMethod]
    public void ShouldRollToday_ProbabilityOne_AlwaysPasses() {
      for (var day = 0; day < 200; day++) {
        Assert.IsTrue(
            MistScheduleRoll.ShouldRollToday(day, foggyMorningProbability: 1f),
            $"day {day} unexpectedly failed at probability 1.");
      }
    }

    [TestMethod]
    public void ShouldRollToday_DifferentDaysIndependentRolls() {
      // A non-zero, non-one probability should produce mixed outcomes
      // across days. Sample 100 days; expect at least some of each.
      var passes = 0;
      var fails = 0;
      for (var day = 1; day <= 100; day++) {
        if (MistScheduleRoll.ShouldRollToday(day, foggyMorningProbability: 0.5f)) passes++;
        else fails++;
      }
      Assert.IsTrue(passes > 5 && fails > 5,
          $"expected mixed outcomes across days; got {passes}/{fails}.");
    }

    #endregion

    #region TryRollTile — density gate

    [TestMethod]
    public void TryRollTile_DensityZero_AlwaysReturnsNull() {
      for (var x = 0; x < 20; x++) {
        for (var y = 0; y < 20; y++) {
          var result = MistScheduleRoll.TryRollTile(
              day: 5, x: x, y: y,
              SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
              density: 0f, currentHourFraction: 0f);
          Assert.IsNull(result, $"density=0 should never schedule tile ({x},{y}).");
        }
      }
    }

    [TestMethod]
    public void TryRollTile_DensityOne_SchedulesWhenWindowsAreFuture() {
      // density=1 → first RNG call always passes the gate. Spawn/despawn
      // times come from subsequent rolls; with sane windows the result
      // is non-null.
      var result = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      Assert.IsNotNull(result);
    }

    #endregion

    #region TryRollTile — determinism

    [TestMethod]
    public void TryRollTile_SameInputs_ProducesIdenticalSchedule() {
      var a = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      var b = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void TryRollTile_DifferentTiles_DifferentScheduleTimes() {
      // (day, x, y) seed difference must surface in the output —
      // otherwise every tile in a chunk would share the same spawn
      // moment.
      var a = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      var b = MistScheduleRoll.TryRollTile(
          day: 5, x: 8, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void TryRollTile_DifferentDays_DifferentScheduleTimes() {
      var a = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      var b = MistScheduleRoll.TryRollTile(
          day: 6, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      Assert.AreNotEqual(a, b);
    }

    #endregion

    #region TryRollTile — day-wrap

    [TestMethod]
    public void TryRollTile_DespawnEarlierThanSpawn_DespawnDayIsNextDay() {
      // SpawnStart=18.5/24, DespawnEnd=1/24 — despawn-window is
      // earlier in the day. Returned despawn timestamp must include
      // the +1 day so the timeline stays monotonic.
      var result = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      Assert.IsNotNull(result);
      Assert.IsTrue(result.Value.DespawnTime > result.Value.SpawnTime,
          "Despawn must always be strictly after spawn on the absolute timeline.");
      Assert.IsTrue(result.Value.DespawnTime > 5f + SpawnEnd,
          "Despawn must land on day 6 (or later) given the day-wrapped window.");
    }

    [TestMethod]
    public void TryRollTile_DespawnLaterThanSpawnSameDay_DespawnDayIsSameDay() {
      // Non-wrapping case: spawn early-morning, despawn late-morning.
      var spawnStart = 8f / 24f;
      var spawnEnd = 8.5f / 24f;
      var despawnStart = 10f / 24f;
      var despawnEnd = 11f / 24f;
      var result = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          spawnStart, spawnEnd, despawnStart, despawnEnd,
          density: 1f, currentHourFraction: 0f);
      Assert.IsNotNull(result);
      Assert.IsTrue(result.Value.DespawnTime < 6f,
          "Same-day windows must keep despawn on day 5.");
    }

    #endregion

    #region TryRollTile — mid-window-load skip

    [TestMethod]
    public void TryRollTile_CurrentHourFractionPastSpawnWindow_ReturnsNull() {
      // Player loads after the spawn window has begun. The rolled
      // spawn moment may be in the past; the roll must skip to avoid
      // compressing the visible lifetime.
      var result = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f,
          currentHourFraction: 23f / 24f);  // past the spawn window entirely
      Assert.IsNull(result);
    }

    [TestMethod]
    public void TryRollTile_CurrentHourFractionBeforeSpawnWindow_StillSchedules() {
      // Roll happens at the start of the spawn window; the rolled
      // spawn moment is necessarily ≥ currentHourFraction.
      var result = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f,
          currentHourFraction: 0f);
      Assert.IsNotNull(result);
    }

    #endregion

    #region TryRollTile — schedule timing bounds

    [TestMethod]
    public void TryRollTile_SpawnTime_FallsWithinSpawnWindowOnTheGivenDay() {
      // Spawn timestamp = day + spawnHourFrac, where spawnHourFrac ∈
      // [SpawnStart, SpawnEnd).
      var result = MistScheduleRoll.TryRollTile(
          day: 5, x: 7, y: 3,
          SpawnStart, SpawnEnd, DespawnStart, DespawnEnd,
          density: 1f, currentHourFraction: 0f);
      Assert.IsNotNull(result);
      Assert.IsTrue(result.Value.SpawnTime >= 5f + SpawnStart);
      Assert.IsTrue(result.Value.SpawnTime <= 5f + SpawnEnd);
    }

    #endregion

  }

}
