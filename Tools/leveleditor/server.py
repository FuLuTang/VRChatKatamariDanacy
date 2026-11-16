#!/usr/bin/env python3
"""The web level editor: one HTTP server for a three.js client and the level core.

    python3 Tools/leveleditor/server.py --level story_01_bench_size

What this process is
--------------------
A **thin transport and state wrapper** around ``Tools/levelbuild``.  Every edit goes
through ``editor_core.Editor``: validation, the ``level.expected`` gate, the canonical
bytes and the atomic export are the *same* code the Blender resident editor and the
CLI use.  This process adds only what a browser needs:

* static files (``web/``) and the prop GLBs (``Docs/Art/.../exports/*.glb``),
* one JSON op per request (the same ``{"op": ..., "args": ...}`` payload),
* undo/redo as a bounded snapshot history (the Editor has undo; the web client also
  needs redo and a one-click diff summary),
* Server-Sent Events so a second tab notices a save,
* a token for writes (the page is reachable over Tailscale; reads stay open),
* a one-writer lock per level file, plus a semantic "the file changed on disk"
  check before every edit (the Blender resident editor can write the same file).

There is no Blender here and no rendering server: the browser renders.  ``render``
is refused with that sentence instead of silently doing nothing.

See ``Docs/Design/Tools/LEVEL_EDITOR_WEB.md`` for the design and
``Tools/leveleditor/README.md`` for how to start it.
"""

import argparse
import hashlib
import json
import math
import mimetypes
import os
import secrets
import signal
import socket
import subprocess
import sys
import threading
import time
import traceback
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import editor_core  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild import ref_models  # noqa: E402
from Tools.levelbuild import ref_stage  # noqa: E402
from Tools.leveleditor import v2_document  # noqa: E402

SERVICE = "vrc-katamari-leveleditor"
PROTOCOL = 1
TOKEN_HEADER = "X-Editor-Token"
MAX_BODY = 4 * 1024 * 1024
DEFAULT_PORT = 8777
WEB_DIR = Path(__file__).resolve().parent / "web"
MAX_HISTORY = 60

#: Ops the web transport handles itself instead of passing to ``Editor.handle``.
LOCAL_OPS = ("undo", "redo", "open", "reload", "shutdown", "render")


def _now():
    return time.strftime("%Y-%m-%dT%H:%M:%S")


def _deepcopy(value):
    return json.loads(json.dumps(value, ensure_ascii=False))


def _round_seq(values, digits=4):
    return [round(float(value), digits) for value in values]


def _sha256(data):
    return hashlib.sha256(data).hexdigest()


# --------------------------------------------------------------------------- #
# the scene adapter: there is no Blender, only counts for the status line
# --------------------------------------------------------------------------- #
class WebScene:
    """The minimal scene ``editor_core.Editor`` needs, with no 3D engine behind it.

    ``sync`` is where the Blender adapter would add/update/remove objects.  Here it
    only records per-kind counts so ``status`` and op responses can say what changed;
    the browser does the real diff after refetching ``/api/level``.
    """

    def __init__(self):
        self.props = 0
        self.structures = 0
        self.cameras = 0
        self.syncs = 0

    def reset(self):
        self.props = 0
        self.structures = 0
        self.cameras = 0

    def sync(self, doc, previous=None):
        keys = (("props", "itemId"), ("structures", "id"), ("cameras", "id"))
        counts = {kind: {"added": 0, "updated": 0, "removed": 0}
                  for kind, _key in keys}
        for kind, key in keys:
            after = {entry.get(key): entry for entry in doc.get(kind) or []}
            before = {} if previous is None else {
                entry.get(key): entry for entry in previous.get(kind) or []}
            counts[kind]["added"] = len(set(after) - set(before))
            counts[kind]["removed"] = len(set(before) - set(after))
            counts[kind]["updated"] = sum(
                1 for item_id in set(after) & set(before)
                if json.dumps(after[item_id], sort_keys=True)
                != json.dumps(before[item_id], sort_keys=True))
        self.props = len(doc.get("props") or [])
        self.structures = len(doc.get("structures") or [])
        self.cameras = len(doc.get("cameras") or [])
        self.syncs += 1
        return counts

    def render(self, spec, doc):
        raise RuntimeError("the web editor renders in the browser; use the screenshot "
                           "button or a headless browser, not the `render` op")

    def status(self):
        return {"backend": "web (no Blender)", "objects": {
            "props": self.props, "structures": self.structures,
            "cameras": self.cameras}, "syncs": self.syncs}


# --------------------------------------------------------------------------- #
# regions.json schema (v1; a sidecar so placement.json is untouched)
# --------------------------------------------------------------------------- #
def validate_regions(payload):
    if not isinstance(payload, dict):
        raise editor_core.OpError("regions.json must be a JSON object")
    if payload.get("schemaVersion") != 1:
        raise editor_core.OpError("regions.json schemaVersion must be 1")
    regions = payload.get("regions")
    if not isinstance(regions, list):
        raise editor_core.OpError("regions.json needs a regions list")
    seen = set()
    for index, region in enumerate(regions):
        if not isinstance(region, dict):
            raise editor_core.OpError("region %d must be an object" % index)
        name = region.get("name")
        if not isinstance(name, str) or not name.strip():
            raise editor_core.OpError("region %d needs a non-empty name" % index)
        if name in seen:
            raise editor_core.OpError("duplicate region name %r" % name)
        seen.add(name)
        for key in ("min", "max"):
            values = region.get(key)
            if not isinstance(values, list) or len(values) != 3:
                raise editor_core.OpError("region %r %s must be [x,y,z]" % (name, key))
            for value in values:
                if isinstance(value, bool) or not isinstance(value, (int, float)):
                    raise editor_core.OpError("region %r %s must be numbers" % (name, key))
                if not math.isfinite(float(value)):
                    raise editor_core.OpError("region %r %s must be finite" % (name, key))
        for axis in range(3):
            if float(region["min"][axis]) >= float(region["max"][axis]):
                raise editor_core.OpError("region %r has min >= max on axis %d"
                                          % (name, axis))
        if "visible" in region and not isinstance(region["visible"], bool):
            raise editor_core.OpError("region %r visible must be a boolean" % name)
        if "color" in region and not isinstance(region["color"], str):
            raise editor_core.OpError("region %r color must be a string" % name)
    return payload


# --------------------------------------------------------------------------- #
# one-writer lock for a level file
# --------------------------------------------------------------------------- #
class LevelLock:
    """An advisory ``flock`` per placement.json, so two editors cannot both write.

    The **flock is the authority**: a lock file whose process died holds no flock, so
    the next editor can take it over without manual cleanup.  The file body is a small
    JSON diagnostic (pid, port, level, placement) that another editor can report when
    the flock is genuinely held; it is never trusted for exclusion.

    The filename carries a scheme prefix (``v2-``); the old sha1/16 name is also
    flocked as a compatibility lock, so an editor still running the pre-rotation code
    cannot acquire the same level during a rolling restart.  The v2 body is the
    diagnostic authority; the old body stays a bare pid for old readers.

    It protects against a second *web* editor (the Blender resident editor uses its
    own single-process rule).  Locks live in the state directory, never in the repo.
    """

    SCHEME = "v2"

    def __init__(self, level_path, state_dir, level=None, port=None):
        self.level_path = Path(level_path).resolve()
        digest = hashlib.sha256(str(self.level_path).encode("utf-8")).hexdigest()
        self.path = (Path(state_dir) / "locks"
                     / ("%s-%s.lock" % (self.SCHEME, digest[:20])))
        #: The pre-rotation name.  New code holds it too while old servers may still
        #: be running, so an old-code writer cannot start on a level this editor owns.
        legacy = hashlib.sha1(str(self.level_path).encode("utf-8")).hexdigest()
        self.legacy_path = Path(state_dir) / "locks" / (legacy[:16] + ".lock")
        self.level = level
        self.port = int(port) if port else None
        self._fd = None
        self._legacy_fd = None
        #: The previous body found when this acquire succeeded (stale holder info).
        self.taken_over = None

    # -- body ----------------------------------------------------------- #
    def _body(self):
        return {
            "pid": os.getpid(),
            "port": self.port,
            "level": self.level,
            "placement": str(self.level_path),
            "started": time.strftime("%Y-%m-%dT%H:%M:%S"),
        }

    @staticmethod
    def _read_body(descriptor):
        """Parse the holder body from an open descriptor; never raise."""
        try:
            os.lseek(descriptor, 0, os.SEEK_SET)
            raw = os.read(descriptor, 4096).decode("utf-8", "replace").strip()
        except OSError:
            return {}
        if not raw:
            return {}
        try:
            body = json.loads(raw)
            if isinstance(body, dict):
                return body
            if isinstance(body, int) and not isinstance(body, bool):
                return {"pid": body}
            return {"raw": raw[:200]}
        except (json.JSONDecodeError, ValueError):
            # A file written by the old code was just "<pid>\n"; keep that useful.
            first = raw.split()[0] if raw.split() else ""
            try:
                return {"pid": int(first)}
            except ValueError:
                return {"raw": raw[:200]}

    @staticmethod
    def describe(body):
        if not isinstance(body, dict) or not body:
            return ""
        parts = []
        if body.get("pid") is not None:
            parts.append("pid %s" % body["pid"])
        if body.get("port") is not None:
            parts.append("port %s" % body["port"])
        if body.get("level"):
            parts.append("level %s" % body["level"])
        if not parts and body.get("raw"):
            parts.append(str(body["raw"])[:120])
        return (", ".join(parts))

    def _write_body(self):
        if self._fd is None:
            return
        payload = (json.dumps(self._body(), ensure_ascii=False, sort_keys=True)
                   + "\n").encode("utf-8")
        os.ftruncate(self._fd, 0)
        os.lseek(self._fd, 0, os.SEEK_SET)
        os.write(self._fd, payload)
        try:
            os.fsync(self._fd)
        except OSError:
            pass

    def announce(self, port=None, level=None):
        """Record this editor's HTTP port/level in the lock body (used in refusals)."""
        if port:
            self.port = int(port)
        if level is not None:
            self.level = level
        self._write_body()

    @staticmethod
    def _pid_alive(pid):
        try:
            pid = int(pid)
        except (TypeError, ValueError):
            return False
        if pid <= 0:
            return False
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        except OSError:
            return False
        return True

    # -- lifecycle ------------------------------------------------------ #
    def acquire(self):
        if self._fd is not None or self._legacy_fd is not None:
            raise RuntimeError("this level editor already holds %s" % self.path.name)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        descriptor = os.open(str(self.path), os.O_RDWR | os.O_CREAT, 0o600)
        try:
            import fcntl
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except ImportError:
            # No advisory locking on this platform; the body is diagnostic only.
            pass
        except OSError:
            holder = self._read_body(descriptor)
            os.close(descriptor)
            detail = self.describe(holder)
            raise RuntimeError(
                "another level editor already holds %s%s; one level, one writer"
                % (self.path.name, (" (%s)" % detail) if detail else ""))
        # Compatibility lock: hold the old name too, so an editor still running the
        # pre-rotation code cannot acquire the same level while this one owns it.
        legacy = os.open(str(self.legacy_path), os.O_RDWR | os.O_CREAT, 0o600)
        try:
            import fcntl
            fcntl.flock(legacy, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except ImportError:
            pass
        except OSError:
            holder = self._read_body(legacy)
            os.close(legacy)
            try:
                import fcntl
                fcntl.flock(descriptor, fcntl.LOCK_UN)
            except (ImportError, OSError):
                pass
            os.close(descriptor)
            detail = self.describe(holder)
            raise RuntimeError(
                "another level editor (older lock format) already holds %s%s; "
                "one level, one writer"
                % (self.legacy_path.name, (" (%s)" % detail) if detail else ""))
        previous = self._read_body(descriptor)
        self._fd = descriptor
        self._legacy_fd = legacy
        self._write_body()
        # Old code reads the legacy body as a bare pid; keep it readable.
        try:
            os.ftruncate(legacy, 0)
            os.lseek(legacy, 0, os.SEEK_SET)
            os.write(legacy, ("%d\n" % os.getpid()).encode("ascii"))
            os.fsync(legacy)
        except OSError:
            pass
        if previous and previous.get("pid") not in (None, os.getpid()):
            self.taken_over = previous
            state = ("pid is gone (stale lock)"
                     if not self._pid_alive(previous.get("pid"))
                     else "previous process still runs but no longer holds the flock")
            detail = self.describe(previous)
            print("leveleditor: took over %s from %s; %s"
                  % (self.path.name, detail or "an unknown holder", state),
                  file=sys.stderr, flush=True)
        return self

    def release(self):
        for attribute in ("_fd", "_legacy_fd"):
            descriptor = getattr(self, attribute, None)
            if descriptor is None:
                continue
            try:
                import fcntl
                fcntl.flock(descriptor, fcntl.LOCK_UN)
            except (ImportError, OSError):
                pass
            try:
                os.close(descriptor)
            finally:
                setattr(self, attribute, None)


# --------------------------------------------------------------------------- #
# the editable level, with history and discovery
# --------------------------------------------------------------------------- #
class WebEditor:
    """One open level + the browser-facing state and history."""

    def __init__(self, level, levels_dir=None, state_dir=None, lock=True,
                 manifests=None, ref_models_dir=None, placement=None,
                 ref_stage_dir=None):
        self.levels_dir = Path(levels_dir) if levels_dir else core.LEVELS_DIR
        self.state_dir = Path(state_dir) if state_dir else default_state_dir()
        self.scene = WebScene()
        self.repo_manifests = manifests if manifests is not None else core.load_manifests()
        self.editor = editor_core.Editor(
            self.scene, manifests=self.repo_manifests,
            auto_export=True, undo_limit=50, levels_dir=self.levels_dir)
        self.ref_models_dir = ref_models_dir
        self.ref_stage_dir = ref_stage_dir
        self.placement_name = placement
        #: True only for a level id starting with ``_ref_``.  Reference levels show
        #: unlicensed local meshes and are read-only (see ``ref_models``).
        self.ref_mode = False
        self.ref_models_root = None
        self.ref_table = {}
        self.ref_resolution = {}
        self.ref_fbx_only = {}
        #: The original stage (terrain, collision, true instance transforms) of a
        #: reference level, read from the byte-level extraction outside the repo.
        self.ref_stage = None
        self._ref_stage_glb = None
        #: True for a ``katamari.placement/2`` document.  v2 is currently a read-only
        #: viewer path: it loads through ``v2_document`` (the one sidecar parser and the
        #: one structural checker) and never enters the v1 editor/validator.
        self.v2_mode = False
        self.v2_document = None
        self.active_placement = None
        self.lock_enabled = bool(lock)
        self.lock = None
        #: The bound HTTP port, announced after ``make_http_server`` so a lock refusal
        #: from another process can say which editor holds the level.
        self.port = None
        self.history = []
        self.redo = []
        self.last_saved = None
        self.version = 0
        self._condition = threading.Condition()
        self.mu = threading.RLock()
        self.started = time.time()
        self.open_level(level)

    # -- discovery ------------------------------------------------------ #
    def list_levels(self):
        levels = {}
        for name in ("placement.v2.json", "placement.json"):
            for path in sorted(self.levels_dir.rglob(name)):
                level_id = path.parent.relative_to(self.levels_dir).as_posix()
                if not core.is_valid_level_id(level_id) or level_id in levels:
                    continue
                try:
                    stat = path.stat()
                except OSError:
                    continue
                levels[level_id] = {
                    "id": level_id, "bytes": stat.st_size,
                    "file": name,
                    "schema": v2_document.detect_schema(path),
                    "modified": time.strftime("%Y-%m-%dT%H:%M:%S",
                                              time.localtime(stat.st_mtime)),
                }
        return [levels[key] for key in sorted(levels)]

    def resolve(self, level, placement=None):
        """The file this level opens.

        A reference level prefers the current ``placement.v2.json``.  The old
        ``placement.model.json`` is only the fallback; loading it puts the page in
        legacy mode with an explicit banner and a one-click switch (the v1 file's
        numbers are not the v2 data's numbers).  ``--placement`` / an explicit open
        wins for both kinds.
        """
        folder = core.level_folder(level, self.levels_dir)
        name = placement or self.placement_name
        if name:
            return folder / name
        if ref_models.is_ref_level(level):
            v2_file = folder / "placement.v2.json"
            if v2_file.is_file():
                return v2_file
            model_file = folder / "placement.model.json"
            if model_file.is_file():
                return model_file
        return folder / "placement.json"

    # -- lifecycle ------------------------------------------------------ #
    def announce(self, port):
        """Publish the bound port for diagnostics and for the lock body."""
        self.port = int(port) if port else None
        if self.lock is not None:
            self.lock.announce(port=self.port, level=self.editor.level)

    @staticmethod
    def _same_file(left, right):
        if left is None or right is None:
            return False
        try:
            return Path(left).resolve() == Path(right).resolve()
        except OSError:
            return False

    def open_level(self, level, placement=None):
        """Open a level without ever self-deadlocking on the one-writer lock.

        * The already-open file is a **reload in place**: no second flock, no history
          jump to another level, same session/lock.  (Reopening the same level used to
          open a second fd for the same lock file, which flock refuses even inside one
          process.)
        * A different level acquires its lock *before* the old one is released, so a
          second process never sees a window with neither lock.  If the open fails the
          old document, lock and history stay exactly as they were.
        * ``placement`` is the explicit file name (``placement.v2.json``,
          ``placement.model.json``); it switches a v1 session onto v2 without a restart.
        """
        path = self.resolve(level, placement=placement)
        if not path.is_file():
            raise editor_core.OpError("cannot read %s" % core.display_path(path))
        if self._same_file(self.editor.path, path):
            # Same file: this is "open the level I already have" = reload from disk.
            return self.reload_level()

        new_lock = None
        if self.lock_enabled:
            new_lock = LevelLock(path, self.state_dir, level=level, port=self.port)
            new_lock.acquire()
        snapshot = {
            "ref": (self.ref_mode, self.ref_models_root, self.ref_table,
                    self.ref_resolution, self.ref_fbx_only, self.ref_stage,
                    self._ref_stage_glb, self.editor.base_manifests),
            "v2": (self.v2_mode, self.v2_document, self.active_placement),
            "editor": {
                "doc": self.editor.doc, "path": self.editor.path,
                "level": self.editor.level, "manifests": self.editor.manifests,
                "base_manifests": self.editor.base_manifests,
                "undo_stack": list(self.editor.undo_stack),
                "known_errors": set(self.editor.known_errors),
                "unexported": self.editor.unexported,
            },
            "scene": (self.scene.props, self.scene.structures, self.scene.cameras,
                      self.scene.syncs),
        }
        is_ref = ref_models.is_ref_level(level)
        is_v2 = v2_document.detect_schema(path) == v2_document.SCHEMA_V2
        try:
            if is_ref:
                self.ref_models_root = ref_models.normalized_models_dir(self.ref_models_dir)
                self.ref_table = ref_models.load_model_table(path.parent)
                level_manifests = core.load_manifests(path.parent / "manifests")
                self.editor.base_manifests = level_manifests or self.repo_manifests
            else:
                self.ref_models_root = None
                self.ref_table = {}
                self.editor.base_manifests = self.repo_manifests
            self.editor.manifests = self.editor.base_manifests
            if is_v2:
                loaded = v2_document.load(path)
                self.v2_mode = True
                self.v2_document = loaded
                self.editor.doc = loaded.view_doc
                self.editor.path = path
                self.editor.level = ((loaded.view_doc.get("level") or {}).get("id")
                                     or level)
                self.editor.manifests = core.manifests_with_models(
                    loaded.view_doc, self.editor.base_manifests)
                self.editor.undo_stack = []
                self.editor.known_errors = set()
                self.editor.unexported = False
                self.editor.last_export = None
                self.scene.reset()
                counts = self.scene.sync(loaded.view_doc, None)
                opened = {
                    "level": self.editor.level,
                    "path": core.display_path(path),
                    "counts": self.editor._counts(loaded.view_doc),
                    "gate": loaded.check_payload(),
                    "scene": counts,
                    "v2": True,
                    "canonical": True,
                }
            else:
                self.v2_mode = False
                self.v2_document = None
                opened = self.editor.open(level=level, path=path)
            self.ref_mode = is_ref
            self.active_placement = placement
            self._refresh_ref_models()
            if is_v2:
                self.ref_stage = {
                    "available": False,
                    "reason": "v2 文档自带 terrain/structures/activityArea，"
                              "旧的 stage.glb 叠加层已停用",
                    "dir": None, "counts": {}, "frame": None, "transforms": None,
                    "collision": None, "zones": [],
                }
                self._ref_stage_glb = None
            else:
                self._refresh_ref_stage()
        except Exception:
            (self.ref_mode, self.ref_models_root, self.ref_table,
             self.ref_resolution, self.ref_fbx_only, self.ref_stage,
             self._ref_stage_glb, self.editor.base_manifests) = snapshot["ref"]
            (self.v2_mode, self.v2_document, self.active_placement) = snapshot["v2"]
            for key, value in snapshot["editor"].items():
                setattr(self.editor, key, value)
            (self.scene.props, self.scene.structures, self.scene.cameras,
             self.scene.syncs) = snapshot["scene"]
            if new_lock is not None:
                new_lock.release()
            raise
        if self.lock is not None:
            self.lock.release()
        self.lock = new_lock
        self.history = []
        self.redo = []
        self.last_saved = None
        self._publish()
        return opened

    def _refresh_ref_models(self):
        """Resolve the local viewer mesh for every def of a reference level.

        Read-only mode means this stays valid for the whole session; ``reload``
        calls it again after re-reading the document.
        """
        self.ref_resolution = {}
        self.ref_fbx_only = {}
        if not self.ref_mode:
            return
        doc = self.editor.doc or {}
        catalog = core.def_catalog(doc)
        for entry in doc.get("defs") or []:
            if not isinstance(entry, dict):
                continue
            def_id = entry.get("id")
            if not isinstance(def_id, str) or not def_id:
                continue
            resolved = ref_models.resolve_model(
                def_id, core.def_model(catalog, def_id), self.ref_table,
                self.ref_models_root)
            if resolved is None:
                continue
            if resolved["kind"] == "glb":
                self.ref_resolution[def_id] = resolved
            else:
                self.ref_fbx_only[def_id] = resolved

    def _refresh_ref_stage(self):
        """Attach the original stage of a reference level (terrain, collision, transforms).

        The extraction is optional: a reference level still opens when it is missing or
        written in another space, and ``ref_state()["stage"]["reason"]`` says why the
        terrain is not drawn.  Nothing here touches the placement document.
        """
        self.ref_stage = None
        self._ref_stage_glb = None
        if not self.ref_mode:
            return
        try:
            directory, reason = ref_stage.resolve_stage_dir(self.ref_stage_dir)
        except ref_stage.RefStageError as refusal:
            self.ref_stage = {"available": False, "reason": str(refusal), "dir": None,
                              "counts": {}, "frame": None, "transforms": None,
                              "collision": None, "zones": []}
            return
        if directory is None:
            self.ref_stage = {"available": False, "reason": reason, "dir": None,
                              "counts": {}, "frame": None, "transforms": None,
                              "collision": None, "zones": []}
            return
        try:
            self.ref_stage = ref_stage.prepare(directory, Path(self.editor.path).parent,
                                               self.editor.doc or {})
        except (ref_stage.RefStageError, OSError, ValueError, KeyError) as refusal:
            self.ref_stage = {"available": False, "reason": str(refusal),
                              "dir": ref_models.display_path(directory),
                              "counts": {}, "frame": None, "transforms": None,
                              "collision": None, "zones": []}

    def ref_stage_glb(self):
        """The converted terrain GLB for this level (cached until the file changes)."""
        if not self.ref_mode or not (self.ref_stage or {}).get("available"):
            raise ref_stage.RefStageError(
                "the original stage is only served while a _ref_* level with a stage "
                "extraction is open")
        if self._ref_stage_glb is None:
            directory, _reason = ref_stage.resolve_stage_dir(self.ref_stage_dir)
            if directory is None:
                raise ref_stage.RefStageError("the stage extraction is gone")
            self._ref_stage_glb = ref_stage.stage_glb_bytes(directory)
        return self._ref_stage_glb

    def reload_level(self):
        if self.v2_mode:
            loaded = v2_document.load(self.editor.path)
            self.v2_document = loaded
            self.editor.doc = loaded.view_doc
            self.editor.level = ((loaded.view_doc.get("level") or {}).get("id")
                                 or self.editor.level)
            self.editor.manifests = core.manifests_with_models(
                loaded.view_doc, self.editor.base_manifests)
            self.editor.undo_stack = []
            self.editor.known_errors = set()
            self.scene.reset()
            counts = self.scene.sync(loaded.view_doc, None)
            self._refresh_ref_models()
            result = {
                "level": self.editor.level,
                "path": core.display_path(self.editor.path),
                "counts": self.editor._counts(loaded.view_doc),
                "gate": loaded.check_payload(),
                "scene": counts,
                "v2": True,
                "undo": "cleared (read-only v2 document)",
            }
        else:
            result = self.editor.op_reload({})
            self._refresh_ref_models()
            self._refresh_ref_stage()
        if self.lock is not None:
            self.lock.announce(port=self.port, level=self.editor.level)
        self.history = []
        self.redo = []
        self.last_saved = None
        self._publish()
        return result

    # -- change notification -------------------------------------------- #
    def _publish(self):
        with self._condition:
            self.version += 1
            self._condition.notify_all()

    def wait_for_change(self, since, timeout=15.0):
        deadline = time.time() + timeout
        with self._condition:
            while self.version == since:
                remaining = deadline - time.time()
                if remaining <= 0:
                    break
                self._condition.wait(remaining)
            return self.version

    # -- conflict detection --------------------------------------------- #
    def disk_conflict(self):
        """A human-readable reason the file no longer matches the edited document.

        Semantic, not byte, comparison: a hand-formatted (non-canonical) file is a
        warning, not a conflict; a different *document* is.  A missing file counts.
        """
        if self.editor.doc is None or self.editor.path is None:
            return None
        path = Path(self.editor.path)
        if self.v2_mode:
            try:
                current = path.read_bytes()
            except OSError:
                return "%s is gone; reload from disk" % core.display_path(path)
            if hashlib.sha256(current).hexdigest() != self.v2_document.sha256:
                return ("%s changed on disk; it is a read-only v2 document, press "
                        "Reload to take the disk version." % core.display_path(path))
            return None
        if not path.is_file():
            return "%s is gone; reload from disk or reopen the level" % core.display_path(path)
        try:
            on_disk = core.load_doc(path)
            same = (core.canonical_doc(on_disk, self.editor.manifests)
                    == core.canonical_doc(self.editor.doc, self.editor.manifests))
        except (OSError, ValueError, json.JSONDecodeError) as error:
            return "%s cannot be read (%s); fix it and press Reload" % (
                core.display_path(path), error)
        if same:
            return None
        return ("%s changed on disk (another editor wrote it). Press Reload to take "
                "the disk version; this edit was not applied." % core.display_path(path))

    # -- diff and save bookkeeping -------------------------------------- #
    @staticmethod
    def diff(before, after):
        result = {}
        for kind, key in (("props", "itemId"), ("structures", "id"),
                          ("defs", "id"), ("cameras", "id")):
            before_map = {entry.get(key): entry for entry in before.get(kind) or []}
            after_map = {entry.get(key): entry for entry in after.get(kind) or []}
            result[kind] = {
                "added": sorted(set(after_map) - set(before_map)),
                "removed": sorted(set(before_map) - set(after_map)),
                "updated": sorted(
                    item_id for item_id in set(after_map) & set(before_map)
                    if json.dumps(after_map[item_id], sort_keys=True)
                    != json.dumps(before_map[item_id], sort_keys=True)),
            }
        result["level"] = before.get("level") != after.get("level")
        result["counts"] = {
            kind: {"added": len(result[kind]["added"]),
                   "updated": len(result[kind]["updated"]),
                   "removed": len(result[kind]["removed"])}
            for kind in ("props", "structures", "defs", "cameras")}
        # Both deltas are shown during the migration: growthMm3 is the live currency,
        # valueUnits is the deprecated one (0 once files are regenerated).
        result["growthMm3"] = core.growth_mm3_total(after) - core.growth_mm3_total(before)
        result["valueUnits"] = core.value_units_total(after) - core.value_units_total(before)
        return result

    def _record(self, label, before, after, export=None):
        if json.dumps(before, sort_keys=True) == json.dumps(after, sort_keys=True):
            return None
        entry = {"label": label, "before": _deepcopy(before), "after": _deepcopy(after),
                 "diff": self.diff(before, after), "at": _now()}
        self.history.append(entry)
        if len(self.history) > MAX_HISTORY:
            self.history.pop(0)
        self.redo = []
        if export and export.get("wrote"):
            self.last_saved = {
                "at": export.get("at") or _now(),
                "bytes": export.get("bytes"),
                "sha256": export.get("sha256"),
                "label": label,
                "diff": entry["diff"],
            }
        self._publish()
        return entry

    def _export_from_response(self, response):
        result = response.get("result")
        if not isinstance(result, dict):
            return None
        if isinstance(result.get("commit"), dict):
            return result["commit"].get("export")
        return result.get("export")

    # -- the transport entry point -------------------------------------- #
    def handle(self, payload):
        started = time.perf_counter()
        if not isinstance(payload, dict):
            return {"ok": False, "error": "the request body must be a JSON object"}
        op = payload.get("op") if "op" in payload else ("batch" if "ops" in payload else None)
        effective_op = self.editor.aliases.get(op, op) if isinstance(op, str) else op
        with self.mu:
            try:
                if self.ref_mode and ("ops" in payload
                                      or effective_op in editor_core.MUTATING_OPS
                                      or effective_op in ("export", "undo", "redo")):
                    raise editor_core.OpError(
                        "UNLICENSED REFERENCE level is read-only: its positions/sizes "
                        "come from a community data mine and its meshes are ripped "
                        "assets. View/select/region statistics only; nothing can be "
                        "edited, exported or baked into the managed props.",
                        ["reopen a normal level (not `_ref_*`) to edit"])
                if self.v2_mode and (effective_op in editor_core.MUTATING_OPS
                                     or effective_op in ("export", "undo", "redo")
                                     or "ops" in payload):
                    raise editor_core.OpError(
                        "this is a read-only katamari.placement/2 document: the v2 "
                        "validator and the v2 edit path are not delivered yet. "
                        "View/query/region statistics only; nothing is written.",
                        ["the v1 editor path is unchanged for v1 levels; open a v1 "
                         "placement.json to edit"])
                if op in ("undo", "redo"):
                    response = self._undo(op, payload.get("args") or {})
                elif op == "open":
                    args = payload.get("args") or {}
                    level = args.get("level")
                    if not isinstance(level, str) or not level:
                        raise editor_core.OpError("open needs level=<id>")
                    placement = args.get("placement")
                    if placement is not None and (not isinstance(placement, str)
                                                  or not placement):
                        raise editor_core.OpError("placement must be a file name")
                    opened = self.open_level(level, placement=placement)
                    response = {"ok": True, "op": "open", "result": opened,
                                "error": "", "details": []}
                elif op == "reload":
                    reloaded = self.reload_level()
                    response = {"ok": True, "op": "reload", "result": reloaded,
                                "error": "", "details": []}
                elif op == "shutdown":
                    response = {"ok": False, "op": "shutdown",
                                "error": "the web editor runs as a managed foreground job; "
                                         "stop it with SIGTERM / Ctrl-C, or kill the job",
                                "details": []}
                elif op == "render":
                    response = {"ok": False, "op": "render",
                                "error": "the web editor renders in the browser; there is no "
                                         "Blender here. Use the Screenshot button.",
                                "details": []}
                elif effective_op == "check" and self.v2_mode:
                    check = self.v2_document.check_payload()
                    response = {"ok": True, "op": "check", "result": check,
                                "error": "", "details": [], "gate": check}
                elif effective_op == "status" and self.v2_mode:
                    response = {"ok": True, "op": "status",
                                "result": dict(self.state_summary(),
                                               scene=self.scene.status(),
                                               v2=True, readOnly=True),
                                "error": "", "details": []}
                elif effective_op in ("list", "get") and self.v2_mode:
                    result = getattr(self.editor, "op_" + effective_op)(
                        payload.get("args") or {})
                    response = {"ok": True, "op": effective_op, "result": result,
                                "error": "", "details": []}
                elif op in editor_core.MUTATING_OPS or "ops" in payload:
                    conflict = self.disk_conflict()
                    if conflict:
                        raise editor_core.OpError(conflict, [
                            "write refused before anything changed; the file is the truth"])
                    before = _deepcopy(self.editor.doc)
                    response = self.editor.handle(payload)
                    if response.get("ok"):
                        export = self._export_from_response(response)
                        result = response.get("result") or {}
                        label = (result.get("label")
                                 or (result.get("commit") or {}).get("label") or op)
                        entry = self._record(label, before, self.editor.doc, export)
                        response["saved"] = self._saved_payload(export, entry)
                    response["conflict"] = None
                elif op == "export":
                    conflict = self.disk_conflict()
                    if conflict:
                        raise editor_core.OpError(conflict, [
                            "export refused before anything changed; the file is the truth"])
                    response = self.editor.handle(payload)
                elif self.v2_mode:
                    raise editor_core.OpError(
                        "v2 read-only supports open/reload/check/status/list/get; "
                        "%r is not available on this document" % effective_op)
                else:
                    response = self.editor.handle(payload)
            except editor_core.OpError as refusal:
                response = {"ok": False, "op": op, "result": None,
                            "error": str(refusal), "details": refusal.details}
            except Exception as unexpected:                 # noqa: BLE001 - transport
                response = {"ok": False, "op": op, "result": None,
                            "error": "%s: %s" % (type(unexpected).__name__, unexpected),
                            "details": [traceback.format_exc(limit=8)]}
            response.setdefault("details", [])
            response["state"] = self.state_summary()
            response["history"] = {"undo": len(self.history), "redo": len(self.redo),
                                   "labels": [item["label"] for item in self.history],
                                   "redoLabels": [item["label"] for item in self.redo]}
            response["seconds"] = round(time.perf_counter() - started, 3)
            if self.editor.doc is not None:
                if self.v2_mode:
                    response["gate"] = (response.get("gate")
                                        or self.v2_document.check_payload())
                else:
                    response["gate"] = response.get("gate") or editor_core.summarize(
                        self.editor._validate(self.editor.doc))
            return response

    def _saved_payload(self, export, entry):
        if entry is None and not export:
            return {"wrote": False, "diff": None}
        saved = {"wrote": bool((export or {}).get("wrote")),
                 "bytes": (export or {}).get("bytes"),
                 "sha256": (export or {}).get("sha256"),
                 "at": (export or {}).get("at") or (entry or {}).get("at"),
                 "diff": (entry or {}).get("diff")}
        return saved

    # -- undo / redo ----------------------------------------------------- #
    def _undo(self, direction, args):
        steps = args.get("steps", 1)
        try:
            steps = int(steps)
        except (TypeError, ValueError):
            raise editor_core.OpError("steps must be a whole number")
        if steps < 1:
            raise editor_core.OpError("steps must be at least 1")
        conflict = self.disk_conflict()
        if conflict:
            raise editor_core.OpError(conflict, [
                "%s refused before anything changed; the file is the truth" % direction])
        source = self.history if direction == "undo" else self.redo
        if not source:
            raise editor_core.OpError("nothing to %s" % direction)
        if steps > len(source):
            raise editor_core.OpError("only %d step(s) can be %s"
                                      % (len(source), "undone" if direction == "undo" else "redone"))
        taken = [source.pop() for _ in range(steps)]
        # ``taken`` is newest-first for undo, oldest-first for redo; the snapshot to
        # restore is always the one on the far side of the whole step group.
        target = taken[-1]["before"] if direction == "undo" else taken[-1]["after"]
        before = _deepcopy(self.editor.doc)
        restored = self._restore(target)
        diff = self.diff(before, self.editor.doc)
        for item in taken:
            self.redo.append(item) if direction == "undo" else self.history.append(item)
        label = "%s %d: %s" % (direction, steps,
                               ", ".join(item["label"] for item in taken))
        self.last_saved = {
            "at": restored.get("export", {}).get("at") or _now(),
            "bytes": restored.get("export", {}).get("bytes"),
            "sha256": restored.get("export", {}).get("sha256"),
            "label": label, "diff": diff,
        }
        self._publish()
        return {"ok": True, "op": direction,
                "result": {"undone" if direction == "undo" else "redone":
                           [item["label"] for item in taken],
                           "steps": steps, "diff": diff,
                           "export": restored.get("export"),
                           "gate": restored.get("gate")},
                "error": "", "details": [],
                "saved": self.last_saved}

    def _restore(self, doc):
        """Write a snapshot through the editor's atomic writer, then re-read it.

        ``Editor._write`` is the one atomic export path (tmp file + ``os.replace``);
        reusing it here means undo/redo cannot produce different bytes than a normal
        save.  ``open`` then rebuilds the document state (known errors, level id) the
        same way a fresh open would.
        """
        report = core.validate(doc, manifests=self.editor.manifests)
        written = self.editor._write(self.editor.path, doc)
        opened = self.editor.open(None, self.editor.path)
        return {"export": {"wrote": True, "path": written["path"],
                           "bytes": written["bytes"], "sha256": written["sha256"],
                           "at": _now(), "gate": editor_core.summarize(report)},
                "open": opened, "gate": opened.get("gate")}

    # -- state ----------------------------------------------------------- #
    def state_summary(self):
        doc = self.editor.doc
        if doc is None:
            return {"open": False, "level": None}
        path = Path(self.editor.path)
        if self.v2_mode:
            loaded = self.v2_document
            check = loaded.check_payload()
            document = loaded.summary["document"]
            counts = {
                "level": self.editor.level,
                "props": loaded.summary["counts"]["props"],
                "structures": loaded.summary["counts"]["solids"],
                "cameras": len(doc.get("cameras") or []),
                "presets": len(doc.get("renderPresets") or []),
                "defs": loaded.summary["counts"]["defs"],
                "valueUnits": 0,
                "growthMm3": loaded.summary["counts"]["growthMm3"],
            }
            file_state = {
                "path": core.display_path(path),
                "bytes": document["bytes"],
                "sha256": document["sha256"],
                "modified": time.strftime("%Y-%m-%dT%H:%M:%S",
                                          time.localtime(path.stat().st_mtime)),
                "sidecar": document.get("sidecarPath"),
                "sidecarSha256": document.get("sidecarSha256"),
                "schema": v2_document.SCHEMA_V2,
            }
            return {
                "open": True,
                "level": self.editor.level,
                "counts": counts,
                "gate": check,
                "file": file_state,
                "dirty": False,
                "history": {"undo": 0, "redo": 0},
                "lastSaved": None,
                "ref": bool(self.ref_mode),
                "v2": True,
                "legacy": None,
                "version": self.version,
            }
        report = self.editor._validate(doc)
        try:
            data = path.read_bytes()
            file_state = {"path": core.display_path(path), "bytes": len(data),
                          "sha256": _sha256(data),
                          "modified": time.strftime("%Y-%m-%dT%H:%M:%S",
                                                    time.localtime(path.stat().st_mtime))}
        except OSError:
            file_state = {"path": core.display_path(path), "bytes": None, "sha256": None}
        return {
            "open": True,
            "level": self.editor.level,
            "counts": self.editor._counts(doc),
            "gate": editor_core.summarize(report),
            "file": file_state,
            "dirty": self.editor._dirty(),
            "history": {"undo": len(self.history), "redo": len(self.redo)},
            "lastSaved": self.last_saved,
            "ref": bool(self.ref_mode),
            "v2": False,
            "legacy": self.legacy_state(),
            "version": self.version,
        }

    def legacy_state(self):
        """A v1 file that has a v2 sibling: say so instead of showing v1 numbers."""
        if self.v2_mode or self.editor.path is None:
            return None
        path = Path(self.editor.path)
        if path.name == "placement.v2.json":
            return None
        v2_file = path.parent / "placement.v2.json"
        if not v2_file.is_file():
            return None
        return {
            "kind": "v1",
            "schema": v2_document.detect_schema(path),
            "replacedBy": v2_file.name,
            "canSwitch": True,
            "message": ("这是 v1 旧文档，已被 v2 取代。页面上显示的错误数来自 v1 校验器，"
                        "不是 v2 数据的当前校验结果。"),
        }

    def derived(self):
        doc = self.editor.doc
        catalog = core.def_catalog(doc)
        props = {}
        for prop in doc.get("props") or []:
            try:
                position = tuple(float(value) for value in prop["position"])
                size = tuple(float(value) for value in prop["aabb"])
                quat = tuple(float(value) for value in prop["rotationQuat"])
                low, high = core.world_aabb(position, size, quat)
            except (KeyError, TypeError, ValueError):
                continue
            if self.v2_mode:
                volume_k = prop.get("volumeK")
                d_req_mm = int(prop.get("absorbDiameterReqMm") or 0)
                props[prop.get("itemId")] = {
                    "dReq": round(d_req_mm / 1000.0, 3),
                    "dReqMm": d_req_mm,
                    "volumeK": volume_k,
                    "worldAabb": [_round_seq(low), _round_seq(high)],
                    "growthMm3": int(prop.get("growthMm3") or 0),
                    "volumePenalty": prop.get("volumePenalty"),
                    "valueUnits": int(prop.get("valueUnits") or 0),
                    "kind": prop.get("kind") or "",
                    "parent": prop.get("parent"),
                    "scale": prop.get("scale") or [1.0, 1.0, 1.0],
                    "behaviour": prop.get("behaviour"),
                    "absorbable": prop.get("absorbable"),
                }
                continue
            volume_k = core.effective_volume_k(prop, catalog)
            props[prop.get("itemId")] = {
                # dReq is the original's `Req Pickup Size`: whole millimetres, floored,
                # priced at V = volumeK * a * b * c.  dReqMm is the integer the comparison
                # actually uses, so a UI never re-rounds it.
                "dReq": round(core.absorb_diameter_req(size, volume_k), 3),
                "dReqMm": core.absorb_diameter_req_mm(size, volume_k),
                "volumeK": volume_k,
                "worldAabb": [_round_seq(low), _round_seq(high)],
                "growthMm3": core.growth_mm3(prop, catalog) or 0,
                "volumePenalty": core.effective_volume_penalty(prop, catalog),
                # Legacy (deprecated) display value; never used for growth.
                "valueUnits": core.effective_value_units(prop, catalog) or 0,
                "kind": core.effective_kind(prop, catalog,
                                            self.editor.manifests) or "",
            }
        structures = {}
        for structure in doc.get("structures") or []:
            try:
                low, high = core.structure_world_aabb(structure)
                normal, point = core.structure_top_plane(structure)
            except (KeyError, TypeError, ValueError):
                continue
            structures[structure.get("id")] = {
                "worldAabb": [_round_seq(low), _round_seq(high)],
                "topPlane": {"normal": _round_seq(normal, 6),
                             "point": _round_seq(point, 4)},
                "slopeDeg": round(core.structure_slope_deg(structure), 3),
            }
        return {"props": props, "structures": structures}

    def manifests(self):
        doc = self.editor.doc
        catalog = core.def_catalog(doc)
        out = {}
        for def_id, entry in self.editor.manifests.items():
            manifest = entry.get("manifest") or {}
            resolved = self.ref_resolution.get(def_id) if self.ref_mode else None
            out[def_id] = {
                "aabb": _round_seq(entry["aabb"]),
                "category": entry.get("category") or "",
                "name": manifest.get("name") or def_id,
                "nameZh": manifest.get("name_zh") or "",
                "materials": manifest.get("materials") or [],
                "colors": manifest.get("material_base_colors") or {},
                "roughness": {material.get("name"): material.get("roughness", 0.65)
                              for material in manifest.get("materials_declared") or []
                              if isinstance(material, dict)},
                "valueUnits": (catalog.get(def_id) or {}).get("valueUnits"),
                "volumeK": core.effective_volume_k({"def": def_id}, catalog),
                "volumePenalty": core.effective_volume_penalty({"def": def_id}, catalog),
                "growthMm3": core.item_growth_mm3(
                    entry["aabb"], core.effective_volume_k({"def": def_id}, catalog),
                    core.effective_volume_penalty({"def": def_id}, catalog)),
                "dReq": round(core.absorb_diameter_req(
                    entry["aabb"], core.effective_volume_k({"def": def_id}, catalog)), 3),
                "dReqMm": core.absorb_diameter_req_mm(
                    entry["aabb"], core.effective_volume_k({"def": def_id}, catalog)),
                "kind": (catalog.get(def_id) or {}).get("kind")
                        or entry.get("category") or "",
                # The viewer only: repo GLBs are centre-pivoted; the reference GLBs are
                # the ripped assets, centred bottom-up at load time (see view.js).
                "viewerPivot": "bottom" if resolved else "center",
                "refModel": bool(resolved),
                "refSource": (resolved or {}).get("source"),
            }
        return out

    # ------------------------------------------------------------------ #
    # regions.json (independent sidecar; not part of placement validation)
    # ------------------------------------------------------------------ #
    def regions_path(self):
        if self.editor.path is None:
            raise editor_core.OpError("no level is open")
        return Path(self.editor.path).parent / "regions.json"

    def load_regions(self):
        path = self.regions_path()
        if not path.is_file():
            return {"schemaVersion": 1, "regions": []}
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise editor_core.OpError("cannot read %s: %s"
                                      % (core.display_path(path), error))
        validate_regions(payload)
        return payload

    def save_regions(self, payload):
        validate_regions(payload)
        path = self.regions_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        text = json.dumps(payload, indent=2, ensure_ascii=False) + "\n"
        temporary = path.with_name("%s.tmp-%d" % (path.name, os.getpid()))
        temporary.write_text(text, encoding="utf-8", newline="\n")
        os.replace(temporary, path)
        return {"path": core.display_path(path), "bytes": len(text.encode("utf-8")),
                "regions": len(payload.get("regions") or []),
                "sha256": hashlib.sha256(text.encode("utf-8")).hexdigest()}

    # ------------------------------------------------------------------ #
    # region statistics (the one implementation; the UI only renders it)
    # ------------------------------------------------------------------ #
    @staticmethod
    def _region_contains(low, high, center, region, mode):
        if mode == "center":
            return all(region["min"][axis] <= center[axis] <= region["max"][axis]
                       for axis in range(3))
        return all(high[axis] >= region["min"][axis]
                   and low[axis] <= region["max"][axis] for axis in range(3))

    def stats(self, params):
        doc = self.editor.doc
        if doc is None:
            raise editor_core.OpError("no level is open")
        level = doc.get("level") or {}
        scope = str(params.get("scope") or "all")
        mode = str(params.get("mode") or "center")
        if mode not in ("center", "bbox"):
            raise editor_core.OpError("mode must be center or bbox")
        metric = str(params.get("metric") or "count")
        if metric not in ("count", "volume", "units"):
            raise editor_core.OpError("metric must be count, volume or units")
        try:
            bin_width = float(params.get("bin") or 0.05)
            diameter = float(params.get("diameter") if params.get("diameter") is not None
                             else level.get("ballDiameterStart", 0.3))
        except (TypeError, ValueError):
            raise editor_core.OpError("bin and diameter must be numbers")
        if not (0.005 <= bin_width <= 2.0):
            raise editor_core.OpError("bin must be between 0.005 and 2.0 m")
        max_diameter = float(level.get("ballDiameterMax") or 2.2)
        start_diameter = float(level.get("ballDiameterStart") or 0.3)
        diameter = max(0.01, min(diameter, max_diameter))

        region = None
        region_name = str(params.get("region") or "")
        ids = None
        if scope == "region":
            if not region_name:
                raise editor_core.OpError("scope=region needs region=<name>")
            payload = self.load_regions()
            region = next((entry for entry in payload.get("regions") or []
                           if entry.get("name") == region_name), None)
            if region is None:
                raise editor_core.OpError("region %r is not saved in regions.json"
                                          % region_name)
        elif scope == "box":
            raw = params.get("box")
            try:
                values = [float(value) for value in (raw or "").split(",")]
            except (TypeError, ValueError):
                values = []
            if len(values) != 6:
                raise editor_core.OpError("box must be minx,miny,minz,maxx,maxy,maxz")
            region = {"name": "(box)", "min": values[:3], "max": values[3:]}
        elif scope == "ids":
            raw = params.get("ids") or ""
            ids = {item for item in str(raw).split(",") if item}
        elif scope != "all":
            raise editor_core.OpError("scope must be all, region, box or ids")

        catalog = core.def_catalog(doc)
        included = []
        for prop in doc.get("props") or []:
            if not isinstance(prop, dict):
                continue
            item_id = prop.get("itemId")
            if ids is not None and item_id not in ids:
                continue
            try:
                position = tuple(float(value) for value in prop["position"])
                size = tuple(float(value) for value in prop["aabb"])
                quat = tuple(float(value) for value in prop["rotationQuat"])
                low, high = core.world_aabb(position, size, quat)
            except (KeyError, TypeError, ValueError):
                continue
            if region is not None:
                try:
                    region_box = {"min": [float(v) for v in region["min"]],
                                  "max": [float(v) for v in region["max"]]}
                except (KeyError, TypeError, ValueError):
                    raise editor_core.OpError("the region has no usable min/max")
                if not self._region_contains(low, high, position, region_box, mode):
                    continue
            volume = size[0] * size[1] * size[2]
            if self.v2_mode:
                d_req_mm = int(prop.get("absorbDiameterReqMm") or 0)
                volume_k = prop.get("volumeK")
                units = int(prop.get("growthMm3") or 0)
            else:
                volume_k = core.effective_volume_k(prop, catalog)
                d_req_mm = core.absorb_diameter_req_mm(size, volume_k)
                units = core.effective_value_units(prop, catalog) or 0
            included.append({
                "id": item_id,
                "def": prop.get("def"),
                # The original gate quantity: Req floored to whole millimetres, priced at
                # the prop's own k.  Every count below compares integers, never floats.
                "dReq": d_req_mm / 1000.0,
                "dReqMm": d_req_mm,
                "volumeK": volume_k,
                "volume": volume,
                "units": units,
                "low": low,
                "high": high,
            })

        total_volume = sum(entry["volume"] for entry in included)
        total_units = sum(entry["units"] for entry in included)

        ball_mm = core.floor_mm(diameter)
        max_mm = core.floor_mm(max_diameter)
        near_mm = core.floor_mm(diameter * 1.2)

        def tier_for(entry):
            if entry["dReqMm"] > max_mm:
                return "grey"
            if entry["dReqMm"] <= ball_mm:
                return "green"
            if entry["dReqMm"] <= near_mm:
                return "yellow"
            return "red"

        tiers = {name: {"count": 0, "volume": 0.0, "units": 0}
                 for name in ("green", "yellow", "red", "grey")}
        for entry in included:
            bucket = tiers[tier_for(entry)]
            bucket["count"] += 1
            bucket["volume"] += entry["volume"]
            bucket["units"] += entry["units"]
        for bucket in tiers.values():
            bucket["volume"] = round(bucket["volume"], 6)

        upper = max(max_diameter, max((entry["dReq"] for entry in included), default=0.0))
        bin_count = max(1, int((upper + bin_width - 1e-9) // bin_width))
        bin_edges_mm = [core.floor_mm(index * bin_width) for index in range(bin_count + 1)]
        bins = []
        for index in range(bin_count):
            low = round(index * bin_width, 6)
            high = round(low + bin_width, 6)
            low_mm = bin_edges_mm[index]
            high_mm = bin_edges_mm[index + 1]
            entries = [entry for entry in included
                       if entry["dReqMm"] >= low_mm
                       and (entry["dReqMm"] < high_mm or index == bin_count - 1)]
            volume = sum(entry["volume"] for entry in entries)
            units = sum(entry["units"] for entry in entries)
            bins.append({
                "low": low,
                "high": high,
                "count": len(entries),
                "volume": round(volume, 6),
                "units": units,
                "volumePct": round(volume / total_volume * 100.0, 3) if total_volume else 0.0,
                "countPct": round(len(entries) / len(included) * 100.0, 3) if included else 0.0,
                "ids": [entry["id"] for entry in entries],
                "items": [{"id": entry["id"], "def": entry["def"],
                           "dReq": round(entry["dReq"], 3),
                           "dReqMm": entry["dReqMm"],
                           "volumeK": entry["volumeK"],
                           "volume": round(entry["volume"], 6),
                           "units": entry["units"]} for entry in entries],
            })

        cumulative = []
        step = max(bin_width, 0.01)
        point = max(0.0, min(start_diameter, diameter))
        guard = 0
        while point <= diameter + 1e-9 and guard < 1000:
            point_mm = core.floor_mm(point)
            absorbable = [entry for entry in included if entry["dReqMm"] <= point_mm]
            cumulative.append({
                "diameter": round(point, 3),
                "count": len(absorbable),
                "volume": round(sum(entry["volume"] for entry in absorbable), 6),
                "units": sum(entry["units"] for entry in absorbable),
            })
            point += step
            guard += 1

        dreqs = sorted(entry["dReq"] for entry in included)
        dreqs_mm = sorted(entry["dReqMm"] for entry in included)
        if dreqs:
            middle = len(dreqs) // 2
            median = (dreqs[middle] if len(dreqs) % 2
                      else (dreqs[middle - 1] + dreqs[middle]) / 2.0)
        else:
            median = None

        return {
            "scope": scope,
            "region": region.get("name") if region else None,
            "regionBox": ({"min": region["min"], "max": region["max"]}
                          if region else None),
            "mode": mode,
            "metric": metric,
            "bin": bin_width,
            "diameter": round(diameter, 3),
            "ballDiameterStart": round(start_diameter, 3),
            "ballDiameterMax": round(max_diameter, 3),
            "totals": {"count": len(included), "volume": round(total_volume, 6),
                       "units": total_units},
            "tiers": tiers,
            "bins": bins,
            "cumulative": cumulative,
            "ballMm": ball_mm,
            "dReqMedian": round(median, 4) if median is not None else None,
            "dReqMedianMm": (dreqs_mm[len(dreqs_mm) // 2] if len(dreqs_mm) % 2
                             else (dreqs_mm[len(dreqs_mm) // 2 - 1]
                                   + dreqs_mm[len(dreqs_mm) // 2]) / 2.0)
                            if dreqs_mm else None,
            "absorbableCountRatio": round(tiers["green"]["count"] / len(included), 6)
                                    if included else 0.0,
            "absorbableVolumeRatio": round(tiers["green"]["volume"] / total_volume, 6)
                                     if total_volume else 0.0,
            "includedIds": [entry["id"] for entry in included],
        }

    def compare_regions(self, params):
        payload = self.load_regions()
        rows = []
        for entry in payload.get("regions") or []:
            stats = self.stats(dict(params, scope="region", region=entry["name"]))
            rows.append({
                "name": entry["name"],
                "visible": bool(entry.get("visible", True)),
                "count": stats["totals"]["count"],
                "volume": stats["totals"]["volume"],
                "units": stats["totals"]["units"],
                "absorbableRatio": stats["absorbableVolumeRatio"],
                "dReqMedian": stats["dReqMedian"],
                "tiers": stats["tiers"],
            })
        return {"regions": rows, "metric": params.get("metric") or "count",
                "diameter": float(params.get("diameter") or 0.3)}

    # -- reference-level facts (display + refusal) ----------------------- #
    def ref_state(self):
        """The reference-level banner facts, or ``None`` for a normal level."""
        if not self.ref_mode:
            return None
        doc = self.editor.doc or {}
        if self.v2_mode:
            links = list(self.v2_document.summary["parents"])
        else:
            links = ref_models.parent_links(doc)
        kinds = {}
        for resolved in self.ref_resolution.values():
            kinds[resolved["source"]] = kinds.get(resolved["source"], 0) + 1
        total = len(doc.get("defs") or [])
        missing = [entry.get("id") for entry in doc.get("defs") or []
                   if isinstance(entry, dict)
                   and entry.get("id") not in self.ref_resolution
                   and entry.get("id") not in self.ref_fbx_only]
        return {
            "unlicensed": True,
            "readOnly": True,
            "banner": ("UNLICENSED REFERENCE — positions/sizes from a community data "
                       "mine, meshes ripped from a commercial game. Not for "
                       "distribution, upload or the managed props. Read-only view."),
            "placement": Path(self.editor.path).name,
            "modelsDir": ref_models.display_path(self.ref_models_root),
            "modelCounts": {"total": total,
                            "resolved": len(self.ref_resolution),
                            "convertible": len(self.ref_fbx_only),
                            "missing": len(missing),
                            "bySource": kinds},
            "missingDefs": missing[:40],
            "convertibleDefs": sorted(self.ref_fbx_only)[:40],
            "parentLinkCount": len(links),
            "parentLinks": links,
            "stage": self.ref_stage_state(),
            "document": (self.v2_document.summary["document"] if self.v2_mode else {
                "path": core.display_path(self.editor.path),
                "name": Path(self.editor.path).name,
                "schema": v2_document.detect_schema(self.editor.path),
            }),
            "v2": bool(self.v2_mode),
        }

    def ref_stage_state(self):
        """The original-stage banner facts (terrain, collision, true transforms)."""
        stage = self.ref_stage or {"available": False, "reason": "not inspected"}
        counts = stage.get("counts") or {}
        frame = stage.get("frame") or {}
        return {
            "available": bool(stage.get("available")),
            "reason": stage.get("reason"),
            "dir": stage.get("dir"),
            "counts": counts,
            "frame": ({key: frame.get(key) for key in
                       ("scale", "matched", "exact", "medianError", "confident",
                        "hypothesis")} if frame else None),
            "glbBytes": counts.get("stageBytes"),
            "terrain": {"vertices": counts.get("meshVertices"),
                        "materials": counts.get("meshMaterials"),
                        "bytes": counts.get("stageBytes")},
            "collision": {"aabbs": counts.get("aabbs"),
                          "triangles": counts.get("triangles")},
            "instances": {"total": counts.get("instances"),
                          "matched": counts.get("matched"),
                          "exact": counts.get("exact"),
                          "anchors": counts.get("anchors"),
                          "rotated": counts.get("rotated"),
                          "scaled": counts.get("scaled"),
                          "withParent": counts.get("withParent"),
                          "parentsResolved": counts.get("parentsResolved"),
                          "withZone": counts.get("withZone")},
            "zones": len(stage.get("zones") or []),
        }

    def state(self):
        with self.mu:
            doc = self.editor.doc
            if self.v2_mode:
                check = self.v2_document.check_payload()
            else:
                check = self.editor.op_check({"report": True})
            payload = self.state_summary()
            payload.update({
                "service": SERVICE,
                "protocol": PROTOCOL,
                "pid": os.getpid(),
                "doc": doc,
                "v2": self.v2_state(),
                "derived": self.derived(),
                "manifests": self.manifests(),
                "check": check,
                "levels": self.list_levels(),
                "bounds": (doc.get("level") or {}).get("bounds"),
                "ref": self.ref_state(),
                "legacy": self.legacy_state(),
                "uptimeSeconds": round(time.time() - self.started, 1),
            })
            return payload

    def v2_state(self):
        """The v2 layers/summary that are small enough for every /api/level response."""
        if not self.v2_mode:
            return None
        loaded = self.v2_document
        return {
            "schema": loaded.summary["schema"],
            "document": loaded.summary["document"],
            "meta": loaded.summary["meta"],
            "counts": loaded.summary["counts"],
            "terrain": loaded.summary["terrain"],
            "activity": loaded.summary["activity"],
            "solids": loaded.summary["solids"],
            "parents": loaded.summary["parents"],
            "layers": v2_document.layer_map(loaded),
            "check": loaded.check_payload(),
        }

    def v2_payload(self, **flags):
        if not self.v2_mode:
            raise editor_core.OpError("no v2 document is open")
        return self.v2_document.view_payload(**flags)

    def v2_terrain_payload(self):
        if not self.v2_mode:
            raise editor_core.OpError("no v2 document is open")
        terrain = self.v2_document.terrain_payload()
        if terrain is None:
            raise editor_core.OpError(
                "v2 terrain sidecar unavailable: %s"
                % "; ".join(self.v2_document.errors) or "not loaded")
        return terrain

    # -- hints (read-only, display only) --------------------------------- #
    def hints(self, diameter):
        """The three-state prediction at a ball diameter, for the overlay only.

        The rule is design section 10.3 (``rise <= 0.25 * D`` = terrain, above it
        blocking; ``D_req <= D`` = food).  It is display-only: the validator's tier
        table remains the authority, and nothing here is written to the file.
        """
        doc = self.editor.doc
        diameter = max(0.01, float(diameter))
        step = core.STEP_RATIO * diameter
        if self.v2_mode:
            meta = self.v2_document.summary["meta"]
            props = []
            for prop in doc.get("props") or []:
                d_req = float(prop.get("absorbDiameterReqMm") or 0) / 1000.0
                props.append({
                    "id": prop.get("itemId"), "def": prop.get("def"),
                    "dReq": round(d_req, 3),
                    "dReqMm": int(prop.get("absorbDiameterReqMm") or 0),
                    "top": None, "rise": 0.0,
                    "kind": "food" if d_req <= diameter + 1e-9 else "blocking",
                })
            food = [entry for entry in props if entry["kind"] == "food"]
            blocking = [entry for entry in props if entry["kind"] == "blocking"]
            return {
                "unsupported": True,
                "reason": ("v2 物理提示需要 v2 校验器/地形支撑查询；在它交付前这里只按 "
                           "absorbDiameterReqMm 分出食物/挡路，地形与 rise 不判定。"),
                "diameter": round(diameter, 3), "stepLimit": round(step, 4),
                "floor": meta.get("groundY"),
                "props": {"food": food, "terrain": [], "blocking": blocking},
                "structures": {"terrain": [], "blocking": []},
                "counts": {"food": len(food), "terrain": 0,
                           "blocking": len(blocking),
                           "climbableStructures": 0, "blockingStructures": 0},
            }
        floor = core.ground_level(doc)
        structures_by_id = {structure.get("id"): structure
                            for structure in doc.get("structures") or []}
        props_by_id = {prop.get("itemId"): prop for prop in doc.get("props") or []}
        catalog = core.def_catalog(doc)
        props = []
        for prop in doc.get("props") or []:
            try:
                position = tuple(float(value) for value in prop["position"])
                size = tuple(float(value) for value in prop["aabb"])
                quat = tuple(float(value) for value in prop["rotationQuat"])
                low, high = core.world_aabb(position, size, quat)
            except (KeyError, TypeError, ValueError):
                continue
            d_req = core.absorb_diameter_req(size, core.effective_volume_k(prop, catalog))
            d_req_mm = core.absorb_diameter_req_mm(
                size, core.effective_volume_k(prop, catalog))
            entry = {"id": prop.get("itemId"), "def": prop.get("def"),
                     "dReq": round(d_req, 3), "dReqMm": d_req_mm,
                     "top": round(high[1], 3), "rise": 0.0, "kind": "food"}
            if d_req_mm <= core.floor_mm(diameter):
                props.append(entry)
                continue
            support = prop.get("support")
            height = floor
            if prop.get("stackedOn"):
                height = low[1]
            elif support in structures_by_id:
                try:
                    normal, point = core.structure_top_plane(structures_by_id[support])
                    height = core.plane_height_at(normal, point, position[0], position[2])
                    if height is None:
                        height = floor
                except (KeyError, TypeError, ValueError):
                    height = floor
            entry["rise"] = round(max(0.0, high[1] - height), 3)
            entry["kind"] = "terrain" if entry["rise"] <= step + 1e-9 else "blocking"
            props.append(entry)
        structures = []
        for structure in doc.get("structures") or []:
            if structure.get("role") not in ("box", "ramp"):
                continue
            try:
                top = core.structure_top_height(structure)
            except (KeyError, TypeError, ValueError):
                continue
            rise = max(0.0, top - floor)
            structures.append({
                "id": structure.get("id"), "role": structure.get("role"),
                "top": round(top, 3), "rise": round(rise, 3),
                "kind": "terrain" if rise <= step + 1e-9 else "blocking",
            })
        def bucket(items, kind):
            return [item for item in items if item["kind"] == kind]
        return {
            "diameter": round(diameter, 3), "stepLimit": round(step, 4),
            "floor": round(floor, 3),
            "props": {
                "food": bucket(props, "food"),
                "terrain": bucket(props, "terrain"),
                "blocking": bucket(props, "blocking"),
            },
            "structures": {"terrain": bucket(structures, "terrain"),
                           "blocking": bucket(structures, "blocking")},
            "counts": {
                "food": len(bucket(props, "food")),
                "terrain": len(bucket(props, "terrain")),
                "blocking": len(bucket(props, "blocking")),
                "climbableStructures": len(bucket(structures, "terrain")),
                "blockingStructures": len(bucket(structures, "blocking")),
            },
        }

    def close(self):
        if self.lock is not None:
            self.lock.release()
            self.lock = None


# --------------------------------------------------------------------------- #
# default state directory / token
# --------------------------------------------------------------------------- #
def default_state_dir():
    base = os.environ.get("XDG_STATE_HOME") or (Path.home() / ".local/state")
    return Path(base) / "vrc-katamari-leveleditor"


def load_or_create_token(state_dir, explicit=None):
    if explicit:
        return explicit
    path = Path(state_dir) / "token"
    if path.is_file():
        token = path.read_text(encoding="utf-8").strip()
        if token:
            return token
    token = secrets.token_urlsafe(24)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(str(path), os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(token + "\n")
    return token


def addresses():
    """Every usable non-loopback IPv4 address, for the URL banner."""
    found = []
    try:
        output = subprocess.run(["tailscale", "ip", "-4"], capture_output=True,
                                text=True, timeout=2).stdout
        for line in output.splitlines():
            line = line.strip()
            if line and line not in found:
                found.append(line)
    except (OSError, subprocess.SubprocessError):
        pass
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            address = info[4][0]
            if address not in found and not address.startswith("127."):
                found.append(address)
    except OSError:
        pass
    if not found:
        try:
            probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            probe.connect(("8.8.8.8", 80))
            found.append(probe.getsockname()[0])
            probe.close()
        except OSError:
            pass
    return found


# --------------------------------------------------------------------------- #
# HTTP
# --------------------------------------------------------------------------- #
class EditorHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


class Handler(BaseHTTPRequestHandler):
    server_version = "vrc-katamari-leveleditor/%d" % PROTOCOL
    protocol_version = "HTTP/1.1"

    # -- plumbing ------------------------------------------------------- #
    def log_message(self, fmt, *args):                       # noqa: D401 - stdlib hook
        if getattr(self.server, "quiet", False):
            return
        print("[http] %s %s" % (self.command, self.path.split("?")[0]), flush=True)

    def _json(self, status, payload, extra_headers=None):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for key, value in (extra_headers or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(body)

    def _error(self, status, message, details=None):
        self._json(status, {"ok": False, "error": message, "details": details or []})

    def _body(self):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length <= 0 or length > MAX_BODY:
            return None, "a JSON body of at most %d bytes is required" % MAX_BODY
        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            return None, "malformed JSON: %s" % error
        if not isinstance(payload, dict):
            return None, "the body must be a JSON object"
        return payload, None

    def _token_ok(self):
        token = self.headers.get(TOKEN_HEADER)
        if not token:
            query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            token = (query.get("token") or [None])[0]
        expected = self.server.token
        if not isinstance(token, str) or not expected:
            return False
        try:
            return secrets.compare_digest(token, expected)
        except TypeError:
            return False

    def _route(self):
        return urllib.parse.urlparse(self.path).path

    # -- GET ------------------------------------------------------------ #
    def do_GET(self):                                        # noqa: N802 - stdlib hook
        path = self._route()
        app = self.server.app
        try:
            if path == "/healthz":
                # The one unauthenticated probe: liveness only, no level content.
                self._json(200, {"ok": True, "service": SERVICE,
                                 "protocol": PROTOCOL, "pid": os.getpid(),
                                 "uptimeSeconds": round(time.time() - app.started, 1)})
                return
            if path.startswith("/api/") and not self._token_ok():
                self._error(403, "bad or missing token; open the URL printed by the server "
                                 "(it carries ?token=…) or send X-Editor-Token")
                return
            if path == "/api/health":
                state = app.state_summary()
                self._json(200, {
                    "ok": True, "service": SERVICE, "protocol": PROTOCOL,
                    "pid": os.getpid(), "level": state.get("level"),
                    "open": state.get("open"), "props": (state.get("counts") or {}).get("props"),
                    "gate": state.get("gate"), "version": app.version,
                    "uptimeSeconds": round(time.time() - app.started, 1),
                })
                return
            if path == "/api/meta":
                self._json(200, {
                    "ok": True, "service": SERVICE, "protocol": PROTOCOL,
                    "tokenRequired": True,
                    "levels": app.list_levels(),
                    "addresses": self.server.addresses,
                    "port": self.server.server_address[1],
                })
                return
            if path == "/api/levels":
                self._json(200, {"ok": True, "levels": app.list_levels(),
                                 "current": app.editor.level})
                return
            if path == "/api/level":
                self._json(200, {"ok": True, "state": app.state()})
                return
            if path == "/api/regions":
                try:
                    with app.mu:
                        payload = app.load_regions()
                except editor_core.OpError as refusal:
                    self._json(200, {"ok": False, "error": str(refusal),
                                     "details": refusal.details})
                    return
                self._json(200, {"ok": True, "path": core.display_path(app.regions_path()),
                                 **payload})
                return
            if path == "/api/stats":
                query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
                params = {key: values[-1] for key, values in query.items()}
                try:
                    with app.mu:
                        if params.get("compare"):
                            result = app.compare_regions(params)
                        else:
                            result = app.stats(params)
                except editor_core.OpError as refusal:
                    self._json(200, {"ok": False, "error": str(refusal),
                                     "details": refusal.details})
                    return
                self._json(200, {"ok": True, "stats": result})
                return
            if path == "/api/render":
                # The same renderer the CLI uses: a persistent headless-Chromium
                # daemon renders the real page.  This endpoint is the HTTP spelling
                # of `shot.py`; parameters are identical (view/views/layers/w/h/...).
                query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
                try:
                    from Tools.leveleditor import shot as shot_render
                except Exception as unexpected:               # noqa: BLE001 - optional
                    self._json(200, {"ok": False, "code": "render_unavailable",
                                     "error": "the shot renderer is not importable: %s"
                                              % unexpected})
                    return
                try:
                    params = shot_render.request_from_query(
                        query, app.editor.level, levels_dir=app.levels_dir,
                        state_dir=Path(app.state_dir))
                    result = shot_render.render_request(
                        params, directory=Path(app.state_dir),
                        token=shot_render.load_token(Path(app.state_dir)))
                except shot_render.ShotError as refusal:
                    self._json(200, {"ok": False, "error": refusal.message,
                                     "code": refusal.code, "details": refusal.details})
                    return
                except Exception as unexpected:               # noqa: BLE001 - transport
                    self._json(200, {"ok": False, "error": "%s: %s"
                                     % (type(unexpected).__name__, unexpected),
                                     "code": "render_failed"})
                    return
                self._json(200, result)
                return
            if path == "/api/check":
                if app.v2_mode:
                    check = app.v2_document.check_payload()
                    self._json(200, {"ok": True, "op": "check", "result": check,
                                     "gate": check, "error": "", "details": []})
                    return
                with app.mu:
                    response = app.editor.handle({"op": "check", "args": {"report": True}})
                self._json(200, response)
                return
            if path == "/api/hints":
                query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
                try:
                    diameter = float((query.get("d") or ["0.6"])[0])
                except ValueError:
                    diameter = 0.6
                self._json(200, {"ok": True, "hints": app.hints(diameter)})
                return
            if path.startswith("/api/models/"):
                self._serve_model(path[len("/api/models/"):])
                return
            if path.startswith("/api/v2/"):
                self._serve_v2(path[len("/api/v2/"):])
                return
            if path.startswith("/api/ref-stage/"):
                self._serve_ref_stage(path[len("/api/ref-stage/"):])
                return
            if path == "/api/events":
                self._serve_events()
                return
            if path.startswith("/api/"):
                self._error(404, "no such endpoint: %s" % path)
                return
            self._serve_static(path)
        except BrokenPipeError:
            pass
        except Exception as unexpected:                      # noqa: BLE001 - transport
            try:
                self._error(500, "%s: %s" % (type(unexpected).__name__, unexpected))
            except OSError:
                pass

    @staticmethod
    def _v2_flag(query, name, default=True):
        raw = (query.get(name) or [None])[0]
        if raw is None:
            return bool(default)
        return str(raw).strip().lower() not in ("0", "false", "no", "off", "")

    def _serve_v2(self, name):
        """The v2 view layers.  Every GUI toggle has one HTTP spelling here."""
        app = self.server.app
        name = urllib.parse.unquote(name).strip("/")
        if not app.v2_mode:
            self._error(404, "no katamari.placement/2 document is open")
            return
        query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        headers = {"X-Unlicensed-Reference": "1"} if app.ref_mode else None
        try:
            if name == "layers":
                self._json(200, {
                    "ok": True, "schema": v2_document.SCHEMA_V2,
                    "level": app.editor.level,
                    "layers": v2_document.layer_map(app.v2_document),
                    "counts": app.v2_document.summary["counts"],
                }, headers)
                return
            if name == "terrain":
                self._json(200, {"ok": True, "terrain": app.v2_terrain_payload()},
                           headers)
                return
            if name == "solids":
                solids = app.v2_document.summary["solids"]
                self._json(200, {"ok": True, "count": len(solids), "solids": solids},
                           headers)
                return
            if name == "activity":
                self._json(200, {"ok": True,
                                 "activity": app.v2_document.summary["activity"]},
                           headers)
                return
            if name == "parents":
                parents = app.v2_document.summary["parents"]
                self._json(200, {"ok": True, "count": len(parents),
                                 "parents": parents}, headers)
                return
            if name == "props":
                rotation = self._v2_flag(query, "rotation", True)
                scale = self._v2_flag(query, "scale", True)
                props = app.v2_document.props_payload(rotation=rotation, scale=scale)
                self._json(200, {"ok": True, "rotation": rotation, "scale": scale,
                                 "count": len(props), "props": props}, headers)
                return
            if name == "view":
                payload = app.v2_payload(
                    terrain=self._v2_flag(query, "terrain", True),
                    solids=self._v2_flag(query, "solids", True),
                    walls=self._v2_flag(query, "walls", True),
                    kill=self._v2_flag(query, "kill", True),
                    parents=self._v2_flag(query, "parents", True),
                    rotation=self._v2_flag(query, "rotation", True),
                    scale=self._v2_flag(query, "scale", True),
                    props=self._v2_flag(query, "props", True),
                )
                self._json(200, payload, headers)
                return
            self._error(404, "no such v2 resource: %s" % name)
        except editor_core.OpError as refusal:
            self._json(404, {"ok": False, "error": str(refusal),
                             "details": refusal.details}, headers)
        except Exception as unexpected:                          # noqa: BLE001 - transport
            self._json(500, {"ok": False, "error": "%s: %s"
                             % (type(unexpected).__name__, unexpected)}, headers)

    def _serve_model(self, def_id):
        app = self.server.app
        def_id = urllib.parse.unquote(def_id)
        if def_id.endswith(".glb"):
            def_id = def_id[:-4]
        if app.ref_mode:
            resolved = app.ref_resolution.get(def_id)
            if resolved is None:
                if def_id in app.ref_fbx_only:
                    self._error(404, "reference def %r has only an FBX source (%s); "
                                     "convert it to viewer_glb/%s.glb first"
                                % (def_id, app.ref_fbx_only[def_id]["source"], def_id))
                else:
                    self._error(404, "no local viewer mesh for reference def %r" % def_id)
                return
            path = Path(resolved["path"])
            if not path.is_file():
                self._error(404, "the resolved reference mesh is gone: %r" % def_id)
                return
            data = path.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "model/gltf-binary")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Unlicensed-Reference", "1")
            self.end_headers()
            self.wfile.write(data)
            return
        manifest = app.editor.manifests.get(def_id)
        path = core.prop_glb(def_id)
        if manifest is None or not path.is_file():
            self._error(404, "no GLB for def %r" % def_id)
            return
        data = path.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "model/gltf-binary")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        self.wfile.write(data)

    def _serve_ref_stage(self, name):
        """The original stage of a reference level: terrain GLB, collision, transforms.

        Same rule as ``/api/models``: the bytes exist only while a ``_ref_*`` level with
        an extraction open, they are never cached by the browser, and the unlicensed
        origin is on the response so no caller can mistake them for project assets.
        """
        app = self.server.app
        name = urllib.parse.unquote(name)
        if not app.ref_mode:
            self._error(404, "the original stage is only available for a _ref_* level")
            return
        stage = app.ref_stage or {}
        if not stage.get("available"):
            self._error(404, "no stage extraction for this level: %s"
                        % (stage.get("reason") or "unknown reason"))
            return
        if name in ("stage.glb", "terrain.glb"):
            try:
                data = app.ref_stage_glb()
            except ref_stage.RefStageError as refusal:
                self._error(404, str(refusal))
                return
            self._send_bytes(data, "model/gltf-binary")
            return
        if name == "contact":
            query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            try:
                tolerance = float((query.get("t") or query.get("tolerance")
                                   or ["0.1"])[0])
            except ValueError:
                tolerance = 0.1
            directory, _reason = ref_stage.resolve_stage_dir(app.ref_stage_dir)
            try:
                report = ref_stage.contact_report(directory, stage,
                                                  app.editor.doc or {},
                                                  max(0.0, tolerance))
            except ref_stage.RefStageError as refusal:
                self._error(404, str(refusal))
                return
            body = json.dumps({"ok": True, **report}, ensure_ascii=False).encode("utf-8")
            self._send_bytes(body, "application/json; charset=utf-8")
            return
        if name in ("collision.json", "transforms.json"):
            payload = stage.get("collision") if name.startswith("collision") else stage.get("transforms")
            if payload is None:
                self._error(404, "no %s for this level" % name)
                return
            body = json.dumps({"ok": True, "zones": stage.get("zones") or [],
                               **payload}, ensure_ascii=False).encode("utf-8")
            self._send_bytes(body, "application/json; charset=utf-8")
            return
        self._error(404, "no such stage resource: %s" % name)

    def _send_bytes(self, data, content_type):
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Unlicensed-Reference", "1")
        self.end_headers()
        self.wfile.write(data)

    def _serve_static(self, path):
        if path in ("", "/"):
            path = "/index.html"
        relative = urllib.parse.unquote(path.lstrip("/"))
        target = (WEB_DIR / relative).resolve()
        try:
            target.relative_to(WEB_DIR.resolve())
        except ValueError:
            self._error(403, "path escapes the web root")
            return
        if not target.is_file():
            self._error(404, "not found: %s" % path)
            return
        content_type, _encoding = mimetypes.guess_type(str(target))
        content_type = content_type or "application/octet-stream"
        if target.suffix == ".js":
            content_type = "text/javascript; charset=utf-8"
        elif target.suffix in (".css",):
            content_type = "text/css; charset=utf-8"
        elif target.suffix == ".html":
            content_type = "text/html; charset=utf-8"
        elif target.suffix == ".json":
            content_type = "application/json; charset=utf-8"
        data = target.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        self.wfile.write(data)

    def _serve_events(self):
        query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        try:
            since = int((query.get("since") or ["0"])[0])
        except ValueError:
            since = 0
        app = self.server.app
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "keep-alive")
        self.send_header("X-Accel-Buffering", "no")
        self.end_headers()
        try:
            self.wfile.write(("retry: 2000\nid: %d\n\n" % app.version).encode("ascii"))
            self.wfile.flush()
            since = max(since, 0)
            while not self.server.stop_event.is_set():
                version = app.wait_for_change(since, timeout=15.0)
                if version != since:
                    self.wfile.write(("id: %d\nevent: change\ndata: %s\n\n"
                                      % (version, json.dumps({"version": version}))).encode("utf-8"))
                    since = version
                else:
                    self.wfile.write(b": keep-alive\n\n")
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, OSError):
            return

    # -- POST ----------------------------------------------------------- #
    def do_POST(self):                                       # noqa: N802 - stdlib hook
        path = self._route()
        if path not in ("/api/op", "/api/open", "/api/regions"):
            self._error(404, "no such endpoint: %s" % path)
            return
        payload, problem = self._body()
        if problem:
            self._error(400, problem)
            return
        token = payload.pop("token", None)
        header_token = self.headers.get(TOKEN_HEADER)
        expected = self.server.token
        ok = False
        for candidate in (header_token, token):
            if not isinstance(candidate, str) or not expected:
                continue
            try:
                if secrets.compare_digest(candidate, expected):
                    ok = True
            except TypeError:
                continue
        if not ok:
            self._error(403, "bad or missing token; open the URL printed by the server "
                             "(it carries ?token=…) or read %s" % self.server.token_path)
            return
        app = self.server.app
        if path == "/api/open":
            payload.setdefault("op", "open")
            args = {"level": payload.pop("level", None)}
            placement = payload.pop("placement", None)
            if placement is not None:
                args["placement"] = placement
            payload["args"] = args
        if path == "/api/regions":
            try:
                with app.mu:
                    result = app.save_regions({
                        "schemaVersion": 1,
                        "regions": payload.get("regions") or [],
                    })
                self._json(200, {"ok": True, "saved": result})
            except editor_core.OpError as refusal:
                self._error(400, str(refusal), refusal.details)
            return
        response = app.handle(payload)
        self._json(200, response)

    # -- misc ----------------------------------------------------------- #
    def do_OPTIONS(self):                                    # noqa: N802 - stdlib hook
        self.send_response(204)
        self.send_header("Allow", "GET, POST, OPTIONS")
        self.send_header("Content-Length", "0")
        self.end_headers()


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def make_http_server(app, host="127.0.0.1", port=0, token=None, token_path=None,
                     quiet=True):
    """Build (but do not serve) the HTTP server; tests use this too."""
    httpd = EditorHTTPServer((host, port), Handler)
    httpd.app = app
    if hasattr(app, "announce"):
        app.announce(httpd.server_address[1])
    httpd.token = token if token is not None else secrets.token_urlsafe(16)
    httpd.token_path = token_path or (Path(app.state_dir) / "token")
    httpd.addresses = addresses()
    httpd.stop_event = threading.Event()
    httpd.quiet = quiet
    return httpd


def build_parser():
    parser = argparse.ArgumentParser(
        prog="server.py", description="Web level editor for katamari.placement/0 levels.")
    parser.add_argument("--level", required=True,
                        help="level id under Docs/Design/Levels (e.g. story_01_bench_size)")
    parser.add_argument("--host", default="0.0.0.0",
                        help="bind address (default 0.0.0.0 so the Tailscale PC can reach it)")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help="port (default %d; 0 = pick one)" % DEFAULT_PORT)
    parser.add_argument("--levels-dir", default=None, help="override Docs/Design/Levels")
    parser.add_argument("--placement", default=None,
                        help="placement file inside the level dir (default placement.json; "
                             "levels whose id starts with _ref_ prefer placement.model.json)")
    parser.add_argument("--ref-models-dir", default=None,
                        help="local mesh root for UNLICENSED reference levels (default: "
                             "$VRC_KATAMARI_REF_MODELS or ~/KatamariRef/models); only used "
                             "for level ids starting with _ref_")
    parser.add_argument("--ref-stage-dir", default=None,
                        help="byte-level stage extraction of a reference level (terrain, "
                             "collision, true instance transforms): default "
                             "$VRC_KATAMARI_REF_STAGE or ~/KatamariRef/noclip_export/kse_out; "
                             "only used for level ids starting with _ref_")
    parser.add_argument("--state-dir", default=None,
                        help="token and lock directory (default: XDG state dir)")
    parser.add_argument("--token", default=None,
                        help="fixed write token (default: read/create one in the state dir)")
    parser.add_argument("--no-lock", action="store_true", help="do not take the per-level lock")
    parser.add_argument("--open", action="store_true",
                        help="open the page in the local browser after starting")
    parser.add_argument("--quiet", action="store_true", help="no per-request log lines")
    return parser


def _urls(host, port, token):
    loopback = "http://127.0.0.1:%d/?token=%s" % (port, token)
    urls = [("this machine", loopback)]
    if host in ("0.0.0.0", "::", ""):
        for address in addresses():
            urls.append(("network", "http://%s:%d/?token=%s" % (address, port, token)))
    else:
        urls.append(("configured host", "http://%s:%d/?token=%s" % (host, port, token)))
    return urls


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        level_id = args.level
        state_dir = Path(args.state_dir).expanduser() if args.state_dir else default_state_dir()
        state_dir.mkdir(parents=True, exist_ok=True)
        token = load_or_create_token(state_dir, args.token)
        app = WebEditor(level_id, levels_dir=args.levels_dir, state_dir=state_dir,
                        lock=not args.no_lock, ref_models_dir=args.ref_models_dir,
                        placement=args.placement, ref_stage_dir=args.ref_stage_dir)
        httpd = make_http_server(app, args.host, args.port, token=token,
                                 token_path=state_dir / "token", quiet=args.quiet)
    except Exception as error:                               # noqa: BLE001 - startup
        print("leveleditor: %s" % error, file=sys.stderr)
        return 1

    port = httpd.server_address[1]
    level_path = core.display_path(Path(app.editor.path))
    print("%s %d" % (SERVICE, PROTOCOL), flush=True)
    print("level:   %s  (%s)" % (app.editor.level, level_path), flush=True)
    if app.v2_mode:
        summary = app.v2_document.summary
        print("document: v2 %s — %s props, %s solids, terrain %s tri / %s verts, "
              "%s activity walls, structural errors %s"
              % (v2_document.SCHEMA_V2, summary["counts"]["props"],
                 summary["counts"]["solids"],
                 summary["counts"]["terrainTriangles"],
                 summary["counts"]["terrainVertices"],
                 summary["counts"]["walls"], len(app.v2_document.errors)), flush=True)
    if app.ref_mode:
        print("REFERENCE: UNLICENSED — this level is read-only; meshes are ripped assets "
              "served only from %s; do not distribute, upload or bake them into the "
              "managed props or Docs/Art."
              % ref_models.display_path(app.ref_models_root), flush=True)
    if app.ref_mode:
        stage = app.ref_stage or {}
        if stage.get("available"):
            counts = stage.get("counts") or {}
            print("stage:   %s — %s vertices, %s collision AABBs, %s collision triangles, "
                  "%s/%s props resolved to an instance, %s parent links, %s zones"
                  % (stage.get("dir"), counts.get("meshVertices"), counts.get("aabbs"),
                     counts.get("triangles"), counts.get("matched"), counts.get("props"),
                     counts.get("parentsResolved"), counts.get("zones")), flush=True)
        else:
            print("stage:   none (%s)" % (stage.get("reason") or "not found"), flush=True)
    print("auth:   every /api read and write requires the token (header %s: … or "
          "?token=…); static files and GET /healthz are open" % TOKEN_HEADER,
          flush=True)
    print("token:   %s" % token, flush=True)
    print("token file: %s" % (state_dir / "token"), flush=True)
    for label, url in _urls(args.host, port, token):
        print("open (%s): %s" % (label, url), flush=True)
    print("tailscale: the Windows PC uses http://<this machine's tailnet IP>:%d/ "
          "(the URLs above list every address this process can see)" % port, flush=True)
    print("stop:    Ctrl-C / SIGTERM (every edit is already exported)", flush=True)

    def stop(_signum=None, _frame=None):
        httpd.stop_event.set()
        threading.Thread(target=httpd.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    if args.open:
        try:
            import webbrowser
            webbrowser.open("http://127.0.0.1:%d/?token=%s" % (port, token))
        except Exception:                                    # noqa: BLE001 - optional
            pass
    try:
        httpd.serve_forever(poll_interval=0.2)
    finally:
        app.close()
        httpd.server_close()
        print("leveleditor: stopped; the file is at %s" % level_path, flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
