#!/usr/bin/env python3
"""Offline C# compile check for the Unity project, using Roslyn and Unity's own references.

Compiles the assemblies that contain our code with the same reference DLLs and #define set
that Unity 2022.3.22f1 uses, so that errors and their positions match the Unity Console
without spending an Editor round:

  Assets/VRC_Katamari/Runtime                 -> VRCKatamari.Runtime
  Assets/VRC_Katamari/Editor/MapGreybox       -> VRCKatamari.Editor   (its asmdef)
  Assets/VRC_Katamari/Editor (everything else)-> Assembly-CSharp-Editor (Unity's default
                                                 editor assembly; it has no asmdef of its own)
  Assets/VRC_Katamari/Tests/EditMode          -> VRCKatamari.Tests.EditMode

This is a C# compile check only. UdonSharp's language subset (no structs, generics, List,
properties, ...) is enforced by the U# compiler, not by Roslyn, and nothing here runs tests
or proves scene behaviour.

Usage:
  python3 Tools/csharp_check/check.py                # check the working tree
  python3 Tools/csharp_check/check.py --project /tmp/snapshot/VRChatKatamariDanacyUnity
  python3 Tools/csharp_check/check.py --json
  python3 Tools/csharp_check/check.py --refresh      # re-probe Windows / re-fetch refs
"""

from __future__ import annotations

import sys

# The cache and the temporary build directories must stay outside the repository, and that
# includes __pycache__: refuse to write bytecode next to the checked-in scripts.
sys.dont_write_bytecode = True

import argparse
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from pathlib import Path

CHECK_DIR = Path(__file__).resolve().parent
REPO_ROOT = CHECK_DIR.parents[1]
CACHE_ROOT = Path(
    os.environ.get("VRCK_CSHARP_CHECK_CACHE", Path.home() / ".cache" / "vrc-katamari-refasm")
)
DOTNET_ROOT = Path(os.environ.get("VRCK_DOTNET_ROOT", Path.home() / ".dotnet"))

DIAGNOSTIC_RE = re.compile(
    r"^(?P<path>.+?)\((?P<line>\d+),(?P<col>\d+)\): (?P<severity>error|warning) "
    r"(?P<code>[A-Z]{2,}\d+): (?P<message>.*)$"
)
# Diagnostics without a source location (e.g. error CS0006: Metadata file ... not found).
LOCATIONLESS_RE = re.compile(
    r"^(?:(?P<path>[^:]+): )?(?P<severity>error|warning) "
    r"(?P<code>[A-Z]{2,}\d+): (?P<message>.*)$"
)


@dataclass
class Unit:
    """One Unity compilation unit (one assembly)."""

    name: str
    # Directory that bounds the sources, relative to the Unity project root.
    root: str
    # True: the unit is exactly the tree under the (single) asmdef found at/below root.
    # False: the unit is Unity's default assembly: everything under root that is not
    #        covered by a nested asmdef.
    asmdef: bool
    sources: list[Path] = field(default_factory=list)
    dependencies: list[str] = field(default_factory=list)


UNITS = [
    Unit("VRCKatamari.Runtime", "Assets/VRC_Katamari/Runtime", asmdef=True),
    Unit("VRCKatamari.Editor", "Assets/VRC_Katamari/Editor", asmdef=True),
    Unit("Assembly-CSharp-Editor", "Assets/VRC_Katamari/Editor", asmdef=False),
    Unit("VRCKatamari.Tests.EditMode", "Assets/VRC_Katamari/Tests/EditMode", asmdef=True),
]


class CheckError(RuntimeError):
    pass


def resolve_csc(explicit: Path | None) -> Path:
    """Prefer the Roslyn build Unity ships (4.3.1) so cascading diagnostics match exactly."""
    if explicit:
        if not explicit.exists():
            raise CheckError(f"--csc path does not exist: {explicit}")
        return explicit
    bundled = CACHE_ROOT / "roslyn" / "csc.dll"
    if bundled.exists() and (CACHE_ROOT / "roslyn" / "Microsoft.CodeAnalysis.CSharp.dll").exists():
        return bundled
    sdk = sorted((DOTNET_ROOT / "sdk").glob("*/Roslyn/bincore/csc.dll"))
    if not sdk:
        raise CheckError(
            f"no csc.dll found (looked for {bundled} and {DOTNET_ROOT / 'sdk'}/*/Roslyn/bincore/)"
        )
    return sdk[-1]


def load_manifest(refresh: bool, host: str) -> dict:
    manifest_path = CACHE_ROOT / "manifest.json"
    if not manifest_path.exists() or refresh:
        import bootstrap

        try:
            bootstrap.bootstrap(
                host,
                os.environ.get(
                    "VRCK_WINDOWS_PROJECT", r"D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity"
                ),
                refresh=refresh,
            )
        except (bootstrap.BootstrapError, subprocess.TimeoutExpired) as exc:
            raise CheckError(f"bootstrap failed: {exc}") from exc
    with manifest_path.open(encoding="utf-8") as handle:
        manifest = json.load(handle)
    if manifest.get("schema") != 1:
        raise CheckError(f"unsupported manifest schema in {manifest_path}")
    return manifest


def find_asmdef_dirs(root: Path) -> list[Path]:
    if not root.is_dir():
        return []
    return sorted({p.parent for p in root.rglob("*.asmdef")})


def collect_sources(project: Path, unit: Unit) -> list[Path]:
    """Return the .cs files Unity compiles into this assembly, relative to `project`."""
    root = project / unit.root
    if not root.is_dir():
        raise CheckError(f"{unit.name}: source directory not found: {root}")
    asmdef_dirs = find_asmdef_dirs(root)
    if unit.asmdef:
        if len(asmdef_dirs) != 1:
            raise CheckError(
                f"{unit.name}: expected exactly one asmdef under {root}, found {len(asmdef_dirs)}"
            )
        roots = asmdef_dirs
    else:
        roots = [root]
    excluded = [] if unit.asmdef else asmdef_dirs
    files: list[Path] = []
    for source_root in roots:
        for path in source_root.rglob("*.cs"):
            if any(parent in excluded for parent in path.parents):
                continue
            files.append(path)
    files = sorted(files)
    if not files:
        raise CheckError(f"{unit.name}: no .cs files found under {root}")
    return files


def reference_paths(manifest: dict, unit: Unit, built: dict[str, Path]) -> list[Path]:
    settings = manifest["assemblies"].get(unit.name)
    if settings is None:
        raise CheckError(
            f"assembly {unit.name!r} is missing from the reference manifest "
            f"({CACHE_ROOT / 'manifest.json'}); run --refresh after opening the project in Unity"
        )
    paths: list[Path] = []
    seen: set[str] = set()
    for name in settings.get("references", []):
        if name in seen:
            continue
        seen.add(name)
        rel = manifest["references"].get(name)
        if rel is None:
            raise CheckError(f"{unit.name}: reference {name!r} has no mirrored DLL")
        paths.append(CACHE_ROOT / rel)
    for name in settings.get("project_references", []):
        if name in built:
            path = built[name]
        else:
            rel = manifest["references"].get(name)
            if rel is None:
                raise CheckError(f"{unit.name}: project reference {name!r} has no mirrored DLL")
            path = CACHE_ROOT / rel
        if name in seen:
            continue
        seen.add(name)
        paths.append(path)
    missing = [str(p) for p in paths if not p.exists()]
    if missing:
        raise CheckError(f"{unit.name}: missing reference DLLs:\n  " + "\n  ".join(missing[:10]))
    return paths


def response_file(
    manifest: dict,
    unit: Unit,
    project: Path,
    refs: list[Path],
    output: Path,
    debug_symbols: bool = False,
) -> str:
    settings = manifest["assemblies"][unit.name]
    defines = settings.get("defines") or []
    langversion = manifest.get("langversion") or "9.0"
    lines = [
        "-nologo",
        "-nostdlib+",
        "-target:library",
        f"-langversion:{langversion}",
        "-warn:4",
        "-nowarn:0169,USG0001",
        "-unsafe-",
        f'-out:"{output}"',
        f'-define:{";".join(defines)}',
    ]
    if debug_symbols:
        # Portable PDBs next to the DLL, so a runner can report file:line for a stack frame.
        lines.append("-debug:portable")
    lines += [f'-r:"{path}"' for path in refs]
    lines += [f'"{path.relative_to(project).as_posix()}"' for path in unit.sources]
    # csc response files are UTF-8; every path here is ASCII on this project.
    return "\n".join(lines) + "\n"


def run_csc(dotnet: str, csc: Path, rsp: Path, project: Path) -> tuple[int, str]:
    # The Unity-matching Roslyn comes from the Microsoft.Net.Compilers.Toolset NuGet package
    # and targets net6.0, so let the newer local runtime run it.
    env = dict(os.environ, DOTNET_ROLL_FORWARD="LatestMajor")
    proc = subprocess.run(
        [dotnet, str(csc), "-noconfig", f"@{rsp}"],
        cwd=project,
        capture_output=True,
        text=True,
        timeout=600,
        env=env,
    )
    return proc.returncode, proc.stdout + proc.stderr


def parse_diagnostics(output: str, project: Path) -> tuple[list[dict], list[str]]:
    diagnostics: list[dict] = []
    other: list[str] = []
    for line in output.splitlines():
        text = line.strip()
        if not text:
            continue
        match = DIAGNOSTIC_RE.match(text)
        if match is None:
            match = LOCATIONLESS_RE.match(text)
            if match is None:
                other.append(text)
                continue
            diagnostics.append(
                {
                    "path": (match.group("path") or "").strip(),
                    "line": 0,
                    "column": 0,
                    "severity": match.group("severity"),
                    "code": match.group("code"),
                    "message": match.group("message"),
                }
            )
            continue
        path = Path(match.group("path"))
        if path.is_absolute():
            try:
                path = path.resolve().relative_to(project.resolve())
            except ValueError:
                pass
        diagnostics.append(
            {
                "path": path.as_posix(),
                "line": int(match.group("line")),
                "column": int(match.group("col")),
                "severity": match.group("severity"),
                "code": match.group("code"),
                "message": match.group("message"),
            }
        )
    return diagnostics, other


def format_diagnostic(d: dict) -> str:
    return f"{d['path']}({d['line']},{d['column']}): {d['severity']} {d['code']}: {d['message']}"


def topological(units: list[Unit]) -> list[Unit]:
    by_name = {u.name: u for u in units}
    ordered: list[Unit] = []
    state: dict[str, int] = {}

    def visit(unit: Unit, stack: list[str]) -> None:
        if state.get(unit.name) == 2:
            return
        if state.get(unit.name) == 1:
            raise CheckError("circular assembly dependency: " + " -> ".join(stack + [unit.name]))
        state[unit.name] = 1
        for dep in unit.dependencies:
            if dep in by_name:
                visit(by_name[dep], stack + [unit.name])
        state[unit.name] = 2
        ordered.append(unit)

    for unit in units:
        visit(unit, [])
    return ordered


def run_local(ordered, project: Path, manifest: dict, args, dotnet: Path, csc_path: Path) -> list[dict]:
    """Compile the units on this machine and return one result dict per unit.

    Units without a dependency between them run concurrently; the reference DLLs are
    read-only, so that is safe.
    """
    workdir = Path(tempfile.mkdtemp(prefix="vrck-csharp-check-"))
    # With --output-dir the DLLs (and PDBs) are kept there instead of in the throw-away
    # workdir; the compile itself is identical.
    outdir = args.output_dir.resolve() if getattr(args, "output_dir", None) else None
    if outdir is not None:
        outdir.mkdir(parents=True, exist_ok=True)
    built: dict[str, Path] = {}
    failed: set[str] = set()
    unit_names = {u.name for u in ordered}
    results: list[dict] = []

    # Levels by dependency depth (ordered is topological, so dependencies come first).
    depth: dict[str, int] = {}
    for unit in ordered:
        deps = [d for d in unit.dependencies if d in unit_names]
        depth[unit.name] = 0 if not deps else max(depth[d] + 1 for d in deps)

    def compile_one(unit):
        refs = reference_paths(manifest, unit, built)
        output = (outdir or workdir) / f"{unit.name}.dll"
        rsp = workdir / f"{unit.name}.rsp"
        rsp.write_text(
            response_file(manifest, unit, project, refs, output, debug_symbols=outdir is not None),
            encoding="utf-8",
        )
        unit_started = time.perf_counter()
        returncode, output_text = run_csc(str(dotnet), csc_path, rsp, project)
        unit_elapsed = time.perf_counter() - unit_started
        diagnostics, other = parse_diagnostics(output_text, project)
        return unit, returncode, unit_elapsed, diagnostics, other, output

    try:
        for level in sorted({d for d in depth.values()}):
            runnable = []
            for unit in ordered:
                if depth[unit.name] != level:
                    continue
                skipped = [d for d in unit.dependencies if d in failed]
                if skipped:
                    results.append(
                        {
                            "assembly": unit.name,
                            "skipped": f"dependency failed: {', '.join(skipped)}",
                            "errors": [],
                            "warnings": [],
                        }
                    )
                    failed.add(unit.name)
                else:
                    runnable.append(unit)
            if not runnable:
                continue
            with ThreadPoolExecutor(max_workers=min(4, len(runnable))) as pool:
                outcomes = list(pool.map(compile_one, runnable))
            for unit, returncode, unit_elapsed, diagnostics, other, output in outcomes:
                errors = [d for d in diagnostics if d["severity"] == "error"]
                warnings = [d for d in diagnostics if d["severity"] == "warning"]
                if returncode not in (0, 1) or (returncode == 1 and not errors):
                    raise CheckError(
                        f"{unit.name}: csc failed with exit code {returncode}\n"
                        + "\n".join(other[:20])
                    )
                results.append(
                    {
                        "assembly": unit.name,
                        "skipped": None,
                        "seconds": round(unit_elapsed, 2),
                        "errors": errors,
                        "warnings": warnings,
                        "other": other,
                    }
                )
                if errors:
                    failed.add(unit.name)
                else:
                    built[unit.name] = output
    finally:
        if args.keep_workdir:
            print(f"workdir kept: {workdir}", file=sys.stderr)
        else:
            shutil.rmtree(workdir, ignore_errors=True)
    # Keep the report in Unity's assembly order (Runtime first) for stable output.
    order = {u.name: index for index, u in enumerate(ordered)}
    results.sort(key=lambda result: order[result["assembly"]])
    return results


def bootstrap_error():
    import bootstrap

    return (bootstrap.BootstrapError, subprocess.TimeoutExpired)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Offline C# compile check for VRC_Katamari.")
    parser.add_argument(
        "--project",
        type=Path,
        default=REPO_ROOT / "VRChatKatamariDanacyUnity",
        help="Unity project root (default: the checkout next to this script)",
    )
    parser.add_argument(
        "--unit",
        action="append",
        default=None,
        metavar="NAME",
        help="check this assembly and its project dependencies (repeatable); default: all",
    )
    parser.add_argument("--json", action="store_true", help="emit machine-readable JSON")
    parser.add_argument("--warnings", action="store_true", help="also print compiler warnings")
    parser.add_argument("--refresh", action="store_true", help="re-probe Windows and re-fetch references")
    parser.add_argument(
        "--csc",
        type=Path,
        default=None,
        help="csc.dll to run (default: the Unity-matching Roslyn from the cache, else the .NET SDK)",
    )
    parser.add_argument("--keep-workdir", action="store_true", help="keep the temporary compile directory")
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=None,
        help="local backend only: write the compiled assemblies (with portable PDBs) here "
        "instead of discarding them (used by run_tests.py)",
    )
    parser.add_argument("--timings", action="store_true", help="print per-assembly compile times")
    parser.add_argument(
        "--backend",
        choices=["local", "windows"],
        default="local",
        help="local = mirrored reference DLLs + Unity's Roslyn release (default); "
        "windows = compile on the Windows PC with Unity's own csc",
    )
    parser.add_argument("--ssh-host", default=os.environ.get("VRCK_SSH_HOST", "pc114514"))
    parser.add_argument("--keep-remote", action="store_true", help="keep the %%TEMP%% staging directory on Windows")
    parser.add_argument("--print-defines", action="store_true", help="print the #define set Unity uses per assembly and exit")
    args = parser.parse_args(argv)

    started = time.perf_counter()
    project = args.project.resolve()
    try:
        manifest = load_manifest(args.refresh, args.ssh_host)
        if args.print_defines:
            for name in [u.name for u in UNITS]:
                settings = manifest["assemblies"].get(name)
                if settings:
                    print(f"# {name}")
                    print(";".join(settings.get("defines") or []))
                    print()
            return 0
        for unit in UNITS:
            unit.sources = collect_sources(project, unit)
            unit.dependencies = list(manifest["assemblies"][unit.name].get("project_references", []))
        by_name = {u.name: u for u in UNITS}
        if args.unit:
            unknown = set(args.unit) - set(by_name)
            if unknown:
                raise CheckError(f"unknown unit(s): {', '.join(sorted(unknown))}")
            keep: set[str] = set()

            def add(name: str) -> None:
                if name in keep:
                    return
                keep.add(name)
                for dependency in by_name[name].dependencies:
                    if dependency in by_name:
                        add(dependency)

            for name in args.unit:
                add(name)
            units = [u for u in UNITS if u.name in keep]
        else:
            units = list(UNITS)
        ordered = topological(units)

        if args.output_dir is not None and args.backend != "local":
            raise CheckError("--output-dir needs --backend local")
        if args.backend == "windows":
            import windows_backend

            try:
                results = windows_backend.compile_on_windows(
                    ordered,
                    project,
                    host=args.ssh_host,
                    keep_remote=args.keep_remote,
                    verbose=args.keep_workdir,
                )
            except bootstrap_error() as exc:
                raise CheckError(f"windows backend failed: {exc}") from exc
        else:
            dotnet = DOTNET_ROOT / "dotnet"
            if not dotnet.exists():
                raise CheckError(f"{dotnet} is missing; run: python3 {CHECK_DIR / 'bootstrap.py'}")
            results = run_local(ordered, project, manifest, args, dotnet, resolve_csc(args.csc))
        all_errors = [d for result in results for d in result["errors"]]

        elapsed = time.perf_counter() - started
        if args.json:
            print(
                json.dumps(
                    {
                        "project": str(project),
                        "elapsed_seconds": round(elapsed, 2),
                        "assemblies": results,
                        "error_count": len(all_errors),
                    },
                    indent=2,
                )
            )
        else:
            for result in results:
                if result["skipped"]:
                    print(f"== {result['assembly']}: skipped ({result['skipped']})")
                    continue
                for diagnostic in result["errors"]:
                    print(format_diagnostic(diagnostic))
                if args.warnings:
                    for diagnostic in result["warnings"]:
                        print(format_diagnostic(diagnostic))
                if args.timings:
                    print(
                        f"== {result['assembly']}: {len(result['errors'])} error(s), "
                        f"{result.get('seconds', 0):.1f} s",
                        file=sys.stderr,
                    )
            checked = [r for r in results if not r["skipped"]]
            if all_errors:
                print(
                    f"Compile check: {len(all_errors)} error(s) in {sum(1 for r in checked if r['errors'])}"
                    f" of {len(checked)} checked assembly/assemblies ({elapsed:.1f} s)",
                    file=sys.stderr,
                )
            else:
                print(
                    f"Compile check: clean ({len(checked)} assembly/assemblies, {elapsed:.1f} s)",
                    file=sys.stderr,
                )
        return 1 if all_errors else 0
    except CheckError as exc:
        print(f"compile check failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
