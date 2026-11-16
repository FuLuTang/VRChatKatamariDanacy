#!/usr/bin/env python3
"""The persistent render daemon behind `shot.py` and `GET /api/render`.

It owns three things and nothing else:

* a private editor server (`server.py --no-lock`) on its own loopback port, so the
  browser loads the *real* page with the *real* level data;
* one headless Chromium (software WebGL) on a throwaway profile;
* an HTTP control port (``/healthz``, ``/status``, ``/render``, ``/stop``) with the
  editor's token.

A render request names a level and views; the daemon plans the cameras with
`shot.py` (Python), asks the page to execute them (`web/js/shot.js`), writes the PNGs
atomically, and answers with the metadata an agent needs.  Keeping the browser alive
is the whole point: the first shot pays for Chromium + 44 GLBs, later ones are a few
hundred milliseconds.
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import math
import os
import re
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.leveleditor import shot as shot_mod          # noqa: E402
from Tools.leveleditor import shot_cdp                 # noqa: E402
from Tools.leveleditor import shot_png                 # noqa: E402

SERVICE = shot_mod.SERVICE
PROTOCOL = shot_mod.PROTOCOL


# --------------------------------------------------------------------------- #
# small utilities
# --------------------------------------------------------------------------- #
def free_port(host="127.0.0.1"):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind((host, 0))
        return sock.getsockname()[1]


def atomic_write(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(".%s.tmp-%d" % (path.name, os.getpid()))
    with open(temporary, "wb") as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, path)


def stamp():
    return time.strftime("%Y%m%dT%H%M%S", time.gmtime())


def slug(text):
    cleaned = re.sub(r"[^A-Za-z0-9_.-]+", "_", str(text or "")).strip("._")
    return cleaned or "view"


def http_post_json(url, payload, token, timeout=30.0):
    request = urllib.request.Request(
        url, data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json", "Accept": "application/json",
                 "X-Editor-Token": token},
        method="POST")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def tail(path, limit=1600):
    try:
        return Path(path).read_text(encoding="utf-8", errors="replace")[-limit:].strip()
    except OSError:
        return ""


# --------------------------------------------------------------------------- #
# the daemon
# --------------------------------------------------------------------------- #
class RenderDaemon:
    def __init__(self, state_dir, token, levels_dir=None, chrome=None,
                 idle_timeout=900.0, quiet=False):
        self.state_dir = Path(state_dir)
        self.token = token
        self.levels_dir = Path(levels_dir) if levels_dir else None
        self.chrome_binary = chrome
        self.idle_timeout = float(idle_timeout)
        self.quiet = quiet
        self.mu = threading.Lock()                 # one render at a time
        self.started = time.time()
        self.last_activity = time.time()
        self.last_render = None
        self.render_count = 0
        self.server = None                         # subprocess: editor server
        self.server_url = None
        self.server_level = None
        self.server_lines = collections.deque(maxlen=120)
        self.server_log_path = self.state_dir / "shot" / "editor-server.log"
        self.chrome = None
        self.chrome_lock = threading.Lock()
        self.page_url = None
        self.page_ready = None
        self.page_load_seconds = None
        self.first_render_done = False
        self.last_level = None
        self.stop_event = threading.Event()
        self.httpd = None

    # -- editor server -------------------------------------------------- #
    def _server_alive(self):
        return self.server is not None and self.server.poll() is None

    def _start_editor_server(self, level):
        self._stop_editor_server()
        port = free_port()
        self.server_log_path.parent.mkdir(parents=True, exist_ok=True)
        handle = open(self.server_log_path, "ab")
        command = [
            sys.executable, str(Path(__file__).resolve().with_name("server.py")),
            "--level", level, "--host", "127.0.0.1", "--port", str(port),
            "--state-dir", str(self.state_dir), "--token", self.token,
            "--no-lock", "--quiet",
        ]
        if self.levels_dir:
            command += ["--levels-dir", str(self.levels_dir)]
        process = subprocess.Popen(command, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                   text=True, bufsize=1, close_fds=True,
                                   start_new_session=True)
        self.server = process
        self.server_lines.clear()

        def drain():
            for line in process.stdout:
                self.server_lines.append(line.rstrip("\n"))
                handle.write((line if line.endswith("\n") else line + "\n").encode("utf-8"))
                handle.flush()
            handle.close()

        threading.Thread(target=drain, name="shot-server-log", daemon=True).start()
        deadline = time.time() + 120.0
        url = None
        while time.time() < deadline:
            for line in list(self.server_lines):
                match = re.search(r"open \(this machine\): (http://\S+)", line)
                if match:
                    url = match.group(1)
                    break
            if url or process.poll() is not None:
                break
            time.sleep(0.05)
        if url is None:
            self._stop_editor_server()
            raise shot_mod.ShotError(
                "the private editor server for %r did not start: %s"
                % (level, tail(self.server_log_path) or "no log"),
                "editor_server_failed", status=503)
        parsed = urllib.parse.urlparse(url)
        self.server_url = "http://127.0.0.1:%d" % parsed.port
        self.server_level = level
        self.page_url = "%s/?token=%s&shot=1" % (
            self.server_url, urllib.parse.quote(self.token))
        self.page_ready = None
        return self.server_url

    def _stop_editor_server(self):
        if self.server is not None:
            try:
                self.server.terminate()
                self.server.wait(timeout=8.0)
            except (OSError, subprocess.TimeoutExpired):
                try:
                    self.server.kill()
                except OSError:
                    pass
        self.server = None
        self.server_url = None
        self.server_level = None
        self.page_url = None
        self.page_ready = None

    def ensure_level(self, level):
        if self._server_alive() and self.server_level == level:
            return
        self._start_editor_server(level)
        if self.chrome is not None and self.chrome.cdp is not None:
            self._load_page()

    def open_level_in_server(self, level):
        """Switch the private server to another level (never touches the owner's)."""
        if not self._server_alive():
            self._start_editor_server(level)
            return
        if self.server_level == level:
            return
        answer = http_post_json(self.server_url + "/api/open", {"level": level},
                                self.token, timeout=120.0)
        if not answer.get("ok"):
            raise shot_mod.ShotError(
                "the private editor server refused to open %r: %s"
                % (level, answer.get("error") or "unknown"),
                "level_open_failed", answer.get("details"), status=503)
        self.server_level = level
        self.page_ready = None
        if self.chrome is not None and self.chrome.cdp is not None:
            self._load_page()

    # -- Chromium ------------------------------------------------------- #
    def start_chrome(self, width=1280, height=720):
        with self.chrome_lock:
            if self.chrome is not None and self.chrome.cdp is not None:
                return self.chrome
            if self.chrome is not None:
                # A previous start failed; do not reuse a half-started Chromium.
                try:
                    self.chrome.stop()
                except Exception:                            # noqa: BLE001 - cleanup
                    pass
                self.chrome = None
            profile = self.state_dir / "shot" / ("chrome-profile-%d" % os.getpid())
            log_path = self.state_dir / "shot" / "chrome.log"
            chrome = shot_cdp.Chrome(
                binary=self.chrome_binary, profile_dir=profile,
                width=max(1280, int(width)), height=max(720, int(height)),
                log_path=log_path)
            chrome.start(timeout=25.0)
            self.chrome = chrome
            return self.chrome

    def _load_page(self):
        if self.chrome is None:
            return
        if not self.page_url:
            return
        self.page_ready = None
        page_started = time.time()
        self.chrome.navigate(self.page_url, timeout=60.0)
        self.page_ready = shot_cdp.wait_for_page_ready(self.chrome, timeout=180.0)
        self.page_load_seconds = round(time.time() - page_started, 3)
        # The page strips ?token= from the address bar; reloading would lose it, so a
        # later navigation always uses the same page_url (token still embedded).
        if not self.quiet:
            print("shot daemon: page ready for %s" % (self.server_level or "?"), flush=True)

    def ensure_page(self):
        self.start_chrome()
        if self.chrome is None or self.chrome.cdp is None:
            raise shot_mod.ShotError("Chromium has no page session", "browser_failed",
                                     status=503)
        if self.page_ready is None:
            self._load_page()

    def stop_chrome(self):
        with self.chrome_lock:
            if self.chrome is not None:
                self.chrome.stop()
                self.chrome = None

    # -- render --------------------------------------------------------- #
    def render(self, params):
        started = time.time()
        level = params.get("level")
        if not level:
            raise shot_mod.ShotError("level is required", "bad_argument")
        requested_levels = params.get("levelsDir")
        if requested_levels:
            requested = Path(requested_levels).expanduser().resolve()
            if self.levels_dir is None or self.levels_dir.resolve() != requested:
                raise shot_mod.ShotError(
                    "this daemon was started with levels-dir %s; run --stop (or a separate "
                    "--port) to use %s" % (self.levels_dir, requested),
                    "daemon_levels_mismatch")
        path, doc = shot_mod.load_level(level, params.get("levelsDir"))
        meta = shot_mod.level_meta(level, path, doc)
        views = params.get("views") or ["sheet"]
        frames = int(params.get("frames") or 1)
        width = int(params.get("w") or shot_mod.DEFAULT_W)
        height = int(params.get("h") or shot_mod.DEFAULT_H)
        out_info = shot_mod.output_target(level, params.get("out"),
                                          state_dir=self.state_dir)
        out_dir = Path(out_info["out"]).expanduser()
        out_file = params.get("outFile") or out_info.get("outFile")
        view_count = sum(len(shot_mod.SHEET_ORDER) if name == "sheet" else 1
                         for name in views)
        tile_w, tile_h, final_w, final_h = shot_mod.tile_size(
            width, height, max(1, view_count),
            int(params.get("maxPx") or shot_mod.DEFAULT_MAX_PX))
        aspect = tile_w / float(tile_h)
        plans = shot_mod.view_specs(params, doc, aspect)
        layout = shot_mod.layout_for(len(plans))
        layers = params.get("layers") or shot_mod.parse_layers(
            None, meta["ballDiameterStart"])

        self.open_level_in_server(level)
        self.ensure_page()

        spec = {
            "level": level,
            "views": [{
                "name": plan["name"], "kind": plan["kind"],
                "position": [round(value, 6) for value in plan["position"]],
                "target": [round(value, 6) for value in plan["target"]],
                "fov": round(float(plan["fov"]), 5),
                "box": [round(value, 5) for value in plan["box"]] if plan.get("box") else None,
                "mark": plan.get("mark"),
            } for plan in plans],
            "layout": layout,
            "tile": {"w": tile_w, "h": tile_h},
            "frames": frames,
            "interval": float(params.get("interval") or 0.0),
            "diff": bool(params.get("diff")),
            "layers": layers,
            "labels": int(layers.get("labels") or 0),
            "time": params.get("time"),
            "watermark": bool(params.get("watermark")),
            "shadows": bool(params.get("shadows")),
        }
        page_started = time.time()
        try:
            result = self.chrome.cdp.evaluate(
                "window.__editorShot.render(%s)" % json.dumps(spec, ensure_ascii=False),
                await_promise=True, timeout=float(params.get("timeout")
                                                 or shot_mod.DEFAULT_TIMEOUT))
        except shot_cdp.CDPError as error:
            console = self.chrome.cdp.errors() if self.chrome and self.chrome.cdp else []
            raise shot_mod.ShotError(
                "the page failed to render: %s" % error, "render_failed",
                list(error.details) + console, status=500)
        if not isinstance(result, dict) or not result.get("ok"):
            reason = (result or {}).get("error") or "no result"
            raise shot_mod.ShotError("the page failed to render: %s" % reason,
                                     "render_failed", status=500)
        page_seconds = round(time.time() - page_started, 3)

        console_errors = list(result.get("errors") or [])
        if self.chrome and self.chrome.cdp:
            console_errors += [row for row in self.chrome.cdp.errors()
                               if row not in console_errors]

        images = result.get("images") or []
        if not images:
            raise shot_mod.ShotError("the page returned no images", "render_failed",
                                     status=500)
        stamp_text = stamp()
        base_name = "%s_%s" % (slug(level), slug(plans[0]["name"]) if len(plans) == 1
                               else "sheet")
        written = []
        used_names = set()
        for image in images:
            data_url = image.get("dataUrl") or ""
            if not data_url.startswith("data:image/png;base64,"):
                raise shot_mod.ShotError("the page returned a non-PNG image",
                                         "render_failed", status=500)
            try:
                import base64
                data = base64.b64decode(data_url.split(",", 1)[1])
            except (ValueError, IndexError) as error:
                raise shot_mod.ShotError("bad PNG payload: %s" % error,
                                         "render_failed", status=500)
            size = shot_png.png_size(data)
            if size is None:
                raise shot_mod.ShotError("the page returned malformed PNG bytes",
                                         "render_failed", status=500)
            text = {
                "Software": "vrc-katamari-leveleditor shot.py",
                "Level": level,
                "View": str(image.get("kind") or "view"),
                "Camera": json.dumps(plans[0].get("name") if len(plans) == 1 else "sheet"),
            }
            if params.get("watermark"):
                text["Unlicensed"] = "UNLICENSED REFERENCE — local evidence only"
            data = shot_png.add_text(data, text)
            if out_file:
                if len(images) != 1:
                    raise shot_mod.ShotError(
                        "--out may name a file only when exactly one image is produced",
                        "bad_argument")
                target = (out_dir / out_file).expanduser().resolve()
            else:
                suffix = ""
                if image.get("kind") == "diff":
                    suffix = "_diff"
                elif frames > 1:
                    suffix = "_f%02d" % int(image.get("frame") or 1)
                name = "%s_%s%s.png" % (base_name, stamp_text, suffix)
                counter = 2
                while name in used_names:
                    name = "%s_%s_%d%s.png" % (base_name, stamp_text, counter, suffix)
                    counter += 1
                used_names.add(name)
                target = Path(out_dir) / name
            atomic_write(target, data)
            written.append({
                "path": str(target),
                "kind": image.get("kind"),
                "frame": image.get("frame"),
                "width": size[0], "height": size[1],
                "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
            })

        bookmark = None
        name = params.get("saveBookmark")
        if name:
            if meta["ref"]:
                bookmark = {"saved": False,
                            "reason": "UNLICENSED reference level is read-only"}
            else:
                plan = plans[0]
                location = shot_mod.three_to_world(plan["position"])
                target = shot_mod.three_to_world(plan["target"])
                lens = 12.0 / math.tan(math.radians(float(plan["fov"]) / 2.0))
                answer = http_post_json(
                    self.server_url + "/api/op",
                    {"op": "camera", "args": {
                        "id": name, "type": "perspective",
                        "location": location, "target": target,
                        "lens": round(lens, 3)}},
                    self.token, timeout=30.0)
                if answer.get("ok"):
                    saved = answer.get("saved") or {}
                    bookmark = {"saved": True, "id": name,
                                "location": location, "target": target,
                                "lens": round(lens, 3),
                                "export": saved.get("export") if isinstance(saved, dict) else None,
                                "sha256": saved.get("sha256") if isinstance(saved, dict) else None}
                else:
                    bookmark = {"saved": False,
                                "reason": answer.get("error") or "camera op refused"}

        world_views = shot_mod.world_view_rows(plans, result.get("views"))

        first = (level != self.last_level) or self.render_count == 0
        self.first_render_done = True
        self.last_level = level
        self.render_count += 1
        self.last_render = {
            "at": time.strftime("%Y-%m-%dT%H:%M:%S"), "level": level,
            "seconds": round(time.time() - started, 3), "images": len(written),
        }
        timing = dict(result.get("timings") or {})
        timing.update({"pageMs": int(page_seconds * 1000),
                       "daemonMs": int((time.time() - started) * 1000)})
        payload = {
            "ok": True,
            "service": SERVICE,
            "protocol": PROTOCOL,
            "level": level,
            "ref": bool(meta["ref"]),
            "unlicensed": bool(meta["ref"]),
            "placement": meta["placement"],
            "levelHash": result.get("levelHash") or meta["sha256"],
            "propCount": (result.get("propCount") if result.get("propCount") is not None
                          else meta["propCount"]),
            "defCount": result.get("defCount") or meta["defCount"],
            "images": written,
            "camera": (world_views[0] if len(world_views) == 1 else
                       {"kind": "sheet", "count": len(world_views),
                        "views": [row["name"] for row in world_views]}),
            "views": world_views,
            "sheet": result.get("sheet"),
            "tile": {"w": tile_w, "h": tile_h},
            "imageSize": {"w": final_w, "h": final_h},
            "layers": result.get("layers"),
            "playback": result.get("playback"),
            "shadows": bool(params.get("shadows")),
            "labels": result.get("labels"),
            "visibleBox": result.get("visibleBox"),
            "sceneBox": result.get("sceneBox"),
            "consoleErrors": console_errors,
            "timings": timing,
            "warm": not first,
            "page": {"loadSeconds": self.page_load_seconds,
                     "ready": result.get("ready")},
            "frames": frames,
            "interval": float(params.get("interval") or 0.0),
            "bookmark": bookmark,
            "stateDir": str(self.state_dir),
            "shotSeconds": round(time.time() - started, 3),
        }
        return payload

    # -- lifecycle ------------------------------------------------------ #
    def touch(self):
        self.last_activity = time.time()

    def idle_seconds(self):
        return round(time.time() - self.last_activity, 1)

    def shutdown(self):
        self.stop_event.set()
        if self.httpd is not None:
            threading.Thread(target=self.httpd.shutdown, daemon=True).start()

    def close(self):
        self.stop_chrome()
        self._stop_editor_server()
        try:
            shot_mod.state_file(self.state_dir).unlink()
        except OSError:
            pass


# --------------------------------------------------------------------------- #
# HTTP control interface
# --------------------------------------------------------------------------- #
class Handler(BaseHTTPRequestHandler):
    server_version = "%s/%d" % (SERVICE, PROTOCOL)
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        if getattr(self.server, "quiet", False):
            return
        print("[shot-http] %s %s" % (self.command, self.path.split("?")[0]), flush=True)

    def _json(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _token_ok(self):
        provided = self.headers.get("X-Editor-Token")
        if not provided:
            query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            provided = (query.get("token") or [None])[0]
        expected = self.server.daemon.token
        if not provided or not expected:
            return False
        import secrets
        try:
            return secrets.compare_digest(provided, expected)
        except TypeError:
            return False

    def _body(self):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length <= 0 or length > 128 * 1024 * 1024:
            return None
        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return None
        return payload if isinstance(payload, dict) else None

    def do_GET(self):                                        # noqa: N802 - stdlib
        path = urllib.parse.urlparse(self.path).path
        daemon = self.server.daemon
        try:
            if path == "/healthz":
                self._json(200, {"ok": True, "service": SERVICE, "protocol": PROTOCOL,
                                 "pid": os.getpid(), "uptimeSeconds":
                                 round(time.time() - daemon.started, 1),
                                 "level": daemon.server_level,
                                 "chrome": daemon.chrome is not None,
                                 "renderCount": daemon.render_count})
                return
            if not self._token_ok():
                self._json(403, {"ok": False, "error": "bad or missing token"})
                return
            if path == "/status":
                state = shot_mod.read_state(daemon.state_dir) or {}
                self._json(200, {
                    "ok": True, "service": SERVICE, "protocol": PROTOCOL,
                    "pid": os.getpid(), "port": state.get("port"),
                    "uptimeSeconds": round(time.time() - daemon.started, 1),
                    "idleSeconds": daemon.idle_seconds(),
                    "idleTimeoutSeconds": daemon.idle_timeout,
                    "stateDir": str(daemon.state_dir),
                    "stateFile": str(shot_mod.state_file(daemon.state_dir)),
                    "level": daemon.server_level,
                    "chrome": bool(daemon.chrome),
                    "chromePid": getattr(daemon.chrome, "process", None)
                    and daemon.chrome.process.pid,
                    "pageReady": bool(daemon.page_ready),
                    "pageLoadSeconds": daemon.page_load_seconds,
                    "renderCount": daemon.render_count,
                    "lastRender": daemon.last_render,
                    "editorServer": daemon.server_url,
                })
                return
            self._json(404, {"ok": False, "error": "no such endpoint: %s" % path})
        except Exception as error:                            # noqa: BLE001 - transport
            self._json(500, {"ok": False, "error": "%s: %s" % (type(error).__name__, error)})

    def do_POST(self):                                       # noqa: N802 - stdlib
        path = urllib.parse.urlparse(self.path).path
        daemon = self.server.daemon
        try:
            payload = self._body() or {}
            if path == "/render" and not self._token_ok():
                payload_token = payload.get("token")
                if not isinstance(payload_token, str) or payload_token != daemon.token:
                    self._json(403, {"ok": False, "error": "bad or missing token"})
                    return
            elif path in ("/stop", "/render", "/level") and not self._token_ok():
                self._json(403, {"ok": False, "error": "bad or missing token"})
                return
            daemon.touch()
            if path == "/stop":
                self._json(200, {"ok": True, "stopping": True, "pid": os.getpid()})
                daemon.shutdown()
                return
            if path == "/level":
                level = payload.get("level")
                if not isinstance(level, str) or not level:
                    self._json(400, {"ok": False, "error": "level is required"})
                    return
                daemon.open_level_in_server(level)
                self._json(200, {"ok": True, "level": level})
                return
            if path == "/render":
                try:
                    with daemon.mu:
                        result = daemon.render(payload)
                except shot_mod.ShotError as refusal:
                    daemon.touch()
                    self._json(refusal.status or 400, {
                        "ok": False, "error": refusal.message, "code": refusal.code,
                        "details": refusal.details})
                    return
                except Exception as error:                    # noqa: BLE001 - transport
                    import traceback
                    daemon.touch()
                    self._json(500, {
                        "ok": False, "error": "%s: %s" % (type(error).__name__, error),
                        "code": "render_failed",
                        "details": [traceback.format_exc(limit=8)]})
                    return
                self._json(200, result)
                return
            self._json(404, {"ok": False, "error": "no such endpoint: %s" % path})
        except BrokenPipeError:
            pass
        except Exception as error:                            # noqa: BLE001 - transport
            try:
                self._json(500, {"ok": False, "error": "%s: %s"
                                 % (type(error).__name__, error)})
            except OSError:
                pass


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def write_state(directory, payload):
    path = shot_mod.state_file(directory)
    atomic_write(path, (json.dumps(payload, indent=2, ensure_ascii=False) + "\n").encode())
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass


def build_parser():
    parser = argparse.ArgumentParser(prog="shot_daemon.py",
                                     description="Persistent headless render daemon")
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--token", required=True)
    parser.add_argument("--port", type=int, default=shot_mod.DEFAULT_PORT,
                        help="control port (0 = pick one)")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--idle-timeout", type=float, default=shot_mod.DEFAULT_IDLE)
    parser.add_argument("--levels-dir", default=None)
    parser.add_argument("--chrome", default=None)
    parser.add_argument("--quiet", action="store_true")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    directory = Path(args.state_dir).expanduser()
    directory.mkdir(parents=True, exist_ok=True)
    lock_path = directory / "shot" / "daemon.lock"
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    lock = open(lock_path, "a+")
    try:
        import fcntl
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except (ImportError, OSError):
        print("shot daemon: another daemon already owns %s" % lock_path, file=sys.stderr)
        return 3

    daemon = RenderDaemon(directory, args.token, levels_dir=args.levels_dir,
                          chrome=args.chrome, idle_timeout=args.idle_timeout,
                          quiet=args.quiet)
    port = args.port
    attempts = 0
    httpd = None
    while attempts < 20:
        try:
            httpd = ThreadingHTTPServer((args.host, port), Handler)
            break
        except OSError:
            if args.port == 0:
                raise
            port = args.port + attempts + 1
            attempts += 1
    if httpd is None:
        print("shot daemon: no free control port near %d" % args.port, file=sys.stderr)
        return 1
    httpd.daemon_threads = True
    httpd.allow_reuse_address = True
    httpd.daemon = daemon
    httpd.quiet = args.quiet
    daemon.httpd = httpd

    def stop(_signum=None, _frame=None):
        daemon.shutdown()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    def monitor():
        while not daemon.stop_event.wait(5.0):
            if daemon.idle_timeout <= 0:
                continue
            if daemon.idle_seconds() >= daemon.idle_timeout:
                if not daemon.quiet:
                    print("shot daemon: idle for %.0fs, stopping" % daemon.idle_seconds(),
                          flush=True)
                daemon.shutdown()
                return

    threading.Thread(target=monitor, name="shot-idle", daemon=True).start()

    def warm_chrome():
        # Chromium takes a second or two on software GL; start it while the CLI is
        # still waiting for /healthz, so the first shot does not pay for it serially.
        try:
            daemon.start_chrome()
        except Exception as error:                            # noqa: BLE001 - optional
            if not daemon.quiet:
                print("shot daemon: Chromium warm start failed: %s" % error, flush=True)

    threading.Thread(target=warm_chrome, name="shot-chrome", daemon=True).start()

    write_state(directory, {
        "service": SERVICE, "protocol": PROTOCOL, "pid": os.getpid(),
        "port": httpd.server_address[1], "host": args.host,
        "started": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "startedEpoch": daemon.started,
        "idleTimeoutSeconds": daemon.idle_timeout,
        "stateDir": str(directory),
        "tokenFile": str(directory / "token"),
        "python": sys.executable,
        "chrome": str(Path(args.chrome).expanduser()) if args.chrome else None,
    })
    if not daemon.quiet:
        print("shot daemon: listening on http://%s:%d (state %s)"
              % (args.host, httpd.server_address[1], directory), flush=True)
    try:
        httpd.serve_forever(poll_interval=0.2)
    finally:
        daemon.close()
        httpd.server_close()
        try:
            lock.close()
        except OSError:
            pass
        if not daemon.quiet:
            print("shot daemon: stopped", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
