"""Local transport for the resident level editor: one JSON op per HTTP request.

Deliberately boring: ``http.server`` on ``127.0.0.1`` (port 0 = pick one), a JSON body
``{"op": …, "args": {…}}`` in and the editor's response out, plus a small endpoint file
that says where the process is listening.  No Blender import here, so the transport is
tested with a fake scene and the same code path serves the real editor.

Why loopback HTTP rather than a Unix socket: the client can be ``curl`` or twenty lines
of stdlib ``urllib`` from any Bash-invoked agent, and the same transport works on
Windows, where the GUI Blender (P1) will run.  A Unix socket would be tidier but would
have to be re-written for Windows and needs a hand-rolled framing protocol.

The endpoint file is ``0600`` in a ``0700`` directory and carries a random token; every
request must repeat it.  That keeps another local account (or a stray script) from
editing the level, while leaving the file human-readable for ``curl`` use.
"""

import json
import os
import secrets
import sys
import tempfile
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

#: What a running editor is: the file name inside the runtime directory.
DEFAULT_NAME = "editor.json"

#: Bumped when the payload shape changes incompatibly; the client checks it.
PROTOCOL = 1


class EditorUnavailable(Exception):
    """No editor answered.  Usually: none started, or it crashed and left a file."""


def _user_tag():
    if hasattr(os, "getuid"):
        return str(os.getuid())
    return os.environ.get("USERNAME") or os.environ.get("USER") or "user"


def runtime_dir():
    return Path(tempfile.gettempdir()) / ("levelbuild-editor-%s" % _user_tag())


def endpoint_path(explicit=None):
    if explicit:
        return Path(explicit).expanduser().resolve()
    return runtime_dir() / DEFAULT_NAME


# --------------------------------------------------------------------------- #
# endpoint file
# --------------------------------------------------------------------------- #
def write_endpoint(path, info):
    """Write the endpoint file ``0600`` in a ``0700`` directory."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        path.parent.chmod(0o700)
    except OSError:
        pass
    payload = dict(info)
    payload.setdefault("protocol", PROTOCOL)
    payload.setdefault("started", time.strftime("%Y-%m-%dT%H:%M:%S"))
    text = json.dumps(payload, indent=2, ensure_ascii=False) + "\n"
    descriptor = os.open(str(path), os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    return payload


def read_endpoint(path=None):
    path = endpoint_path(path)
    if not path.is_file():
        raise EditorUnavailable(
            "no editor endpoint at %s; start one with\n"
            "  python3 Tools/levelbuild/levelserve.py start --level <levelId>" % path)
    try:
        info = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise EditorUnavailable("%s is unreadable (%s); the editor probably died "
                                "mid-write - remove it and start a new one" % (path, error))
    if info.get("protocol") != PROTOCOL:
        raise EditorUnavailable("%s speaks protocol %r, this client speaks %d"
                                % (path, info.get("protocol"), PROTOCOL))
    info["_path"] = str(path)
    return info


def clear_endpoint(path=None, only_pid=None):
    """Remove the endpoint file (only when it belongs to ``only_pid``, if given)."""
    path = endpoint_path(path)
    if not path.is_file():
        return False
    if only_pid is not None:
        try:
            if int(json.loads(path.read_text(encoding="utf-8")).get("pid") or -1) != int(only_pid):
                return False
        except (OSError, json.JSONDecodeError):
            pass
    try:
        path.unlink()
        return True
    except OSError:
        return False


# --------------------------------------------------------------------------- #
# client
# --------------------------------------------------------------------------- #
def _url(endpoint, path):
    host = endpoint.get("host") or "127.0.0.1"
    port = int(endpoint.get("port") or 0)
    if host in ("0.0.0.0", "::"):
        host = "127.0.0.1"
    return "http://%s:%d%s" % (host, port, path)


def call(payload, endpoint=None, path=None, timeout=120.0, token=None):
    """POST one request; returns the editor's response object.

    ``payload`` is ``{"op": …, "args": …}`` or ``{"ops": [...]}``.  Raises
    :class:`EditorUnavailable` when nothing answers (the caller prints the hint).
    """
    info = read_endpoint(endpoint if endpoint is not None else path)
    body = dict(payload)
    body["token"] = token or info.get("token")
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        _url(info, "/op"), data=data,
        headers={"Content-Type": "application/json; charset=utf-8"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        raw = error.read().decode("utf-8", "replace")
        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            raise EditorUnavailable("editor answered HTTP %d: %s" % (error.code, raw[:400]))
    except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as error:
        raise EditorUnavailable(
            "no editor answered at %s:%s (%s); the endpoint file %s is stale - "
            "remove it and start a new editor"
            % (info.get("host"), info.get("port"), error, info.get("_path")))


def health(endpoint=None, path=None, timeout=2.0):
    """``GET /health`` (no token needed); ``None`` when nothing answers."""
    try:
        info = read_endpoint(endpoint if endpoint is not None else path)
    except EditorUnavailable:
        return None
    try:
        with urllib.request.urlopen(_url(info, "/health"), timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError, ConnectionError, OSError,
            json.JSONDecodeError):
        return None


# --------------------------------------------------------------------------- #
# server
# --------------------------------------------------------------------------- #
class _Handler(BaseHTTPRequestHandler):
    server_version = "levelbuild-editor/%d" % PROTOCOL

    def log_message(self, fmt, *args):                       # noqa: D401 - stdlib hook
        self.server.note("http %s" % (fmt % args))

    def _send(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):                                        # noqa: N802 - stdlib hook
        if self.path.split("?")[0] != "/health":
            self._send(404, {"ok": False, "error": "only GET /health exists"})
            return
        self._send(200, self.server.health_payload())

    def do_POST(self):                                       # noqa: N802 - stdlib hook
        if self.path.split("?")[0] != "/op":
            self._send(404, {"ok": False, "error": "only POST /op exists"})
            return
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length <= 0 or length > 4 * 1024 * 1024:
            self._send(400, {"ok": False, "error": "a JSON body is required"})
            return
        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            self._send(400, {"ok": False, "error": "malformed JSON: %s" % error})
            return
        if not isinstance(payload, dict):
            self._send(400, {"ok": False, "error": "the body must be a JSON object"})
            return
        token = payload.pop("token", None)
        if not self.server.token_ok(token):
            self._send(403, {"ok": False, "error": "bad or missing token (read it from %s)"
                             % self.server.endpoint_path})
            return
        response = self.server.app.handle(payload)
        self._send(200, response)


class Server:
    """A single-threaded loopback HTTP server around :meth:`Editor.handle`."""

    def __init__(self, editor, endpoint, token=None, host="127.0.0.1", port=0,
                 extra=None, verbose=True):
        self.editor = editor
        self.endpoint_path = endpoint_path(endpoint)
        self.token = token or secrets.token_hex(16)
        self.extra = dict(extra or {})
        self.verbose = verbose
        self._httpd = HTTPServer((host, port), _Handler)
        self._httpd.app = editor            # the thing with .handle(payload)
        self._httpd.token_ok = self.token_ok
        self._httpd.health_payload = self.health_payload
        self._httpd.note = self.note
        self._httpd.endpoint_path = str(self.endpoint_path)
        self._httpd.timeout = 0.25          # makes handle_request() poll stop flags
        self.info = None

    # -- handlers ------------------------------------------------------- #
    def token_ok(self, token):
        return isinstance(token, str) and secrets.compare_digest(token, self.token)

    def health_payload(self):
        doc = self.editor.doc
        return {
            "ok": True,
            "protocol": PROTOCOL,
            "pid": os.getpid(),
            "level": self.editor.level,
            "open": doc is not None,
            "props": None if doc is None else len(doc.get("props") or []),
            "uptimeSeconds": round(time.time() - self.editor.started, 1),
            "blender": self.extra.get("blender"),
        }

    def note(self, message):
        if self.verbose:
            print("[editor] %s" % message, flush=True)

    # -- lifecycle ------------------------------------------------------ #
    def start(self):
        host, port = self._httpd.server_address[:2]
        self.info = write_endpoint(self.endpoint_path, {
            "host": host,
            "port": int(port),
            "token": self.token,
            "pid": os.getpid(),
            "level": self.editor.level,
            "path": str(self.editor.path) if self.editor.path else None,
            "log": self.extra.get("log"),
            "blender": self.extra.get("blender"),
            "cmd": self.extra.get("cmd"),
        })
        self.note("listening on %s:%d, endpoint %s" % (host, port, self.endpoint_path))
        return self.info

    def serve(self, should_stop=None):
        """Handle requests one at a time until ``should_stop`` (or the editor) says so."""
        self.note("ready: %s" % json.dumps(self.health_payload(), ensure_ascii=False))
        try:
            while True:
                if self.editor.stop_requested:
                    break
                if should_stop is not None and should_stop():
                    break
                self._httpd.handle_request()
        except KeyboardInterrupt:
            self.note("interrupted")
        finally:
            self.close()

    def close(self):
        try:
            self._httpd.server_close()
        finally:
            clear_endpoint(self.endpoint_path, only_pid=os.getpid())
            self.note("stopped")

    def url(self):
        host, port = self._httpd.server_address[:2]
        return "http://%s:%d" % (host, port)


def fail(message, code=1):
    print("levelctl: %s" % message, file=sys.stderr)
    return code
