namespace Keystone.Mod.Diagnostics.SelfTests {

  /// <summary>
  /// Surface exposed by Keystone singletons whose successful
  /// initialisation is worth verifying after game load. Implementers
  /// set <see cref="IsLoaded"/> to <c>true</c> at the end of their
  /// final lifecycle step (typically <c>PostLoad</c>), so a failure to
  /// complete -- whether the step threw, was skipped, or hung -- is
  /// observable as <c>IsLoaded == false</c>.
  ///
  /// <para><b>Why this interface exists.</b> A loader that throws in
  /// PostLoad logs the exception to <c>Player.log</c> and otherwise
  /// fails silently from the player's perspective -- downstream code
  /// just sees an empty catalog or a null cache. <see cref="LoaderSurvivalTest"/>
  /// iterates every <c>IKeystoneLoadStatus</c> bound at startup and
  /// flags any whose <see cref="IsLoaded"/> is <c>false</c>, so a
  /// failed load is surfaced on the Test tab the next time the
  /// developer runs the self-test battery rather than only when they
  /// happen to grep <c>Player.log</c>.</para>
  ///
  /// <para><b>Adding a new loader.</b> Implement this interface on the
  /// loader, set <see cref="IsLoaded"/> at the end of the final
  /// lifecycle step, and add a <c>MultiBind&lt;IKeystoneLoadStatus&gt;().To&lt;...&gt;()</c>
  /// line in <c>KeystoneConfigurator</c>. The test picks it up
  /// automatically -- no changes needed to <see cref="LoaderSurvivalTest"/>
  /// itself.</para>
  /// </summary>
  public interface IKeystoneLoadStatus {

    /// <summary>Short, human-readable label for this loader, used in
    /// the self-test report. Conventionally the implementing type's
    /// name (e.g. <c>"BuildingCatalogLoader"</c>).</summary>
    string LoaderName { get; }

    /// <summary>True once the loader's final initialisation step has
    /// run to completion. Set inside the lifecycle method (typically
    /// <c>PostLoad</c>) as its last statement; an exception earlier in
    /// the method leaves this false.</summary>
    bool IsLoaded { get; }

  }

}
