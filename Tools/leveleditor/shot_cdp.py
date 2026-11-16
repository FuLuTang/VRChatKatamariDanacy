#!/usr/bin/env python3
"""A tiny Chrome DevTools Protocol client for the shot daemon.

No third-party packages: the WebSocket client is a few dozen lines of RFC 6455
(a local, trusted, text-only connection), and the Chrome process is launched with
``--remote-debugging-port=0`` plus the ``DevToolsActivePort`` file Chrome writes.

Only the domains the renderer needs are used: Page (navigate), Runtime (evaluate +
console errors), Log (browser/page errors), Emulation (deterministic viewport).
"""

from __future__ import annotations

import base64
import glob
import json
import os
import shutil
import socket
import struct
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

CHROME_CANDIDATES = (
    "chromium", "chromium-browser", "google-chrome", "google-chrome-stable",
    "brave-browser", "microsoft-edge", "chrome",
)


class CDPError(Exception):
    def __init__(self, message, details=None):
        super().__init__(message)
        self.details = details or []


def find_chrome(explicit=None):
    """An executable Chrome/Chromium, preferring the Playwright download cache."""
    candidates = []
    if explicit:
        candidates.append(Path(explicit).expanduser())
    env = os.environ.get("VRC_KATAMARI_CHROME")
    if env:
        candidates.append(Path(env).expanduser())
    cache = Path.home() / ".cache" / "ms-playwright"
    for pattern in ("chromium_headless_shell-*/chrome-headless-shell-linux-*/chrome-headless-shell",
                    "chromium-*/chrome-linux*/chrome"):
        for match in sorted(glob.glob(str(cache / pattern)), reverse=True):
            candidates.append(Path(match))
    for name in CHROME_CANDIDATES:
        found = shutil.which(name)
        if found:
            candidates.append(Path(found))
    for candidate in candidates:
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return candidate
    raise CDPError(
        "no Chromium/Chrome found; install one, set VRC_KATAMARI_CHROME, or pass --chrome")


# --------------------------------------------------------------------------- #
# RFC 6455 client (text frames, fragmentation, ping/pong)
# --------------------------------------------------------------------------- #
class WebSocket:
    def __init__(self, url, timeout=30.0):
        if not url.startswith("ws://"):
            raise CDPError("only ws:// URLs are supported, got %r" % url)
        rest = url[len("ws://"):]
        host_port, _, path = rest.partition("/")
        host, _, port = host_port.partition(":")
        self.socket = socket.create_connection((host, int(port or 80)), timeout=timeout)
        self.socket.settimeout(timeout)
        self.buffer = b""
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        request = (
            "GET /%s HTTP/1.1\r\n"
            "Host: %s\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            "Sec-WebSocket-Key: %s\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n" % (path, host_port, key))
        self.socket.sendall(request.encode("ascii"))
        header = b""
        while b"\r\n\r\n" not in header:
            chunk = self.socket.recv(4096)
            if not chunk:
                raise CDPError("the DevTools WebSocket closed during the handshake")
            header += chunk
        head, _, self.buffer = header.partition(b"\r\n\r\n")
        status = head.split(b"\r\n", 1)[0]
        if b" 101 " not in status:
            raise CDPError("DevTools WebSocket refused: %s"
                           % status.decode("latin-1", "replace"))

    # -- framing -------------------------------------------------------- #
    def send_text(self, text):
        payload = text.encode("utf-8")
        header = bytearray([0x81])
        length = len(payload)
        if length < 126:
            header.append(0x80 | length)
        elif length < (1 << 16):
            header.append(0x80 | 126)
            header += struct.pack(">H", length)
        else:
            header.append(0x80 | 127)
            header += struct.pack(">Q", length)
        mask = os.urandom(4)
        header += mask
        masked = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))
        self.socket.sendall(bytes(header) + masked)

    def _read_exact(self, count):
        while len(self.buffer) < count:
            chunk = self.socket.recv(max(65536, count - len(self.buffer)))
            if not chunk:
                raise CDPError("the DevTools WebSocket closed")
            self.buffer += chunk
        data, self.buffer = self.buffer[:count], self.buffer[count:]
        return data

    def recv_message(self):
        fragments = []
        opcode = None
        while True:
            first, second = self._read_exact(2)
            fin = bool(first & 0x80)
            frame_opcode = first & 0x0F
            masked = bool(second & 0x80)
            length = second & 0x7F
            if length == 126:
                length = struct.unpack(">H", self._read_exact(2))[0]
            elif length == 127:
                length = struct.unpack(">Q", self._read_exact(8))[0]
            mask = self._read_exact(4) if masked else None
            payload = self._read_exact(length)
            if mask:
                payload = bytes(byte ^ mask[index % 4]
                                for index, byte in enumerate(payload))
            if frame_opcode == 0x9:                       # ping -> pong
                self._send_control(0xA, payload)
                continue
            if frame_opcode == 0xA:                       # pong
                continue
            if frame_opcode == 0x8:
                raise CDPError("the DevTools WebSocket closed the connection")
            if frame_opcode != 0x0:
                opcode = frame_opcode
            fragments.append(payload)
            if fin:
                return b"".join(fragments).decode("utf-8", "replace")

    def _send_control(self, opcode, payload):
        mask = os.urandom(4)
        header = bytes([0x80 | opcode, 0x80 | len(payload)]) + mask
        masked = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))
        self.socket.sendall(header + masked)

    def close(self):
        try:
            self._send_control(0x8, b"")
        except OSError:
            pass
        try:
            self.socket.close()
        except OSError:
            pass


# --------------------------------------------------------------------------- #
# CDP connection
# --------------------------------------------------------------------------- #
class CDP:
    """One page-target connection; ``call`` dispatches events while it waits."""

    def __init__(self, ws_url, timeout=60.0):
        self.ws = WebSocket(ws_url, timeout=timeout)
        self.timeout = timeout
        self.counter = 0
        self.console = []
        self.exceptions = []
        self.log_entries = []
        self.on_event = None

    def close(self):
        try:
            self.call("Page.close", timeout=2.0)
        except CDPError:
            pass
        self.ws.close()

    def _remember(self, method, params):
        if method == "Runtime.consoleAPICalled":
            kind = params.get("type") or "log"
            text = " ".join(_remote_text(argument)
                            for argument in params.get("args") or [])
            if kind in ("error", "warning", "assert"):
                self.console.append({"type": kind, "text": text[:2000],
                                     "at": round(time.time(), 3)})
        elif method == "Runtime.exceptionThrown":
            details = params.get("exceptionDetails") or {}
            text = details.get("text") or ""
            exception = details.get("exception") or {}
            description = exception.get("description") or exception.get("value") or ""
            self.exceptions.append(("%s %s" % (text, description)).strip()[:2000])
        elif method == "Log.entryAdded":
            entry = params.get("entry") or {}
            if entry.get("level") in ("error", "warning"):
                self.log_entries.append({
                    "level": entry.get("level"), "text": (entry.get("text") or "")[:2000],
                    "url": entry.get("url"), "line": entry.get("lineNumber")})
        if self.on_event:
            self.on_event(method, params)

    def call(self, method, params=None, timeout=None):
        self.counter += 1
        request_id = self.counter
        message = {"id": request_id, "method": method}
        if params is not None:
            message["params"] = params
        self.ws.send_text(json.dumps(message, ensure_ascii=False))
        deadline = time.time() + (timeout or self.timeout)
        while True:
            remaining = deadline - time.time()
            if remaining <= 0:
                raise CDPError("CDP %s timed out after %.1fs"
                               % (method, timeout or self.timeout))
            self.ws.socket.settimeout(remaining)
            try:
                raw = self.ws.recv_message()
            except socket.timeout:
                raise CDPError("CDP %s timed out" % method)
            try:
                payload = json.loads(raw)
            except json.JSONDecodeError:
                continue
            if "method" in payload:
                self._remember(payload["method"], payload.get("params") or {})
                continue
            if payload.get("id") != request_id:
                continue
            if "error" in payload:
                error = payload["error"]
                raise CDPError("%s: %s" % (method, error.get("message")),
                               [error.get("data")] if error.get("data") else [])
            return payload.get("result") or {}

    # -- convenience ---------------------------------------------------- #
    def evaluate(self, expression, await_promise=False, timeout=None, by_value=True):
        result = self.call("Runtime.evaluate", {
            "expression": expression,
            "awaitPromise": bool(await_promise),
            "returnByValue": bool(by_value),
            "userGesture": False,
        }, timeout=timeout)
        if result.get("exceptionDetails"):
            details = result["exceptionDetails"]
            text = details.get("text") or "page exception"
            exception = details.get("exception") or {}
            description = exception.get("description") or exception.get("value") or ""
            raise CDPError("%s %s" % (text, description), [details.get("url")])
        return (result.get("result") or {}).get("value")

    def errors(self):
        rows = []
        seen = set()
        for entry in self.console:
            key = ("console", entry["text"])
            if key in seen:
                continue
            seen.add(key)
            rows.append("console.%s: %s" % (entry["type"], entry["text"]))
        for entry in self.log_entries:
            key = ("log", entry["text"])
            if key in seen:
                continue
            seen.add(key)
            rows.append("log.%s: %s" % (entry["level"], entry["text"]))
        for text in self.exceptions:
            key = ("exception", text)
            if key in seen:
                continue
            seen.add(key)
            rows.append("exception: %s" % text)
        return rows[:30]

    def clear_errors(self):
        self.console = []
        self.exceptions = []
        self.log_entries = []


def _remote_text(argument):
    if not isinstance(argument, dict):
        return str(argument)
    if "value" in argument:
        return str(argument["value"])
    if argument.get("unserializableValue"):
        return str(argument["unserializableValue"])
    return str(argument.get("description") or argument.get("type") or "")


# --------------------------------------------------------------------------- #
# Chrome process
# --------------------------------------------------------------------------- #
class Chrome:
    """A headless Chromium with a throwaway profile and a page-target CDP session."""

    def __init__(self, binary=None, profile_dir=None, width=1280, height=720,
                 log_path=None, extra_args=None):
        self.binary = Path(binary).expanduser() if binary else find_chrome()
        self.profile = Path(profile_dir) if profile_dir else None
        self.width, self.height = int(width), int(height)
        self.log_path = Path(log_path) if log_path else None
        self.extra_args = list(extra_args or [])
        self.process = None
        self.port = None
        self.cdp = None
        self._log_handle = None

    def _args(self, sandbox):
        command = [
            str(self.binary),
            "--remote-debugging-port=0",
            "--user-data-dir=%s" % self.profile,
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--disable-dev-shm-usage",
            "--hide-scrollbars",
            "--mute-audio",
            "--force-device-scale-factor=1",
            "--window-size=%d,%d" % (self.width, self.height),
            # Software WebGL.  SwiftShader is bundled with Chrome; without these flags
            # a headless box with no GPU gets a 0x0 WebGL context and the page renders
            # nothing.
            "--use-gl=angle",
            "--use-angle=swiftshader",
            "--enable-unsafe-swiftshader",
        ]
        if not sandbox:
            command.append("--no-sandbox")
        command += self.extra_args
        command.append("about:blank")
        return command

    def _spawn(self, sandbox):
        if self._log_handle is None:
            if self.log_path:
                self.log_path.parent.mkdir(parents=True, exist_ok=True)
                self._log_handle = open(self.log_path, "ab")
            else:
                self._log_handle = subprocess.DEVNULL
        return subprocess.Popen(
            self._args(sandbox), stdout=self._log_handle, stderr=self._log_handle,
            stdin=subprocess.DEVNULL, start_new_session=True, close_fds=True)

    def start(self, timeout=20.0):
        self.profile.mkdir(parents=True, exist_ok=True)
        active_port = self.profile / "DevToolsActivePort"
        try:
            active_port.unlink()
        except OSError:
            pass
        self.process = self._spawn(sandbox=True)
        # Many Linux hosts (AppArmor, containers, CI) have no usable unprivileged user
        # namespace: Chrome prints "No usable sandbox!" and exits at once.  Retry once
        # without the sandbox; the page is a local, token-protected editor.
        deadline = time.time() + min(4.0, timeout)
        while time.time() < deadline:
            if active_port.is_file():
                break
            if self.process.poll() is not None:
                self.process = self._spawn(sandbox=False)
                break
            time.sleep(0.05)
        deadline = time.time() + timeout
        while time.time() < deadline:
            if active_port.is_file():
                break
            if self.process.poll() is not None:
                raise CDPError(
                    "Chromium exited during startup (log: %s)\n%s"
                    % (self.log_path, _tail_log(self.log_path)))
            time.sleep(0.1)
        else:
            raise CDPError("Chromium did not open a DevTools port within %.1fs (log: %s)"
                           % (timeout, self.log_path))
        lines = active_port.read_text(encoding="utf-8").splitlines()
        self.port = int(lines[0].strip())
        deadline = time.time() + timeout
        targets = None
        while time.time() < deadline:
            try:
                with urllib.request.urlopen(
                        "http://127.0.0.1:%d/json/list" % self.port, timeout=2.0) as response:
                    targets = json.loads(response.read().decode("utf-8"))
                break
            except (OSError, ValueError):
                time.sleep(0.1)
        if not targets:
            raise CDPError("Chromium's DevTools endpoint did not answer")
        page = next((target for target in targets if target.get("type") == "page"), None)
        if page is None:
            raise CDPError("Chromium has no page target")
        self.cdp = CDP(page["webSocketDebuggerUrl"])
        self.cdp.call("Page.enable")
        self.cdp.call("Runtime.enable")
        self.cdp.call("Log.enable")
        self.cdp.call("Emulation.setDeviceMetricsOverride", {
            "width": self.width, "height": self.height,
            "deviceScaleFactor": 1, "mobile": False,
        })
        return self

    def navigate(self, url, timeout=60.0):
        if self.cdp is None:
            raise CDPError("Chrome is not started")
        self.cdp.clear_errors()
        self.cdp.call("Page.navigate", {"url": url}, timeout=timeout)
        return self

    def stop(self, timeout=8.0):
        if self.process is None:
            return
        try:
            if self.cdp is not None:
                self.cdp.call("Browser.close", timeout=2.0)
        except CDPError:
            pass
        try:
            self.process.terminate()
        except OSError:
            pass
        try:
            self.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            try:
                self.process.kill()
                self.process.wait(timeout=3.0)
            except (OSError, subprocess.TimeoutExpired):
                pass
        if self.cdp is not None:
            try:
                self.cdp.ws.close()
            except OSError:
                pass
            self.cdp = None
        self.process = None
        if self.profile:
            shutil.rmtree(self.profile, ignore_errors=True)


def _tail_log(path, limit=1200):
    try:
        return Path(path).read_text(encoding="utf-8", errors="replace")[-limit:].strip()
    except (OSError, TypeError):
        return ""


def wait_for_page_ready(chrome, timeout=120.0, poll=0.1):
    """Poll the page's own ``window.__editorReady`` promise - no fixed sleeps."""
    deadline = time.time() + timeout
    last_error = None
    while time.time() < deadline:
        try:
            kind = chrome.cdp.evaluate(
                "typeof window.__editorShot !== 'undefined' && "
                "typeof window.__editorShot.readiness === 'function'")
            if kind:
                break
        except CDPError as error:
            last_error = str(error)
        time.sleep(poll)
    else:
        raise CDPError("shot.js never loaded (page error: %s)" % (last_error or "none"))
    remaining = max(5.0, deadline - time.time())
    try:
        result = chrome.cdp.evaluate("window.__editorReady", await_promise=True,
                                     timeout=remaining)
    except CDPError as error:
        raise CDPError("the editor page did not become ready: %s" % error)
    if isinstance(result, dict) and result.get("ok") is False:
        raise CDPError("the editor page did not become ready: %s"
                       % (result.get("error") or "unknown"))
    return result
