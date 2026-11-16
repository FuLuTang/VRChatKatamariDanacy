"""Client for the ``motion_sim`` .NET tool (the editor's only motion evaluator).

The motion classes live once, in
``VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/MotionMath.cs``.  That file
and the two it depends on (``Runtime/Core/KatamariMath.cs`` and
``Runtime/Pickup/PhaseSeed.cs``) are compiled as source by ``motion_sim.csproj`` into a
small command-line tool, and this module is how the level editor calls it.

Nothing here knows what a motion *is*: there are no class names, no speeds, no periods and
no waveform arithmetic on this side.  This module only

* builds the tool when its output is missing or older than any source it compiles,
* runs it with the documented arguments, and
* parses the JSON it prints.

That is what makes the editor, the importer and the game share one implementation of the 13
programs: if ``MotionMath.cs`` changes, the editor's next request rebuilds and follows it.

Usage from the editor::

    from Tools.motion_sim import motion_cli
    payload = motion_cli.params(level_path)                  # no time simulated
    payload = motion_cli.trajectory(level_path, seconds=30)  # world poses at 30 Hz

Both raise :class:`MotionSimError` with a stable ``code`` when the tool is unavailable
(``dotnet_missing``), fails to build (``build_failed``) or refuses the document
(``bad_document``).  The caller turns that into an explicit "motion preview unavailable"
message; it must never fall back to a second implementation.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import threading
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent
PROJECT = TOOL_DIR / "motion_sim.csproj"
REPO_ROOT = TOOL_DIR.parents[1]
#: The Unity sources the project links; a change to any of them invalidates the build.
LINKED_SOURCES = (
    REPO_ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/MotionMath.cs",
    REPO_ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PhaseSeed.cs",
    REPO_ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Core/KatamariMath.cs",
)
LOCAL_SOURCES = (
    TOOL_DIR / "Program.cs",
    TOOL_DIR / "UnityStubs.cs",
    PROJECT,
)

CONFIGURATION = "Release"
FRAMEWORK = "net8.0"
DLL_NAME = "motion_sim.dll"

#: Wall-clock ceilings.  A cold build is the slow path (~10 s on this machine); a
#: trajectory for the whole reference level is under a second.
BUILD_TIMEOUT_S = 300
RUN_TIMEOUT_S = 180

_build_lock = threading.Lock()


class MotionSimError(RuntimeError):
    """The motion tool is unavailable, failed to build, or refused the document."""

    def __init__(self, message, code="motion_sim_error", details=None):
        super().__init__(message)
        self.code = code
        self.details = details


# --------------------------------------------------------------------------- #
# toolchain
# --------------------------------------------------------------------------- #
def dotnet_path(explicit=None):
    """The ``dotnet`` executable to use, or ``None``.

    ``Tools/csharp_check`` installs the SDK into ``~/.dotnet`` when the machine has none, so
    that location is checked as well as PATH; ``DSH_DOTNET`` / ``MOTION_SIM_DOTNET`` override
    both for a machine with a different layout.
    """
    for name in ("MOTION_SIM_DOTNET", "DSH_DOTNET"):
        value = os.environ.get(name)
        if value and Path(value).exists():
            return str(value)
    if explicit:
        return str(explicit) if Path(explicit).exists() else None
    found = shutil.which("dotnet")
    if found:
        return found
    candidate = Path.home() / ".dotnet" / "dotnet"
    if candidate.exists():
        return str(candidate)
    return None


def output_path():
    return TOOL_DIR / "bin" / CONFIGURATION / FRAMEWORK / DLL_NAME


def _newest_source_mtime():
    newest = 0.0
    for path in LOCAL_SOURCES + LINKED_SOURCES:
        try:
            newest = max(newest, path.stat().st_mtime)
        except OSError:
            # A missing linked source is a build failure the compiler will report with a
            # name; treating it as "newer" keeps the rebuild going instead of hiding it.
            return float("inf")
    return newest


def build(force=False, dotnet=None):
    """Compile the tool if needed; returns the DLL path.

    The build is serialised with a thread lock and is a no-op once the DLL is newer than
    every source, so a page reload never pays for it twice.
    """
    executable = dotnet_path(dotnet)
    if executable is None:
        raise MotionSimError(
            "the .NET SDK is not installed, so the motion evaluator cannot be built; run "
            "`python3 Tools/csharp_check/check.py` once (it installs the SDK into ~/.dotnet) "
            "or set MOTION_SIM_DOTNET", "dotnet_missing")
    dll = output_path()
    with _build_lock:
        if not force and dll.is_file() and dll.stat().st_mtime >= _newest_source_mtime():
            return dll
        environment = dict(os.environ)
        environment.setdefault("DOTNET_NOLOGO", "1")
        environment.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
        environment.setdefault("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")
        try:
            result = subprocess.run(
                [executable, "build", str(PROJECT), "-c", CONFIGURATION,
                 "-v", "quiet", "--nologo"],
                cwd=str(TOOL_DIR), env=environment, capture_output=True, text=True,
                timeout=BUILD_TIMEOUT_S)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise MotionSimError("building motion_sim failed: %s" % error, "build_failed")
        if result.returncode != 0 or not dll.is_file():
            output = (result.stdout or "") + (result.stderr or "")
            raise MotionSimError(
                "building motion_sim failed (exit %d): %s"
                % (result.returncode, output.strip()[-4000:] or "no output"),
                "build_failed")
        return dll


# --------------------------------------------------------------------------- #
# running the tool
# --------------------------------------------------------------------------- #
def _run(mode, level_path, items=None, seconds=None, rate=None, dotnet=None):
    executable = dotnet_path(dotnet)
    dll = build(dotnet=dotnet)
    command = [executable, str(dll), "--level", str(level_path), "--mode", mode]
    if items:
        command += ["--items", ",".join(items)]
    if seconds is not None:
        command += ["--seconds", repr(float(seconds))]
    if rate is not None:
        command += ["--rate", str(int(rate))]
    environment = dict(os.environ)
    environment.setdefault("DOTNET_NOLOGO", "1")
    try:
        result = subprocess.run(command, env=environment, capture_output=True, text=True,
                                timeout=RUN_TIMEOUT_S)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise MotionSimError("running motion_sim failed: %s" % error, "run_failed")
    if result.returncode != 0:
        message = (result.stderr or result.stdout or "").strip()
        code = "bad_document" if result.returncode == 3 else "run_failed"
        raise MotionSimError("motion_sim %s failed (exit %d): %s"
                             % (mode, result.returncode, message[-2000:]),
                             code)
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise MotionSimError("motion_sim returned non-JSON: %s" % error, "run_failed")


def params(level_path, items=None, dotnet=None):
    """Resolved per-prop motion facts (class, phase, speed, radius, assumptions)."""
    return _run("params", level_path, items=items, dotnet=dotnet)


def trajectory(level_path, seconds=30.0, rate=30, items=None, dotnet=None):
    """World poses sampled at ``rate`` Hz for ``seconds`` seconds of game time."""
    if not (0 < float(seconds) <= 3600):
        raise MotionSimError("seconds must be 0..3600", "bad_argument")
    if int(rate) not in (5, 10, 15, 30):
        raise MotionSimError("rate must be 5, 10, 15 or 30", "bad_argument")
    return _run("trajectory", level_path, items=items, seconds=seconds, rate=rate,
                dotnet=dotnet)


def availability(dotnet=None):
    """``(ok, detail)`` - whether the tool can run here, without building it."""
    executable = dotnet_path(dotnet)
    if executable is None:
        return False, ("the .NET SDK is not installed (~/.dotnet/dotnet or PATH); "
                       "the motion preview needs it")
    dll = output_path()
    if dll.is_file():
        return True, "built"
    return True, "needs a build (first request compiles MotionMath.cs, ~10 s)"


def _main(argv):                                                 # pragma: no cover - CLI
    """``python3 -m Tools.motion_sim.motion_cli [params|trajectory|status] …``

    The same two modes the editor's HTTP endpoints serve, so an agent can answer "where is
    this prop at t" without a browser or a running editor.
    """
    import argparse
    parser = argparse.ArgumentParser(
        prog="python3 -m Tools.motion_sim.motion_cli",
        description="Run the motion evaluator that compiles MotionMath.cs.")
    parser.add_argument("command", nargs="?", default="status",
                        choices=("status", "params", "trajectory"))
    parser.add_argument("level", nargs="?", help="path to a katamari.placement/2 document")
    parser.add_argument("--items", default=None, help="comma list of itemIds")
    parser.add_argument("--seconds", type=float, default=30.0)
    parser.add_argument("--rate", type=int, default=30, choices=(5, 10, 15, 30))
    parser.add_argument("--out", default="-", help="output file, or - for stdout")
    args = parser.parse_args(argv)

    if args.command == "status":
        ok, detail = availability()
        print(json.dumps({"dotnet": dotnet_path(), "available": ok, "detail": detail,
                          "dll": str(output_path())}, indent=2))
        return 0 if ok else 1
    if not args.level:
        parser.error("a level path is required for params/trajectory")
    items = [entry.strip() for entry in (args.items or "").split(",") if entry.strip()]
    try:
        if args.command == "params":
            payload = params(args.level, items=items or None)
        else:
            payload = trajectory(args.level, seconds=args.seconds, rate=args.rate,
                                 items=items or None)
    except MotionSimError as error:
        print(json.dumps({"ok": False, "code": error.code, "error": str(error)}))
        return 1
    text = json.dumps(payload, ensure_ascii=False)
    if args.out == "-":
        sys.stdout.write(text + "\n")
    else:
        Path(args.out).write_text(text + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":                                       # pragma: no cover - CLI
    sys.exit(_main(sys.argv[1:]))
