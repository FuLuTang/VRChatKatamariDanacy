#!/usr/bin/env python3
"""Stdlib-only CLI for MCP servers on the user's Windows PC, reached through SSH port forwards.

Two server profiles share one client:

    unity     MCP for Unity v10.x, Windows 127.0.0.1:8080  (HTTPLocal, GET /health)
    browser   Playwright MCP in extension mode, Windows localhost:8931  (/mcp, no /health)

    python3 mcp_cli.py [--server unity|browser] tunnel up|down|status
    python3 mcp_cli.py --server unity state              # read-only editor snapshot
    python3 mcp_cli.py [--server ...] tools [--names]
    python3 mcp_cli.py [--server ...] schema browser_tabs
    python3 mcp_cli.py [--server ...] call browser_tabs '{"action":"list"}'

One short-lived MCP session per invocation; nothing is cached on disk.
"""

from __future__ import annotations

import argparse
import base64
import json
import mimetypes
import os
import re
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, replace
from typing import Any, Optional

PROTOCOL_VERSION = "2025-06-18"
CLIENT_NAME, CLIENT_VERSION = "mcp-cli", "0.2.0"
EDITOR_STATE_URI = "mcpforunity://editor/state"
SSH_HOST = os.environ.get("MCP_SSH_HOST", "pc114514")
TIMEOUT = float(os.environ.get("MCP_TIMEOUT", "120"))
IMAGE_DIR = os.environ.get("MCP_IMAGE_DIR", "/tmp/mcp-images")
REMOTE_START_WAIT = 40.0  # seconds for a scheduled task to make the Windows port listen


class CliError(Exception):
    """An expected failure, reported without a traceback."""


def env_int(name: str) -> Optional[int]:
    raw = os.environ.get(name, "").strip()
    if not raw:
        return None
    if not raw.isdigit():
        raise CliError(f"{name} must be a port number, got {raw!r}")
    return int(raw)


@dataclass(frozen=True)
class Profile:
    """One MCP server: where it listens locally, remotely, and how to prove it is alive."""

    name: str
    local_port: int
    remote_host: str
    remote_port: int
    health: str                  # "http" (GET /health) or "mcp" (initialize handshake)
    start_task: str = ""         # Windows scheduled task that starts the server on demand
    hint: str = ""               # extra text for "did not answer" errors
    host_header: str = ""        # override the HTTP Host header (servers with an allowlist)

    @property
    def remote(self) -> str:
        return f"{self.remote_host}:{self.remote_port}"

    @property
    def spec(self) -> str:
        return f"{self.local_port}:{self.remote}"


PROFILES: dict[str, Profile] = {
    "unity": Profile(
        name="unity", local_port=18080, remote_host="127.0.0.1", remote_port=8080,
        health="http",
        hint="is the Unity Editor open with MCP for Unity running?",
    ),
    "browser": Profile(
        name="browser", local_port=18931, remote_host="localhost", remote_port=8931,
        health="mcp", start_task="DshPlaywrightMcp",
        hint="is Chrome open with the Playwright extension connected?",
        host_header="localhost:8931",  # Playwright MCP only accepts these Host values
    ),
}


def port_open(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(1.0)
        return sock.connect_ex(("127.0.0.1", port)) == 0


def http_health(port: int, timeout: float = 3.0) -> Optional[Any]:
    """Return the parsed /health body, or None when nothing healthy answers."""
    url = f"http://127.0.0.1:{port}/health"
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:
            body = resp.read().decode("utf-8", "replace")
            if not 200 <= resp.status < 300:
                return None
    except (urllib.error.URLError, OSError, ValueError):
        return None
    try:
        return json.loads(body)
    except ValueError:
        return body


def mcp_ready(profile: Profile, timeout: float = 5.0) -> bool:
    """True when an MCP initialize handshake succeeds (used for /health-less servers)."""
    try:
        session = McpSession(profile, timeout)
        session.open()
        session.close()
        return True
    except (CliError, OSError):
        return False


def health_detail(profile: Profile, timeout: float = 3.0) -> Optional[Any]:
    """A truthy health description, or None when the server is not usable."""
    if profile.health == "http":
        return http_health(profile.local_port, timeout)
    if not port_open(profile.local_port):
        return None
    return "mcp initialize ok" if mcp_ready(profile) else None


def find_forward_pids(profile: Profile) -> list[int]:
    """PIDs of ssh processes carrying exactly this profile's -L spec (verified via /proc)."""
    try:
        out = subprocess.run(["pgrep", "-f", "--", f"-L {profile.spec}"],
                             capture_output=True, text=True, check=False).stdout
    except FileNotFoundError as exc:
        raise CliError("pgrep not found; cannot inspect running forwards") from exc
    pids: list[int] = []
    for raw in out.split():
        try:
            pid = int(raw)
            with open(f"/proc/{pid}/cmdline", "rb") as fh:
                argv = [a.decode("utf-8", "replace") for a in fh.read().split(b"\0") if a]
        except (ValueError, OSError):
            continue
        if (argv and os.path.basename(argv[0]) == "ssh" and "-N" in argv
                and profile.spec in argv):  # exact argv element, not a substring
            pids.append(pid)
    return pids


def remote_listening(profile: Profile, host: str = SSH_HOST) -> bool:
    """Ask Windows whether the server's port is listening (one SSH round trip)."""
    cmd = f'cmd.exe /c "netstat -ano | findstr :{profile.remote_port}"'
    try:
        out = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                              host, cmd],
                             capture_output=True, text=True, timeout=40, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise CliError(f"cannot inspect Windows port {profile.remote_port}: {exc}") from exc
    for line in out.stdout.splitlines():
        if "LISTENING" in line.upper() and re.search(rf"[:.]{profile.remote_port}(?!\d)", line):
            return True
    return False


def ensure_started(profile: Profile, host: str = SSH_HOST) -> None:
    """Profile hook: start the Windows scheduled task when the remote port is closed."""
    if not profile.start_task or remote_listening(profile, host):
        return
    print(f"{profile.name}: Windows port {profile.remote_port} is closed; "
          f"starting {profile.start_task}", file=sys.stderr)
    result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", host,
                             f'cmd.exe /c "schtasks /run /tn {profile.start_task}"'],
                            capture_output=True, text=True, timeout=60, check=False)
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip() or f"exit {result.returncode}"
        raise CliError(f"schtasks /run {profile.start_task} failed: {detail}")
    deadline = time.time() + REMOTE_START_WAIT
    while time.time() < deadline:
        if remote_listening(profile, host):
            print(f"{profile.name}: Windows port {profile.remote_port} is listening",
                  file=sys.stderr)
            return
        time.sleep(2.0)
    raise CliError(f"{profile.start_task} ran but port {profile.remote_port} never listened; "
                   "check %USERPROFILE%\\.dsh\\playwright-mcp\\server.log on Windows")


def wait_healthy(profile: Profile, timeout: float) -> bool:
    """Poll until the profile answers; deep MCP probes are throttled to one per 2 s."""
    deadline = time.time() + timeout
    last_probe = 0.0
    while time.time() < deadline:
        now = time.time()
        if profile.health == "http" or now - last_probe >= 2.0:
            last_probe = now
            if health_detail(profile):
                return True
        time.sleep(0.25)
    return False


def tunnel_up(profile: Profile, host: str = SSH_HOST, wait: float = 15.0) -> bool:
    """Ensure the forward is up. Returns True when this call started it."""
    if health_detail(profile):
        print(f"{profile.name}: already up (port {profile.local_port})", file=sys.stderr)
        return False
    owned = find_forward_pids(profile)
    if not owned and port_open(profile.local_port):
        raise CliError(f"port {profile.local_port} is occupied but is not our forward "
                       f"({profile.spec}); free it or pass --port")
    started = False
    if not owned:
        cmd = ["ssh", "-f", "-N", "-o", "ExitOnForwardFailure=yes",
               "-o", "ServerAliveInterval=30", "-o", "BatchMode=yes",
               "-o", "ConnectTimeout=10", "-L", profile.spec, host]
        print("tunnel: starting " + " ".join(cmd), file=sys.stderr)
        result = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            detail = (result.stderr or result.stdout).strip() or f"exit {result.returncode}"
            raise CliError(f"ssh forward failed: {detail}")
        started = True
    ensure_started(profile, host)
    if wait_healthy(profile, wait):
        print(f"{profile.name}: up (127.0.0.1:{profile.local_port} -> {profile.remote} via {host})",
              file=sys.stderr)
        return started
    raise CliError(f"forward is up but {profile.remote} never answered "
                   f"({profile.health} check); {profile.hint}")


def tunnel_down(profile: Profile) -> int:
    """Kill only forwards carrying this profile's exact -L spec."""
    pids = find_forward_pids(profile)
    if not pids:
        print(f"{profile.name}: no forward for port {profile.local_port}")
        return 0
    for pid in pids:
        try:
            os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        except PermissionError as exc:
            raise CliError(f"cannot signal pid {pid}: {exc}") from exc
    deadline = time.time() + 5
    while time.time() < deadline and find_forward_pids(profile):
        time.sleep(0.2)
    for pid in find_forward_pids(profile):  # stubborn ones
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    time.sleep(0.2)
    if find_forward_pids(profile):
        raise CliError(f"forward on port {profile.local_port} still running after SIGKILL")
    print(f"{profile.name}: down (stopped {len(pids)} ssh forward(s) on port "
          f"{profile.local_port})")
    return 0


def tunnel_status(profile: Profile) -> int:
    detail = health_detail(profile)
    payload = {
        "server": profile.name,
        "up": detail is not None,
        "local_port": profile.local_port,
        "remote": profile.remote,
        "forward": bool(find_forward_pids(profile)),
        "health": detail,
    }
    print(json.dumps(payload, indent=2, ensure_ascii=False))
    return 0 if detail is not None else 1


class McpSession:
    """One short-lived JSON-RPC session over Streamable HTTP."""

    def __init__(self, profile: Profile, timeout: float) -> None:
        self.profile = profile
        self.url = f"http://127.0.0.1:{profile.local_port}/mcp"
        self.timeout = timeout
        self.session_id: Optional[str] = None
        self._next_id = 0

    def _headers(self) -> dict[str, str]:
        headers = {"Content-Type": "application/json",
                   "Accept": "application/json, text/event-stream"}
        if self.profile.host_header:
            headers["Host"] = self.profile.host_header
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
            headers["MCP-Protocol-Version"] = PROTOCOL_VERSION
        return headers

    def _post(self, payload: dict) -> Optional[dict]:
        req = urllib.request.Request(self.url, data=json.dumps(payload).encode(),
                                     headers=self._headers(), method="POST")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                sid = resp.headers.get("Mcp-Session-Id")
                if sid:
                    self.session_id = sid
                raw = resp.read().decode("utf-8", "replace")
                ctype = resp.headers.get("Content-Type", "")
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", "replace").strip()
            raise CliError(f"HTTP {exc.code} from {self.url}: {body[:500]}") from exc
        except (TimeoutError, socket.timeout) as exc:
            raise CliError(f"request to {self.url} timed out after {self.timeout:g}s") from exc
        except urllib.error.URLError as exc:
            raise CliError(f"cannot reach {self.url} ({exc.reason}); check `tunnel up` "
                           f"and that the {self.profile.name} server is running") from exc
        return self._parse(raw, ctype, payload.get("id"))

    @staticmethod
    def _parse(raw: str, content_type: str, rid: Any = None) -> Optional[dict]:
        """Accept a plain JSON body or an SSE stream; pick the message for rid."""
        raw = raw.strip()
        if not raw:
            return None
        if "text/event-stream" in content_type or raw.startswith("data:"):
            messages = []
            for line in raw.splitlines():
                if line.strip().startswith("data:"):
                    chunk = line.strip()[len("data:"):].strip()
                    if chunk and chunk != "[DONE]":
                        try:
                            messages.append(json.loads(chunk))
                        except ValueError:
                            continue
            if not messages:
                raise CliError(f"could not parse SSE response: {raw[:400]}")
            for message in messages:  # skip interleaved notifications
                if isinstance(message, dict) and message.get("id") == rid:
                    return message
            return messages[-1]
        try:
            return json.loads(raw)
        except ValueError as exc:
            raise CliError(f"could not parse JSON response: {raw[:400]}") from exc

    def request(self, method: str, params: Optional[dict] = None) -> dict:
        self._next_id += 1
        payload: dict = {"jsonrpc": "2.0", "id": self._next_id, "method": method}
        if params is not None:
            payload["params"] = params
        message = self._post(payload)
        if not message:
            raise CliError("empty response from server")
        if "error" in message:
            err = message["error"]
            raise CliError(f"JSON-RPC error {err.get('code')}: {err.get('message')}")
        if "result" not in message:
            raise CliError(f"unexpected response: {json.dumps(message)[:400]}")
        return message["result"]

    def notify(self, method: str) -> None:
        self._post({"jsonrpc": "2.0", "method": method})  # server replies 202, no body

    def open(self) -> None:
        self.request("initialize", {"protocolVersion": PROTOCOL_VERSION,
                                    "capabilities": {},
                                    "clientInfo": {"name": CLIENT_NAME,
                                                   "version": CLIENT_VERSION}})
        self.notify("notifications/initialized")
        if not self.session_id:
            raise CliError("server did not return Mcp-Session-Id")

    def close(self) -> None:
        """Best-effort DELETE so long-running servers do not accumulate sessions."""
        if not self.session_id:
            return
        headers = {"Mcp-Session-Id": self.session_id}
        if self.profile.host_header:
            headers["Host"] = self.profile.host_header
        req = urllib.request.Request(self.url, method="DELETE", headers=headers)
        try:
            urllib.request.urlopen(req, timeout=5).close()
        except (urllib.error.URLError, OSError, ValueError):
            pass
        self.session_id = None

    def list_tools(self) -> list[dict]:
        tools: list[dict] = []
        cursor: Optional[str] = None
        while True:
            result = self.request("tools/list", {"cursor": cursor} if cursor else {})
            tools.extend(result.get("tools") or [])
            cursor = result.get("nextCursor")
            if not cursor:
                return tools

    def call_tool(self, name: str, arguments: dict) -> dict:
        return self.request("tools/call", {"name": name, "arguments": arguments})


def open_session(args: argparse.Namespace) -> McpSession:
    session = McpSession(args.profile, args.timeout)
    session.open()
    return session


def list_tools(args: argparse.Namespace) -> int:
    tools = open_session(args).list_tools()
    if args.names:
        print("\n".join(t.get("name", "?") for t in tools))
        return 0
    width = max((len(t.get("name", "")) for t in tools), default=0)
    for tool in tools:
        lines = (tool.get("description") or "").strip().splitlines()
        print(f"{tool.get('name', '?'):<{width}}  {lines[0] if lines else ''}")
    print(f"\n{len(tools)} tool(s)", file=sys.stderr)
    return 0


def show_schema(args: argparse.Namespace) -> int:
    for tool in open_session(args).list_tools():
        if tool.get("name") == args.tool:
            print(json.dumps(tool.get("inputSchema") or {}, indent=2, ensure_ascii=False))
            return 0
    raise CliError(f"unknown tool on {args.profile.name}: {args.tool} (run `tools --names`)")


def show_state(args: argparse.Namespace) -> int:
    result = open_session(args).request("resources/read", {"uri": args.uri})
    contents = result.get("contents") or []
    if not contents:
        raise CliError(f"resource {args.uri} returned no contents")
    for item in contents:
        text = item.get("text") or ""
        try:
            text = json.dumps(json.loads(text), indent=2, ensure_ascii=False)
        except ValueError:
            pass
        print(text)
    return 0


def load_arguments(arg: Optional[str]) -> dict:
    if not arg:
        return {}
    if arg == "-":
        text = sys.stdin.read()
    elif arg.startswith("@"):
        try:
            with open(arg[1:], "r", encoding="utf-8") as fh:
                text = fh.read()
        except OSError as exc:
            raise CliError(f"cannot read {arg[1:]}: {exc.strerror}") from exc
    else:
        text = arg
    try:
        parsed = json.loads(text)
    except ValueError as exc:
        raise CliError(f"arguments are not valid JSON: {exc}") from exc
    if not isinstance(parsed, dict):
        raise CliError("arguments must be a JSON object")
    return parsed


def save_image(item: dict, directory: str, name: str, seen: dict[str, str]) -> str:
    """Write one base64 image item to disk and return its path (deduped per invocation)."""
    data = item.get("data")
    if not isinstance(data, str) or not data:
        raise CliError("image content item has no base64 data")
    if data in seen:
        return seen[data]
    mime = item.get("mimeType") or "image/png"
    ext = mimetypes.guess_extension(mime) or ".png"
    try:
        payload = base64.b64decode(data, validate=False)
    except ValueError as exc:
        raise CliError(f"image content item is not valid base64: {exc}") from exc
    stem = re.sub(r"[^A-Za-z0-9_.-]", "_", name) or "image"
    stamp = time.strftime("%Y%m%d-%H%M%S") + f"-{int(time.time() * 1000) % 1000:03d}"
    path = os.path.join(directory, f"{stem}-{stamp}-{len(seen) + 1:02d}{ext}")
    try:
        os.makedirs(directory, exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(payload)
    except OSError as exc:
        raise CliError(f"cannot save image to {path}: {exc}") from exc
    seen[data] = path
    return path


def scrub_images(value: Any, directory: str, name: str, seen: dict[str, str]) -> Any:
    """Replace every {type: image, data: base64} node with {type: image, path: ...}."""
    if isinstance(value, dict):
        if value.get("type") == "image" and isinstance(value.get("data"), str):
            return {"type": "image", "mimeType": value.get("mimeType", ""),
                    "path": save_image(value, directory, name, seen)}
        return {k: scrub_images(v, directory, name, seen) for k, v in value.items()}
    if isinstance(value, list):
        return [scrub_images(v, directory, name, seen) for v in value]
    return value


def call_tool(args: argparse.Namespace) -> int:
    result = open_session(args).call_tool(args.tool, load_arguments(args.args))
    directory = args.save_images or IMAGE_DIR
    seen: dict[str, str] = {}
    lines: list[str] = []
    for item in result.get("content") or []:
        kind = item.get("type")
        if kind == "text":
            lines.append(item.get("text", ""))
        elif kind == "image":
            lines.append(f"[image] {save_image(item, directory, args.tool, seen)}")
        else:
            lines.append(json.dumps(item, indent=2, ensure_ascii=False))
    structured = scrub_images(result.get("structuredContent"), directory, args.tool, seen)
    if structured is not None and len(lines) == 1:
        try:  # don't print the same payload twice
            if json.loads(lines[0]) == structured:
                lines = []
        except ValueError:
            pass
    for line in lines:
        print(line)
    if structured is not None:
        print(json.dumps(structured, indent=2, ensure_ascii=False))
    if not lines and structured is None:
        print(json.dumps(result, indent=2, ensure_ascii=False))
    if result.get("isError"):
        print(f"error: tool {args.tool} reported isError", file=sys.stderr)
        return 1
    return 0


def cmd_tunnel(args: argparse.Namespace) -> int:
    profiles = list(PROFILES.values()) if args.all else [args.profile]
    if args.action == "status":
        if len(profiles) != 1:
            raise CliError("--all applies to `tunnel up` / `tunnel down`, not `status`")
        return tunnel_status(args.profile)
    if args.action == "down":
        for profile in profiles:
            tunnel_down(profile)
        return 0
    for profile in profiles:
        tunnel_up(profile, args.host)
    return 0


def resolve_profile(args: argparse.Namespace) -> Profile:
    profile = PROFILES.get(args.server)
    if profile is None:
        raise CliError(f"unknown server {args.server!r}; choose one of: {', '.join(PROFILES)}")
    return replace(profile, local_port=args.port) if args.port else profile


def validate_args(args: argparse.Namespace) -> None:
    """Reject profile/command combinations before touching any tunnel."""
    if args.command == "state" and args.profile.name != "unity":
        raise CliError("`state` reads an MCP for Unity resource; use --server unity")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="mcp_cli.py",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description="Drive MCP servers on the Windows PC through SSH forwards.",
        epilog=("examples:\n"
                "  mcp_cli.py tools --names\n"
                "  mcp_cli.py --server unity state\n"
                "  mcp_cli.py --server browser schema browser_tabs\n"
                "  mcp_cli.py --server browser call browser_tabs '{\"action\":\"list\"}'\n"
                "  mcp_cli.py tunnel down --all\n"
                "env: MCP_SERVER, MCP_PORT, MCP_SSH_HOST, MCP_TIMEOUT, MCP_IMAGE_DIR"))
    parser.add_argument("--server", choices=sorted(PROFILES),
                        default=os.environ.get("MCP_SERVER", "unity").strip().lower(),
                        help="server profile (default unity, or MCP_SERVER)")
    parser.add_argument("--port", type=int, default=env_int("MCP_PORT"),
                        help="override the profile's local forward port")
    parser.add_argument("--host", default=SSH_HOST,
                        help=f"SSH host alias (default {SSH_HOST})")
    parser.add_argument("--timeout", type=float, default=TIMEOUT,
                        help=f"per-request timeout seconds (default {TIMEOUT:g})")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("tunnel", help="manage the SSH port forward")
    p.add_argument("action", choices=("up", "down", "status"))
    p.add_argument("--all", action="store_true",
                   help="operate on every profile (up/down only)")
    p.set_defaults(func=cmd_tunnel, needs_tunnel=False)

    p = sub.add_parser("state", help="read-only Unity editor state snapshot (unity only)")
    p.add_argument("--uri", default=EDITOR_STATE_URI, help=f"default {EDITOR_STATE_URI}")
    p.set_defaults(func=show_state, needs_tunnel=True)

    p = sub.add_parser("tools", help="list MCP tools")
    p.add_argument("--names", action="store_true", help="print names only")
    p.set_defaults(func=list_tools, needs_tunnel=True)

    p = sub.add_parser("schema", help="print one tool's input schema")
    p.add_argument("tool")
    p.set_defaults(func=show_schema, needs_tunnel=True)

    p = sub.add_parser("call", help="call a tool")
    p.add_argument("tool")
    p.add_argument("args", nargs="?", default=None,
                   help="JSON object, @file.json, or - for stdin")
    p.add_argument("--save-images", metavar="DIR", default=None,
                   help=f"where to write image results (default {IMAGE_DIR})")
    p.set_defaults(func=call_tool, needs_tunnel=True)
    return parser


def main(argv: Optional[list[str]] = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        args.profile = resolve_profile(args)
        validate_args(args)
        if getattr(args, "needs_tunnel", False):
            tunnel_up(args.profile, args.host)
        return args.func(args)
    except KeyboardInterrupt:
        print("interrupted", file=sys.stderr)
        return 130
    except CliError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
