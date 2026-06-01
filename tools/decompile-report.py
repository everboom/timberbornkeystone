#!/usr/bin/env python3
"""Decompile a set of .NET DLLs to a single Markdown report.

Wraps ``ilspycmd`` (the ICSharpCode ILSpy CLI, installed as a dotnet global
tool) and concatenates the resulting C# source into one file. Output is
sectioned per DLL; within each DLL, each decompiled .cs file becomes a
subsection. Boilerplate that ILSpy emits (assembly attributes, Unity's
generated MonoScript type tables) is filtered out by default.

Intended use: produce a raw, scannable surface dump for a third-party Timberborn
mod so a human (or an LLM) can read it linearly and write the Keystone-usage
companion doc under ``docs/`` -- analogous to how ``generate-api-cache.ps1``
produces ``docs/timberborn-api-full.md``.

Examples
--------

Decompile a single mod's DLLs (one directory) to ``dump/modsettings.md``::

    python tools/decompile-report.py \\
        --source "<steam-library>/steamapps/workshop/content/1062090/<item-id>/version-1.0/Scripts" \\
        --out dump/modsettings.md

Decompile an explicit set of DLLs::

    python tools/decompile-report.py \\
        --dll path/to/Foo.dll path/to/Bar.dll \\
        --out dump/foobar.md

Use a custom title and skip extra files::

    python tools/decompile-report.py \\
        --source ... \\
        --title "ModdableTimberborn" \\
        --skip-name "*Patches" \\
        --out dump/moddable.md
"""

from __future__ import annotations

import argparse
import fnmatch
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

# Files ILSpy emits that almost never carry useful API surface.
DEFAULT_SKIP_NAMES = (
    "AssemblyInfo.cs",
    "UnitySourceGeneratedAssemblyMonoScriptTypes_v1.cs",
)

# Directories within ILSpy's per-DLL output that we don't want to dump.
DEFAULT_SKIP_DIRS = (
    "Properties",
)


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Decompile DLLs to a single Markdown report.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    src = p.add_mutually_exclusive_group(required=True)
    src.add_argument(
        "--source",
        type=Path,
        help="Directory containing the DLLs to decompile. Non-recursive.",
    )
    src.add_argument(
        "--dll",
        type=Path,
        nargs="+",
        help="Explicit list of DLLs to decompile.",
    )

    p.add_argument(
        "--out",
        type=Path,
        required=True,
        help="Output Markdown path. Will be overwritten.",
    )
    p.add_argument(
        "--title",
        type=str,
        default=None,
        help="Title for the report (default: derived from the source dir or first DLL).",
    )
    p.add_argument(
        "--filter",
        type=str,
        default="*.dll",
        help="Filename glob for --source (default: *.dll).",
    )
    p.add_argument(
        "--skip-name",
        type=str,
        action="append",
        default=[],
        help=(
            "Additional filename glob(s) to skip when emitting decompiled "
            "files. May be repeated. Defaults already strip AssemblyInfo "
            "and Unity MonoScript tables."
        ),
    )
    p.add_argument(
        "--skip-dir",
        type=str,
        action="append",
        default=[],
        help=(
            "Additional directory names (basename match) to skip when "
            "walking ILSpy output. May be repeated."
        ),
    )
    p.add_argument(
        "--ilspy",
        type=str,
        default="ilspycmd",
        help="Path to ilspycmd binary (default: PATH lookup).",
    )
    p.add_argument(
        "--keep-temp",
        action="store_true",
        help="Don't delete the per-DLL decompiled source tree. Useful for "
        "looking at the raw output ILSpy produced.",
    )
    p.add_argument(
        "--quiet",
        action="store_true",
        help="Suppress per-DLL progress messages.",
    )
    return p.parse_args()


def collect_dlls(args: argparse.Namespace) -> list[Path]:
    if args.dll:
        for d in args.dll:
            if not d.is_file():
                sys.exit(f"DLL not found: {d}")
        return list(args.dll)
    if not args.source.is_dir():
        sys.exit(f"Source directory not found: {args.source}")
    dlls = sorted(args.source.glob(args.filter))
    if not dlls:
        sys.exit(f"No DLLs matched {args.filter!r} in {args.source}")
    return dlls


def derive_title(args: argparse.Namespace, dlls: list[Path]) -> str:
    if args.title:
        return args.title
    if args.source:
        # E.g. ".../1062090/3283831040/version-1.0/Scripts" -> two-level hint.
        parts = args.source.resolve().parts
        return "/".join(parts[-2:]) if len(parts) >= 2 else parts[-1]
    return dlls[0].stem


def run_ilspy(ilspy: str, dll: Path, out_dir: Path, quiet: bool) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    # -p projects the decompiled output to a folder tree (one file per type).
    cmd = [ilspy, str(dll), "-o", str(out_dir), "-p"]
    try:
        proc = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            check=False,
        )
    except FileNotFoundError:
        sys.exit(
            f"Could not find ilspycmd at {ilspy!r}. Install with "
            "'dotnet tool install -g ilspycmd' or pass --ilspy <path>."
        )
    if proc.returncode != 0:
        # ilspycmd can return non-zero on missing dependencies even when it
        # produced useful output. Don't bail; just log.
        if not quiet:
            print(
                f"  ilspycmd exit code {proc.returncode} on {dll.name}: "
                f"{proc.stderr.strip().splitlines()[-1] if proc.stderr else '<no stderr>'}",
                file=sys.stderr,
            )


def collect_cs_files(
    out_dir: Path,
    skip_name_globs: tuple[str, ...],
    skip_dirs: tuple[str, ...],
) -> list[Path]:
    files: list[Path] = []
    for path in out_dir.rglob("*.cs"):
        # Skip if any directory component matches a skip-dir.
        if any(part in skip_dirs for part in path.relative_to(out_dir).parts):
            continue
        if any(fnmatch.fnmatch(path.name, g) for g in skip_name_globs):
            continue
        files.append(path)
    # Group by parent dir (namespace folder) then by name, for stable ordering.
    files.sort(key=lambda p: (p.parent.relative_to(out_dir).as_posix(), p.name.lower()))
    return files


def main() -> None:
    args = parse_args()
    dlls = collect_dlls(args)
    title = derive_title(args, dlls)

    skip_name_globs = tuple(DEFAULT_SKIP_NAMES) + tuple(args.skip_name)
    skip_dirs = tuple(DEFAULT_SKIP_DIRS) + tuple(args.skip_dir)

    args.out.parent.mkdir(parents=True, exist_ok=True)

    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    source_label = str(args.source) if args.source else ", ".join(str(d) for d in dlls)

    with args.out.open("w", encoding="utf-8", newline="\n") as out:
        out.write(f"# Decompilation report: {title}\n\n")
        out.write(
            f"Generated by `tools/decompile-report.py` on {timestamp}.\n\n"
            f"Source: `{source_label}`\n\n"
            f"DLLs (in order): {', '.join('`' + d.name + '`' for d in dlls)}.\n\n"
            "Skipped filename patterns: "
            f"{', '.join('`' + g + '`' for g in skip_name_globs)}.\n\n"
            "---\n\n"
        )

        with tempfile.TemporaryDirectory(prefix="decompile-report-") as tmp:
            tmp_root = Path(tmp)
            for dll in dlls:
                if not args.quiet:
                    print(f"Decompiling {dll.name}...", file=sys.stderr)
                dll_tmp = tmp_root / dll.stem
                run_ilspy(args.ilspy, dll, dll_tmp, args.quiet)
                cs_files = collect_cs_files(dll_tmp, skip_name_globs, skip_dirs)

                out.write(f"## `{dll.name}`\n\n")
                if not cs_files:
                    out.write("_No source files emitted by ilspycmd._\n\n")
                    continue
                out.write(f"{len(cs_files)} type(s) emitted.\n\n")

                for cs in cs_files:
                    rel = cs.relative_to(dll_tmp).as_posix()
                    out.write(f"### `{rel}`\n\n")
                    out.write("```csharp\n")
                    try:
                        text = cs.read_text(encoding="utf-8", errors="replace")
                    except OSError as e:
                        text = f"// failed to read {rel}: {e}"
                    # Strip trailing whitespace per line; preserve internal blanks.
                    out.write("\n".join(line.rstrip() for line in text.splitlines()))
                    if not text.endswith("\n"):
                        out.write("\n")
                    out.write("```\n\n")

                if args.keep_temp:
                    keep_dir = args.out.parent / f"{args.out.stem}.raw" / dll.stem
                    keep_dir.parent.mkdir(parents=True, exist_ok=True)
                    if keep_dir.exists():
                        shutil.rmtree(keep_dir)
                    shutil.copytree(dll_tmp, keep_dir)

    if not args.quiet:
        print(f"Wrote {args.out} ({args.out.stat().st_size:,} bytes).", file=sys.stderr)


if __name__ == "__main__":
    main()
