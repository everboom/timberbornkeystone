using System.Collections.Generic;
using Keystone.Core.Ecology;
using Keystone.Core.Tiles;

namespace Keystone.Core.Survey {

  /// <summary>
  /// 4-connected flood-fill across surveyed surfaces sharing
  /// <c>(Z, IsCave)</c> with the seed surface. The result is the set of
  /// surfaces the seed "belongs to" as a single flat plateau.
  ///
  /// <para>Cliff tiles at the rim of a flat top are included naturally:
  /// they share Z with the interior even though their tag is Cliff. Cave
  /// and non-cave surfaces at the same Z are kept separate (the
  /// <c>IsCave</c> equality check prevents the fill crossing into either
  /// kind from the other).</para>
  ///
  /// <para>Pure simulation code: takes only a <see cref="TerrainSurveyor"/>
  /// (read-only) and returns Core value types. The Mod layer is
  /// responsible for picking the seed (cursor) and visualising the result.</para>
  ///
  /// <para>Performance: a fill on a typical Timberborn map's largest
  /// plateau visits a few thousand surfaces, well under per-frame budget.
  /// The optional <c>maxFillSize</c> cap is a safety belt against pathological
  /// "plateau is the whole map" cases.</para>
  /// </summary>
  public sealed class PlateauFinder {

    #region Constants

    /// <summary>Default cap on flood-fill size. ~70k surfaces is a typical 256x256 single-Z map.</summary>
    public const int DefaultMaxFillSize = 100_000;

    #endregion

    #region Fields

    private readonly TerrainSurveyor _surveyor;

    #endregion

    #region Construction

    public PlateauFinder(TerrainSurveyor surveyor) {
      _surveyor = surveyor;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Find the plateau containing <paramref name="seed"/>. Returns an empty
    /// set if the seed isn't in the survey. The returned set always
    /// contains the seed when non-empty.
    /// </summary>
    /// <param name="seed">Starting surface. Must be a surveyed coordinate.</param>
    /// <param name="maxFillSize">Safety cap. Fill stops when the visited set reaches this size; the partial result is returned. Pass <see cref="int.MaxValue"/> to disable the cap.</param>
    public IReadOnlyCollection<SurfaceCoord> Find(SurfaceCoord seed, int maxFillSize = DefaultMaxFillSize) {
      var result = new HashSet<SurfaceCoord>();
      if (!_surveyor.Surfaces.TryGet(seed, out var seedSurvey)) {
        return result;
      }
      var seedZ = seed.Z;
      var seedIsCave = seedSurvey.IsCave;

      var queue = new Queue<SurfaceCoord>();
      queue.Enqueue(seed);
      result.Add(seed);

      while (queue.Count > 0 && result.Count < maxFillSize) {
        var current = queue.Dequeue();
        TryEnqueueNeighbor(current.X, current.Y + 1, seedZ, seedIsCave, result, queue, maxFillSize); // north
        TryEnqueueNeighbor(current.X + 1, current.Y, seedZ, seedIsCave, result, queue, maxFillSize); // east
        TryEnqueueNeighbor(current.X, current.Y - 1, seedZ, seedIsCave, result, queue, maxFillSize); // south
        TryEnqueueNeighbor(current.X - 1, current.Y, seedZ, seedIsCave, result, queue, maxFillSize); // west
      }

      return result;
    }

    #endregion

    #region Helpers

    private void TryEnqueueNeighbor(
        int nx, int ny, int seedZ, bool seedIsCave,
        HashSet<SurfaceCoord> visited,
        Queue<SurfaceCoord> queue,
        int maxFillSize) {
      if (visited.Count >= maxFillSize) {
        return;
      }
      var neighbor = new SurfaceCoord(nx, ny, seedZ);
      if (visited.Contains(neighbor)) {
        return;
      }
      if (!_surveyor.Surfaces.TryGet(neighbor, out var survey)) {
        return; // no surface at exactly this Z in the neighbor column
      }
      if (survey.IsCave != seedIsCave) {
        return; // cave/non-cave boundary -- different plateau
      }
      visited.Add(neighbor);
      queue.Enqueue(neighbor);
    }

    #endregion

  }

}
