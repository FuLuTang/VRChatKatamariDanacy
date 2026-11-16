#!/usr/bin/env python3
"""Bootstrap the local C# check: install a .NET SDK and mirror Unity's reference assemblies.

Everything this script downloads or copies lives outside the repository:

  ~/.dotnet                          .NET SDK (only used to run Roslyn's csc.dll)
  ~/.cache/vrc-katamari-refasm/      reference DLLs copied from the Windows Unity install
                                     (Unity/VRChat proprietary binaries; never committed)

The Windows side is read-only except for a staging directory under %TEMP%, which is removed
again at the end.  Nothing under the Unity project (Assets/, Library/, Packages/,
ProjectSettings/) is read or written except the Unity-generated *.csproj manifests.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tarfile
import tempfile
import zipfile
import time
from pathlib import Path

CHECK_DIR = Path(__file__).resolve().parent
DEFAULT_HOST = os.environ.get("VRCK_SSH_HOST", "pc114514")
DEFAULT_WINDOWS_PROJECT = os.environ.get(
    "VRCK_WINDOWS_PROJECT", r"D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity"
)
CACHE_ROOT = Path(
    os.environ.get("VRCK_CSHARP_CHECK_CACHE", Path.home() / ".cache" / "vrc-katamari-refasm")
)
DOTNET_ROOT = Path(os.environ.get("VRCK_DOTNET_ROOT", Path.home() / ".dotnet"))
DOTNET_VERSION = os.environ.get("VRCK_DOTNET_VERSION", "8.0.428")
# Unity 2022.3.22f1 bundles Roslyn 4.3.1.  Cascading diagnostics (e.g. CS0029 after CS0246)
# depend on the compiler build, so use the same Roslyn instead of whatever the SDK ships.
ROSLYN_VERSION = os.environ.get("VRCK_ROSLYN_VERSION", "4.3.1")

# Compile units mirrored from Unity.  Unity compiles editor scripts that are not covered by
# an asmdef into the default Assembly-CSharp-Editor assembly; that is where most of our
# editor code lives, so it must be checked as well as the three asmdef assemblies.
WINDOWS_ASSEMBLIES = [
    "VRCKatamari.Runtime",
    "VRCKatamari.Editor",
    "Assembly-CSharp-Editor",
    "VRCKatamari.Tests.EditMode",
]


class BootstrapError(RuntimeError):
    pass


def run(cmd: list[str], *, timeout: int = 600, check: bool = True, quiet: bool = False):
    if not quiet:
        print("  $ " + " ".join(cmd), file=sys.stderr)
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    if check and proc.returncode != 0:
        raise BootstrapError(
            f"command failed ({proc.returncode}): {' '.join(cmd)}\n"
            f"stdout:\n{proc.stdout}\nstderr:\n{proc.stderr}"
        )
    return proc


def ssh(host: str, command: str, *, timeout: int = 600, check: bool = True):
    return run(["ssh", "-o", "BatchMode=yes", host, command], timeout=timeout, check=check)


def _scp_remote(host: str, remote: str) -> str:
    # scp uses the SFTP protocol by default, so the path must not be shell-quoted and must
    # not contain whitespace (the Windows %TEMP% path does not).
    if " " in remote:
        raise BootstrapError(f"remote path with spaces is not supported by this helper: {remote!r}")
    return f"{host}:{remote.replace(chr(92), '/')}"


def scp_to(host: str, local: Path, remote: str):
    run(["scp", "-q", str(local), _scp_remote(host, remote)])


def scp_from(host: str, remote: str, local: Path):
    run(["scp", "-q", _scp_remote(host, remote), str(local)], timeout=900)


def remote_powershell(host: str, script: str, args: str = "", *, timeout: int = 600):
    command = f'powershell -NoProfile -ExecutionPolicy Bypass -File "{script}"'
    if args:
        command += " " + args
    return ssh(host, command, timeout=timeout)


def detect_windows_temp(host: str) -> str:
    proc = ssh(host, 'powershell -NoProfile -Command "$env:TEMP"')
    temp = proc.stdout.strip().splitlines()[-1].strip()
    if not temp or not temp.startswith("C:"):
        raise BootstrapError(f"could not determine Windows %TEMP% (got {temp!r})")
    return temp


def probe(host: str, windows_project: str, staging: str) -> dict:
    """Copy the probe script to Windows, run it, and return its JSON manifest."""
    remote_script = staging + r"\probe_windows.ps1"
    run(["ssh", "-o", "BatchMode=yes", host, f'powershell -NoProfile -Command "New-Item -ItemType Directory -Force -Path \'{staging}\' | Out-Null"'])
    scp_to(host, CHECK_DIR / "probe_windows.ps1", remote_script)
    proc = remote_powershell(
        host,
        remote_script,
        f'-ProjectRoot "{windows_project}" -Assemblies "{",".join(WINDOWS_ASSEMBLIES)}"',
        timeout=300,
    )
    text = proc.stdout.strip()
    start = text.find("{")
    if start < 0:
        raise BootstrapError(f"probe returned no JSON:\n{text}\n{proc.stderr}")
    try:
        manifest = json.loads(text[start:])
    except json.JSONDecodeError as exc:
        raise BootstrapError(f"could not parse probe JSON: {exc}\n{text[:2000]}") from exc
    missing = [r["path"] for r in manifest["references"] if not r["exists"]]
    for asm in manifest["assemblies"]:
        missing += [pr["path"] for pr in asm.get("project_references", []) if not pr["exists"] and pr["assembly"] not in WINDOWS_ASSEMBLIES]
    if missing:
        raise BootstrapError("reference DLLs missing on Windows:\n  " + "\n  ".join(missing[:20]))
    return manifest


def install_dotnet() -> Path:
    dotnet = DOTNET_ROOT / "dotnet"
    if dotnet.exists():
        return dotnet
    print(f"Installing .NET SDK {DOTNET_VERSION} into {DOTNET_ROOT} (user-local, no sudo) ...", file=sys.stderr)
    DOTNET_ROOT.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="vrck-dotnet-install-") as tmp:
        script = Path(tmp) / "dotnet-install.sh"
        run(["curl", "-fsSL", "https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh", "-o", str(script)])
        run(["bash", str(script), "--version", DOTNET_VERSION, "--install-dir", str(DOTNET_ROOT), "--no-path"], timeout=900)
    if not dotnet.exists():
        raise BootstrapError(f"dotnet install finished but {dotnet} is missing")
    return dotnet


def ensure_roslyn() -> Path:
    """Download the Microsoft.Net.Compilers.Toolset NuGet package into the cache.

    It contains IL (not ReadyToRun) binaries for net6.0, which run on any newer .NET runtime;
    check.py sets DOTNET_ROLL_FORWARD=LatestMajor for it.
    """
    dest = CACHE_ROOT / "roslyn"
    csc = dest / "csc.dll"
    if csc.exists() and (dest / "Microsoft.CodeAnalysis.CSharp.dll").exists():
        return csc
    dest.mkdir(parents=True, exist_ok=True)
    url = f"https://www.nuget.org/api/v2/package/Microsoft.Net.Compilers.Toolset/{ROSLYN_VERSION}"
    print(f"Fetching Roslyn {ROSLYN_VERSION} (Unity's compiler version) ...", file=sys.stderr)
    with tempfile.TemporaryDirectory(prefix="vrck-roslyn-") as tmp:
        nupkg = Path(tmp) / "toolset.nupkg"
        run(["curl", "-fsSL", url, "-o", str(nupkg)], timeout=600)
        with zipfile.ZipFile(nupkg) as archive:
            prefix = "tasks/net6.0/bincore/"
            members = [
                name for name in archive.namelist()
                if name.startswith(prefix) and not name.endswith("/") and name.count("/") == 3
            ]
            if not members:
                raise BootstrapError("Roslyn toolset package has no tasks/net6.0/bincore binaries")
            for name in members:
                (dest / name.rsplit("/", 1)[-1]).write_bytes(archive.read(name))
    if not csc.exists():
        raise BootstrapError(f"Roslyn csc.dll missing after extracting {url}")
    return csc


def warn_roslyn_mismatch(manifest: dict) -> None:
    """Unity's Roslyn build affects cascading diagnostics; warn when the pin is stale."""
    unity_version = (manifest.get("unity_csc_version") or "").split("-")[0].strip()
    if not unity_version:
        return
    csc = CACHE_ROOT / "roslyn" / "csc.dll"
    env = dict(os.environ, DOTNET_ROLL_FORWARD="LatestMajor")
    proc = subprocess.run(
        [str(DOTNET_ROOT / "dotnet"), str(csc), "-version"],
        capture_output=True,
        text=True,
        env=env,
    )
    local_version = (proc.stdout.strip().split() or [""])[0].split("-")[0]
    if local_version and not local_version.startswith(unity_version) and not unity_version.startswith(local_version):
        print(
            f"warning: Unity ships Roslyn {unity_version} but the cache has {local_version}; "
            "cascading diagnostics may differ. Re-run with "
            f"VRCK_ROSLYN_VERSION={unity_version} python3 {CHECK_DIR / 'bootstrap.py'} --refresh",
            file=sys.stderr,
        )


def ensure_dotnet() -> str:
    dotnet = DOTNET_ROOT / "dotnet"
    if not dotnet.exists():
        dotnet = install_dotnet()
    proc = subprocess.run([str(dotnet), "--list-sdks"], capture_output=True, text=True)
    if proc.returncode != 0:
        raise BootstrapError(f"{dotnet} --list-sdks failed:\n{proc.stderr}")
    return str(dotnet)


def fetch_references(host: str, windows_project: str, manifest: dict, staging: str) -> Path:
    """Stage every reference DLL on Windows, tar it, bring it back, extract into the cache."""
    refs_dir = CACHE_ROOT / "refs"
    refs_dir.mkdir(parents=True, exist_ok=True)

    # Re-key the manifest by assembly name for the local checker.
    by_name: dict[str, dict] = {}
    for ref in manifest["references"]:
        name = ref["assembly"]
        entry = by_name.get(name)
        if entry and Path(entry["windows"]).name.lower() != Path(ref["path"]).name.lower():
            raise BootstrapError(f"two different DLLs share the assembly name {name!r}:\n  {entry['windows']}\n  {ref['path']}")
        by_name[name] = {"windows": ref["path"]}
    for asm in manifest["assemblies"]:
        for pr in asm.get("project_references", []):
            by_name.setdefault(
                pr["assembly"],
                {"windows": pr["path"]},
            )

    (CACHE_ROOT / "probe.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    local_manifest = {
        "schema": 1,
        "generated_at_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "host": host,
        "windows_project_root": windows_project,
        "unity_version": (manifest.get("unity_version") or "").strip(),
        "unity_editor": manifest.get("unity_editor"),
        "unity_csc": manifest.get("unity_csc"),
        "unity_runtime": manifest.get("unity_runtime"),
        "langversion": (manifest.get("langversion") or "9.0").strip(),
        "assemblies": {},
        "references": {},
    }
    for asm in manifest["assemblies"]:
        if asm.get("missing_csproj"):
            raise BootstrapError(f"missing Windows csproj: {asm['csproj']} (open the project in Unity once to regenerate them)")
        local_manifest["assemblies"][asm["name"]] = {
            "csproj": asm["csproj"],
            "defines": [d for d in (asm.get("defines") or "").split(";") if d],
            "references": asm.get("references", []),
            "project_references": [pr["assembly"] for pr in asm.get("project_references", [])],
        }
    for name, ref in by_name.items():
        local_manifest["references"][name] = f"refs/{name}.dll"

    manifest_path = CACHE_ROOT / "manifest.json"
    manifest_path.write_text(json.dumps(local_manifest, indent=2) + "\n", encoding="utf-8")

    remote_manifest = staging + r"\manifest.json"
    with tempfile.TemporaryDirectory(prefix="vrck-manifest-") as tmp:
        tmp_manifest = Path(tmp) / "manifest.json"
        tmp_manifest.write_text(json.dumps(manifest, ensure_ascii=False), encoding="utf-8")
        scp_to(host, tmp_manifest, remote_manifest)
    scp_to(host, CHECK_DIR / "stage_refs.ps1", staging + r"\stage_refs.ps1")
    remote_powershell(
        host,
        staging + r"\stage_refs.ps1",
        f'-ManifestPath "{remote_manifest}" -StageDir "{staging}\\refs"',
        timeout=300,
    )
    ssh(host, f'cmd /c "tar -cf {staging}\\refs.tar -C {staging} refs"', timeout=300)

    tar_local = CACHE_ROOT / "refs.tar"
    scp_from(host, staging + r"\refs.tar", tar_local)
    with tarfile.open(tar_local, "r:") as tar:
        members = [m for m in tar.getmembers() if m.name.startswith("refs/") and not m.name.endswith("/")]
        for member in members:
            if "/" in member.name[5:] or "\\" in member.name[5:]:
                raise BootstrapError(f"unexpected tar member {member.name!r}")
        tar.extractall(CACHE_ROOT, members)
    tar_local.unlink()

    expected = {CACHE_ROOT / rel for rel in local_manifest["references"].values()}
    missing = sorted(str(p) for p in expected if not p.exists())
    if missing:
        raise BootstrapError("reference DLLs missing after extraction:\n  " + "\n  ".join(missing[:20]))
    return manifest_path


def bootstrap(host: str, windows_project: str, *, refresh: bool = False, keep_remote: bool = False) -> Path:
    manifest_path = CACHE_ROOT / "manifest.json"
    if manifest_path.exists() and not refresh:
        print(f"Reference mirror already present: {CACHE_ROOT}", file=sys.stderr)
        ensure_roslyn()
        ensure_dotnet()
        return manifest_path

    print(f"Probing Windows Unity project on {host} ...", file=sys.stderr)
    windows_temp = detect_windows_temp(host)
    staging = windows_temp.rstrip("\\/") + r"\vrck_csharp_check"
    try:
        raw = probe(host, windows_project, staging)
        print(
            f"  Unity {(raw.get('unity_version') or '').strip()} at {raw.get('unity_editor')}; "
            f"{len(raw['references'])} reference DLLs",
            file=sys.stderr,
        )
        fetch_references(host, windows_project, raw, staging)
    finally:
        if not keep_remote:
            ssh(host, f'cmd /c "rmdir /s /q {staging}"', check=False)
        else:
            print(f"Windows staging kept at {staging}", file=sys.stderr)
    ensure_roslyn()
    ensure_dotnet()
    warn_roslyn_mismatch(raw)
    print(f"Cache ready: {CACHE_ROOT}", file=sys.stderr)
    return manifest_path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--host", default=DEFAULT_HOST, help="SSH alias of the Windows PC")
    parser.add_argument("--windows-project", default=DEFAULT_WINDOWS_PROJECT, help="Unity project root on Windows")
    parser.add_argument("--refresh", action="store_true", help="re-probe Windows and re-fetch the reference DLLs")
    parser.add_argument("--keep-remote", action="store_true", help="keep the Windows %%TEMP%%\\vrck_csharp_check staging directory")
    parser.add_argument("--dotnet-only", action="store_true", help="only install/verify the local .NET SDK")
    args = parser.parse_args(argv)
    try:
        if not args.dotnet_only:
            bootstrap(args.host, args.windows_project, refresh=args.refresh, keep_remote=args.keep_remote)
        else:
            print(ensure_dotnet())
    except (BootstrapError, subprocess.TimeoutExpired) as exc:
        print(f"bootstrap failed: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
