"""Resident level editor: one open level, incremental ops, export, undo. No ``bpy``.

The state machine lives here and imports **no** Blender module.  The bpy side is a
:class:`SceneAdapter` injected by ``blender/editor_server.py``; the tests inject a
fake.  So "what does a move do to the document" is testable without Blender, and
there is exactly one implementation of it.

Truth model (design: ``LEVEL_AUTHORING`` section 16)
---------------------------------------------------
* The **resident state** - the document plus the Blender scene built from it - is the
  *working* state.  It is not a copy of the file: it is the thing being edited.
* The **export** - ``placement.json`` on disk, canonical bytes - is the state every
  other consumer reads: the Unity importer, the one-shot render/export scripts, git.
* Every committed operation ends with an export, so a crash costs at most the
  operation that was in flight; nothing before it is lost.
* An operation is committed only if it does not introduce an error this session has
  never seen.  Otherwise it is rolled back, the file is left alone, and the response
  names the rule it broke.  (A file that opens red stays editable: its own errors are
  "known", so the editor can be used to repair it and must not be blocked by them.)

Ops are named.  Every mutating op is a pure document edit followed by one
``SceneAdapter.sync(new_doc, previous_doc)``; sync is a diff, so moving one prop
touches one object and ``undo`` is the same diff run backwards.
"""

import copy
import hashlib
import json
import math
import os
import time
import traceback
from pathlib import Path

from Tools.levelbuild import placement_core as core
from Tools.levelbuild import supports as support_geometry

MISSING = object()

#: Fields an op may set on a prop (``aabb``/``absorb*`` follow from ``def``).
PROP_SETTABLE = ("def", "kind", "zone", "group", "groupId", "position", "rotationQuat", "yaw",
                 "support", "stackedOn", "parent", "parentDerived", "valueUnits", "volumeK",
                 "volumePenalty", "notes")

#: Fields an op may set on a ``defs[]`` entry.  ``volumeK`` is the original's per-item
#: volume factor (``Volume = k * a * b * c``, default 0.70); it is authored on the DEF so a
#: whole type shares one number, and a ``props[].volumeK`` override exists for one-offs.
DEF_SETTABLE = ("kind", "valueUnits", "model", "volumeK", "volumePenalty", "notes")

#: Fields an op may set on a structure.
STRUCTURE_SETTABLE = ("kind", "role", "shape", "center", "size", "rotationQuat", "yaw",
                      "collision", "absorbable", "support", "meets", "material", "notes")

#: Ops that mutate the document and may therefore appear inside ``batch``.
MUTATING_OPS = ("add_prop", "add_structure", "set", "set_def", "move", "rotate", "remove",
                "seat", "derive_supports", "add_def", "camera", "preset", "expected")


class OpError(Exception):
    """A request the editor refuses.  ``details`` is a list of concrete reasons."""

    def __init__(self, message, details=None):
        super().__init__(message)
        self.details = list(details or [])


# --------------------------------------------------------------------------- #
# request parsing
# --------------------------------------------------------------------------- #
def _args(payload):
    args = payload.get("args")
    if args is None:
        return {}
    if not isinstance(args, dict):
        raise OpError("args must be an object, got %s" % type(args).__name__)
    return args


def _known(args, allowed):
    unknown = sorted(set(args) - set(allowed))
    if unknown:
        raise OpError("unknown argument(s) %s; accepted: %s"
                      % (", ".join(unknown), ", ".join(allowed)))


def _required(args, key):
    if key not in args or args[key] is None:
        raise OpError("%s is required" % key)
    return args[key]


def _string(args, key, default=MISSING):
    value = args.get(key, MISSING if default is MISSING else default)
    if value is MISSING:
        raise OpError("%s is required" % key)
    if not isinstance(value, str) or not value.strip():
        raise OpError("%s must be a non-empty string" % key)
    return value


def _number(args, key, default=MISSING):
    value = args.get(key, MISSING if default is MISSING else default)
    if value is MISSING:
        raise OpError("%s is required" % key)
    try:
        number = float(value)
    except (TypeError, ValueError):
        raise OpError("%s must be a number, got %r" % (key, value))
    if math.isnan(number) or math.isinf(number):
        raise OpError("%s must be finite, got %r" % (key, value))
    return number


def _integer(args, key, default=MISSING, minimum=None):
    value = args.get(key, MISSING if default is MISSING else default)
    if value is MISSING:
        raise OpError("%s is required" % key)
    if isinstance(value, bool):
        raise OpError("%s must be a whole number, got %r" % (key, value))
    if isinstance(value, float) and value.is_integer():
        value = int(value)
    if not isinstance(value, int):
        raise OpError("%s must be a whole number, got %r" % (key, value))
    if minimum is not None and value < minimum:
        raise OpError("%s must be >= %d, got %d" % (key, minimum, value))
    return value


def _vec3(value, what):
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        raise OpError("%s must be a list of 3 numbers, got %r" % (what, value))
    out = []
    for index, item in enumerate(value):
        try:
            number = float(item)
        except (TypeError, ValueError):
            raise OpError("%s[%d] must be a number, got %r" % (what, index, item))
        if math.isnan(number) or math.isinf(number):
            raise OpError("%s[%d] must be finite, got %r" % (what, index, item))
        out.append(number)
    return out


def _vec3_arg(args, key, default=MISSING):
    value = args.get(key, MISSING if default is MISSING else default)
    if value is MISSING:
        raise OpError("%s is required" % key)
    return _vec3(value, key)


def _quat(value, what):
    if not isinstance(value, (list, tuple)) or len(value) != 4:
        raise OpError("%s must be a list of 4 numbers (x, y, z, w), got %r" % (what, value))
    numbers = _vec3(value[:3], what) + [0.0]
    try:
        numbers[3] = float(value[3])
    except (TypeError, ValueError):
        raise OpError("%s[3] must be a number" % what)
    length = math.sqrt(sum(number * number for number in numbers))
    if length < 1e-6:
        raise OpError("%s is a zero quaternion" % what)
    return [number / length for number in numbers]


def _yaw_quat(degrees):
    return list(core.quat_from_yaw(math.radians(float(degrees))))


def _bool(value, what):
    if not isinstance(value, bool):
        raise OpError("%s must be true or false, got %r" % (what, value))
    return value


def _deepcopy(doc):
    return json.loads(json.dumps(doc, ensure_ascii=False))


# --------------------------------------------------------------------------- #
# document helpers (shared by the ops)
# --------------------------------------------------------------------------- #
def _props(doc):
    return [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]


def _structures(doc):
    return [structure for structure in doc.get("structures") or []
            if isinstance(structure, dict)]


def _prop_by_id(doc, item_id):
    for prop in _props(doc):
        if prop.get("itemId") == item_id:
            return prop
    return None


def _structure_by_id(doc, item_id):
    for structure in _structures(doc):
        if structure.get("id") == item_id:
            return structure
    return None


def find_entity(doc, entity_id):
    """``(type, entry)`` for a prop or a structure id, else ``(None, None)``."""
    prop = _prop_by_id(doc, entity_id)
    if prop is not None:
        return "prop", prop
    structure = _structure_by_id(doc, entity_id)
    if structure is not None:
        return "structure", structure
    return None, None


def next_item_id(doc, def_id):
    """``<def>_01``, ``<def>_02``, ... - the first free one (deterministic)."""
    used = {prop.get("itemId") for prop in _props(doc)}
    index = 1
    while True:
        candidate = "%s_%02d" % (def_id, index)
        if candidate not in used:
            return candidate
        index += 1


def next_structure_id(doc, prefix="structure"):
    used = {structure.get("id") for structure in _structures(doc)}
    used |= {prop.get("itemId") for prop in _props(doc)}
    index = 1
    while True:
        candidate = "%s_%02d" % (prefix, index)
        if candidate not in used:
            return candidate
        index += 1


def refresh_expected(doc):
    """``level.expected`` is the design total; recompute it from the document.

    Only an op that says so (``updateExpected``) or the ``expected`` op calls this:
    silently rewriting the design gate on every edit would make "127 props / 1500 u"
    mean nothing.
    """
    level = doc.setdefault("level", {})
    expected = dict(level.get("expected") or {})
    totals = core.expected_totals(doc)
    expected["propCount"] = totals["propCount"]
    # The content total is DERIVED growth in mm^3; the retired valueUnits total is dropped.
    expected["growthMm3"] = totals["growthMm3"]
    expected.pop("valueUnits", None)
    # The structure side is optional; refresh it only where the level declares it (a curve
    # ramp counts once in structureCount and by its segments in staticObbs).
    for key in ("structureCount", "staticObbs"):
        if key in expected:
            expected[key] = totals[key]
    level["expected"] = expected
    return expected


def seat_props(doc, item_ids=None):
    """Seat props on their declared support surface (the one implementation).

    Used by the editor's ``seat`` op **and** by ``levelbuild.py seat`` - the CLI is
    where this logic used to live, and two copies would drift.  Mutates ``doc`` in
    place and returns ``{"moved": [...], "skipped": [...], "details": [...],
    "props": n}``.  A prop already seated at file precision is not reported as moved,
    so a no-op re-run does not rewrite the file.
    """
    structures = core._index_structures(doc)
    props = _props(doc)
    by_id = {prop.get("itemId"): prop for prop in props}
    floor = core.ground_level(doc)
    moved, skipped, details = [], [], []
    for prop in props:
        item_id = prop.get("itemId")
        if item_ids and item_id not in item_ids:
            continue
        try:
            position = tuple(float(value) for value in prop["position"])
            size = tuple(float(value) for value in prop["aabb"])
            quat = tuple(float(value) for value in prop["rotationQuat"])
        except (KeyError, TypeError, ValueError):
            skipped.append(item_id)
            continue
        if prop.get("stackedOn"):
            other = by_id.get(prop["stackedOn"])
            if other is None:
                skipped.append(item_id)
                continue
            try:
                top = core.world_aabb(tuple(float(v) for v in other["position"]),
                                             tuple(float(v) for v in other["aabb"]),
                                             tuple(float(v) for v in other["rotationQuat"]))[1][1]
            except (KeyError, TypeError, ValueError):
                skipped.append(item_id)
                continue
            normal, point = (0.0, 1.0, 0.0), (0.0, top, 0.0)
        elif prop.get("support") in structures:
            normal, point = core.structure_top_plane(structures[prop["support"]])
        else:
            normal, point = (0.0, 1.0, 0.0), (0.0, floor, 0.0)

        new_position, new_quat = core.reseat(position, size, quat, normal, point)
        if (_same_at_precision(position, quat, new_position, new_quat)):
            continue
        details.append({
            "itemId": item_id,
            "previous": [core.rounded(v, core.POSITION_DECIMALS)
                         for v in position],
            "position": [core.rounded(v, core.POSITION_DECIMALS)
                         for v in new_position],
            "previousQuat": [core.rounded(v, core.QUAT_DECIMALS)
                             for v in quat],
            "rotationQuat": [core.rounded(v, core.QUAT_DECIMALS)
                             for v in new_quat],
        })
        prop["position"] = details[-1]["position"]
        prop["rotationQuat"] = details[-1]["rotationQuat"]
        moved.append(item_id)
    return {"moved": moved, "skipped": skipped, "details": details, "props": len(props)}


def _same_at_precision(position, quat, new_position, new_quat):
    return (
        all(core.rounded(a, core.POSITION_DECIMALS) == core.rounded(b, core.POSITION_DECIMALS)
            for a, b in zip(position, new_position))
        and all(core.rounded(a, core.QUAT_DECIMALS) == core.rounded(b, core.QUAT_DECIMALS)
                for a, b in zip(quat, new_quat)))


def summarize(report):
    return {"ok": report.ok, "errors": list(report.errors),
            "warnings": list(report.warnings), "notes": list(report.notes),
            "errorCount": len(report.errors), "warningCount": len(report.warnings)}


def _canonical_bytes(doc, manifests):
    return core.dump_doc(doc, manifests).encode("utf-8")


# --------------------------------------------------------------------------- #
# the editor
# --------------------------------------------------------------------------- #
class Editor:
    """One open level, edited in place; every committed op is exported.

    ``scene`` is the adapter (``blender/blender_common.LevelScene`` and the tests):

    ``reset()``
        Drop every object the editor owns.
    ``sync(doc, previous=None) -> dict``
        Make the scene match ``doc``, touching only the differences against
        ``previous``; returns per-kind counts.
    ``render(spec, doc) -> dict``
        Render one frame; ``spec`` has ``camera`` (a ``cameras[]`` entry), ``engine``
        (logical name), ``res``, ``samples``, ``path``.  Returns ``{"path",
        "engine_id", "seconds"}``.
    ``status() -> dict``
        Adapter-side facts for ``status`` (Blender version, object counts, ...).
    """

    def __init__(self, scene, manifests=None, auto_export=True, undo_limit=50,
                 out_dir=None, levels_dir=None):
        self.scene = scene
        self.manifests = manifests if manifests is not None else core.load_manifests()
        #: Pristine manifest tree; ``manifests`` is rebuilt from it on every open/reload
        #: because a def's declared ``model`` can drop a repo entry (the id-collision case).
        self.base_manifests = self.manifests
        self.auto_export = bool(auto_export)
        self.undo_limit = int(undo_limit)
        self.out_dir = Path(out_dir) if out_dir else None
        self.levels_dir = Path(levels_dir) if levels_dir else None
        self.doc = None
        self.path = None
        self.level = None
        self.undo_stack = []
        self.known_errors = set()
        #: True only when committed work is *not* on disk (auto-export off, or an
        #: export that could not write).  ``dirty`` (file != document) is a different
        #: fact: it also happens when a human edits the file under a running editor.
        self.unexported = False
        self.last_export = None
        self.stop_requested = False
        self.started = time.time()
        self.op_count = 0
        self._ops = {
            "status": self.op_status,
            "open": self.op_open,
            "reload": self.op_reload,
            "list": self.op_list,
            "get": self.op_get,
            "add_prop": self.op_add_prop,
            "add_structure": self.op_add_structure,
            "set": self.op_set,
            "move": self.op_move,
            "rotate": self.op_rotate,
            "remove": self.op_remove,
            "seat": self.op_seat,
            "derive_supports": self.op_derive_supports,
            "add_def": self.op_add_def,
            "set_def": self.op_set_def,
            "expected": self.op_expected,
            "camera": self.op_camera,
            "preset": self.op_preset,
            "check": self.op_check,
            "export": self.op_export,
            "render": self.op_render,
            "undo": self.op_undo,
            "shutdown": self.op_shutdown,
        }
        self.aliases = {
            "save": "export",
            "set_camera": "camera",
            "set_preset": "preset",
            "add": "add_prop",
        }

    # ------------------------------------------------------------------ #
    # transport-facing entry point
    # ------------------------------------------------------------------ #
    def handle(self, payload):
        """Handle one JSON request object; never raises for a bad request."""
        started = time.perf_counter()
        try:
            if not isinstance(payload, dict):
                raise OpError("the request must be a JSON object")
            self.op_count += 1
            if "ops" in payload:
                result, ok = self._handle_batch(payload)
                op_name = "batch"
            else:
                name = payload.get("op")
                if not isinstance(name, str) or not name:
                    raise OpError("op is required (a string); see `levelctl.py ops`")
                name = self.aliases.get(name, name)
                handler = self._ops.get(name)
                if handler is None:
                    raise OpError("unknown op %r; known: %s"
                                  % (name, ", ".join(sorted(self._ops))))
                result = handler(_args(payload))
                op_name = name
                ok = True
            error, details = "", []
        except OpError as refusal:
            result, ok = None, False
            op_name = ("batch" if isinstance(payload, dict) and "ops" in payload
                       else (payload.get("op") if isinstance(payload, dict) else None))
            error, details = str(refusal), refusal.details
        except Exception as unexpected:                       # noqa: BLE001 - transport
            result, ok = None, False
            op_name = payload.get("op") if isinstance(payload, dict) else None
            error = "%s: %s" % (type(unexpected).__name__, unexpected)
            details = [traceback.format_exc(limit=6)]
        response = {
            "ok": ok,
            "op": op_name,
            "result": result,
            "error": error,
            "details": details,
            "state": self.state_summary(),
            "seconds": round(time.perf_counter() - started, 3),
        }
        if isinstance(result, dict) and "gate" in result:
            response["gate"] = result["gate"]
        return response

    def _handle_batch(self, payload):
        ops = payload.get("ops")
        if not isinstance(ops, list) or not ops:
            raise OpError("batch needs a non-empty ops list")
        self._require_open()
        original = self.doc
        results = []
        try:
            for index, item in enumerate(ops):
                if not isinstance(item, dict):
                    raise OpError("batch item %d must be an object" % index)
                name = self.aliases.get(item.get("op"), item.get("op"))
                if name not in MUTATING_OPS:
                    raise OpError("batch item %d: %r is not a mutating op "
                                  "(batch is all-or-nothing and exports once; "
                                  "run read-only ops separately)" % (index, name))
                handler = self._ops.get(name)
                if handler is None:
                    raise OpError("batch item %d: unknown op %r" % (index, name))
                try:
                    results.append(handler(_args(item), defer=True))
                except OpError as refusal:
                    raise OpError("batch item %d (%s): %s" % (index, name, refusal),
                                  refusal.details)
        except Exception:
            self.doc = original
            raise
        try:
            commit = self._commit(self.doc, "batch(%d ops)" % len(ops), previous=original)
        except OpError:
            # The ops themselves were fine but the resulting document is not: put the
            # whole batch back, so "atomic" also covers the gate.
            self.doc = original
            raise
        return ({"ops": results, "commit": commit}, True)

    # ------------------------------------------------------------------ #
    # state / io
    # ------------------------------------------------------------------ #
    def _require_open(self):
        if self.doc is None:
            raise OpError("no level is open; use `open --level <id>` (or --path)")

    def state_summary(self):
        if self.doc is None:
            return {"level": None, "path": None, "open": False, "undo": 0,
                    "autoExport": self.auto_export}
        summary = self._counts(self.doc)
        summary.update({
            "open": True,
            "path": core.display_path(self.path),
            "level": self.level,
            "undo": len(self.undo_stack),
            "dirty": self._dirty(),
            "unexported": self.unexported,
            "autoExport": self.auto_export,
        })
        return summary

    def _counts(self, doc):
        return {
            "level": (doc.get("level") or {}).get("id"),
            "props": len(_props(doc)),
            "structures": len(_structures(doc)),
            "cameras": len(doc.get("cameras") or []),
            "presets": len(doc.get("renderPresets") or []),
            "defs": len(doc.get("defs") or []),
            "growthMm3": core.growth_mm3_total(doc),
            # Legacy key kept so an older web client does not read `undefined`; it is the
            # deprecated valueUnits total and reads 0 once the file is regenerated.
            "valueUnits": core.value_units_total(doc),
            "supportsDerived": support_geometry.count_derived(doc),
        }

    def _validate(self, doc):
        return core.validate(doc, manifests=self.manifests)

    def _flush_pending(self):
        """Export work that only lives in memory before the document is replaced.

        Only possible with ``--no-auto-export`` (or after a refused export); switching
        level or reloading must not throw it away silently.
        """
        if self.doc is None or self.path is None or not self.unexported:
            return None
        return self._export(self.doc, force=True)

    def _dirty(self):
        if self.doc is None or self.path is None:
            return False
        try:
            return self.path.read_bytes() != _canonical_bytes(self.doc, self.manifests)
        except OSError:
            return True

    def _write(self, path, doc):
        data = _canonical_bytes(doc, self.manifests)
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_name("%s.tmp-%d" % (path.name, os.getpid()))
        temporary.write_bytes(data)
        os.replace(temporary, path)
        return {"path": core.display_path(path), "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest()}

    def _resolve(self, path_text):
        """A relative path from a request means the repository root, not Blender's cwd."""
        path = Path(path_text).expanduser()
        return path if path.is_absolute() else (core.ROOT / path)

    def _export(self, doc, target=None, force=False):
        """Validate, then write the canonical file.  The gate must be green (or forced)."""
        report = self._validate(doc)
        if report.errors and not force:
            return {"wrote": False, "gate": summarize(report),
                    "why": "%d error(s); pass force=true only to persist a known-red "
                           "work in progress" % len(report.errors)}
        target = self._resolve(target) if target else self.path
        written = self._write(target, doc)
        written["wrote"] = True
        written["gate"] = summarize(report)
        written["truth"] = (self.path is not None
                            and target.resolve() == Path(self.path).resolve())
        if written["truth"]:
            written["at"] = time.strftime("%Y-%m-%dT%H:%M:%S")
            self.last_export = written
            self.unexported = False
        return written

    # ------------------------------------------------------------------ #
    # commit
    # ------------------------------------------------------------------ #
    def _commit(self, new_doc, label, previous=None, force=False, defer=False):
        """Validate, export, sync - or roll back and refuse.

        Export happens **before** the scene sync on purpose: the file is the truth,
        so it must never be behind the scene.  If the scene then refuses the change
        (a missing library asset, say), the previous document is written back and the
        op fails, so the two cannot stay apart.
        """
        self._require_open()
        if defer:
            changed = new_doc != self.doc
            self.doc = new_doc
            return {"changed": changed, "deferred": True}
        previous = self.doc if previous is None else previous
        if new_doc == previous:
            return {"changed": False, "gate": summarize(self._validate(new_doc))}
        # The absorb gate is DERIVED data (``absorbDiameterReq``/``absorbable`` come from the
        # prop's aabb and its def's ``volumeK``), so the document is put through the writer
        # before it is validated: the same call the export makes, which means an op that
        # changes a def's k - or a prop's aabb through its def - can never leave a stale row
        # behind for the validator to reject. One implementation, no second formula.
        new_doc = core.canonical_doc(new_doc, self.manifests)
        report = self._validate(new_doc)
        fresh = [error for error in report.errors if error not in self.known_errors]
        if fresh and not force:
            hint = ""
            if all("level.expected" in error for error in fresh):
                hint = ("; the design total is the only rule broken - pass "
                        "updateExpected=true, or batch an `expected` op")
            raise OpError("%s would break %d gate rule(s)%s; nothing was written"
                          % (label, len(fresh), hint), fresh)
        gate = summarize(report)
        exported = None
        if self.auto_export:
            exported = self._export(new_doc, force=True)
            gate = exported["gate"]
        else:
            self.unexported = True
        self.doc = new_doc
        self.known_errors = set(report.errors)
        try:
            counts = self.scene.sync(new_doc, previous)
        except Exception as error:                             # noqa: BLE001 - rollback
            self.doc = previous
            if self.auto_export:
                self._export(previous, force=True)
            self.known_errors = set(self._validate(previous).errors)
            raise OpError("%s could not be applied to the Blender scene (%s); rolled back"
                          % (label, error))
        self.undo_stack.append({"label": label, "doc": previous})
        if len(self.undo_stack) > self.undo_limit:
            self.undo_stack.pop(0)
        return {"changed": True, "label": label, "export": exported,
                "gate": gate, "scene": counts}

    def _mutate(self, new_doc, label, defer=False, extra=None):
        out = dict(extra or {})
        out.update(self._commit(new_doc, label, defer=defer))
        return out

    # ------------------------------------------------------------------ #
    # ops: lifecycle
    # ------------------------------------------------------------------ #
    def op_status(self, args):
        _known(args, ())
        self._require_open()
        report = self._validate(self.doc)
        scene = {}
        if hasattr(self.scene, "status"):
            try:
                scene = self.scene.status() or {}
            except Exception as error:                         # noqa: BLE001 - status
                scene = {"error": str(error)}
        file_state = {}
        if self.path is not None and self.path.is_file():
            text = self.path.read_text(encoding="utf-8")
            file_state = {"bytes": len(text.encode("utf-8")),
                          "canonical": text == core.dump_doc(self.doc, self.manifests)}
        return {
            "level": self.level,
            "path": core.display_path(self.path),
            "counts": self._counts(self.doc),
            "gate": summarize(report),
            "dirty": self._dirty(),
            "unexported": self.unexported,
            "file": file_state,
            "lastExport": self.last_export,
            "undo": [entry["label"] for entry in self.undo_stack],
            "autoExport": self.auto_export,
            "uptimeSeconds": round(time.time() - self.started, 1),
            "opsServed": self.op_count,
            "scene": scene,
            "outDir": str(self.out_dir) if self.out_dir else None,
        }

    def op_open(self, args):
        _known(args, ("level", "path"))
        return self.open(args.get("level"), args.get("path"))

    def open(self, level=None, path=None):
        if path is None and level is None:
            raise OpError("pass level (a directory name under Docs/Design/Levels) or path")
        flushed = self._flush_pending()
        if path is None:
            path = core.placement_path(level, self.levels_dir)
        else:
            path = Path(path).expanduser()
            if not path.is_absolute():
                path = core.ROOT / path
        if not path.is_file():
            raise OpError("cannot read %s" % core.display_path(path))
        try:
            doc = core.load_doc(path)
        except json.JSONDecodeError as error:
            raise OpError("%s is not valid JSON: %s" % (core.display_path(path), error))
        level_id = (doc.get("level") or {}).get("id")
        if level and level_id and str(level_id) != str(level):
            raise OpError("%s says level.id=%r, not %r" % (core.display_path(path),
                                                           level_id, level))
        # A def that declares its own ``model`` may keep its manifest beside the model
        # instead of in the repo's props/exports; the same merge the validator does.
        self.manifests = core.manifests_with_models(doc, self.base_manifests)
        report = self._validate(doc)
        self.doc = doc
        self.path = path
        self.level = level_id or level
        self.undo_stack = []
        self.known_errors = set(report.errors)
        self.unexported = False
        self.scene.reset()
        counts = self.scene.sync(doc, None)
        return {"level": self.level, "path": core.display_path(path),
                "counts": self._counts(doc), "gate": summarize(report),
                "scene": counts, "flushed": flushed,
                "canonical": path.read_text(encoding="utf-8")
                == core.dump_doc(doc, self.manifests)}

    def op_reload(self, args):
        _known(args, ("path",))
        self._require_open()
        flushed = self._flush_pending()
        path = self._resolve(args["path"]) if args.get("path") else self.path
        previous = self.doc
        doc = core.load_doc(path)
        self.manifests = core.manifests_with_models(doc, self.base_manifests)
        report = self._validate(doc)
        self.path = Path(path)
        self.doc = doc
        self.level = (doc.get("level") or {}).get("id") or self.level
        self.undo_stack = []
        self.known_errors = set(report.errors)
        self.unexported = False
        counts = self.scene.sync(doc, previous)
        return {"level": self.level, "path": core.display_path(self.path),
                "counts": self._counts(doc), "gate": summarize(report),
                "scene": counts, "flushed": flushed,
                "undo": "cleared (the document jumped)"}

    def op_shutdown(self, args):
        _known(args, ("save",))
        save = args.get("save", True)
        out = {"stopped": True}
        if self.doc is not None and save and self._dirty():
            out["export"] = self._export(self.doc)
        self.stop_requested = True
        return out

    # ------------------------------------------------------------------ #
    # ops: reading
    # ------------------------------------------------------------------ #
    def op_list(self, args):
        _known(args, ("zone", "def", "kind", "support", "stackedOn", "parent", "text", "near",
                      "radius", "limit"))
        self._require_open()
        items = _props(self.doc)
        if "zone" in args:
            items = [prop for prop in items if prop.get("zone") == args["zone"]]
        if "def" in args:
            items = [prop for prop in items if prop.get("def") == args["def"]]
        if "kind" in args:
            items = [prop for prop in items if prop.get("kind") == args["kind"]]
        if "support" in args:
            items = [prop for prop in items if prop.get("support") == args["support"]]
        if "stackedOn" in args:
            items = [prop for prop in items if prop.get("stackedOn") == args["stackedOn"]]
        if "parent" in args:
            items = [prop for prop in items if core.prop_parent(prop) == args["parent"]]
        if "text" in args:
            needle = str(args["text"]).lower()
            items = [prop for prop in items
                     if needle in str(prop.get("itemId", "")).lower()
                     or needle in str(prop.get("def", "")).lower()
                     or needle in str(prop.get("zone", "")).lower()]
        if "near" in args:
            point = args["near"]
            if not isinstance(point, (list, tuple)) or len(point) not in (2, 3):
                raise OpError("near must be [x, z] (or [x, y, z]), got %r" % (point,))
            try:
                near_x, near_z = float(point[0]), float(point[-1])
            except (TypeError, ValueError):
                raise OpError("near must be two numbers, got %r" % (point,))
            radius = _number(args, "radius", 1.0)
            inside = []
            for prop in items:
                try:
                    dx = float(prop["position"][0]) - near_x
                    dz = float(prop["position"][2]) - near_z
                except (KeyError, TypeError, ValueError, IndexError):
                    continue
                if math.hypot(dx, dz) <= radius:
                    inside.append(prop)
            items = inside
        limit = _integer(args, "limit", len(items), minimum=0)
        return {
            "total": len(items),
            "count": min(limit, len(items)),
            "props": [{"itemId": prop.get("itemId"), "def": prop.get("def"),
                       "zone": prop.get("zone"), "kind": prop.get("kind"),
                       "position": prop.get("position"),
                       "support": prop.get("support"),
                       "stackedOn": prop.get("stackedOn"),
                       "parent": prop.get("parent"),
                       "parentDerived": prop.get("parentDerived")}
                      for prop in items[:limit]],
        }

    def op_get(self, args):
        _known(args, ("id",))
        self._require_open()
        entity_id = _string(args, "id")
        kind, entry = find_entity(self.doc, entity_id)
        if kind is not None:
            return {"type": kind, "entry": entry}
        for camera in self.doc.get("cameras") or []:
            if camera.get("id") == entity_id:
                return {"type": "camera", "entry": camera}
        for preset in self.doc.get("renderPresets") or []:
            if preset.get("id") == entity_id:
                return {"type": "preset", "entry": preset}
        raise OpError("%r is not a prop, structure, camera or preset id" % entity_id)

    def op_check(self, args):
        _known(args, ("report", "strict"))
        self._require_open()
        report = self._validate(self.doc)
        out = summarize(report)
        if args.get("strict"):
            out["ok"] = out["ok"] and not report.warnings
        if args.get("report"):
            out["tables"] = report.tables
            out["text"] = core.format_report_tables(
                report.tables, expected=(self.doc.get("level") or {}).get("expected"))
        out["dirty"] = self._dirty()
        if self.path is not None and self.path.is_file():
            text = self.path.read_text(encoding="utf-8")
            out["file"] = core.display_path(self.path)
            out["inSync"] = text == core.dump_doc(self.doc, self.manifests)
            out["canonical"] = text == core.dump_doc(self.doc, self.manifests)
        return out

    # ------------------------------------------------------------------ #
    # ops: exporting
    # ------------------------------------------------------------------ #
    def op_export(self, args):
        _known(args, ("to", "force"))
        self._require_open()
        return self._export(self.doc, target=args.get("to"),
                            force=bool(args.get("force", False)))

    # ------------------------------------------------------------------ #
    # ops: props
    # ------------------------------------------------------------------ #
    def op_add_prop(self, args, defer=False):
        _known(args, ("itemId", "def", "position", "yaw", "rotationQuat", "kind", "zone",
                      "group", "groupId", "support", "stackedOn", "parent", "valueUnits",
                      "volumeK", "volumePenalty", "notes", "seat", "updateExpected"))
        self._require_open()
        new_doc = _deepcopy(self.doc)
        def_id = _string(args, "def")
        manifest = self.manifests.get(def_id)
        if manifest is None:
            raise OpError("def %r has no manifest in Docs/Art/OriginalModels/props/exports"
                          % def_id)
        item_id = args.get("itemId") or next_item_id(new_doc, def_id)
        if not core.is_valid_item_id(item_id):
            raise OpError("%r is not a valid itemId" % item_id)
        if find_entity(new_doc, item_id)[0] is not None:
            raise OpError("%r already exists" % item_id)

        aabb = [core.rounded(value, core.SIZE_DECIMALS) for value in manifest["aabb"]]
        if "position" in args:
            position = _vec3_arg(args, "position")
        else:
            floor = core.ground_level(new_doc)
            position = [0.0, floor + aabb[1] / 2.0, 0.0]
        if "rotationQuat" in args:
            quat = _quat(_required(args, "rotationQuat"), "rotationQuat")
        else:
            quat = _yaw_quat(args.get("yaw", 0.0))
        catalog = core.def_catalog(new_doc)
        prop = {
            "itemId": item_id,
            "def": def_id,
            "position": [core.rounded(v, core.POSITION_DECIMALS) for v in position],
            "rotationQuat": [core.rounded(v, core.QUAT_DECIMALS)
                             for v in core.canonical_quat(tuple(quat))],
            "aabb": aabb,
        }
        kind = args.get("kind") or (catalog.get(def_id) or {}).get("kind") \
            or manifest.get("category")
        if kind:
            prop["kind"] = kind
        if "zone" in args:
            prop["zone"] = _string(args, "zone")
        if "group" in args:
            prop["group"] = _string(args, "group")
        if "groupId" in args:
            prop["groupId"] = _string(args, "groupId")
        if "support" in args:
            prop["support"] = _string(args, "support")
        if "stackedOn" in args:
            prop["stackedOn"] = _string(args, "stackedOn")
        if "parent" in args:
            prop["parent"] = _string(args, "parent")
        if "notes" in args:
            prop["notes"] = _string(args, "notes")
        # `valueUnits` is DEPRECATED: growth is derived from aabb x k x penalty, so a prop
        # needs no value argument at all.  An old caller may still pass one; it is kept on
        # the row (and dropped by the canonical writer) but never used for growth.
        if "valueUnits" in args:
            prop["valueUnits"] = _integer(args, "valueUnits", minimum=1)
        new_doc.setdefault("props", []).append(prop)
        if args.get("seat"):
            seat_props(new_doc, {item_id})
        if args.get("updateExpected"):
            refresh_expected(new_doc)
        return self._mutate(new_doc, "add_prop %s" % item_id, defer=defer,
                            extra={"itemId": item_id, "prop": prop})

    def op_add_structure(self, args, defer=False):
        _known(args, ("id", "role", "shape", "kind", "center", "size", "rotationQuat",
                      "yaw", "collision", "absorbable", "support", "meets", "material",
                      "notes"))
        self._require_open()
        new_doc = _deepcopy(self.doc)
        role = _string(args, "role")
        if role not in core.PROP_ROLES:
            raise OpError("role must be one of %s" % ", ".join(core.PROP_ROLES))
        structure_id = args.get("id") or next_structure_id(new_doc)
        if find_entity(new_doc, structure_id)[0] is not None:
            raise OpError("%r already exists" % structure_id)
        shape = args.get("shape") or ("plane" if role in ("ground", "boundary") else "box")
        if shape not in core.SHAPES:
            raise OpError("shape must be one of %s" % ", ".join(core.SHAPES))
        center = _vec3_arg(args, "center")
        size = _vec3_arg(args, "size")
        if any(value <= 0.0 for value in size):
            raise OpError("size must be positive on every axis, got %r" % (size,))
        if "rotationQuat" in args:
            quat = _quat(_required(args, "rotationQuat"), "rotationQuat")
        else:
            quat = _yaw_quat(args.get("yaw", 0.0))
        structure = {
            "id": structure_id,
            "role": role,
            "shape": shape,
            "center": [core.rounded(v, core.POSITION_DECIMALS) for v in center],
            "size": [core.rounded(v, core.SIZE_DECIMALS) for v in size],
            "rotationQuat": [core.rounded(v, core.QUAT_DECIMALS)
                             for v in core.canonical_quat(tuple(quat))],
            "collision": args.get("collision")
            or ("none" if role in ("ground", "boundary") else "box"),
            "absorbable": bool(args.get("absorbable", False)),
            "support": bool(args.get("support", role in ("ground", "box", "ramp"))),
        }
        if structure["collision"] not in core.COLLISIONS:
            raise OpError("collision must be one of %s" % ", ".join(core.COLLISIONS))
        if "kind" in args:
            structure["kind"] = _string(args, "kind")
        for key in ("meets", "material", "notes"):
            if key in args:
                structure[key] = _string(args, key)
        new_doc.setdefault("structures", []).append(structure)
        return self._mutate(new_doc, "add_structure %s" % structure_id, defer=defer,
                            extra={"id": structure_id, "structure": structure})

    def op_set(self, args, defer=False):
        _known(args, ("id", "updateExpected") + PROP_SETTABLE + STRUCTURE_SETTABLE)
        self._require_open()
        entity_id = _string(args, "id")
        kind, _entry = find_entity(self.doc, entity_id)
        if kind is None:
            raise OpError("%r is not a prop or structure id" % entity_id)
        new_doc = _deepcopy(self.doc)
        kind, entry = find_entity(new_doc, entity_id)
        if kind == "prop":
            self._set_prop(entry, args)
            fields = sorted(set(args) - {"id"})
            label = "set %s (%s)" % (entity_id, ",".join(fields))
        else:
            self._set_structure(entry, args)
            fields = sorted(set(args) - {"id"})
            label = "set %s (%s)" % (entity_id, ",".join(fields))
        if args.get("updateExpected"):
            refresh_expected(new_doc)
        return self._mutate(new_doc, label, defer=defer,
                            extra={"type": kind, "entry": entry})

    def _set_prop(self, prop, args):
        if "def" in args:
            def_id = _string(args, "def")
            manifest = self.manifests.get(def_id)
            if manifest is None:
                raise OpError("def %r has no manifest" % def_id)
            prop["def"] = def_id
            prop["aabb"] = [core.rounded(value, core.SIZE_DECIMALS)
                            for value in manifest["aabb"]]
        if "position" in args:
            prop["position"] = [core.rounded(v, core.POSITION_DECIMALS)
                                for v in _vec3_arg(args, "position")]
        if "rotationQuat" in args:
            prop["rotationQuat"] = self._quat_field(args)
        elif "yaw" in args:
            prop["rotationQuat"] = self._yaw_field(args)
        for key in ("kind", "zone", "group", "notes"):
            if key in args:
                prop[key] = _string(args, key)
        if "groupId" in args:
            if args["groupId"] is None:
                prop.pop("groupId", None)
            else:
                prop["groupId"] = _string(args, "groupId")
        for key in ("support", "stackedOn", "parent"):
            if key not in args:
                continue
            if args[key] is None:
                prop.pop(key, None)
            else:
                prop[key] = _string(args, key)
        if "parentDerived" in args:
            # Only meaningful next to a `parent`; clearing it restores "hand-authored".
            if args["parentDerived"] is None:
                prop.pop("parentDerived", None)
            else:
                prop["parentDerived"] = _bool(args["parentDerived"], "parentDerived")
        if "valueUnits" in args:
            # DEPRECATED: accepted for old callers, kept on the row, never used for growth.
            prop["valueUnits"] = _integer(args, "valueUnits", minimum=1)
        if "volumeK" in args:
            prop["volumeK"] = self._volume_k_field(args["volumeK"])
        if "volumePenalty" in args:
            prop["volumePenalty"] = self._volume_penalty_field(args["volumePenalty"])

    def _volume_k_field(self, value):
        """A validated ``volumeK``: finite, ``0 < k <= 32`` (core.volume_k_error)."""
        if value is None:
            raise OpError("volumeK cannot be cleared to null; delete the key to restore the "
                          "0.70 default")
        reason = core.volume_k_error(value)
        if reason is not None:
            raise OpError("volumeK %r %s" % (value, reason))
        return core.rounded(float(value), 4)

    def _volume_penalty_field(self, value):
        """A validated ``volumePenalty``: finite, ``0 < penalty <= 1`` (core rule)."""
        if value is None:
            raise OpError("volumePenalty cannot be cleared to null; delete the key to "
                          "restore the 0.80 default")
        reason = core.volume_penalty_error(value)
        if reason is not None:
            raise OpError("volumePenalty %r %s" % (value, reason))
        return core.rounded(float(value), 4)

    def _set_structure(self, structure, args):
        if "center" in args:
            structure["center"] = [core.rounded(v, core.POSITION_DECIMALS)
                                   for v in _vec3_arg(args, "center")]
        if "size" in args:
            size = _vec3_arg(args, "size")
            if any(value <= 0.0 for value in size):
                raise OpError("size must be positive on every axis, got %r" % (size,))
            structure["size"] = [core.rounded(v, core.SIZE_DECIMALS) for v in size]
        if "rotationQuat" in args:
            structure["rotationQuat"] = self._quat_field(args)
        elif "yaw" in args:
            structure["rotationQuat"] = self._yaw_field(args)
        if "role" in args:
            if args["role"] not in core.PROP_ROLES:
                raise OpError("role must be one of %s" % ", ".join(core.PROP_ROLES))
            structure["role"] = args["role"]
        if "shape" in args:
            if args["shape"] not in core.SHAPES:
                raise OpError("shape must be one of %s" % ", ".join(core.SHAPES))
            structure["shape"] = args["shape"]
        if "collision" in args:
            if args["collision"] not in core.COLLISIONS:
                raise OpError("collision must be one of %s" % ", ".join(core.COLLISIONS))
            structure["collision"] = args["collision"]
        if "absorbable" in args:
            structure["absorbable"] = _bool(args["absorbable"], "absorbable")
        if "support" in args:
            structure["support"] = _bool(args["support"], "support")
        for key in ("kind", "meets", "material", "notes"):
            if key in args:
                structure[key] = _string(args, key)

    def _quat_field(self, args):
        return [core.rounded(v, core.QUAT_DECIMALS)
                for v in core.canonical_quat(tuple(_quat(_required(args, "rotationQuat"),
                                                        "rotationQuat")))]

    def _yaw_field(self, args):
        return [core.rounded(v, core.QUAT_DECIMALS)
                for v in core.canonical_quat(tuple(_yaw_quat(_number(args, "yaw"))))]

    def op_move(self, args, defer=False):
        _known(args, ("id", "to", "delta", "x", "y", "z"))
        self._require_open()
        entity_id = _string(args, "id")
        kind, entry = find_entity(self.doc, entity_id)
        if kind is None:
            raise OpError("%r is not a prop or structure id" % entity_id)
        key = "position" if kind == "prop" else "center"
        current = [float(value) for value in entry[key]]
        if ("to" in args) == ("delta" in args):
            raise OpError("pass exactly one of to (absolute) or delta (relative)")
        if "to" in args:
            target = _vec3_arg(args, "to")
        else:
            target = [current[index] + value
                      for index, value in enumerate(_vec3_arg(args, "delta"))]
        for axis in ("x", "y", "z"):
            if axis in args:
                target["xyz".index(axis)] = _number(args, axis)
        new_doc = _deepcopy(self.doc)
        kind, new_entry = find_entity(new_doc, entity_id)
        new_entry[key] = [core.rounded(value, core.POSITION_DECIMALS) for value in target]
        return self._mutate(new_doc, "move %s" % entity_id, defer=defer,
                            extra={"type": kind, "id": entity_id,
                                   "previous": current, key: new_entry[key]})

    def op_rotate(self, args, defer=False):
        _known(args, ("id", "yaw", "quat", "rotationQuat", "axis", "degrees"))
        self._require_open()
        entity_id = _string(args, "id")
        kind, _entry = find_entity(self.doc, entity_id)
        if kind is None:
            raise OpError("%r is not a prop or structure id" % entity_id)
        given = [key for key in ("yaw", "quat", "rotationQuat", "axis")
                 if key in args]
        if len(given) != 1:
            raise OpError("pass exactly one of yaw (degrees), quat/rotationQuat, "
                          "or axis + degrees")
        if given[0] == "yaw":
            quat = _yaw_quat(_number(args, "yaw"))
        elif given[0] in ("quat", "rotationQuat"):
            key = "quat" if "quat" in args else "rotationQuat"
            quat = _quat(_required(args, key), key)
        else:
            axis = _vec3_arg(args, "axis")
            degrees = _number(args, "degrees")
            if math.sqrt(sum(value * value for value in axis)) < 1e-6:
                raise OpError("axis must not be zero")
            quat = list(core.quat_axis_angle(tuple(axis), math.radians(degrees)))
        new_doc = _deepcopy(self.doc)
        kind, new_entry = find_entity(new_doc, entity_id)
        new_entry["rotationQuat"] = [core.rounded(v, core.QUAT_DECIMALS)
                                     for v in core.canonical_quat(tuple(quat))]
        return self._mutate(new_doc, "rotate %s" % entity_id, defer=defer,
                            extra={"type": kind, "id": entity_id,
                                   "rotationQuat": new_entry["rotationQuat"]})

    def op_remove(self, args, defer=False):
        _known(args, ("id", "ids", "updateExpected"))
        self._require_open()
        ids = args.get("ids")
        if ids is None:
            ids = [_string(args, "id")]
        if not isinstance(ids, list) or not ids:
            raise OpError("ids must be a non-empty list")
        new_doc = _deepcopy(self.doc)
        removed = []
        for entity_id in ids:
            kind, entry = find_entity(new_doc, str(entity_id))
            if kind is None:
                raise OpError("%r is not a prop or structure id" % entity_id)
            if kind == "prop":
                new_doc["props"] = [prop for prop in new_doc["props"]
                                    if prop.get("itemId") != entity_id]
            else:
                new_doc["structures"] = [structure for structure in new_doc["structures"]
                                         if structure.get("id") != entity_id]
            removed.append({"id": entity_id, "type": kind})
        if args.get("updateExpected"):
            refresh_expected(new_doc)
        return self._mutate(new_doc, "remove %s" % ",".join(removed_item["id"]
                                                           for removed_item in removed),
                            defer=defer, extra={"removed": removed})

    def op_seat(self, args, defer=False):
        _known(args, ("id", "ids", "all"))
        self._require_open()
        ids = args.get("ids")
        if ids is None and "id" in args:
            ids = [args["id"]]
        if ids is not None:
            if not isinstance(ids, list):
                raise OpError("ids must be a list")
            ids = [str(item) for item in ids]
        new_doc = _deepcopy(self.doc)
        moved = seat_props(new_doc, set(ids) if ids else None)
        return self._mutate(new_doc, "seat %d prop(s)" % len(moved["moved"]),
                            defer=defer, extra=moved)

    def op_derive_supports(self, args, defer=False):
        """Derive ``parent`` pointers from geometry; dry run by default.

        ``apply=false`` (the default) answers "what would change": the links the geometry
        finds that the file does not declare yet (``new``), the ones it already agrees
        with, the declarations it cannot reproduce (``conflict`` / ``orphaned``) and the
        props that rest on nothing but the ground or a structure (``noSupport``).  It
        writes nothing.  ``apply=true`` writes only ``new`` links, as ``parent`` +
        ``parentDerived``; it never rewrites or clears an existing pointer.
        """
        _known(args, ("id", "ids", "apply"))
        self._require_open()
        ids = args.get("ids")
        if ids is None and "id" in args:
            ids = [args["id"]]
        if ids is not None:
            if not isinstance(ids, list):
                raise OpError("ids must be a list")
            ids = [str(item) for item in ids]
        scope = set(ids) if ids else None
        if not _bool(args.get("apply", False), "apply"):
            diff = support_geometry.propose(self.doc, scope)
            diff["applied"] = False
            return diff
        new_doc = _deepcopy(self.doc)
        diff = support_geometry.apply_supports(new_doc, scope)
        diff["applied"] = True
        if not diff["written"]:
            # Nothing to write: do not commit, do not export, do not push an undo entry.
            diff["changed"] = False
            return diff
        return self._mutate(new_doc, "derive_supports %d link(s)" % len(diff["written"]),
                            defer=defer, extra=diff)

    def op_add_def(self, args, defer=False):
        _known(args, ("id", "kind", "valueUnits", "model", "volumeK", "volumePenalty",
                      "notes"))
        self._require_open()
        def_id = _string(args, "id")
        new_doc = _deepcopy(self.doc)
        entries = new_doc.setdefault("defs", [])
        entry = next((item for item in entries if item.get("id") == def_id), None)
        if entry is None:
            entry = {"id": def_id}
            entries.append(entry)
        if "kind" in args:
            entry["kind"] = _string(args, "kind")
        if "valueUnits" in args:
            entry["valueUnits"] = _integer(args, "valueUnits", minimum=1)
        if "model" in args:
            if args["model"] is None:
                entry.pop("model", None)
            else:
                entry["model"] = _string(args, "model")
        if "volumeK" in args:
            entry["volumeK"] = self._volume_k_field(args["volumeK"])
        if "volumePenalty" in args:
            entry["volumePenalty"] = self._volume_penalty_field(args["volumePenalty"])
        if "notes" in args:
            entry["notes"] = _string(args, "notes")
        # `valueUnits` is no longer required: growth is DERIVED from aabb/k/penalty, and
        # the deprecated key is only kept when a caller still passes it (old scripts).
        return self._mutate(new_doc, "add_def %s" % def_id, defer=defer,
                            extra={"def": entry})

    def op_set_def(self, args, defer=False):
        """Set one ``defs[]`` row's fields, ``volumeK``/``volumePenalty`` included."""
        _known(args, ("id", "updateExpected") + DEF_SETTABLE)
        self._require_open()
        def_id = _string(args, "id")
        new_doc = _deepcopy(self.doc)
        entries = new_doc.setdefault("defs", [])
        entry = next((item for item in entries if item.get("id") == def_id), None)
        if entry is None:
            raise OpError("def %r is not declared in this document; use add_def" % def_id)
        if "kind" in args:
            entry["kind"] = _string(args, "kind")
        if "valueUnits" in args:
            entry["valueUnits"] = _integer(args, "valueUnits", minimum=1)
        if "model" in args:
            if args["model"] is None:
                entry.pop("model", None)
            else:
                entry["model"] = _string(args, "model")
        if "volumeK" in args:
            if args["volumeK"] is None:
                # Clearing is the documented way back to the 0.70 default: the key is
                # removed, so the canonical file is byte-identical to a never-authored one.
                entry.pop("volumeK", None)
            else:
                entry["volumeK"] = self._volume_k_field(args["volumeK"])
        if "notes" in args:
            entry["notes"] = _string(args, "notes")
        fields = sorted(set(args) - {"id"})
        if args.get("updateExpected"):
            refresh_expected(new_doc)
        return self._mutate(new_doc, "set_def %s (%s)" % (def_id, ",".join(fields)),
                            defer=defer, extra={"def": entry})

    def op_expected(self, args, defer=False):
        _known(args, ("propCount", "growthMm3", "valueUnits", "structureCount", "staticObbs",
                      "overlapBudget", "fromDocument"))
        self._require_open()
        new_doc = _deepcopy(self.doc)
        level = new_doc.setdefault("level", {})
        expected = dict(level.get("expected") or {})
        if args.get("fromDocument", True):
            totals = core.expected_totals(new_doc)
            expected["propCount"] = totals["propCount"]
            expected["growthMm3"] = totals["growthMm3"]
            expected.pop("valueUnits", None)
            for key in ("structureCount", "staticObbs"):
                if key in expected:
                    expected[key] = totals[key]
        if "propCount" in args:
            expected["propCount"] = _integer(args, "propCount", minimum=0)
        if "growthMm3" in args:
            # Informational: canonical_doc always re-derives it from the props.
            expected["growthMm3"] = _integer(args, "growthMm3", minimum=0)
        if "structureCount" in args:
            expected["structureCount"] = _integer(args, "structureCount", minimum=0)
        if "staticObbs" in args:
            expected["staticObbs"] = _integer(args, "staticObbs", minimum=0)
        if "overlapBudget" in args:
            if args["overlapBudget"] is None:
                expected.pop("overlapBudget", None)
            else:
                expected["overlapBudget"] = _integer(args, "overlapBudget", minimum=0)
        level["expected"] = expected
        return self._mutate(new_doc, "expected %s" % json.dumps(expected, sort_keys=True),
                            defer=defer, extra={"expected": expected})

    # ------------------------------------------------------------------ #
    # ops: cameras and presets
    # ------------------------------------------------------------------ #
    def op_camera(self, args, defer=False):
        _known(args, ("action", "id", "type", "location", "target", "lens", "coverage_m"))
        self._require_open()
        action = args.get("action", "set")
        if action == "list":
            return {"cameras": list(self.doc.get("cameras") or [])}
        if action not in ("set", "remove"):
            raise OpError("action must be set, remove or list")
        camera_id = _string(args, "id")
        new_doc = _deepcopy(self.doc)
        cameras = new_doc.setdefault("cameras", [])
        existing = next((item for item in cameras if item.get("id") == camera_id), None)
        if action == "remove":
            if existing is None:
                raise OpError("camera %r does not exist" % camera_id)
            new_doc["cameras"] = [item for item in cameras if item.get("id") != camera_id]
            users = [preset.get("id") for preset in new_doc.get("renderPresets") or []
                     if preset.get("camera") == camera_id]
            return self._mutate(new_doc, "remove camera %s" % camera_id, defer=defer,
                                extra={"removed": camera_id,
                                       "presetsUsingIt": users})
        camera = dict(existing or {"id": camera_id})
        camera["type"] = args.get("type", camera.get("type", "perspective"))
        if camera["type"] not in core.CAMERA_TYPES:
            raise OpError("type must be one of %s" % ", ".join(core.CAMERA_TYPES))
        if "location" in args:
            camera["location"] = [core.rounded(v, core.CAMERA_DECIMALS)
                                  for v in _vec3_arg(args, "location")]
        if "target" in args:
            camera["target"] = [core.rounded(v, core.CAMERA_DECIMALS)
                                for v in _vec3_arg(args, "target")]
        if "lens" in args:
            camera["lens"] = _number(args, "lens", 45.0)
        if "coverage_m" in args:
            camera["coverage_m"] = _number(args, "coverage_m", 10.0)
        if "location" not in camera:
            raise OpError("camera %r needs location" % camera_id)
        if camera["type"] == "perspective":
            if "target" not in camera:
                raise OpError("a perspective camera needs target")
            camera.setdefault("lens", 45.0)
        else:
            camera["target"] = [camera["location"][0],
                                float((camera.get("target") or [0.0, 0.0, 0.0])[1]),
                                camera["location"][2]]
            camera.setdefault("coverage_m", 10.0)
        if existing is None:
            cameras.append(camera)
        else:
            existing.clear()
            existing.update(camera)
        return self._mutate(new_doc, "camera %s" % camera_id, defer=defer,
                            extra={"camera": camera})

    def op_preset(self, args, defer=False):
        _known(args, ("action", "id", "camera", "engine", "res", "samples"))
        self._require_open()
        action = args.get("action", "set")
        if action == "list":
            return {"presets": list(self.doc.get("renderPresets") or [])}
        if action not in ("set", "remove"):
            raise OpError("action must be set, remove or list")
        preset_id = _string(args, "id")
        new_doc = _deepcopy(self.doc)
        presets = new_doc.setdefault("renderPresets", [])
        existing = next((item for item in presets if item.get("id") == preset_id), None)
        if action == "remove":
            if existing is None:
                raise OpError("preset %r does not exist" % preset_id)
            new_doc["renderPresets"] = [item for item in presets
                                        if item.get("id") != preset_id]
            return self._mutate(new_doc, "remove preset %s" % preset_id, defer=defer,
                                extra={"removed": preset_id})
        preset = dict(existing or {"id": preset_id})
        if "camera" in args:
            camera_id = _string(args, "camera")
            known = {camera.get("id") for camera in new_doc.get("cameras") or []}
            if camera_id not in known:
                raise OpError("camera %r does not exist (known: %s)"
                              % (camera_id, ", ".join(sorted(known))))
            preset["camera"] = camera_id
        if "engine" in args:
            engine = _string(args, "engine").upper()
            if engine not in core.ENGINES:
                raise OpError("engine must be one of %s" % ", ".join(core.ENGINES))
            preset["engine"] = engine
        if "res" in args:
            res = args["res"]
            if not isinstance(res, (list, tuple)) or len(res) != 2:
                raise OpError("res must be [width, height]")
            preset["res"] = [_integer({"w": res[0]}, "w", minimum=1),
                             _integer({"h": res[1]}, "h", minimum=1)]
        if "samples" in args:
            preset["samples"] = _integer(args, "samples", minimum=0)
        if "camera" not in preset:
            raise OpError("preset %r needs camera" % preset_id)
        preset.setdefault("engine", "WORKBENCH")
        preset.setdefault("res", [1280, 720])
        if existing is None:
            presets.append(preset)
        else:
            existing.clear()
            existing.update(preset)
        return self._mutate(new_doc, "preset %s" % preset_id, defer=defer,
                            extra={"preset": preset})

    # ------------------------------------------------------------------ #
    # ops: render and undo
    # ------------------------------------------------------------------ #
    def op_render(self, args):
        _known(args, ("preset", "camera", "engine", "res", "samples", "out", "name"))
        self._require_open()
        preset = None
        if args.get("preset"):
            preset_id = str(args["preset"])
            preset = next((item for item in self.doc.get("renderPresets") or []
                           if item.get("id") == preset_id), None)
            if preset is None:
                raise OpError("preset %r is not in renderPresets (known: %s)"
                              % (preset_id, ", ".join(sorted(
                                  str(item.get("id"))
                                  for item in self.doc.get("renderPresets") or []))))
            camera_id = preset.get("camera")
            camera = next((item for item in self.doc.get("cameras") or []
                           if item.get("id") == camera_id), None)
            if camera is None:
                raise OpError("preset %r points at camera %r which does not exist"
                              % (preset_id, camera_id))
            label = preset_id
        elif args.get("camera"):
            camera = self._inline_camera(args["camera"])
            label = str(camera["id"])
        else:
            raise OpError("pass preset <id> or camera {…}")

        engine = str(args.get("engine") or (preset or {}).get("engine")
                     or "WORKBENCH").upper()
        if engine not in core.ENGINES:
            raise OpError("engine must be one of %s" % ", ".join(core.ENGINES))
        res = args.get("res") or (preset or {}).get("res") or [1280, 720]
        if not isinstance(res, (list, tuple)) or len(res) != 2:
            raise OpError("res must be [width, height]")
        try:
            res = [int(res[0]), int(res[1])]
        except (TypeError, ValueError):
            raise OpError("res must be two whole numbers, got %r" % (res,))
        if res[0] < 1 or res[1] < 1:
            raise OpError("res must be positive, got %r" % (res,))
        samples = int(args.get("samples") or (preset or {}).get("samples") or 0)

        out_dir = (self._resolve(args["out"]) if args.get("out") else self.out_dir
                   or (Path("/tmp/levelbuild-render") / str(self.level or "level")))
        name = args.get("name") or "%s_%s_%s" % (self.level or "level", label,
                                                 engine.lower())
        path = Path(out_dir) / (name if str(name).endswith(".png") else str(name) + ".png")
        path.parent.mkdir(parents=True, exist_ok=True)
        spec = {"camera": camera, "engine": engine, "res": res, "samples": samples,
                "path": str(path), "preset": preset, "label": label}
        result = self.scene.render(spec, self.doc)
        out = {"path": str(Path(result.get("path", path)).resolve()),
               "requested": str(path), "engine": engine,
               "engineId": result.get("engine_id"),
               "res": res, "preset": label, "seconds": result.get("seconds"),
               "bytes": None}
        if Path(out["path"]).is_file():
            out["bytes"] = Path(out["path"]).stat().st_size
        return out

    def _inline_camera(self, value):
        if not isinstance(value, dict):
            raise OpError("camera must be an object with type/location/target")
        _known(value, ("id", "type", "location", "target", "lens", "coverage_m"))
        camera = dict(value)
        camera.setdefault("id", "adhoc")
        camera.setdefault("type", "perspective")
        if camera["type"] not in core.CAMERA_TYPES:
            raise OpError("camera.type must be one of %s" % ", ".join(core.CAMERA_TYPES))
        if "location" not in camera:
            raise OpError("camera.location is required")
        camera["location"] = _vec3_arg(camera, "location")
        if camera["type"] == "perspective":
            if "target" not in camera:
                raise OpError("a perspective camera needs target")
            camera["target"] = _vec3_arg(camera, "target")
            camera.setdefault("lens", 45.0)
        else:
            camera.setdefault("coverage_m", 10.0)
            camera.setdefault("target", [camera["location"][0], 0.0, camera["location"][2]])
        return camera

    def op_undo(self, args):
        _known(args, ("steps",))
        self._require_open()
        steps = _integer(args, "steps", 1, minimum=1)
        if not self.undo_stack:
            raise OpError("nothing to undo")
        if steps > len(self.undo_stack):
            raise OpError("only %d step(s) in the undo stack" % len(self.undo_stack))
        taken = self.undo_stack[-steps:]
        del self.undo_stack[-steps:]
        target = _deepcopy(taken[0]["doc"])
        report = self._validate(target)
        exported = self._export(target, force=True)
        previous = self.doc
        self.doc = target
        self.known_errors = set(report.errors)
        try:
            counts = self.scene.sync(target, previous)
        except Exception as error:                             # noqa: BLE001 - rollback
            self.doc = previous
            self._export(previous, force=True)
            self.undo_stack.extend(taken)
            raise OpError("undo could not be applied to the Blender scene (%s); "
                          "the document was left as it was" % error)
        return {"undone": [entry["label"] for entry in taken],
                "steps": steps, "export": exported,
                "gate": exported["gate"], "scene": counts,
                "counts": self._counts(target)}


# --------------------------------------------------------------------------- #
# op catalogue (``levelctl.py ops``)
# --------------------------------------------------------------------------- #
OP_HELP = {
    "status": "the open level, counts, gate, dirty flag, undo depth, scene facts",
    "open": "open a level (level=<id> or path=<placement.json>) and build the scene",
    "reload": "re-read placement.json from disk and re-sync the scene",
    "list": "props, filtered by zone/def/kind/support/stackedOn/text/near+radius",
    "get": "one prop, structure, camera or preset by id",
    "add_prop": "def + optional itemId/position/yaw/kind/zone/support/stackedOn/valueUnits/seat",
    "add_structure": "role + center + size + rotationQuat/yaw (+kind/shape/collision/…)",
    "set": "set fields on a prop or structure (position, rotationQuat, yaw, zone, …)",
    "move": "move a prop/structure: to=[x,y,z] or delta=[dx,dy,dz]",
    "rotate": "rotate: yaw=<deg>, quat=[x,y,z,w], or axis=[..] + degrees",
    "remove": "remove by id (or ids=[...])",
    "seat": "re-seat props on their declared support (id/ids, or all)",
    "derive_supports": "derive parent pointers from geometry: dry run, or apply=true to write",
    "add_def": "add or update a defs[] entry (valueUnits is required)",
    "expected": "set level.expected (defaults to the document's own totals)",
    "camera": "camera presets: action=set/remove/list",
    "preset": "render presets: action=set/remove/list",
    "check": "validate the resident document (report=true adds the tables)",
    "export": "validate + write canonical placement.json (to=<path> for a copy, force=true)",
    "render": "render: preset=<id> or camera={…}; engine/res/samples/out/name",
    "undo": "undo the last N committed operations (steps=1)",
    "shutdown": "export if dirty and stop the resident editor",
    "batch": "ops=[{op,args},…] applied atomically with one export and one scene sync",
}
