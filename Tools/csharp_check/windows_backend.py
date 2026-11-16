"""Optional Windows backend: compile with the Roslyn and reference assemblies Unity ships.

The Linux backend mirrors those files into ~/.cache and uses Unity's Roslyn release; this
backend instead stages the sources under %TEMP% on the Windows PC and runs Unity's own
csc.dll through Unity's bundled .NET runtime. It is the ground truth when a diagnostic looks
compiler-version dependent, and it needs neither a local .NET SDK nor the reference mirror.

Nothing under the Unity project is written: the sources are copied to
%TEMP%\\vrck_csharp_check and the staging directory is removed afterwards.
"""

from __future__ import annotations

import json
import re
import subprocess
import tempfile
from pathlib import Path

import bootstrap

STAGING_NAME = "vrck_csharp_check_win"


def _win(path: Path) -> str:
    return str(path).replace("/", "\\")


def _remote_run(host: str, command: str, *, timeout: int = 900, check: bool = True):
    return bootstrap.ssh(host, command, timeout=timeout, check=check)


def _pack_sources(project: Path, units, archive: Path) -> None:
    sources: list[str] = []
    seen: set[str] = set()
    for unit in units:
        for path in unit.sources:
            rel = path.relative_to(project).as_posix()
            if rel not in seen:
                seen.add(rel)
                sources.append(rel)
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as handle:
        handle.write("\n".join(sources) + "\n")
        list_path = Path(handle.name)
    try:
        subprocess.run(
            ["tar", "-cf", str(archive), "-C", str(project), "-T", str(list_path)],
            check=True,
            capture_output=True,
            text=True,
        )
    finally:
        list_path.unlink()


def compile_on_windows(units, project: Path, *, host: str, keep_remote: bool = False, verbose: bool = False) -> list[dict]:
    """Compile `units` (already source-populated and topologically ordered) on Windows."""
    raw = json.loads((bootstrap.CACHE_ROOT / "probe.json").read_text(encoding="utf-8"))
    unity_csc = raw.get("unity_csc")
    unity_runtime = raw.get("unity_runtime")
    if not unity_csc or not unity_runtime:
        raise bootstrap.BootstrapError(
            "the Windows probe did not find Unity's compiler "
            "(Editor/Data/DotNetSdkRoslyn/csc.dll and NetCoreRuntime/dotnet.exe)"
        )

    windows_temp = bootstrap.detect_windows_temp(host)
    staging = windows_temp.rstrip("\\/") + "\\" + STAGING_NAME
    remote_src = staging + r"\src"
    remote_work = staging + r"\work"
    remote_out = staging + r"\out"
    created = False
    try:
        _remote_run(
            host,
            'powershell -NoProfile -Command "New-Item -ItemType Directory -Force -Path '
            f"'{remote_src}','{remote_work}','{remote_out}' | Out-Null\"",
        )
        created = True

        archive = Path(tempfile.mkdtemp(prefix="vrck-win-src-")) / "src.tar"
        _pack_sources(project, units, archive)
        bootstrap.scp_to(host, archive, staging + r"\src.tar")
        _remote_run(host, f'cmd /c "tar -xf {staging}\\src.tar -C {remote_src}"', timeout=300)

        # One response file per unit, written in Windows path space.
        by_name = {u.name: u for u in units}
        settings_by_name = {a["name"]: a for a in raw["assemblies"]}
        built: dict[str, str] = {}
        jobs: list[dict] = []
        rsp_files: list[tuple[Path, str]] = []
        for unit in units:
            settings = settings_by_name[unit.name]
            refs: list[str] = []
            seen: set[str] = set()
            for name in settings.get("references", []):
                if name in seen:
                    continue
                seen.add(name)
                ref = next((r for r in raw["references"] if r["assembly"] == name), None)
                if ref is None:
                    raise bootstrap.BootstrapError(f"{unit.name}: reference {name!r} not in probe manifest")
                refs.append(ref["path"])
            for project_ref in settings.get("project_references", []):
                name = project_ref["assembly"] if isinstance(project_ref, dict) else project_ref
                if name in seen:
                    continue
                seen.add(name)
                if name in built:
                    refs.append(built[name])
                    continue
                ref = next((r for r in raw["references"] if r["assembly"] == name), None)
                if ref is None:
                    candidate = raw["project_root"] + "\\Library\\ScriptAssemblies\\" + name + ".dll"
                    refs.append(candidate)
                else:
                    refs.append(ref["path"])
            out = remote_out + "\\" + unit.name + ".dll"
            built[unit.name] = out
            lines = [
                "-nologo",
                "-nostdlib+",
                "-target:library",
                f"-langversion:{raw.get('langversion') or '9.0'}",
                "-warn:4",
                "-nowarn:0169,USG0001",
                "-unsafe-",
                "-preferreduilang:en",
                f'-out:"{out}"',
                f'-define:{";".join(settings.get("defines") or [])}',
            ]
            lines += [f'-r:"{ref}"' for ref in refs]
            lines += [
                f'"{remote_src}\\{path.relative_to(project).as_posix().replace("/", chr(92))}"'
                for path in unit.sources
            ]
            local_rsp = Path(tempfile.mkdtemp(prefix="vrck-win-rsp-")) / f"{unit.name}.rsp"
            local_rsp.write_text("\n".join(lines) + "\n", encoding="utf-8")
            rsp_files.append((local_rsp, remote_work + "\\" + unit.name + ".rsp"))
            jobs.append({"name": unit.name, "rsp": remote_work + "\\" + unit.name + ".rsp",
                         "deps": [d for d in unit.dependencies if d in by_name]})

        for local_rsp, remote_rsp in rsp_files:
            bootstrap.scp_to(host, local_rsp, remote_rsp)
        job = {
            "dotnet": unity_runtime,
            "csc": unity_csc,
            "jobs": jobs,
        }
        with tempfile.TemporaryDirectory(prefix="vrck-win-job-") as tmp:
            job_path = Path(tmp) / "job.json"
            job_path.write_text(json.dumps(job), encoding="utf-8")
            bootstrap.scp_to(host, job_path, remote_work + "\\job.json")
        bootstrap.scp_to(host, bootstrap.CHECK_DIR / "compile_windows.ps1", staging + "\\compile_windows.ps1")

        proc = _remote_run(
            host,
            'powershell -NoProfile -ExecutionPolicy Bypass -File '
            f'"{staging}\\compile_windows.ps1" -JobPath "{remote_work}\\job.json" -ResultPath "{staging}\\results.json"',
            timeout=900,
        )
        if verbose:
            print(proc.stdout, file=sys.stderr)

        local_results = Path(tempfile.mkdtemp(prefix="vrck-win-result-")) / "results.json"
        bootstrap.scp_from(host, staging + "\\results.json", local_results)
        payload = json.loads(local_results.read_text(encoding="utf-8-sig"))
        return _normalize(payload)
    finally:
        if created and not keep_remote:
            _remote_run(host, f'cmd /c "rmdir /s /q {staging}"', check=False)
        elif created:
            print(f"Windows staging kept at {staging}", file=sys.stderr)


def _normalize(payload: list[dict]) -> list[dict]:
    """Turn the Windows results into the same shape as the local backend."""
    import check  # local module; avoids a circular import at module load

    prefix = re.compile(r"^.*?\\src\\", re.IGNORECASE)
    results: list[dict] = []
    for item in payload:
        errors: list[dict] = []
        warnings: list[dict] = []
        for line in (item.get("output") or "").splitlines():
            text = line.strip()
            if not text:
                continue
            match = check.DIAGNOSTIC_RE.match(text) or check.LOCATIONLESS_RE.match(text)
            if match is None:
                continue
            path = (match.groupdict().get("path") or "").strip()
            normalized = prefix.sub("", path).replace("\\", "/")
            if re.match(r"^[A-Za-z]:[\\/]", path):
                normalized = path.replace("\\", "/")
            diagnostic = {
                "path": normalized,
                "line": int(match.groupdict().get("line") or 0),
                "column": int(match.groupdict().get("col") or 0),
                "severity": match.group("severity"),
                "code": match.group("code"),
                "message": match.group("message"),
            }
            (errors if diagnostic["severity"] == "error" else warnings).append(diagnostic)
        results.append(
            {
                "assembly": item["name"],
                "skipped": item.get("skipped"),
                "seconds": item.get("seconds"),
                "errors": errors,
                "warnings": warnings,
                "other": [],
            }
        )
    return results
