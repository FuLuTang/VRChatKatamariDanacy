#!/usr/bin/env python3
"""Minimal, stdlib-only CLI for "MCP for Unity" (v10.x, HTTPLocal transport).

Reaches the Unity MCP server on the user's Windows PC through an on-demand SSH
port forward, so agents on this Linux host can list and call Unity tools.

    python3 unity_mcp.py tunnel up|down|status
    python3 unity_mcp.py state                  # read-only editor snapshot
    python3 unity_mcp.py tools [--names]
    python3 unity_mcp.py schema read_console
    python3 unity_mcp.py call read_console '{"action":"get","count":20}'

One short-lived MCP session per invocation; nothing is cached on disk.
"""

from __future__ import annotations

import argparse
import json
import os
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from typing import Any, Optional

PROTOCOL_VERSION = "2025-06-18"
CLIENT_NAME, CLIENT_VERSION = "unity-mcp-cli", "0.1.0"
EDITOR_STATE_URI = "mcpforunity://editor/state"
DEFAULT_SSH_HOST = os.environ.get("UNITY_MCP_SSH_HOST", "pc114514")
DEFAULT_LOCAL_PORT = int(os.environ.get("UNITY_MCP_PORT", "18080"))
REMOTE_PORT = int(os.environ.get("UNITY_MCP_REMOTE_PORT", "8080"))
DEFAULT_TIMEOUT = float(os.environ.get("UNITY_MCP_TIMEOUT", "120"))


class CliError(Exception):
    """An expected failure, reported without a traceback."""


def forward_spec(local_port: int) -> str:
    return f"{local_port}:127.0.0.1:{REMOTE_PORT}"


def probe_health(local_port: int, timeout: float = 3.0) -> Optional[str]:
    """Return the /health body, or None when nothing healthy answers."""
    url = f"http://127.0.0.1:{local_port}/health"
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:
            body = resp.read().decode("utf-8", "replace")
            return body if 200 <= resp.status < 300 else None
    except (urllib.error.URLError, OSError, ValueError):
        return None


def port_open(local_port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(1.0)
        return sock.connect_ex(("127.0.0.1", local_port)) == 0


def find_forward_pids(spec: str) -> list[int]:
    """PIDs of ssh processes carrying exactly this -L spec (verified via /proc)."""
    try:
        out = subprocess.run(["pgrep", "-f", "--", f"-L {spec}"],
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
        if argv and os.path.basename(argv[0]) == "ssh" and "-N" in argv and spec in argv:
            pids.append(pid)
    return pids


def tunnel_up(local_port: int, host: str, wait: float = 15.0) -> bool:
    """Ensure the forward is up. Returns True when this call started it."""
    if probe_health(local_port):
        print(f"tunnel: already up (port {local_port})", file=sys.stderr)
        return False
    if port_open(local_port):
        raise CliError(f"port {local_port} is occupied but /health does not answer; "
                       "something else is using it (set UNITY_MCP_PORT)")
    cmd = ["ssh", "-f", "-N", "-o", "ExitOnForwardFailure=yes",
           "-o", "ServerAliveInterval=30", "-o", "BatchMode=yes",
           "-o", "ConnectTimeout=10", "-L", forward_spec(local_port), host]
    print("tunnel: starting " + " ".join(cmd), file=sys.stderr)
    result = subprocess.run(cmd, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip() or f"exit {result.returncode}"
        raise CliError(f"ssh forward failed: {detail}")
    deadline = time.time() + wait
    while time.time() < deadline:
        if probe_health(local_port):
            print(f"tunnel: up (127.0.0.1:{local_port} -> {host}:{REMOTE_PORT})",
                  file=sys.stderr)
            return True
        time.sleep(0.25)
    raise CliError(f"forward started but /health on port {local_port} never answered; "
                   "is the Unity Editor open with MCP for Unity running?")


def tunnel_down(local_port: int) -> int:
    spec = forward_spec(local_port)
    pids = find_forward_pids(spec)
    if not pids:
        print(f"tunnel: no forward for port {local_port}")
        return 0
    for pid in pids:
        try:
            os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        except PermissionError as exc:
            raise CliError(f"cannot signal pid {pid}: {exc}") from exc
    deadline = time.time() + 5
    while time.time() < deadline and find_forward_pids(spec):
        time.sleep(0.2)
    for pid in find_forward_pids(spec):  # stubborn ones
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    time.sleep(0.2)
    if find_forward_pids(spec):
        raise CliError(f"forward on port {local_port} still running after SIGKILL")
    print(f"tunnel: down (stopped {len(pids)} ssh forward(s) on port {local_port})")
    return 0


def tunnel_status(local_port: int) -> int:
    body = probe_health(local_port)
    if body is None:
        print(json.dumps({"up": False, "port": local_port}, indent=2))
        return 1
    try:
        health: Any = json.loads(body)
    except ValueError:
        health = body
    print(json.dumps({"up": True, "port": local_port, "health": health}, indent=2))
    return 0


class McpSession:
    """One short-lived JSON-RPC session over Streamable HTTP."""

    def __init__(self, local_port: int, timeout: float) -> None:
        self.url = f"http://127.0.0.1:{local_port}/mcp"
        self.timeout = timeout
        self.session_id: Optional[str] = None
        self._next_id = 0

    def _post(self, payload: dict) -> Optional[dict]:
        headers = {"Content-Type": "application/json",
                   "Accept": "application/json, text/event-stream"}
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
            headers["MCP-Protocol-Version"] = PROTOCOL_VERSION
        req = urllib.request.Request(self.url, data=json.dumps(payload).encode(),
                                     headers=headers, method="POST")
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
                           "and that the Unity Editor is open") from exc
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
    session = McpSession(args.port, args.timeout)
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
    raise CliError(f"unknown tool: {args.tool} (run `tools --names`)")


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


def call_tool(args: argparse.Namespace) -> int:
    result = open_session(args).call_tool(args.tool, load_arguments(args.args))
    texts = [c.get("text", "") for c in result.get("content") or []
             if c.get("type") == "text"]
    structured = result.get("structuredContent")
    if structured is not None and len(texts) == 1:
        try:  # don't print the same payload twice
            if json.loads(texts[0]) == structured:
                texts = []
        except ValueError:
            pass
    for text in texts:
        print(text)
    if structured is not None:
        print(json.dumps(structured, indent=2, ensure_ascii=False))
    if not texts and structured is None:
        print(json.dumps(result, indent=2, ensure_ascii=False))
    if result.get("isError"):
        print(f"error: tool {args.tool} reported isError", file=sys.stderr)
        return 1
    return 0


def cmd_tunnel(args: argparse.Namespace) -> int:
    if args.action == "down":
        return tunnel_down(args.port)
    if args.action == "status":
        return tunnel_status(args.port)
    tunnel_up(args.port, args.host)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="unity_mcp.py",
        description="Drive Unity Editor via MCP for Unity through an SSH forward.",
        epilog=("examples:\n"
                "  unity_mcp.py tools --names\n"
                "  unity_mcp.py schema read_console\n"
                "  unity_mcp.py call read_console '{\"action\":\"get\",\"count\":20}'\n"
                "  unity_mcp.py tunnel down\n"
                "env: UNITY_MCP_PORT, UNITY_MCP_SSH_HOST, UNITY_MCP_REMOTE_PORT, "
                "UNITY_MCP_TIMEOUT"))
    parser.add_argument("--port", type=int, default=DEFAULT_LOCAL_PORT,
                        help=f"local forward port (default {DEFAULT_LOCAL_PORT})")
    parser.add_argument("--host", default=DEFAULT_SSH_HOST,
                        help=f"SSH host alias (default {DEFAULT_SSH_HOST})")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT,
                        help=f"per-request timeout seconds (default {DEFAULT_TIMEOUT:g})")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("tunnel", help="manage the SSH port forward")
    p.add_argument("action", choices=("up", "down", "status"))
    p.set_defaults(func=cmd_tunnel, needs_tunnel=False)

    p = sub.add_parser("state", help="read-only editor state snapshot (MCP resource)")
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
    p.set_defaults(func=call_tool, needs_tunnel=True)
    return parser


def main(argv: Optional[list[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if getattr(args, "needs_tunnel", False):
            tunnel_up(args.port, args.host)
        return args.func(args)
    except KeyboardInterrupt:
        print("interrupted", file=sys.stderr)
        return 130
    except CliError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
