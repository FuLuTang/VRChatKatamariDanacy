#!/usr/bin/env python3
"""Start / stop / inspect the resident level editor (a headless Blender process).

    python3 Tools/levelbuild/levelserve.py start --level story_01_bench_size
    python3 Tools/levelbuild/levelserve.py status
    python3 Tools/levelbuild/levelserve.py logs -n 40
    python3 Tools/levelbuild/levelserve.py stop

``start`` finds Blender, launches ``blender/editor_server.py`` **detached** (its own
session, output to a log file), and waits until the endpoint answers ``/health``.
Blender itself is never guessed badly: ``BLENDER`` from the environment wins, else the
``export`` lines of ``~/tools/blender-env.sh`` are read (that file is the documented
entry point for this box), else ``blender`` on ``PATH``.

There is one editor per user and machine - one endpoint file, one open level - because
one Blender per 2-core box is the budget.  A second ``start`` refuses while the first
answers, and a stale endpoint file (the process crashed) is detected and cleaned.
"""

import argparse
import json
import os
import re
import shlex
import signal
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import editor_http  # noqa: E402

SERVER_SCRIPT = ROOT / "Tools/levelbuild/blender/editor_server.py"
DEFAULT_ENV_SCRIPT = Path.home() / "tools/blender-env.sh"


# --------------------------------------------------------------------------- #
# Blender discovery
# --------------------------------------------------------------------------- #
#: ``$NAME`` or ``${NAME}`` inside an env-script value.
_VARIABLE = re.compile(r"\$(?:\{([A-Za-z_][A-Za-z0-9_]*)\}|([A-Za-z_][A-Za-z0-9_]*))")


#: ``${NAME:+TEXT}`` - TEXT only when NAME is set (the script uses it to append the
#: inherited path).  Plain ``$NAME`` substitution cannot express that.
_ALTERNATE = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*):\+([^}]*)\}")


def _expand(value, mapping):
    def alternate(match):
        return match.group(2) if mapping.get(match.group(1)) else ""

    def replace(match):
        name = match.group(1) or match.group(2)
        return str(mapping.get(name, match.group(0)))

    for _ in range(3):
        expanded = _VARIABLE.sub(replace, _ALTERNATE.sub(alternate, value))
        if expanded == value:
            break
        value = expanded
    return value


def read_env_script(path):
    """``export NAME="value"`` lines -> env overrides (no shell is spawned).

    Values are expanded against the environment *and* the lines already read, because
    ``blender-env.sh`` defines ``BLENDER="$BLENDER_ROOT/usr/bin/blender"`` after
    ``BLENDER_ROOT``; ``os.path.expandvars`` alone would leave a literal ``$BLENDER_ROOT``
    (measured - the first ``start`` died on exactly that path).
    """
    path = Path(path)
    if not path.is_file():
        return {}
    resolved = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line.startswith("export "):
            continue
        name, _, value = line[len("export "):].partition("=")
        name = name.strip()
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        if name:
            resolved[name] = _expand(value, {**os.environ, **resolved})
    return resolved


def find_blender(env_script=None, blender=None):
    """``(executable, flags, env)`` for the headless Blender this box uses."""
    overrides = read_env_script(env_script or DEFAULT_ENV_SCRIPT)
    env = dict(os.environ)
    env.update(overrides)
    executable = blender or env.get("BLENDER")
    if not executable:
        import shutil
        executable = shutil.which("blender")
    if not executable:
        raise SystemExit("no Blender found: set BLENDER, or pass --env-script, or put "
                         "blender on PATH (this box: source ~/tools/blender-env.sh)")
    flags = env.get("BLENDER_FLAGS") or "--background --factory-startup --python-exit-code 2"
    return executable, shlex.split(flags), env


# --------------------------------------------------------------------------- #
# helpers
# --------------------------------------------------------------------------- #
def _alive(pid):
    """Best-effort liveness check; POSIX only (on Windows use health instead)."""
    if os.name == "nt" or not pid:
        return True
    try:
        os.kill(int(pid), 0)
    except OSError:
        return False
    return True


def _tail(path, lines=30):
    try:
        text = Path(path).read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return ""
    return "\n".join(text[-lines:])


def _print_result(response, as_json=False):
    if as_json:
        print(json.dumps(response, ensure_ascii=False, indent=2))
        return
    from Tools.levelbuild import levelctl
    levelctl.print_summary(response)


def _stop_editor(endpoint, timeout=20.0):
    """Ask the editor to export+exit; returns True when the process is gone.

    Waits for the *process*, not just the socket: Blender closes the listening socket
    and then takes a moment to quit, and a script that starts a new editor right after
    ``stop`` must not race a dying one (measured: the first version returned with the
    process still alive).
    """
    pid = None
    try:
        pid = int(editor_http.read_endpoint(endpoint).get("pid") or 0)
    except (editor_http.EditorUnavailable, TypeError, ValueError):
        pass
    try:
        editor_http.call({"op": "shutdown"}, endpoint=endpoint, timeout=timeout)
    except editor_http.EditorUnavailable:
        editor_http.clear_endpoint(endpoint)
        return True
    deadline = time.time() + timeout
    while time.time() < deadline:
        gone = editor_http.health(endpoint=endpoint, timeout=0.5) is None
        if gone and (pid is None or not _alive(pid)):
            editor_http.clear_endpoint(endpoint)
            return True
        time.sleep(0.2)
    return False


# --------------------------------------------------------------------------- #
# commands
# --------------------------------------------------------------------------- #
def cmd_start(args):
    endpoint = editor_http.endpoint_path(args.endpoint)
    existing = editor_http.health(endpoint=endpoint)
    if existing is not None:
        print("an editor is already running (pid %s, level %s) at %s"
              % (existing.get("pid"), existing.get("level"), endpoint))
        print("stop it first: python3 Tools/levelbuild/levelserve.py stop")
        return 1
    if endpoint.exists():
        print("removing stale endpoint %s" % endpoint)
        editor_http.clear_endpoint(endpoint)

    executable, flags, env = find_blender(args.env_script, args.blender)
    log = Path(args.log).expanduser() if args.log else \
        (editor_http.runtime_dir() / "editor.log")
    log.parent.mkdir(parents=True, exist_ok=True)
    command = [executable] + flags + ["--python", str(SERVER_SCRIPT), "--"]
    if args.level:
        command += ["--level", args.level]
    if args.placement:
        command += ["--placement", args.placement]
    command += ["--endpoint", str(endpoint), "--port", str(args.port), "--log", str(log)]
    if args.out_dir:
        command += ["--out-dir", args.out_dir]
    if args.exports:
        command += ["--exports", args.exports]
    if args.undo_limit:
        command += ["--undo-limit", str(args.undo_limit)]
    if args.no_auto_export:
        command += ["--no-auto-export"]

    print("launching %s" % executable)
    print("  flags: %s" % " ".join(flags))
    print("  log:   %s" % log)
    handle = log.open("w", encoding="utf-8")
    handle.write("$ %s\n" % " ".join(shlex.quote(part) for part in command))
    handle.flush()
    process = subprocess.Popen(
        command, cwd=str(ROOT), env=env,
        stdin=subprocess.DEVNULL, stdout=handle, stderr=subprocess.STDOUT,
        start_new_session=(os.name != "nt"))

    deadline = time.time() + args.wait
    info = None
    while time.time() < deadline:
        if process.poll() is not None:
            handle.flush()
            print("the editor exited during startup (code %s); log tail:" % process.returncode)
            print(_tail(log, 40))
            return 1
        info = editor_http.health(endpoint=endpoint)
        if info is not None and info.get("pid") == process.pid:
            break
        time.sleep(0.2)
    else:
        print("no endpoint after %.0fs; log tail:" % args.wait)
        print(_tail(log, 40))
        return 1

    print("editor ready after %.1fs" % (time.time() - (deadline - args.wait)))
    print("  pid:      %d" % process.pid)
    print("  endpoint: %s" % endpoint)
    print("  health:   %s" % json.dumps(info, ensure_ascii=False))
    if args.json:
        print(json.dumps({"pid": process.pid, "endpoint": str(endpoint), "health": info},
                         ensure_ascii=False, indent=2))
    return 0


def cmd_stop(args):
    endpoint = editor_http.endpoint_path(args.endpoint)
    info = None
    if endpoint.is_file():
        try:
            info = editor_http.read_endpoint(endpoint)
        except editor_http.EditorUnavailable as error:
            print("%s; removing it" % error)
            editor_http.clear_endpoint(endpoint)
    if info is None:
        print("no editor endpoint at %s; nothing to stop" % endpoint)
        return 0
    print("stopping editor pid %s (level %s)" % (info.get("pid"), info.get("level")))
    if _stop_editor(endpoint, timeout=args.timeout):
        print("stopped")
        return 0
    print("the editor did not stop within %.0fs; endpoint left in place" % args.timeout)
    return 1


def cmd_status(args):
    endpoint = editor_http.endpoint_path(args.endpoint)
    health = editor_http.health(endpoint=endpoint)
    if health is None:
        print("not running (no endpoint answer at %s)" % endpoint)
        return 1
    try:
        response = editor_http.call({"op": "status"}, endpoint=endpoint, timeout=30)
    except editor_http.EditorUnavailable as error:
        print("health answered but status failed: %s" % error)
        return 1
    if args.json:
        print(json.dumps({"health": health, "status": response}, ensure_ascii=False, indent=2))
    else:
        print("health: %s" % json.dumps(health, ensure_ascii=False))
        _print_result(response)
        info = editor_http.read_endpoint(endpoint)
        print("endpoint: %s  log: %s" % (endpoint, info.get("log")))
    return 0 if response.get("ok") else 1


def cmd_logs(args):
    endpoint = editor_http.endpoint_path(args.endpoint)
    info = None
    if endpoint.is_file():
        try:
            info = editor_http.read_endpoint(endpoint)
        except editor_http.EditorUnavailable:
            info = None
    log = Path(args.log or (info or {}).get("log") or (editor_http.runtime_dir() / "editor.log"))
    if not log.is_file():
        print("no log at %s" % log)
        return 1
    print("# %s" % log)
    print(_tail(log, args.lines))
    return 0


def cmd_health(args):
    endpoint = editor_http.endpoint_path(args.endpoint)
    health = editor_http.health(endpoint=endpoint)
    print(json.dumps(health if health is not None else {"ok": False, "running": False},
                     ensure_ascii=False))
    return 0 if health is not None else 1


def build_parser():
    parser = argparse.ArgumentParser(
        prog="levelserve.py", description="Run the resident level editor (headless Blender).")
    parser.add_argument("--endpoint", default=None,
                        help="endpoint file (default: the shared runtime path)")
    sub = parser.add_subparsers(dest="command")

    start = sub.add_parser("start", help="launch the editor detached and wait for /health")
    start.add_argument("--level", help="level id under Docs/Design/Levels")
    start.add_argument("--placement", help="explicit placement.json (instead of --level)")
    start.add_argument("--out-dir", help="default directory for render output")
    start.add_argument("--exports", help="override props/exports (tests, probes)")
    start.add_argument("--port", type=int, default=0, help="0 = pick a free port")
    start.add_argument("--undo-limit", type=int, default=50)
    start.add_argument("--no-auto-export", action="store_true",
                       help="do not write the file after each change (dirty state stays)")
    start.add_argument("--env-script", default=str(DEFAULT_ENV_SCRIPT))
    start.add_argument("--blender", default=None, help="explicit Blender executable")
    start.add_argument("--log", default=None)
    start.add_argument("--wait", type=float, default=120.0, help="startup budget in seconds")
    start.add_argument("--json", action="store_true")
    start.set_defaults(func=cmd_start)

    stop = sub.add_parser("stop", help="export if dirty and stop the editor")
    stop.add_argument("--timeout", type=float, default=20.0)
    stop.set_defaults(func=cmd_stop)

    status = sub.add_parser("status", help="health + the editor's status op")
    status.add_argument("--json", action="store_true")
    status.set_defaults(func=cmd_status)

    logs = sub.add_parser("logs", help="tail the editor log")
    logs.add_argument("-n", "--lines", type=int, default=40)
    logs.add_argument("--log", default=None)
    logs.set_defaults(func=cmd_logs)

    health = sub.add_parser("health", help="just GET /health (no token, no state)")
    health.set_defaults(func=cmd_health)
    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    if not getattr(args, "func", None):
        parser.print_help()
        return 2
    if args.command == "start" and not (args.level or args.placement):
        parser.error("start needs --level <id> or --placement <file>")
    try:
        return args.func(args)
    except KeyboardInterrupt:
        print("interrupted", file=sys.stderr)
        return 130


if __name__ == "__main__":
    sys.exit(main())
