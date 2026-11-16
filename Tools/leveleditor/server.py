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

    It protects against a second *web* editor (the Blender resident editor uses its
    own single-process rule).  A stale lock dies with its process, so there is no
    manual cleanup.  The lock lives in the state directory, never in the repo.
    """

    def __init__(self, level_path, state_dir):
        digest = hashlib.sha1(str(Path(level_path).resolve()).encode("utf-8")).hexdigest()
        self.path = Path(state_dir) / "locks" / (digest[:16] + ".lock")
        self._fd = None

    def acquire(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        descriptor = os.open(str(self.path), os.O_RDWR | os.O_CREAT, 0o600)
        try:
            import fcntl
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except ImportError:
            pass
        except OSError:
            try:
                holder = os.read(descriptor, 200).decode("ascii", "replace").strip()
            except OSError:
                holder = ""
            os.close(descriptor)
            raise RuntimeError(
                "another level editor already holds %s%s; one level, one writer"
                % (self.path.name, (" (pid %s)" % holder) if holder else ""))
        os.ftruncate(descriptor, 0)
        os.write(descriptor, ("%d\n" % os.getpid()).encode("ascii"))
        self._fd = descriptor

    def release(self):
        if self._fd is None:
            return
        try:
            import fcntl
            fcntl.flock(self._fd, fcntl.LOCK_UN)
        except (ImportError, OSError):
            pass
        try:
            os.close(self._fd)
        finally:
            self._fd = None


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
        self.lock_enabled = bool(lock)
        self.lock = None
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
        levels = []
        for path in sorted(self.levels_dir.rglob("placement.json")):
            level_id = path.parent.relative_to(self.levels_dir).as_posix()
            if not core.is_valid_level_id(level_id):
                continue
            try:
                stat = path.stat()
            except OSError:
                continue
            levels.append({"id": level_id, "bytes": stat.st_size,
                           "modified": time.strftime("%Y-%m-%dT%H:%M:%S",
                                                     time.localtime(stat.st_mtime))})
        return levels

    def resolve(self, level):
        """The file this level opens.

        A reference level prefers the converted ``placement.model.json`` (it is the
        one that carries the defs' ``model`` and the original's ``group`` parent
        links); an explicit ``--placement`` wins for both kinds.
        """
        folder = core.level_folder(level, self.levels_dir)
        if self.placement_name:
            return folder / self.placement_name
        if ref_models.is_ref_level(level):
            model_file = folder / "placement.model.json"
            if model_file.is_file():
                return model_file
        return folder / "placement.json"

    # -- lifecycle ------------------------------------------------------ #
    def open_level(self, level):
        path = self.resolve(level)
        if not path.is_file():
            raise editor_core.OpError("cannot read %s" % core.display_path(path))
        new_lock = None
        if self.lock_enabled:
            new_lock = LevelLock(path, self.state_dir)
            new_lock.acquire()
        previous = (self.ref_mode, self.ref_models_root, self.ref_table,
                    self.ref_resolution, self.ref_fbx_only, self.ref_stage,
                    self._ref_stage_glb, self.editor.base_manifests)
        is_ref = ref_models.is_ref_level(level)
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
            opened = self.editor.open(level=level, path=path)
            self.ref_mode = is_ref
            self._refresh_ref_models()
            self._refresh_ref_stage()
        except Exception:
            (self.ref_mode, self.ref_models_root, self.ref_table,
             self.ref_resolution, self.ref_fbx_only, self.ref_stage,
             self._ref_stage_glb, self.editor.base_manifests) = previous
            self.editor.manifests = self.editor.base_manifests
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
        previous_path = Path(self.editor.path) if self.editor.path else None
        result = self.editor.reload()
        self._refresh_ref_models()
        self._refresh_ref_stage()
        if previous_path is not None and self.lock is not None:
            # The lock is per path; reload never changes it.
            pass
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
                if op in ("undo", "redo"):
                    response = self._undo(op, payload.get("args") or {})
                elif op == "open":
                    args = payload.get("args") or {}
                    level = args.get("level")
                    if not isinstance(level, str) or not level:
                        raise editor_core.OpError("open needs level=<id>")
                    opened = self.open_level(level)
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
        report = self.editor._validate(doc)
        path = Path(self.editor.path)
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
            "version": self.version,
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
            volume_k = core.effective_volume_k(prop, catalog)
            included.append({
                "id": item_id,
                "def": prop.get("def"),
                # The original gate quantity: Req floored to whole millimetres, priced at
                # the prop's own k.  Every count below compares integers, never floats.
                "dReq": core.absorb_diameter_req(size, volume_k),
                "dReqMm": core.absorb_diameter_req_mm(size, volume_k),
                "volumeK": volume_k,
                "volume": volume,
                "units": core.effective_value_units(prop, catalog) or 0,
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
            check = self.editor.op_check({"report": True})
            payload = self.state_summary()
            payload.update({
                "service": SERVICE,
                "protocol": PROTOCOL,
                "pid": os.getpid(),
                "doc": doc,
                "derived": self.derived(),
                "manifests": self.manifests(),
                "check": check,
                "levels": self.list_levels(),
                "bounds": (doc.get("level") or {}).get("bounds"),
                "ref": self.ref_state(),
                "uptimeSeconds": round(time.time() - self.started, 1),
            })
            return payload

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
            payload["args"] = {"level": payload.pop("level", None)}
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
