using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Keystone.Mod.Diagnostics;
using Timberborn.BlueprintSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateCollectionSystem;
using Timberborn.Timbermesh;

namespace Keystone.Mod.Debug {

  /// <summary>
  /// At PostLoad, dumps every <see cref="TimbermeshSpec"/> reachable
  /// from any loaded root <see cref="Blueprint"/> to a clean CSV at
  /// <c>%USERPROFILE%/Documents/Timberborn/Keystone/mesh-paths.csv</c>.
  /// One row per (root blueprint, child path, mesh path) tuple,
  /// sorted by root blueprint name then by child path.
  ///
  /// <para><b>Why this exists.</b> Mesh paths are JSON strings inside
  /// <see cref="TimbermeshSpec.Model"/> and the naming convention is
  /// inconsistent across content (some live under
  /// <c>NaturalResources/Trees/...</c>, others under
  /// <c>NaturalResources/Crops/...</c>, others under faction-specific
  /// subdirs). Authoring a Keystone blueprint that references a vanilla
  /// mesh by path means knowing the right path -- without this dump,
  /// the answer requires reading SDK source. The dump regenerates on
  /// every load <b>while dev mode is enabled</b> (see
  /// <see cref="KeystoneDevMode"/>), so it stays in sync with whichever
  /// content mods are active without writing into a player's Documents
  /// folder during normal play.</para>
  ///
  /// <para><b>Why a recursive walk.</b> Vanilla flora puts
  /// <see cref="TimbermeshSpec"/> on nested children
  /// (<c>blueprint.Children</c>'s descendants), not at the root.
  /// <see cref="ISpecService.GetSpecs"/> only surfaces top-level
  /// specs; a recursive walk through every root blueprint's children
  /// is the only path that captures the full set.</para>
  ///
  /// <para><b>Output format.</b> CSV with header
  /// <c>blueprint,child_path,mesh_path</c>. <c>child_path</c> is the
  /// slash-joined sequence of child names from the root down to (but
  /// not including) the node carrying the <see cref="TimbermeshSpec"/>;
  /// empty string means the spec is on the root blueprint itself.
  /// </para>
  ///
  /// <para><b>No player.log spam.</b> A single one-line summary is
  /// logged on success or failure; the full per-mesh list lives only
  /// in the CSV.</para>
  /// </summary>
  public sealed class MeshPathProbe : IPostLoadableSingleton {

    private const string OutputDirRelative = "Timberborn/Keystone";
    private const string OutputFileName = "mesh-paths.csv";

    private readonly TemplateCollectionService _templates;

    public MeshPathProbe(TemplateCollectionService templates) {
      _templates = templates;
    }

    /// <inheritdoc />
    public void PostLoad() {
      // Developer authoring aid only — don't write a dev CSV into a
      // player's Documents folder on every load. Gated behind dev mode,
      // the same switch the perf window's debug rows use.
      if (!KeystoneDevMode.IsEnabled) return;

      List<(string Blueprint, string ChildPath, string MeshPath)> rows;
      try {
        rows = CollectRows();
      } catch (Exception ex) {
        KeystoneLog.Verbose(
            $"[Keystone] MeshPathProbe: enumeration threw " +
            $"{ex.GetType().Name}: {ex.Message}. Skipping dump.");
        return;
      }

      string path;
      try {
        path = ResolveOutputPath();
      } catch (Exception ex) {
        KeystoneLog.Verbose(
            $"[Keystone] MeshPathProbe: could not resolve output path: " +
            $"{ex.GetType().Name}: {ex.Message}. Dump skipped.");
        return;
      }

      try {
        WriteCsv(path, rows);
      } catch (Exception ex) {
        KeystoneLog.Verbose(
            $"[Keystone] MeshPathProbe: write to '{path}' threw " +
            $"{ex.GetType().Name}: {ex.Message}. Dump skipped.");
        return;
      }

      var blueprintCount = rows.Select(r => r.Blueprint).Distinct().Count();
      KeystoneLog.Verbose(
          $"[Keystone] MeshPathProbe: wrote {rows.Count} mesh path(s) " +
          $"across {blueprintCount} blueprint(s) to {path}.");
    }

    private List<(string Blueprint, string ChildPath, string MeshPath)> CollectRows() {
      var rows = new List<(string Blueprint, string ChildPath, string MeshPath)>();
      foreach (var root in _templates.AllTemplates) {
        if (root == null || string.IsNullOrEmpty(root.Name)) continue;
        WalkBlueprint(root, rootName: root.Name, currentPath: "", rows: rows);
      }
      // Stable sort for clean diffs across loads.
      rows.Sort((a, b) => {
        var byBlueprint = string.CompareOrdinal(a.Blueprint, b.Blueprint);
        if (byBlueprint != 0) return byBlueprint;
        var byChild = string.CompareOrdinal(a.ChildPath, b.ChildPath);
        return byChild != 0 ? byChild : string.CompareOrdinal(a.MeshPath, b.MeshPath);
      });
      return rows;
    }

    /// <summary>Recursively walks <paramref name="node"/>'s spec list
    /// and children. Each <see cref="TimbermeshSpec"/> encountered
    /// emits a row labelled with the root blueprint's name and the
    /// slash-joined child path from root down to the node carrying
    /// the spec.</summary>
    private static void WalkBlueprint(
        Blueprint node, string rootName, string currentPath,
        List<(string Blueprint, string ChildPath, string MeshPath)> rows) {
      foreach (var spec in node.Specs) {
        if (spec is TimbermeshSpec mesh) {
          var meshPath = mesh.Model?.Path;
          if (!string.IsNullOrEmpty(meshPath)) {
            rows.Add((rootName, currentPath, meshPath!));
          }
        }
      }
      foreach (var child in node.Children) {
        if (child == null) continue;
        var childName = child.Name ?? "<unnamed>";
        var nextPath = string.IsNullOrEmpty(currentPath)
            ? childName
            : currentPath + "/" + childName;
        WalkBlueprint(child, rootName, nextPath, rows);
      }
    }

    private static string ResolveOutputPath() {
      var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
      if (string.IsNullOrEmpty(documents)) {
        throw new InvalidOperationException(
            "Environment.SpecialFolder.MyDocuments resolved to empty.");
      }
      var dir = Path.Combine(documents, OutputDirRelative);
      Directory.CreateDirectory(dir);
      return Path.Combine(dir, OutputFileName);
    }

    private static void WriteCsv(
        string path, List<(string Blueprint, string ChildPath, string MeshPath)> rows) {
      var sb = new StringBuilder();
      sb.AppendLine("blueprint,child_path,mesh_path");
      foreach (var (blueprint, childPath, meshPath) in rows) {
        sb.Append(EscapeCsv(blueprint));
        sb.Append(',');
        sb.Append(EscapeCsv(childPath));
        sb.Append(',');
        sb.Append(EscapeCsv(meshPath));
        sb.Append('\n');
      }
      File.WriteAllText(path, sb.ToString());
    }

    private static string EscapeCsv(string value) {
      if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

  }

}
